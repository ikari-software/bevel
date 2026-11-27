using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Bevel.Pal.Abstractions;
using Bevel.Taskbar;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The task-button menu's tab list is warmed on hover and reused (bevel-1fy8).
///
/// <para>Right-clicking a browser or terminal button used to show NOTHING for 100-300 ms — ~0.65 s on a
/// 100-tab Arc — because an open MenuFlyout never repaints, so the rows had to exist before the flyout
/// and the enumeration (an osascript spawn, favicon lookups, a decode per row) ran on the click. Nothing
/// was kept, so every right-click paid again.</para>
///
/// <para>These tests drive <see cref="TabPrefetch"/> directly with a counting provider and an injected
/// clock, so the reuse, the expiry and the failure behaviour are asserted without sleeping through a
/// 1.5 s window or depending on a real browser.</para>
/// </summary>
public sealed class TabPrefetchTests
{
    private const string App = "com.example.browser";
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan Freshness = TimeSpan.FromMilliseconds(1500);

    private sealed class CountingTabs : ITabProvider
    {
        private readonly TaskCompletionSource<bool> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls;
        public bool Hold;                  // when set, GetTabsAsync blocks until Release()
        public Exception? Fault;
        public List<AppTab> Tabs = [];

        public void Release() => _gate.TrySetResult(true);

        public Capabilities Capabilities { get; } = new(true, TrayCapability.Mirrored, []);
        public bool SupportsApp(string? bundleId) => bundleId == App;

        public async ValueTask<IReadOnlyList<AppTab>> GetTabsAsync(string bundleId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            if (Hold) await _gate.Task.WaitAsync(ct);
            if (Fault is { } ex) throw ex;
            return Tabs.ToArray();
        }

        public Task<TabActivationResult> ActivateAsync(AppTab tab, CancellationToken ct = default)
            => Task.FromResult(TabActivationResult.Activated);
    }

    private static AppTab Tab(int index, byte[]? icon = null) =>
        new(App, "1", index, $"Tab {index}", Url: $"https://example.com/{index}", IconPng: icon);

    /// <summary>A long clock the test advances by hand, so the freshness window is exercised exactly
    /// rather than by sleeping.</summary>
    private sealed class FakeClock
    {
        public long Now;
        public Func<long> Read => () => Now;
    }

    /// <summary>The hover fetch is what the right-click then uses: a second Warm inside the freshness
    /// window returns the SAME task and starts no second enumeration. This is the whole mechanism — if
    /// it regressed, every right-click would pay for osascript again.</summary>
    [Fact]
    public async Task Invalidate_drops_the_slot_so_the_next_warm_re_enumerates()
    {
        var tabs = new CountingTabs { Tabs = [Tab(1)] };
        var prefetch = new TabPrefetch(tabs, Budget, Freshness);
        await prefetch.Warm(App)!;
        Assert.Equal(1, prefetch.Fetches);

        prefetch.Invalidate(App);   // bevel-bw1v: a stale activation result drops the cached list
        await prefetch.Warm(App)!;
        Assert.Equal(2, prefetch.Fetches);
        Assert.Equal(2, tabs.Calls);
    }

