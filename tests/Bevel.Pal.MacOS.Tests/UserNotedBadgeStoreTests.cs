using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bevel.Pal.MacOS;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>
/// <see cref="UserNotedBadgeStore"/> reads usernoted's private db (app.identifier → app.badge), the
/// raw counts the Dock truncates. The store must be honest about failure: a missing, locked or
/// corrupt db yields an EMPTY map, never an exception — the badge poll treats empty as "no raw
/// counts" and falls back to the Dock's label. Synthetic-db tests run on every OS (SQLitePCLRaw
/// ships native libs for all CI legs, same as TabFaviconStoreTests); only the live-db and
/// chmod-based fallback tests are platform-gated.
/// </summary>
public class UserNotedBadgeStoreTests : IDisposable
{
    private readonly List<string> _tempDirs = new();
    private readonly TimeSpan _savedInterval = UserNotedBadgeStore.MinSnapshotInterval;

    public void Dispose()
    {
        UserNotedBadgeStore.MinSnapshotInterval = _savedInterval;
        UserNotedBadgeStore.ResetSnapshotStateForTests();
        foreach (var d in _tempDirs)
            try { Directory.Delete(d, recursive: true); } catch { }
    }

    private string NewTempDir()
    {
        var d = Directory.CreateTempSubdirectory("bevel-usernoted-test-");
        _tempDirs.Add(d.FullName);
        return d.FullName;
    }

    private string NewDb(string dir, string name = "db")
    {
        var path = Path.Combine(dir, name);
        using var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE app (app_id INTEGER PRIMARY KEY, identifier VARCHAR, badge INTEGER NULL);" +
            "INSERT INTO app (identifier, badge) VALUES ('net.whatsapp.whatsapp', 12);" +
            "INSERT INTO app (identifier, badge) VALUES ('com.tapbots.ivory', 1082);" +   // 4 digits
            "INSERT INTO app (identifier, badge) VALUES ('com.apple.reminders', NULL);" +    // no badge
            "INSERT INTO app (identifier, badge) VALUES ('com.apple.mobilephone', 0);" +    // zero badge
            "INSERT INTO app (identifier, badge) VALUES (NULL, 5);";                         // no identifier
        cmd.ExecuteNonQuery();
        return path;
    }

    [Fact]
    public void Missing_database_yields_empty_not_an_exception_and_no_snapshot_attempt()
    {
        var ghost = Path.Combine(NewTempDir(), "no-such-db");
        var before = UserNotedBadgeStore.SnapshotAttemptsForTests;

        var counts = UserNotedBadgeStore.ReadCounts(ghost);

        Assert.NotNull(counts);
        Assert.Empty(counts);
        // A missing source must not even attempt a snapshot — that is the temp-dir-leak guard.
        Assert.Equal(before, UserNotedBadgeStore.SnapshotAttemptsForTests);
    }

    [Fact]
    public void Directory_as_db_path_yields_empty_and_no_snapshot_attempt()
    {
        var dir = NewTempDir();
        var before = UserNotedBadgeStore.SnapshotAttemptsForTests;

        var counts = UserNotedBadgeStore.ReadCounts(dir);

        Assert.Empty(counts);
        Assert.Equal(before, UserNotedBadgeStore.SnapshotAttemptsForTests);
    }

    [Fact]
    public void Corrupt_file_yields_empty_not_an_exception()
    {
        var path = Path.Combine(NewTempDir(), "corrupt.db");
        File.WriteAllBytes(path, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 1, 2, 3, 4 });

        var counts = UserNotedBadgeStore.ReadCounts(path);

