using Bevel.Core;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>
/// The peer-side settings snapshot cache (bevel-7s9n) and the strict blob projection it depends on
/// (bevel-lej1). Every test roots its cache at a throwaway temp dir — never the real config dir (the
/// <see cref="BevelConfigDir"/> guard would throw, and it has clobbered live settings before).
/// </summary>
public sealed class SettingsSnapshotCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bevel-peer-cache-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private const string LunaBlob = """
        {
          "schemaVersion": 1,
          "themeId": "luna",
          "taskbarRows": 2,
          "theme:luna": { "CrispBevels": true }
        }
        """;

    [Fact]
    public void Missing_file_reads_as_no_cache()
        => Assert.Null(new SettingsSnapshotCache(_dir).TryRead());

    [Fact]
    public void Write_then_read_round_trips_the_version_and_the_settings()
    {
        var cache = new SettingsSnapshotCache(_dir);
        Assert.True(cache.TryWrite(42, LunaBlob));

        var read = cache.TryRead();
        Assert.NotNull(read);
        Assert.Equal(42, read.Value.Version);
        // Re-serialised, so compare by content — the same rule the peer uses against a live snapshot.
        Assert.True(SettingsService.BlobsEquivalent(LunaBlob, read.Value.Json));
        // The cache deliberately holds the LEGACY id, because that is what an existing install's file
        // contains. Projecting it must yield the current id — this is the migration proven end to end
        // through the real projection, not just through ThemeIds.Canonical in isolation.
        Assert.Equal(ThemeIds.Blue2001, SettingsService.ProjectBlob(read.Value.Json).Settings.ThemeId);
        Assert.Equal(2, SettingsService.ProjectBlob(read.Value.Json).Settings.TaskbarRows);
    }

    [Fact]
    public void Write_is_atomic_and_leaves_no_temp_file_behind()
    {
        var cache = new SettingsSnapshotCache(_dir);
        cache.TryWrite(1, LunaBlob);
        cache.TryWrite(2, LunaBlob);
        Assert.Equal(new[] { SettingsSnapshotCache.FileName },
            Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("\"a string\"")]
    public void A_blob_that_is_not_a_settings_object_is_refused_by_the_writer(string bad)
    {
        var cache = new SettingsSnapshotCache(_dir);
        Assert.False(cache.TryWrite(1, bad));
        Assert.Null(cache.TryRead());
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage {")]
    [InlineData("{\"version\": 3}")]                      // no settings object
    [InlineData("{\"version\": 3, \"settings\": [1]}")]   // settings not an object
    public void A_torn_or_malformed_cache_file_reads_as_no_cache(string contents)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, SettingsSnapshotCache.FileName), contents);
        Assert.Null(new SettingsSnapshotCache(_dir).TryRead());
    }

    // ── SettingsService.TryProjectBlob (bevel-lej1): a defaulted read is never real data ─────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    [InlineData("not json")]
    [InlineData("{ \"themeId\": ")]
    [InlineData("[]")]
    public void TryProjectBlob_refuses_what_ParseRawOrDefault_would_have_turned_into_defaults(string? blob)
    {
        Assert.False(SettingsService.TryProjectBlob(blob, out _, out _));
        // The permissive path still yields defaults — that contract (missing file → defaults) is the
        // store's, not the peer's; the point is that a peer can now tell the two apart.
        Assert.Equal(ThemeIds.Industrial1999, SettingsService.ProjectBlob(blob).Settings.ThemeId);
    }

    [Fact]
    public void TryProjectBlob_accepts_an_empty_object_as_a_real_all_defaults_blob()
    {
        Assert.True(SettingsService.TryProjectBlob("{}", out var s, out var overrides));
        Assert.Equal(ThemeIds.Industrial1999, s.ThemeId);
        Assert.Empty(overrides);
    }

    [Fact]
    public void TryProjectBlob_projects_a_real_blob_with_its_overrides()
    {
        Assert.True(SettingsService.TryProjectBlob(LunaBlob, out var s, out var overrides));
        Assert.Equal(ThemeIds.Blue2001, s.ThemeId);
        // Keyed by the CURRENT id even though the blob says "theme:luna": projecting migrates the
        // override keys too, so an existing install's per-theme knobs are not orphaned by the rename.
        Assert.True(overrides[ThemeIds.Blue2001].CrispBevels);
    }

    // ── SettingsService.BlobsEquivalent ──────────────────────────────────────────────────────────────

    [Fact]
    public void BlobsEquivalent_ignores_whitespace_and_key_order_but_not_values()
    {
        // Deliberately the LEGACY literals: BlobsEquivalent compares raw JSON, before any projection or
        // id migration, so the fixture and its twin must match byte-for-byte in meaning — canonical ids
        // here would be comparing a migrated blob against an unmigrated one.
        Assert.True(SettingsService.BlobsEquivalent(LunaBlob,
            "{\"theme:luna\":{\"CrispBevels\":true},\"taskbarRows\":2,\"themeId\":\"luna\",\"schemaVersion\":1}"));
        Assert.False(SettingsService.BlobsEquivalent(LunaBlob, LunaBlob.Replace("\"luna\"", "\"win2000\"")));
        Assert.False(SettingsService.BlobsEquivalent(LunaBlob, "{}"));
        Assert.False(SettingsService.BlobsEquivalent(LunaBlob, null));
        Assert.False(SettingsService.BlobsEquivalent("not json", "not json"));
    }
}
