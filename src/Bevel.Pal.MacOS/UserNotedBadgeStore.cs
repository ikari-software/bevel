using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Bevel.Pal.MacOS;

/// <summary>
/// Raw badge counts from <c>usernoted</c>'s database — the same integers the Dock reads before it
/// ellipsizes them for display (bevel badge de-ellipsization).
///
/// <para><b>Why this exists.</b> The only public cross-app badge read is the Dock's accessibility
/// tree (<see cref="MacOSAppBadgeSource"/>), and the Dock paints wide counts truncated — macOS
/// reports <c>"..82"</c> for 1082. The untruncated count lives in the user-notification daemon's
/// SQLite db at <c>~/Library/Group Containers/group.com.apple.usernoted/db2/db</c>, table
/// <c>app(identifier, badge)</c>, one row per bundle id with pending notifications. Verified live
/// on macOS 26: <c>SELECT identifier, badge FROM app</c> returns e.g. <c>net.whatsapp.whatsapp|12</c>.
/// The file is owned by the user and world-readable to them, so no TCC grant is needed for this.</para>
///
/// <para><b>Sharp edges, honestly.</b> (1) The schema is private to usernoted and can change between
/// macOS versions — every failure (missing file, locked db, changed schema) yields an EMPTY map,
/// never an exception, and the caller falls back to the Dock's label. (2) This only knows
/// Notification-Center-driven badges; apps that badge with a custom string (Calendar's date) do not
/// appear here, which is why the Dock label stays the primary source. (3) A WAL-mode db opened
/// read-only needs its <c>-shm</c> beside it; when even that fails we snapshot db+wal+shm to a temp
/// copy and read that, the same trick <see cref="TabFaviconStore"/> uses for Gecko's db.</para>
/// </summary>
internal static class UserNotedBadgeStore
{
    /// <summary>The usernoted database, relative to the user's home.</summary>
    public const string RelativeDatabasePath =
        "Library/Group Containers/group.com.apple.usernoted/db2/db";

    private const string CountSql =
        "SELECT identifier, badge FROM app WHERE badge IS NOT NULL AND badge > 0";

    /// <summary>Absolute path of the live usernoted db for the current user.</summary>
    public static string DefaultDatabasePath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, RelativeDatabasePath);
    }

    /// <summary>
    /// Snapshot of <c>bundle id → raw badge count</c> for every app with a positive badge right now.
    /// Empty when the db is missing, unreadable, or has an unexpected schema — callers treat empty as
    /// "no raw counts available", not as "no badges".
    /// </summary>
    public static IReadOnlyDictionary<string, int> ReadCounts() => ReadCounts(DefaultDatabasePath());

    /// <summary>Reads <paramref name="dbPath"/> directly; on failure retries once against a temp
    /// snapshot of db+wal+shm. Never throws.</summary>
    internal static IReadOnlyDictionary<string, int> ReadCounts(string dbPath)
    {
        var direct = TryRead(dbPath, Mode.ReadOnly);
        if (direct is not null)
            return direct;

        // WAL-mode read-only open can fail with "database is locked" when the -shm cannot be
        // initialised in place (crash leftovers, permissions). Snapshot the trio to a private temp
        // dir and read the copy — a copy is always openable read-write by its owner.
        var snap = TrySnapshot(dbPath);
        if (snap is null)
            return new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            return TryRead(snap, Mode.ReadWrite)
                ?? new Dictionary<string, int>(StringComparer.Ordinal);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(snap)!, recursive: true); } catch { /* temp litter is fine */ }
        }
    }

    private enum Mode { ReadOnly, ReadWrite }

    /// <summary>Returns the counts, or null when the db could not be opened/queried at all.</summary>
    private static IReadOnlyDictionary<string, int>? TryRead(string dbPath, Mode mode)
    {
        if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
            return null;

        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            var cs = mode == Mode.ReadOnly
                ? $"Data Source={dbPath};Mode=ReadOnly;Pooling=False"
                : $"Data Source={dbPath};Mode=ReadWrite;Pooling=False";
            using var conn = new SqliteConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = CountSql;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                // SQLite columns are dynamically typed — a TEXT-stored badge must not throw the row
                // (let alone the poll), so convert defensively and skip rows that don't parse.
                string? id;
                int badge;
                try
                {
                    id = reader.IsDBNull(0) ? null : reader.GetValue(0) as string;
                    badge = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1));
                }
                catch { continue; }
                if (!string.IsNullOrEmpty(id) && badge > 0)
                    result[id] = badge;
            }
            return result;
        }
        catch (SqliteException) { return null; }   // locked, corrupt, wrong schema — caller falls back
        catch (InvalidCastException) { return null; }
        catch (FormatException) { return null; }
        catch (OverflowException) { return null; }
        catch (DllNotFoundException) { return null; }   // not a real macOS host
        catch (EntryPointNotFoundException) { return null; }
        catch (IOException) { return null; }
    }

    /// <summary>Copies db + -wal + -shm into a fresh temp dir; returns the copied db path, or null.</summary>
    private static string? TrySnapshot(string dbPath)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "bevel-usernoted-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, "db");
            File.Copy(dbPath, dest);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var side = dbPath + suffix;
                if (File.Exists(side))
                    File.Copy(side, dest + suffix);
            }
            return dest;
        }
        catch { return null; }
    }
}
