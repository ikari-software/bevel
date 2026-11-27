using Microsoft.Data.Sqlite;

namespace Bevel.Pal.MacOS;

/// <summary>
/// Raw badge counts from <c>usernoted</c>'s database — the same integers the Dock reads before it
/// ellipsizes them for display (bevel-lfr0 badge de-ellipsization).
///
/// <para><b>Why this exists.</b> The only public cross-app badge read is the Dock's accessibility
/// tree (<see cref="MacOSAppBadgeSource"/>), and the Dock paints wide counts truncated — macOS
/// reports <c>"..82"</c> for 1082. The untruncated count lives in the user-notification daemon's
/// SQLite db at <c>~/Library/Group Containers/group.com.apple.usernoted/db2/db</c>, table
/// <c>app(identifier, badge)</c>, one row per bundle id with pending notifications. Verified live
/// on macOS 26: <c>SELECT identifier, badge FROM app</c> returns e.g. <c>net.whatsapp.whatsapp|12</c>.
/// Note usernoted stores identifiers LOWERCASED, so lookups compare case-insensitively.
/// The file is owned by the user and readable by them, so no TCC grant is needed for this (the app
/// is not sandboxed — see Bevel.entitlements; a future sandboxed build would fail this read
/// silently into the empty-map fallback, which is fail-safe).</para>
///
/// <para><b>Sharp edges, honestly.</b> (1) The schema is private to usernoted and can change between
/// macOS versions — every failure (missing file, locked db, changed schema) yields an EMPTY map,
/// never an exception, and the caller falls back to the Dock's label. (2) This only knows
/// Notification-Center-driven badges; apps that badge with a custom string (Calendar's date) do not
/// appear here, which is why the Dock label stays the primary source. (3) A WAL-mode db opened
/// read-only needs its <c>-shm</c> beside it (and may create/attach it — a minor side effect on
/// another daemon's directory); when the direct open fails we snapshot db+wal+shm with the same
/// raw-syscall copy <see cref="TabFaviconStore"/> uses for Gecko's locked dbs, into a per-user
/// application-data dir (never /tmp — see TabFaviconStore's crafted-db rationale), throttled and
/// size-capped so a persistently failing direct read cannot turn into a copy on every 2 s poll.</para>
/// </summary>
internal static class UserNotedBadgeStore
{
    /// <summary>The usernoted database, relative to the user's home.</summary>
    public const string RelativeDatabasePath =
        "Library/Group Containers/group.com.apple.usernoted/db2/db";

    private const string CountSql =
        "SELECT identifier, badge FROM app WHERE badge IS NOT NULL AND badge > 0";

    // Same floors as TabFaviconStore.FreshCopy: the snapshot fallback exists for a TRANSIENT direct
    // failure; a persistent one must not copy db+wal+shm on every 2 s badge poll tick.
    internal static TimeSpan MinSnapshotInterval = TimeSpan.FromMinutes(2);
    private const long MaxSourceBytes = 256L * 1024 * 1024;

    // Bundle ids are matched case-INSENSITIVELY: usernoted lowercases every identifier in its db
    // (verified: 205/205 rows lowercase), while CFBundleGetIdentifier returns the Info.plist's
    // canonical case (e.g. one.ayugram.AyuGramDesktop) — an Ordinal lookup would miss those.
    private static readonly IReadOnlyDictionary<string, int> Empty =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private static readonly object SnapshotLock = new();
    private static DateTime _lastSnapshotAttemptUtc = DateTime.MinValue;

    /// <summary>Test-only observability: how many snapshot attempts ran (throttle/leak pins).</summary>
    internal static int SnapshotAttemptsForTests;

    /// <summary>Test-only: clears the snapshot throttle clock so a test starts from a clean slate.</summary>
    internal static void ResetSnapshotStateForTests()
    {
        lock (SnapshotLock) { _lastSnapshotAttemptUtc = DateTime.MinValue; }
    }

    private enum Mode { ReadOnly, ReadWrite }

