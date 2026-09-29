using System.Diagnostics;
using System.Security.Cryptography;
using Bevel.App.ShellCore;
using Bevel.Core;
using Bevel.Pal.Fake;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The peer settings path a split taskbar / filer / desktop paints from (bevel-7s9n + bevel-lej1),
/// over a real <see cref="ShellCoreServer"/> on a live Unix socket with the real DB-backed core
/// <see cref="SettingsService"/> behind it. What <c>App.OnFrameworkInitializationCompleted</c> reads for
/// the first frame is <c>settings.Current</c> right after <c>LoadAsync</c> returns — so "paints Luna on
/// its first frame" is "Current.ThemeId is luna when LoadAsync returns", and "the theme never changes
/// without a settings write" is "Changed never fires unless the projected settings actually differ".
/// Every settings dir here is a throwaway temp dir — never the developer's real config.
/// </summary>
public sealed class RemoteSettingsFirstPaintTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static CancellationToken Ct => new CancellationTokenSource(Timeout).Token;

    private readonly string _coreDir = Path.Combine(Path.GetTempPath(), $"bvlset-{Guid.NewGuid():N}");
    private readonly string _peerDir = Path.Combine(Path.GetTempPath(), $"bvlpeer-{Guid.NewGuid():N}");
    private readonly string _socket = Path.Combine(Path.GetTempPath(), $"bvlcore-{Guid.NewGuid():N}"[..14] + ".sock");
    private readonly byte[] _nonce = RandomNumberGenerator.GetBytes(32);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        foreach (var d in new[] { _coreDir, _peerDir })
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private SettingsSnapshotCache Cache => new(_peerDir);

    private static async Task WaitFor(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(15);
        Assert.True(condition(), because);
    }

    /// <summary>A real core settings store at <see cref="_coreDir"/> with the persisted theme set to Luna.</summary>
    private async Task<SettingsService> LunaCoreAsync()
    {
        var core = new SettingsService(_coreDir);
        await core.LoadAsync(Ct);
        await core.UpdateAsync(s => { s.ThemeId = "luna"; s.TaskbarRows = 2; }, Ct);
        return core;
    }

    private ShellCoreServer Server(ISettingsService settings)
        => new(new FakeWindowManager(), new FakeAppEnvironment(), new FakeSystemTrayHost(), settings, _socket, _nonce);

    // 1. The whole point: with the core NOT reachable at all, a peer that cached Luna last session comes up
    //    on Luna — zero IPC, and without waiting out the connect bound. Before, this was the taskbar
    //    painting Win2000 (and one row) and re-theming a second later, or staying that way forever.
    [Fact]
    public async Task Cold_boot_with_no_core_paints_the_cached_theme_without_waiting_on_the_socket()
    {
        var blob = SettingsService.SerializeBlob(new BevelSettings { ThemeId = "luna", TaskbarRows = 2 },
            new Dictionary<string, ThemeOverrides>());
        Assert.True(Cache.TryWrite(7, blob));

        await using var client = new ShellCoreClient(_socket, _nonce); // nothing is listening here
        await using var remote = new RemoteSettingsService(client, Cache);
        var changedCount = 0;
        remote.Changed += () => Interlocked.Increment(ref changedCount);

        var sw = Stopwatch.StartNew();
        await remote.LoadAsync(Ct);
        sw.Stop();

        Assert.Equal("luna", remote.Current.ThemeId);
        Assert.Equal(2, remote.Current.TaskbarRows);
        Assert.Equal(7, remote.Version);
        Assert.False(remote.HasLiveSnapshot);
        Assert.Equal(0, changedCount);   // the seed IS the first paint; nothing changed from anything
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"LoadAsync took {sw.Elapsed} — a cached peer must not wait out the 5s core bound");
    }

    // 2. The cache was right: the core's first live snapshot is adopted SILENTLY. No Changed, so nothing
    //    re-templates on a boot where nothing changed (the theme flash this bead is about).
    [Fact]
    public async Task First_live_snapshot_equal_to_the_cache_adopts_its_version_without_raising_Changed()
    {
        using var core = await LunaCoreAsync();
        // Same content, an older version stamp — proves the match is by content, not by version.
        Assert.True(Cache.TryWrite(core.Version - 1, core.SnapshotJson()));

        await using var server = Server(core);
        await server.StartAsync(Ct);
        await using var client = new ShellCoreClient(_socket, _nonce);
        await using var remote = new RemoteSettingsService(client, Cache);
        var changedCount = 0;
        remote.Changed += () => Interlocked.Increment(ref changedCount);

        await remote.LoadAsync(Ct);
        Assert.Equal("luna", remote.Current.ThemeId);   // first paint from the cache

        await WaitFor(() => remote.HasLiveSnapshot, "the live snapshot should land after the background pull connects");
        Assert.Equal(core.Version, remote.Version);      // the live version was adopted
        Assert.Equal("luna", remote.Current.ThemeId);
        await Task.Delay(200);                            // give a spurious Changed every chance to show up
        Assert.Equal(0, changedCount);
    }

    // 3. The cache was stale (settings changed while this peer was down): the first paint is the cached
    //    state, the live snapshot corrects it with exactly one Changed, and the cache is rewritten.
    [Fact]
    public async Task First_live_snapshot_that_differs_from_the_cache_corrects_it_once_and_rewrites_the_cache()
    {
        using var core = await LunaCoreAsync();
        var stale = SettingsService.SerializeBlob(new BevelSettings { ThemeId = "win2000" }, new Dictionary<string, ThemeOverrides>());
        Assert.True(Cache.TryWrite(core.Version + 100, stale)); // a HIGHER cached version must not win over live content

        await using var server = Server(core);
        await server.StartAsync(Ct);
        await using var client = new ShellCoreClient(_socket, _nonce);
        await using var remote = new RemoteSettingsService(client, Cache);
        var changedCount = 0;
        remote.Changed += () => Interlocked.Increment(ref changedCount);

        await remote.LoadAsync(Ct);
        Assert.Equal("win2000", remote.Current.ThemeId);   // painted from the (stale) cache first

        await WaitFor(() => remote.Current.ThemeId == "luna", "the live snapshot should correct the stale cache");
        await Task.Delay(200);
        Assert.Equal(1, changedCount);
        Assert.Equal(core.Version, remote.Version);

        var rewritten = Cache.TryRead();
        Assert.NotNull(rewritten);
        Assert.Equal(core.Version, rewritten.Value.Version);
        Assert.True(SettingsService.BlobsEquivalent(core.SnapshotJson(), rewritten.Value.Json));
    }

    // 4. Write-through: a settings write during the session lands in the cache, so the NEXT boot's first
    //    paint is the new state, not last boot's.
    [Fact]
    public async Task A_settings_write_is_written_through_to_the_cache()
    {
        using var core = await LunaCoreAsync();
        await using var server = Server(core);
        await server.StartAsync(Ct);
        await using var client = new ShellCoreClient(_socket, _nonce);
        await using var remote = new RemoteSettingsService(client, Cache);

        await remote.LoadAsync(Ct);                      // no cache yet → the bounded live pull
        await WaitFor(() => Cache.TryRead() is not null, "the first live snapshot should be cached");

        await remote.UpdateAsync(s => s.TaskbarOpacity = 55, Ct);
        await WaitFor(() => remote.Current.TaskbarOpacity == 55, "the SettingsChanged broadcast should land");
        await WaitFor(() => Cache.TryRead() is { } c && SettingsService.ProjectBlob(c.Json).Settings.TaskbarOpacity == 55,
            "the applied SettingsChanged should be written through to the cache");
        Assert.Equal("luna", SettingsService.ProjectBlob(Cache.TryRead()!.Value.Json).Settings.ThemeId);

        // And a fresh peer boots straight onto it, core or no core.
        await using var client2 = new ShellCoreClient(Path.Combine(Path.GetTempPath(), $"bvlnone-{Guid.NewGuid():N}"[..14] + ".sock"), _nonce);
        await using var remote2 = new RemoteSettingsService(client2, Cache);
        await remote2.LoadAsync(Ct);
        Assert.Equal(55, remote2.Current.TaskbarOpacity);
        Assert.Equal("luna", remote2.Current.ThemeId);
    }

    // 5. bevel-lej1: a snapshot that fails to parse is REFUSED — never projected as all-defaults. Before,
    //    ParseRawOrDefault turned "" / garbage into a default BevelSettings and the peer applied it as if
    //    the user had switched to Win2000 (and one row, and no overrides).
    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    public async Task A_snapshot_that_is_not_a_settings_object_never_flips_the_theme_to_defaults(string badBlob)
    {
        var luna = SettingsService.SerializeBlob(new BevelSettings { ThemeId = "luna", TaskbarRows = 2 }, new Dictionary<string, ThemeOverrides>());
        Assert.True(Cache.TryWrite(3, luna));

        // A core whose settings snapshot is broken, at a version far above the cached one.
        await using var server = Server(new BrokenSettings(badBlob, version: 999));
        await server.StartAsync(Ct);
        await using var client = new ShellCoreClient(_socket, _nonce);
        await using var remote = new RemoteSettingsService(client, Cache);
        var changedCount = 0;
        remote.Changed += () => Interlocked.Increment(ref changedCount);

        await remote.LoadAsync(Ct);
        await WaitFor(() => client.IsConnected, "the peer should connect to the (broken) core");
        // Both the on-connect push and the GetSettings reply have had time to arrive and be refused.
        await Task.Delay(300);

        Assert.Equal("luna", remote.Current.ThemeId);
        Assert.Equal(2, remote.Current.TaskbarRows);
        Assert.Equal(3, remote.Version);
        Assert.False(remote.HasLiveSnapshot);
        Assert.Equal(0, changedCount);
        Assert.Equal(3, Cache.TryRead()!.Value.Version);   // and the cache was not overwritten with junk
    }

    // 6. After the first live snapshot the store version is monotonic: a re-push of the same or an older
    //    version (a duplicate on-connect snapshot; a stale core answering a reconnect) is dropped.
    [Fact]
    public async Task A_live_snapshot_with_a_lower_or_equal_version_is_dropped_after_the_first_one()
    {
        using var core = await LunaCoreAsync();
        await using var server = Server(core);
        await server.StartAsync(Ct);
        await using var client = new ShellCoreClient(_socket, _nonce);
        await using var remote = new RemoteSettingsService(client, Cache);
        var changedCount = 0;
        remote.Changed += () => Interlocked.Increment(ref changedCount);

        await remote.LoadAsync(Ct);                       // live pull + the on-connect push: two same-version copies
        await WaitFor(() => remote.HasLiveSnapshot, "live snapshot");
        await Task.Delay(200);
        Assert.Equal(1, changedCount);                    // one apply, not one per copy
        Assert.Equal("luna", remote.Current.ThemeId);
    }

    // 7. The pre-cache contract still holds for a first run (no cache, core down): defaults for the first
    //    paint, and the core's on-connect snapshot corrects it live once the core comes up.
    [Fact]
    public async Task No_cache_and_no_core_seeds_defaults_then_the_first_live_snapshot_corrects_them()
    {
        await using var client = new ShellCoreClient(_socket, _nonce);
        await using var remote = new RemoteSettingsService(client, Cache);
        await remote.LoadAsync(Ct);
        Assert.Equal("win2000", remote.Current.ThemeId);
        Assert.Null(Cache.TryRead());                     // defaults are NOT cached — they were never real data

        using var core = await LunaCoreAsync();
        await using var server = Server(core);
        await server.StartAsync(Ct);
        await WaitFor(() => remote.Current.ThemeId == "luna", "the reconnect supervisor should deliver the real snapshot");
        await WaitFor(() => Cache.TryRead() is not null, "and cache it for the next boot");
    }

    /// <summary>A core whose settings snapshot is not a settings object — what a torn or empty blob on
    /// the wire looks like to a peer.</summary>
    private sealed class BrokenSettings(string blob, int version) : ISettingsService
    {
        public BevelSettings Current { get; } = new();
        public int Version => version;
        public event Action? Changed { add { } remove { } }
        public ThemeOverrides ThemeOverridesFor(string themeId) => new();
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateAsync(Action<BevelSettings> update, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateThemeOverridesAsync(string themeId, Action<ThemeOverrides> update, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ReloadIfChangedAsync(CancellationToken ct = default) => Task.FromResult(false);
        public string SnapshotJson() => blob;
        public Task ApplyPatchJsonAsync(string patchJson, CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }
}
