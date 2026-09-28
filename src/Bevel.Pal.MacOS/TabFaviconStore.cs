using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Bevel.Pal.MacOS;

/// <summary>
/// Favicons for tab-menu rows (bevel-l17f), read from the browsers' own on-disk caches — no
/// network, no prompts. Two SQLite schemas cover every supported browser: the Chromium family's
/// per-profile <c>Favicons</c> DB (icon_mapping → favicon_bitmaps) and Gecko's
/// <c>favicons.sqlite</c> (moz_pages_w_icons → moz_icons_to_pages → moz_icons); both key on the
/// exact page url and store ready-made PNG bitmaps (verified live against Arc and Zen 2026-08-17).
///
/// The live DBs are unreachable in place (see RawCopy), so lookups run against a COPY refreshed
/// when the source mtime moves — a checkpoint-stale icon beats a torn read. A per-bundle url→png
/// memo makes repeated menu opens free, and a minimum re-copy interval keeps an actively-browsing
/// Gecko profile (which rewrites its DB continuously; Firefox's here is 68 MB) from charging a
/// full copy per right-click. Everything is best-effort: any failure yields "no icon", never an
/// error.
/// </summary>
internal static class TabFaviconStore
{
    // Chromium: page_url → bitmaps; prefer the icon nearest 16 px, favouring >=16 (menu rows render
    // at 16 and downscaling beats upscaling). LIMIT > 1 so a non-PNG best row can be skipped.
    private const string ChromiumSql = """
        SELECT fb.image_data FROM icon_mapping im
        JOIN favicon_bitmaps fb ON fb.icon_id = im.icon_id
        WHERE im.page_url = $url
        ORDER BY (fb.width >= 16) DESC, ABS(fb.width - 16) ASC LIMIT 4
        """;

    // Gecko: same idea, three-table join; rows can be raw SVG (width 65535) which Avalonia won't
    // decode from bytes — the reader skips non-PNG blobs.
    private const string GeckoSql = """
        SELECT i.data FROM moz_pages_w_icons p
        JOIN moz_icons_to_pages ip ON ip.page_id = p.id
        JOIN moz_icons i ON i.id = ip.icon_id
        WHERE p.page_url = $url
        ORDER BY (i.width >= 16) DESC, ABS(i.width - 16) ASC LIMIT 4
        """;

