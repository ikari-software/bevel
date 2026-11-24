using System;
using System.Collections.Generic;
using System.IO;
using Bevel.Pal.MacOS;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>
/// <see cref="UserNotedBadgeStore"/> reads usernoted's private db (app.identifier → app.badge), the
/// raw counts the Dock truncates. The store must be honest about failure: a missing, locked or
/// corrupt db yields an EMPTY map, never an exception — the badge poll treats empty as "no raw
/// counts" and falls back to the Dock's label.
/// </summary>
public class UserNotedBadgeStoreTests
{
    [Fact]
    public void Missing_database_yields_empty_not_an_exception()
    {
        var ghost = Path.Combine(Path.GetTempPath(), "bevel-no-such-" + Guid.NewGuid().ToString("N"), "db");
        var counts = UserNotedBadgeStore.ReadCounts(ghost);
        Assert.NotNull(counts);
        Assert.Empty(counts);
    }

    [Fact]
    public void Corrupt_file_yields_empty_not_an_exception()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var path = Path.Combine(Path.GetTempPath(), "bevel-corrupt-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            File.WriteAllBytes(path, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 1, 2, 3, 4 });
            var counts = UserNotedBadgeStore.ReadCounts(path);
            Assert.Empty(counts);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Reads_counts_from_a_real_usernoted_shaped_db()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var path = Path.Combine(Path.GetTempPath(), "bevel-usernoted-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            // Same shape as the real db2/db: app(identifier, badge) — plus rows the query must skip.
            using (var conn = new SqliteConnection($"Data Source={path};Mode=ReadWriteCreate;Pooling=False"))
            {
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
            }

            var counts = UserNotedBadgeStore.ReadCounts(path);

            Assert.Equal(2, counts.Count);
            Assert.Equal(12, counts["net.whatsapp.whatsapp"]);
            Assert.Equal(1082, counts["com.tapbots.ivory"]);
            Assert.False(counts.ContainsKey("com.apple.reminders"));
            Assert.False(counts.ContainsKey("com.apple.mobilephone"));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Text_typed_badge_column_still_reads_because_sqlite_is_dynamically_typed()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var path = Path.Combine(Path.GetTempPath(), "bevel-usernoted-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var conn = new SqliteConnection($"Data Source={path};Mode=ReadWriteCreate;Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                // Badge stored as TEXT — SQLite columns carry values, not types.
                cmd.CommandText =
                    "CREATE TABLE app (app_id INTEGER PRIMARY KEY, identifier VARCHAR, badge INTEGER NULL);" +
                    "INSERT INTO app (identifier, badge) VALUES ('org.telegram.Telegram', '4321');";
                cmd.ExecuteNonQuery();
            }

            var counts = UserNotedBadgeStore.ReadCounts(path);
            Assert.Equal(4321, counts["org.telegram.Telegram"]);
        }
        finally { try { File.Delete(path); } catch { } }
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