    /// <summary>Absolute path of the live usernoted db for the current user.</summary>
    public static string DefaultDatabasePath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, RelativeDatabasePath);
    }

    /// <summary>Per-user snapshot root — application data, NOT the temp dir: when TMPDIR is unset
    /// (daemon contexts) Path.GetTempPath falls back to world-writable /tmp, where a local user
    /// could pre-place a crafted DB (the same rationale as TabFaviconStore's CacheDir).</summary>
    internal static string SnapshotRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "bevel", "usernoted-snap");

    /// <summary>
    /// Snapshot of <c>bundle id → raw badge count</c> for every app with a positive badge right now.
    /// Empty when the db is missing, unreadable, or has an unexpected schema — callers treat empty as
    /// "no raw counts available", not as "no badges". Never throws.
    /// </summary>
    public static IReadOnlyDictionary<string, int> ReadCounts() => ReadCounts(DefaultDatabasePath());

    /// <summary>Reads <paramref name="dbPath"/> directly; on failure retries at most once per
    /// <see cref="MinSnapshotInterval"/> against a snapshot of db+wal+shm. Never throws.</summary>
    internal static IReadOnlyDictionary<string, int> ReadCounts(string dbPath)
    {
        var direct = TryRead(dbPath, Mode.ReadOnly);
        if (direct is not null)
            return direct;

        // No source file → nothing to snapshot; return before touching the throttle or creating any
        // directory, so a host with no usernoted db at all leaks nothing per poll.
        if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
            return Empty;

        lock (SnapshotLock)
        {
            if (DateTime.UtcNow - _lastSnapshotAttemptUtc < MinSnapshotInterval)
                return Empty;
            _lastSnapshotAttemptUtc = DateTime.UtcNow;
        }

        var snap = TrySnapshot(dbPath);
        if (snap is null)
            return Empty;
        try
        {
            return TryRead(snap, Mode.ReadWrite) ?? Empty;
        }
        finally
        {
            TryDeleteTree(Path.GetDirectoryName(snap)!);
        }
    }

    /// <summary>Returns the counts, or null when the db could not be opened/queried at all.
    /// Never throws — any failure (locked, corrupt, wrong schema, odd path characters) is a null.</summary>
    private static IReadOnlyDictionary<string, int>? TryRead(string dbPath, Mode mode)
    {
        if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
            return null;

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Builder, not interpolation: a home path containing ';'/'=' must not be parsed as
            // connection-string keywords (the builder quotes the DataSource value).
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = mode == Mode.ReadOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
                Pooling = false,
            };
            using var conn = new SqliteConnection(csb.ConnectionString);
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
        catch (Exception)
        {
            // Locked, corrupt, wrong schema, bad path, native lib missing — all are "unavailable".
            // The never-throws contract is deliberate: the badge poll must fall back to the Dock's
            // label, not fault the whole GetBadgesAsync task.
            return null;
        }
    }

    /// <summary>Copies db + -wal + -shm into a fresh dir under <see cref="SnapshotRoot"/> via the
    /// raw-syscall copy (managed File.Copy can throw on SQLite-locked files on macOS); returns the
    /// copied db path, or null — and never leaves the created dir behind on failure.</summary>
    internal static string? TrySnapshot(string dbPath)
    {
        SnapshotAttemptsForTests++;
        var dir = Path.Combine(SnapshotRoot, "snap-" + Guid.NewGuid().ToString("N"));
        var created = false;
        try
        {
            if (new FileInfo(dbPath).Length > MaxSourceBytes)
                return null;

            Directory.CreateDirectory(dir);
            created = true;
            var dest = Path.Combine(dir, "db");
            TabFaviconStore.RawCopy(dbPath, dest, System.Threading.CancellationToken.None);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var side = dbPath + suffix;
                if (File.Exists(side))
                {
                    if (new FileInfo(side).Length > MaxSourceBytes)
                        return null;
                    TabFaviconStore.RawCopy(side, dest + suffix, System.Threading.CancellationToken.None);
                }
            }
            return dest;
        }
        catch
        {
            if (created)
                TryDeleteTree(dir);
            return null;
        }
    }

    private static void TryDeleteTree(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* temp litter is fine — it is under our own application-data root */ }
    }
}