    public async Task A_warm_slot_is_reused_instead_of_re_enumerating()
    {
        var provider = new CountingTabs { Tabs = [Tab(1), Tab(2)] };
        var clock = new FakeClock();
        using var prefetch = new TabPrefetch(provider, Budget, Freshness, decode: _ => null, nowMs: clock.Read);

        var hover = prefetch.Warm(App)!;                 // pointer-enter
        var rows = await hover;
        Assert.Equal(2, rows.Count);

        clock.Now += 500;                                 // the user right-clicks half a second later
        var click = prefetch.Warm(App)!;

        Assert.Same(hover, click);
        Assert.True(click.IsCompletedSuccessfully,
            "the rows must be available with no await — that is what lets the menu open on the click");
        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, prefetch.Fetches);
    }

    /// <summary>bevel-8zx2: the freshness window is WIDER than the fetch budget — a row enumerated at
    /// the start of a full-budget fetch is already budget-old when shown, so reusing a result up to
    /// the wider window adds no staleness the design did not already accept. Now safe because
    /// activation is identity-verified (bevel-6asm/bw1v): a stale row reports NotFound and invalidates
    /// the slot instead of pressing the wrong tab.</summary>
    [Fact]
    public async Task A_result_older_than_the_budget_but_inside_the_freshness_window_is_still_served()
    {
        var wideFreshness = TimeSpan.FromSeconds(10);   // bevel-8zx2: TabPrefetchFreshness on the view
        var provider = new CountingTabs { Tabs = [Tab(1)] };
        var clock = new FakeClock();
        using var prefetch = new TabPrefetch(provider, Budget, wideFreshness, decode: _ => null, nowMs: clock.Read);

        await prefetch.Warm(App)!;
        clock.Now += (long)Budget.TotalMilliseconds + 1;   // older than the budget, well inside 10 s

        var second = prefetch.Warm(App)!;
        Assert.Equal(1, prefetch.Fetches);
        Assert.Equal(1, provider.Calls);
        Assert.Same(second, prefetch.Warm(App));   // still the same slot, no re-enumeration
    }

    /// <summary>Past the window the result is DISCARDED and re-fetched. The bound is correctness, not
    /// tidiness: AppTab refs are positional (<c>set active tab index of window N to K</c>), so a row
    /// whose tab has since closed selects whatever now sits at that index — serving old rows risks
    /// activating the wrong page.</summary>
    [Fact]
    public async Task A_stale_result_is_discarded_rather_than_served()
    {
        var provider = new CountingTabs { Tabs = [Tab(1)] };
        var clock = new FakeClock();
        using var prefetch = new TabPrefetch(provider, Budget, Freshness, decode: _ => null, nowMs: clock.Read);

        await prefetch.Warm(App)!;
        clock.Now += (long)Freshness.TotalMilliseconds + 1;

        var second = prefetch.Warm(App)!;
        await second;

        Assert.Equal(2, provider.Calls);
    }

    /// <summary>A fetch still in flight is awaited, not duplicated — the slow case waits out the
    /// REMAINDER. Two concurrent osascript spawns for one app would make it slower, not faster.</summary>
    [Fact]
    public async Task An_unfinished_fetch_is_shared_not_duplicated()
    {
        var provider = new CountingTabs { Hold = true, Tabs = [Tab(1)] };
        var clock = new FakeClock();
        using var prefetch = new TabPrefetch(provider, Budget, Freshness, decode: _ => null, nowMs: clock.Read);

        var hover = prefetch.Warm(App)!;
        Assert.False(hover.IsCompleted);

        // Even well PAST the freshness window: an unfinished fetch is about to answer either way.
        clock.Now += (long)Freshness.TotalMilliseconds * 3;
        var click = prefetch.Warm(App)!;
        Assert.Same(hover, click);

        provider.Release();
        Assert.Single(await click);
        Assert.Equal(1, provider.Calls);
    }

    /// <summary>An app with no tab dialect never starts a fetch — it must not pay a query, or on macOS
    /// an Automation consent prompt, for a guaranteed-empty answer. Hover now reaches this for EVERY
    /// task button, so the gate matters more than it did when only right-click did.</summary>
    [Fact]
    public void An_unsupported_app_never_starts_a_fetch()
    {
        var provider = new CountingTabs();
        using var prefetch = new TabPrefetch(provider, Budget, Freshness, decode: _ => null);

        Assert.Null(prefetch.Warm("com.apple.TextEdit"));
        Assert.Null(prefetch.Warm(null));
        Assert.Equal(0, provider.Calls);
    }

    /// <summary>A failed or timed-out fetch yields empty rows (the menu opens without a Tabs section)
    /// and must not poison the slot: once it ages out, the next hover tries again.</summary>
    [Fact]
    public async Task A_failed_fetch_yields_no_rows_and_is_retried_later()
    {
        var provider = new CountingTabs { Fault = new InvalidOperationException("app quit mid-query") };
        var clock = new FakeClock();
        using var prefetch = new TabPrefetch(provider, Budget, Freshness, decode: _ => null, nowMs: clock.Read);

        Assert.Empty(await prefetch.Warm(App)!);

        provider.Fault = null;
        provider.Tabs = [Tab(1)];
        clock.Now += (long)Freshness.TotalMilliseconds + 1;

        Assert.Single(await prefetch.Warm(App)!);
        Assert.Equal(2, provider.Calls);
    }

    /// <summary>bevel-vtc6 review: the Start menu's tabs search warms this cache from thread-pool
    /// queries while the UI thread warms on pointer-enter. Concurrent Warm calls for one app must share
    /// a SINGLE fetch — a check-and-set race would start N osascript spawns for one app.</summary>
    [Fact]
    public async Task Concurrent_warm_calls_share_a_single_fetch()
    {
        var provider = new CountingTabs { Hold = true, Tabs = [Tab(1)] };
        using var prefetch = new TabPrefetch(provider, Budget, Freshness, decode: _ => null);

        var works = new List<Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?>();
        var collect = new object();
        Parallel.For(0, 32, _ =>
        {
            var w = prefetch.Warm(App);
            lock (collect) works.Add(w);
        });

        Assert.Equal(32, works.Count);
        Assert.All(works, w => Assert.NotNull(w));
        Assert.All(works, w => Assert.Same(works[0], w));   // one shared task, not 32
        Assert.Equal(1, prefetch.Fetches);

        provider.Release();
        Assert.Single(await works[0]!);
        Assert.Equal(1, provider.Calls);
    }

    /// <summary>bevel-vtc6 review: the favicon cache is an LRU capped at 512 entries — a long-lived
    /// taskbar warming a new site on every hover must not grow it forever. Distinct PNG bytes per tab
    /// (the key is a content hash), a real decode for each.</summary>
    [AvaloniaFact]
    public async Task The_favicon_cache_is_capped_and_evicts_old_entries()
    {
        var png = Png16();
        var tabs = Enumerable.Range(0, 600)
            .Select(i => new AppTab(App, "1", i + 1, $"Tab {i}", $"https://example.com/{i}",
                IconPng: new[] { (byte)i, (byte)(i >> 8) }))
            .ToList();
        var provider = new CountingTabs { Tabs = tabs };
        using var prefetch = new TabPrefetch(provider, Budget, Freshness,
            decode: _ => Bitmap.DecodeToWidth(new System.IO.MemoryStream(png), 16));

        var rows = await prefetch.Warm(App)!;

        Assert.Equal(600, rows.Count);
        Assert.Equal(512, prefetch.IconCount);   // capped, not 600
    }

    /// <summary>Byte-identical favicons decode ONCE and the rows share the bitmap. Tabs of one site
    /// repeat constantly, so a 100-tab window decoded 100 images for a handful of distinct icons —
    /// and, more importantly, a row that doesn't own its bitmap is what makes reusing a prefetched
    /// row list safe at all (the old code disposed every icon when the menu closed).</summary>
    [AvaloniaFact]
    public async Task Identical_favicons_decode_once_and_are_shared()
    {
        var png = Png16();
        var provider = new CountingTabs { Tabs = [Tab(1, png), Tab(2, (byte[])png.Clone()), Tab(3, png)] };
        var decodes = 0;
        using var prefetch = new TabPrefetch(provider, Budget, Freshness,
            decode: bytes => { decodes++; return Bitmap.DecodeToWidth(new System.IO.MemoryStream(bytes), 16); });

        var rows = await prefetch.Warm(App)!;

        Assert.Equal(3, rows.Count);
        Assert.Equal(1, decodes);
        Assert.NotNull(rows[0].Icon);
        Assert.Same(rows[0].Icon, rows[1].Icon);
        Assert.Same(rows[0].Icon, rows[2].Icon);
    }

    /// <summary>A 1x1 RGB PNG — enough for a real decode without carrying a fixture file.</summary>
    private static byte[] Png16() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");
}