        Assert.Empty(counts);
    }

    [Fact]
    public void Schema_drift_yields_empty_not_an_exception()
    {
        // A valid SQLite file whose schema no longer has app(identifier, badge) — the macOS-update case.
        var path = Path.Combine(NewTempDir(), "drift.db");
        using (var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE something_else (x INTEGER); INSERT INTO something_else VALUES (1);";
            cmd.ExecuteNonQuery();
        }

        var counts = UserNotedBadgeStore.ReadCounts(path);

        Assert.Empty(counts);
    }

    [Fact]
    public void Reads_counts_from_a_real_usernoted_shaped_db()
    {
        var path = NewDb(NewTempDir());

        var counts = UserNotedBadgeStore.ReadCounts(path);

        Assert.Equal(2, counts.Count);
        Assert.Equal(12, counts["net.whatsapp.whatsapp"]);
        Assert.Equal(1082, counts["com.tapbots.ivory"]);
        Assert.False(counts.ContainsKey("com.apple.reminders"));
        Assert.False(counts.ContainsKey("com.apple.mobilephone"));
    }

    [Fact]
    public void Text_typed_badge_column_still_reads_because_sqlite_is_dynamically_typed()
    {
        var path = Path.Combine(NewTempDir(), "text.db");
        using (var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            // Badge stored as TEXT — SQLite columns carry values, not types.
            cmd.CommandText =
                "CREATE TABLE app (app_id INTEGER PRIMARY KEY, identifier VARCHAR, badge INTEGER NULL);" +
                "INSERT INTO app (identifier, badge) VALUES ('org.telegram.Telegram', '4321');" +
                "INSERT INTO app (identifier, badge) VALUES ('com.example.junk', 'abc');" +  // unparseable → row skipped
                "INSERT INTO app (identifier, badge) VALUES ('com.example.huge', 99999999999);"; // overflows int → row skipped
            cmd.ExecuteNonQuery();
        }

        var counts = UserNotedBadgeStore.ReadCounts(path);

        Assert.Equal(4321, counts["org.telegram.Telegram"]);
        Assert.False(counts.ContainsKey("com.example.junk"));
        Assert.False(counts.ContainsKey("com.example.huge"));
    }

    [Fact]
    public void Snapshot_fallback_is_throttled_so_a_persistent_failure_cannot_copy_every_poll()
    {
        var path = Path.Combine(NewTempDir(), "corrupt.db");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });   // exists, but unreadable as sqlite
        UserNotedBadgeStore.ResetSnapshotStateForTests();
        UserNotedBadgeStore.MinSnapshotInterval = TimeSpan.FromMinutes(10);
        var before = UserNotedBadgeStore.SnapshotAttemptsForTests;

        var first = UserNotedBadgeStore.ReadCounts(path);
        var second = UserNotedBadgeStore.ReadCounts(path);

        Assert.Empty(first);
        Assert.Empty(second);
        // First failure attempts one snapshot; the second call within the interval must not.
        Assert.Equal(before + 1, UserNotedBadgeStore.SnapshotAttemptsForTests);
    }

    [Fact]
    public void Snapshot_fallback_reads_a_locked_out_db_and_cleans_up_its_dir()
    {
        if (OperatingSystem.IsWindows()) return;   // chmod semantics are POSIX

        var dir = NewTempDir();
        var path = NewDb(dir);
        // WAL-mode db cleanly closed: header says WAL, no -shm/-wal beside it. In a read-only
        // directory a read-only open cannot create the -shm → direct read fails; the snapshot
        // (raw byte copy, read permission suffices) then reads fine from a writable dir.
        using (var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }

        UserNotedBadgeStore.ResetSnapshotStateForTests();
        UserNotedBadgeStore.MinSnapshotInterval = TimeSpan.Zero;
        var attemptsBefore = UserNotedBadgeStore.SnapshotAttemptsForTests;
        var mode = File.GetUnixFileMode(dir);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        try
        {
            var counts = UserNotedBadgeStore.ReadCounts(path);

            Assert.True(UserNotedBadgeStore.SnapshotAttemptsForTests > attemptsBefore,
                "direct read should have failed in the read-only dir, forcing the snapshot fallback");
            Assert.Equal(12, counts["net.whatsapp.whatsapp"]);
            Assert.Equal(1082, counts["com.tapbots.ivory"]);
        }
        finally
        {
            File.SetUnixFileMode(dir, mode);
        }

        // The fallback must never leave its snapshot dir behind.
        var root = UserNotedBadgeStore.SnapshotRoot;
        var leftovers = Directory.Exists(root) ? Directory.GetDirectories(root, "snap-*") : Array.Empty<string>();
        Assert.Empty(leftovers);
    }

    [Fact]
    public void Snapshot_dir_is_under_application_data_never_tmp()
    {
        // Repo convention (TabFaviconStore.CacheDir): no db copies in world-writable /tmp.
        var root = UserNotedBadgeStore.SnapshotRoot;
        Assert.Contains("bevel", root, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_path_points_at_the_usernoted_group_container()
    {
        var p = UserNotedBadgeStore.DefaultDatabasePath();
        Assert.EndsWith(UserNotedBadgeStore.RelativeDatabasePath, p, StringComparison.Ordinal);
        Assert.Contains("group.com.apple.usernoted", p, StringComparison.Ordinal);
    }

    [Fact]
    public void Live_read_never_throws_even_when_usernoted_is_mid_write()
    {
        if (!OperatingSystem.IsMacOS()) return;

        // The real db may or may not exist/be readable in CI; either way the contract is: a map
        // (possibly empty), never a throw. Hammering it mimics the 2s badge poll.
        for (var i = 0; i < 10; i++)
        {
            var counts = UserNotedBadgeStore.ReadCounts();
            Assert.NotNull(counts);
            Assert.All(counts, kv => Assert.True(kv.Value > 0, "zero/negative badge leaked through"));
        }
    }
}
