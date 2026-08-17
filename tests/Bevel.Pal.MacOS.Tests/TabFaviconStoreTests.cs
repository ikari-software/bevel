using Bevel.Pal.Abstractions;
using Bevel.Pal.MacOS;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Bevel.Pal.MacOS.Tests;

// The favicon store (bevel-l17f) against synthetic DBs in both schemas: nearest-16px selection,
// the PNG-magic filter (Gecko stores raw SVG rows Avalonia can't decode), url de-duplication, and
// profile resolution. Uses real SQLite files in a temp dir — the same engine the store reads.
public sealed class TabFaviconStoreTests : IDisposable
{
    private static readonly byte[] Png16 = { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4, 5 };
    private static readonly byte[] Png32 = { 0x89, 0x50, 0x4E, 0x47, 9, 9, 9, 9, 9 };
    private static readonly byte[] Svg = System.Text.Encoding.UTF8.GetBytes("<svg xmlns='x'/>");

    private readonly string _dir = Directory.CreateTempSubdirectory("bevel-favicon-tests").FullName;

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string ChromiumDb(params (string Url, int Width, byte[] Blob)[] rows)
    {
        var path = Path.Combine(_dir, Path.GetRandomFileName() + ".sqlite");
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        Exec(conn, """
            CREATE TABLE icon_mapping (page_url TEXT, icon_id INTEGER);
            CREATE TABLE favicon_bitmaps (icon_id INTEGER, width INTEGER, image_data BLOB);
            """);
        var iconId = 0;
        foreach (var (url, width, blob) in rows)
        {
            iconId++;
            Exec(conn, $"INSERT INTO icon_mapping VALUES ('{url}', {iconId});");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"INSERT INTO favicon_bitmaps VALUES ({iconId}, {width}, $blob);";
            cmd.Parameters.AddWithValue("$blob", blob);
            cmd.ExecuteNonQuery();
        }
        return path;
    }