    /// <summary>Data roots per bundle id (relative to ~/Library/Application Support). Chromium roots
    /// hold profile subdirs ("Default", "Profile 1", …) each with a Favicons DB; Gecko roots hold
    /// Profiles/*/favicons.sqlite. (Safari's cache is TCC-locked behind Full Disk Access — out of
    /// scope by design, so Safari tabs stay icon-less.)</summary>
    private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47 };

    /// <summary>Sources above this are refused outright — a favicon DB in the hundreds of MB is
    /// either corrupt or not worth a synchronous copy on a menu-prefetch path.</summary>
    private const long MaxSourceBytes = 256L * 1024 * 1024;

    /// <summary>Floor between re-copies of one bundle's DB even when the source mtime moved — an
    /// actively-browsing Gecko profile rewrites its DB continuously, and icons a couple of minutes
    /// stale are indistinguishable from fresh. Internal-settable so tests can zero it.</summary>
    internal static TimeSpan MinRecopyInterval = TimeSpan.FromMinutes(2);

    // Per-bundle memo: url → png (null = looked up, none found), valid for one source stamp.
    // Makes the second and later menu opens free. Guarded by its own lock; bounded by ClearAt.
    private static readonly object MemoLock = new();
    private static readonly Dictionary<string, (DateTime Stamp, Dictionary<string, byte[]?> ByUrl)> Memo = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTime> LastCopyAt = new(StringComparer.OrdinalIgnoreCase);
    private const int MemoClearAt = 4096;

    /// <summary>Fill <see cref="Bevel.Pal.Abstractions.AppTab.IconPng"/> for every tab whose url has
    /// a cached favicon. Returns the input list untouched when the app has no favicon store.</summary>
    public static IReadOnlyList<Bevel.Pal.Abstractions.AppTab> Enrich(
        string bundleId, IReadOnlyList<Bevel.Pal.Abstractions.AppTab> tabs, CancellationToken ct)
    {
        var app = TabBrowserRegistry.For(bundleId);
        if (tabs.Count == 0 || ct.IsCancellationRequested || app?.FaviconRoot is null) return tabs;
        try
        {
            var appSupport = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support");
            var source = ResolveDbPath(appSupport, app.FaviconRoot, app.IsGecko);
            var dbCopy = FreshCopy(source, bundleId, ct);
            if (dbCopy is null) return tabs;
            return EnrichFromDb(dbCopy, app.IsGecko, bundleId, tabs, ct);
        }
        catch
        {
            return tabs;   // schema drift, torn copy, locked temp — icons are decoration
        }
    }

    /// <summary>The lookup core, separated from profile/copy plumbing so tests can run it against a
    /// synthetic DB. Failures inside still bubble to Enrich's catch.</summary>
    internal static IReadOnlyList<Bevel.Pal.Abstractions.AppTab> EnrichFromDb(
        string dbPath, bool isGecko, string bundleId, IReadOnlyList<Bevel.Pal.Abstractions.AppTab> tabs, CancellationToken ct)
    {
        var byUrl = MemoFor(bundleId, dbPath);
        SqliteConnection? conn = null;
        SqliteCommand? cmd = null;
        SqliteParameter? urlParam = null;
        try
        {
            var result = new Bevel.Pal.Abstractions.AppTab[tabs.Count];
            for (var i = 0; i < tabs.Count; i++) result[i] = tabs[i];
            for (var i = 0; i < tabs.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                var tab = tabs[i];
                if (tab.Url is not { Length: > 0 } url) continue;
                byte[]? png;
                lock (MemoLock)
                {
                    byUrl.TryGetValue(url, out png);
                    var known = byUrl.ContainsKey(url);
                    if (!known) png = null;
                    if (known) { if (png is not null) result[i] = tab with { IconPng = png }; continue; }
                }
                if (conn is null)
                {
                    // ReadWrite, not ReadOnly: Gecko's DB is WAL-mode, and a read-only open with no
                    // -shm beside it cannot initialize shared memory — SQLite reports "database is
                    // locked". The path is always our PRIVATE copy, so write access costs nothing.
                    conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWrite;Pooling=False");
                    conn.Open();
                    cmd = conn.CreateCommand();
                    cmd.CommandText = isGecko ? GeckoSql : ChromiumSql;
                    urlParam = cmd.CreateParameter();
                    urlParam.ParameterName = "$url";
                    cmd.Parameters.Add(urlParam);
                }
                urlParam!.Value = url;
                png = FirstPng(cmd!);
                lock (MemoLock) byUrl[url] = png;
                if (png is not null) result[i] = tab with { IconPng = png };
            }
            return result;
        }
        finally
        {
            cmd?.Dispose();
            conn?.Dispose();
        }
    }

    /// <summary>First PNG-magic blob the query yields — skips Gecko's raw-SVG rows, and any
    /// TEXT-typed row SQLite's dynamic typing may surface (a cast throw here would take down the
    /// whole enrichment, not one row).</summary>
    private static byte[]? FirstPng(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetValue(0) is byte[] blob
                && blob.Length > PngMagic.Length && blob.AsSpan(0, 4).SequenceEqual(PngMagic))
                return blob;
        }
        return null;
    }

    private static Dictionary<string, byte[]?> MemoFor(string bundleId, string dbPath)
    {
        var stamp = File.GetLastWriteTimeUtc(dbPath);
        lock (MemoLock)
        {
            if (Memo.TryGetValue(bundleId, out var entry) && entry.Stamp == stamp)
            {
                if (entry.ByUrl.Count > MemoClearAt) entry.ByUrl.Clear();
                return entry.ByUrl;
            }
            var fresh = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
            Memo[bundleId] = (stamp, fresh);
            return fresh;
        }
    }

    // ── Profile / copy management ────────────────────────────────────────────

    /// <summary>The favicon DB of the app's active profile: the newest-written candidate — for
    /// Chromium the busiest profile subdir IS the one the live window belongs to (a fixed
    /// "Default" preference starved secondary-profile users of icons); for Gecko this matches
    /// <see cref="GeckoSessionStore"/>'s freshest-profile rule.</summary>
    internal static string? ResolveDbPath(string appSupportDir, string root, bool isGecko)
    {
        var baseDir = Path.Combine(appSupportDir, root);
        return isGecko
            ? NewestFile(Path.Combine(baseDir, "Profiles"), "favicons.sqlite")
            : NewestFile(baseDir, "Favicons");
    }

    private static string? NewestFile(string parent, string fileName)
    {
        if (!Directory.Exists(parent)) return null;
        string? best = null;
        var bestTime = DateTime.MinValue;
        foreach (var dir in Directory.EnumerateDirectories(parent))
        {
            var candidate = Path.Combine(dir, fileName);
            if (!File.Exists(candidate)) continue;
            var t = File.GetLastWriteTimeUtc(candidate);
            if (t <= bestTime) continue;
            bestTime = t;
            best = candidate;
        }
        return best;
    }

    /// <summary>Cache directory: per-user application data, NOT the temp dir — when TMPDIR is
    /// unset (daemon contexts) Path.GetTempPath falls back to world-writable /tmp, where a local
    /// user could pre-place a crafted DB for our decoder.</summary>
    private static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "bevel", "favicons");

    internal static string? FreshCopy(string? source, string bundleId, CancellationToken ct)
    {
        if (source is null || !File.Exists(source)) return null;
        var info = new FileInfo(source);
        if (info.Length > MaxSourceBytes) return null;
        var stamp = info.LastWriteTimeUtc;
        var dir = CacheDir;
        Directory.CreateDirectory(dir);
        // Defense-in-depth: reachable bundle ids come from the TabBrowserRegistry allow-list, but this method is
        // internal — never let a caller-supplied id path-traverse the cache dir.
        var copy = Path.Combine(dir, Path.GetFileName(bundleId) + ".sqlite");

        var upToDate = File.Exists(copy) && File.GetLastWriteTimeUtc(copy) == stamp;
        if (!upToDate)
        {
            lock (MemoLock)
            {
                // An actively-browsing Gecko profile bumps its DB mtime continuously; without a
                // floor every right-click re-copies tens of MB. Slightly stale icons are fine.
                if (File.Exists(copy)
                    && LastCopyAt.TryGetValue(bundleId, out var last)
                    && DateTime.UtcNow - last < MinRecopyInterval)
                    return copy;
                LastCopyAt[bundleId] = DateTime.UtcNow;
            }
            // A hard-killed previous process can leave -wal/-shm from the OLD copy; File.Move
            // replaces only the main file, and SQLite would apply the stale WAL to the new one.
            TryDelete(copy + "-wal");
            TryDelete(copy + "-shm");
            RawCopy(source, copy, ct);
            File.SetLastWriteTimeUtc(copy, stamp);   // mtime IS the cache key
        }
        return copy;
    }

    /// <summary>Byte copy through raw syscalls, and nothing else works (all verified live against
    /// a running Zen):
    /// - SQLite-protocol readers (the CLI included) get SQLITE_BUSY — the Firefox family opens its
    ///   DBs with locking_mode=EXCLUSIVE for the browser's whole lifetime, which also rules out
    ///   the Online Backup API.
    /// - Managed file APIs (File.Copy / FileStream) throw "in use by another process" — on macOS
    ///   .NET flocks every open, and the OS makes flock contend with SQLite's fcntl locks.
    /// Direct open/read syscalls take no locks. A mid-write copy can be torn; the query side
    /// treats that as "no icons this round" and the next mtime change re-copies. Writes go
    /// through a unique temp name + rename so a concurrent caller or a crash can never publish a
    /// half-copy under a valid mtime.</summary>
    private static void RawCopy(string source, string destination, CancellationToken ct)
    {
        // The raw-syscall path exists ONLY to dodge macOS's flock↔fcntl contention (see remarks above).
        // Off-macOS that rationale is moot and libSystem.dylib isn't present — a managed copy through the
        // same unique-temp + rename discipline is correct and keeps FreshCopy portable for CI's Linux/
        // Windows runners (bevel-8kxc). Never taken in production; Bevel ships on macOS only.
        if (!OperatingSystem.IsMacOS())
        {
            var tmpManaged = $"{destination}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.Copy(source, tmpManaged, overwrite: true);
                File.Move(tmpManaged, destination, overwrite: true);
            }
            catch { TryDelete(tmpManaged); throw; }
            return;
        }

        var fd = open(source, O_RDONLY);
        if (fd < 0) throw new IOException($"open({source}) failed: errno {Marshal.GetLastPInvokeError()}");
        var tmp = $"{destination}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buf = new byte[1 << 16];
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var n = read(fd, buf, buf.Length);
                    if (n < 0)
                    {
                        if (Marshal.GetLastPInvokeError() == EINTR) continue;   // signal — retry
                        throw new IOException($"read failed: errno {Marshal.GetLastPInvokeError()}");
                    }
                    if (n == 0) break;
                    dst.Write(buf, 0, (int)n);
                }
            }
            File.Move(tmp, destination, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
        finally { _ = close(fd); }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best-effort */ }
    }

    private const int O_RDONLY = 0;
    private const int EINTR = 4;

    // open(2) is variadic (mode follows only with O_CREAT, which we never pass); on arm64/x64
    // macOS a two-arg call matches the non-variadic declaration's ABI.
    [DllImport(Frameworks.LibSystem, SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport(Frameworks.LibSystem, SetLastError = true)]
    private static extern nint read(int fd, byte[] buf, nint count);

    [DllImport(Frameworks.LibSystem, SetLastError = true)]
    private static extern int close(int fd);
}