    private string GeckoDb(params (string Url, int Width, byte[] Blob)[] rows)
    {
        var path = Path.Combine(_dir, Path.GetRandomFileName() + ".sqlite");
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        Exec(conn, """
            CREATE TABLE moz_pages_w_icons (id INTEGER PRIMARY KEY, page_url TEXT);
            CREATE TABLE moz_icons_to_pages (page_id INTEGER, icon_id INTEGER);
            CREATE TABLE moz_icons (id INTEGER PRIMARY KEY, width INTEGER, data BLOB);
            """);
        var id = 0;
        foreach (var (url, width, blob) in rows)
        {
            id++;
            Exec(conn, $"INSERT OR IGNORE INTO moz_pages_w_icons VALUES ({UrlId(url)}, '{url}');");
            Exec(conn, $"INSERT INTO moz_icons_to_pages VALUES ({UrlId(url)}, {id});");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"INSERT INTO moz_icons VALUES ({id}, {width}, $blob);";
            cmd.Parameters.AddWithValue("$blob", blob);
            cmd.ExecuteNonQuery();
        }
        return path;

        static int UrlId(string url) => Math.Abs(url.GetHashCode() % 1000) + 1;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static AppTab Tab(string url, int index = 1) =>
        new("com.google.Chrome", "1", index, $"Tab {index}", url);

    // ── Lookup selection ─────────────────────────────────────────────────────

    [Fact]
    public void Chromium_lookup_prefers_the_icon_nearest_16px()
    {
        var db = ChromiumDb(("https://a.test/", 32, Png32), ("https://a.test/", 16, Png16));
        var tabs = TabFaviconStore.EnrichFromDb(db, isGecko: false, "test.nearest16", new[] { Tab("https://a.test/") }, default);
        Assert.Equal(Png16, tabs[0].IconPng);
    }

    [Fact]
    public void Gecko_lookup_skips_svg_rows_and_takes_the_next_png()
    {
        // The 16px row is raw SVG (best by size) — the PNG at 32px must win instead.
        var db = GeckoDb(("https://a.test/", 16, Svg), ("https://a.test/", 32, Png32));
        var tabs = TabFaviconStore.EnrichFromDb(db, isGecko: true, "test.svgskip", new[] { Tab("https://a.test/") }, default);
        Assert.Equal(Png32, tabs[0].IconPng);
    }

    [Fact]
    public void Tabs_without_urls_or_without_cached_icons_stay_iconless()
    {
        var db = ChromiumDb(("https://known.test/", 16, Png16));
        var tabs = TabFaviconStore.EnrichFromDb(db, isGecko: false, "test.iconless", new[]
        {
            Tab("https://known.test/", 1),
            Tab("https://unknown.test/", 2),
            new AppTab("com.google.Chrome", "1", 3, "No url"),
        }, default);
        Assert.Equal(Png16, tabs[0].IconPng);
        Assert.Null(tabs[1].IconPng);
        Assert.Null(tabs[2].IconPng);
    }

    [Fact]
    public void Repeated_urls_share_one_lookup_result()
    {
        var db = ChromiumDb(("https://a.test/", 16, Png16));
        var tabs = TabFaviconStore.EnrichFromDb(db, isGecko: false, "test.dedup",
            new[] { Tab("https://a.test/", 1), Tab("https://a.test/", 2) }, default);
        Assert.Equal(Png16, tabs[0].IconPng);
        Assert.Equal(Png16, tabs[1].IconPng);
    }

    // ── Profile resolution ───────────────────────────────────────────────────

    [Fact]
    public void Chromium_picks_the_most_recently_written_profile()
    {
        // The busiest profile is the one the live window belongs to — a fixed "Default" preference
        // starved secondary-profile users of icons (review finding).
        var root = Path.Combine(_dir, "Google", "Chrome");
        Directory.CreateDirectory(Path.Combine(root, "Default"));
        Directory.CreateDirectory(Path.Combine(root, "Profile 1"));
        File.WriteAllBytes(Path.Combine(root, "Default", "Favicons"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(root, "Profile 1", "Favicons"), new byte[] { 2 });
        File.SetLastWriteTimeUtc(Path.Combine(root, "Default", "Favicons"), DateTime.UtcNow.AddDays(-7));

        var resolved = TabFaviconStore.ResolveDbPath(_dir, "Google/Chrome", isGecko: false);
        Assert.Equal(Path.Combine(root, "Profile 1", "Favicons"), resolved);
    }

    [Fact]
    public void Text_typed_blob_row_skips_without_killing_the_enrichment()
    {
        // SQLite is dynamically typed: a TEXT value in a BLOB column must cost one row, not the
        // whole enrichment (review finding: the old hard cast unwound to the outer catch).
        var db = ChromiumDb(("https://a.test/", 16, System.Text.Encoding.UTF8.GetBytes("not-a-blob")));
        using (var conn = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            conn.Open();
            Exec(conn, "UPDATE favicon_bitmaps SET image_data = 'plain text';");
            Exec(conn, "INSERT INTO icon_mapping VALUES ('https://b.test/', 99);");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO favicon_bitmaps VALUES (99, 16, $blob);";
            cmd.Parameters.AddWithValue("$blob", Png16);
            cmd.ExecuteNonQuery();
        }
        var tabs = TabFaviconStore.EnrichFromDb(db, isGecko: false, "test.textrow",
            new[] { Tab("https://a.test/", 1), Tab("https://b.test/", 2) }, default);
        Assert.Null(tabs[0].IconPng);
        Assert.Equal(Png16, tabs[1].IconPng);
    }

    [Fact]
    public void Cancelled_token_returns_unenriched_rows()
    {
        var db = ChromiumDb(("https://a.test/", 16, Png16));
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        var tabs = TabFaviconStore.EnrichFromDb(db, isGecko: false, "test.cancelled",
            new[] { Tab("https://a.test/") }, cts.Token);
        Assert.Null(tabs[0].IconPng);
    }

    // ── Copy cache ───────────────────────────────────────────────────────────

    [Fact]
    public void FreshCopy_copies_once_then_reuses_until_the_source_changes()
    {
        var prev = TabFaviconStore.MinRecopyInterval;
        TabFaviconStore.MinRecopyInterval = TimeSpan.Zero;
        try
        {
            var source = Path.Combine(_dir, "source.sqlite");
            File.WriteAllBytes(source, new byte[] { 1, 2, 3 });
            var id = "test.copycache." + Guid.NewGuid().ToString("N");

            var copy1 = TabFaviconStore.FreshCopy(source, id, default)!;
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(copy1));
            var mtime1 = File.GetLastWriteTimeUtc(copy1);

            // Unchanged source: the copy is reused (mtime is the cache key, so it must not move).
            var copy2 = TabFaviconStore.FreshCopy(source, id, default)!;
            Assert.Equal(copy1, copy2);
            Assert.Equal(mtime1, File.GetLastWriteTimeUtc(copy2));

            // Changed source: re-copied with the new content.
            File.WriteAllBytes(source, new byte[] { 9, 9 });
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(1));
            var copy3 = TabFaviconStore.FreshCopy(source, id, default)!;
            Assert.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(copy3));
            File.Delete(copy3);
        }
        finally { TabFaviconStore.MinRecopyInterval = prev; }
    }

    [Fact]
    public void Gecko_picks_the_most_recently_written_profile()
    {
        var profiles = Path.Combine(_dir, "zen", "Profiles");
        var stale = Path.Combine(profiles, "aaa.old");
        var fresh = Path.Combine(profiles, "bbb.default");
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(fresh);
        File.WriteAllBytes(Path.Combine(stale, "favicons.sqlite"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(fresh, "favicons.sqlite"), new byte[] { 2 });
        File.SetLastWriteTimeUtc(Path.Combine(stale, "favicons.sqlite"), DateTime.UtcNow.AddDays(-30));

        var resolved = TabFaviconStore.ResolveDbPath(_dir, "zen", isGecko: true);
        Assert.Equal(Path.Combine(fresh, "favicons.sqlite"), resolved);
    }

    [Fact]
    public void Missing_roots_resolve_to_null()
    {
        Assert.Null(TabFaviconStore.ResolveDbPath(_dir, "Nothing/Here", isGecko: false));
        Assert.Null(TabFaviconStore.ResolveDbPath(_dir, "nothing", isGecko: true));
    }
}
