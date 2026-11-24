using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Bevel.Pal.Abstractions;

namespace Bevel.Taskbar;

/// <summary>
/// Warms a task button's tab list before the menu needs it (bevel-1fy8).
///
/// <para>The task-button context menu cannot fill in after it opens — an open <c>MenuFlyout</c> never
/// repaints — so the tab rows have to exist BEFORE the flyout is shown. That made the right-click pay
/// for a whole enumeration: an <c>/usr/bin/osascript</c> spawn, favicon lookups in the browser's
/// on-disk cache, and a bitmap decode per row. The repo's own measurements: 100-300 ms for an
/// iTerm-sized app, ~0.65 s for a 100-tab Arc — on the buttons people right-click most, and paid
/// again on every single right-click because nothing was kept.</para>
///
/// <para>So the fetch moves to pointer-enter, which always precedes a right-click, and this type owns
/// the result: <see cref="Warm"/> starts one fetch per app and hands the same task to whoever asks
/// next. A caller that finds it finished uses the rows with no await at all; one that finds it still
/// running waits out the REMAINDER rather than starting a second enumeration.</para>
///
/// <para><b>Freshness is a correctness bound, not a nicety.</b> <see cref="AppTab"/>'s refs are
/// positional — the activation scripts do <c>set active tab index of window N to K</c> — so a row
/// whose tab has since closed does not no-op, it selects whatever tab now occupies that index.
/// Serving arbitrarily old rows would risk activating the WRONG page, which is worse than a slow
/// menu, so a result older than the freshness window is discarded and re-fetched.</para>
/// </summary>
internal sealed class TabPrefetch : IDisposable
{
    private readonly ITabProvider? _provider;
    private readonly TimeSpan _budget;
    private readonly TimeSpan _freshness;
    private readonly Func<byte[], Bitmap?> _decode;
    private readonly Func<long> _nowMs;

    private readonly Dictionary<string, (Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>> Work, long StartedAt)>
        _slots = new(StringComparer.Ordinal);

    /// <summary>Decoded favicons keyed by a content hash of the PNG, for this object's whole life —
    /// the same pattern and reason as <c>ShellModel</c>'s window-icon cache. Two things fall out:
    /// many tabs of one site share a favicon, so a 100-tab window decodes a handful of images rather
    /// than 100; and a row no longer OWNS its bitmap, which is what makes it safe to hand the same
    /// rows to a second menu. The old per-open decode had to dispose on close, which is exactly
    /// what made a reusable prefetch impossible.</summary>
    private readonly ConcurrentDictionary<long, Bitmap> _icons = new();

    private int _disposed;

    public TabPrefetch(ITabProvider? provider, TimeSpan budget, TimeSpan freshness,
        Func<byte[], Bitmap?>? decode = null, Func<long>? nowMs = null)
    {
        _provider = provider;
        _budget = budget;
        _freshness = freshness;
        _decode = decode ?? DecodeFavicon;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>Fetches actually started, including the ones served from a reused slot's caller. Lets a
    /// test assert that a warm slot does NOT spawn a second enumeration.</summary>
    public int Fetches { get; private set; }

    /// <summary>Starts a tab enumeration for <paramref name="bundleId"/>, or returns the one already
    /// running or recently finished. Null when there is no provider or the app has no tab dialect, so
    /// an unsupported app never pays a query (or a consent prompt). Call on one thread — the view's
    /// pointer and context handlers are both on the UI thread.</summary>
    public Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>? Warm(string? bundleId)
    {
        if (_provider is null || !_provider.SupportsApp(bundleId)) return null;

        var now = _nowMs();
        if (_slots.TryGetValue(bundleId!, out var slot)
            // Reuse while fresh, and reuse a stale-but-UNFINISHED fetch too: it is about to answer
            // (or time out) either way, and a second osascript for the same app would make the slow
            // case slower, not faster.
            && (now - slot.StartedAt <= (long)_freshness.TotalMilliseconds || !slot.Work.IsCompleted))
            return slot.Work;

        Fetches++;
        var work = FetchAsync(bundleId!);
        _slots[bundleId!] = (work, now);
        return work;
    }

    /// <summary>bevel-bw1v: drops the slot for <paramref name="bundleId"/> so the next
    /// <see cref="Warm"/> re-enumerates. Called when an activation reported NotFound/Failed — the
    /// cached list no longer reflects the app, and the next menu open must show the truth.</summary>
    public void Invalidate(string? bundleId)
    {
        if (bundleId is not null) _slots.Remove(bundleId);
    }

    private async Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>> FetchAsync(string bundleId)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(_budget);
            var tabs = await _provider!.GetTabsAsync(bundleId, cts.Token).ConfigureAwait(false);
            // Favicon PNGs decode OFF the UI thread (never-block rule) — the menu then only composes
            // ready Image sources.
            var rows = await Task.Run(() => tabs.Select(t => new TaskButtonMenu.TabMenuRow(t, Icon(t))).ToList())
                .ConfigureAwait(false);
            // Enumeration over budget is killed in the provider (empty list); enrichment over budget is
            // abandoned there (unenriched list). Neither throws — log every outcome so "0 tabs in
            // ~budget ms" reads as a timeout. Elapsed includes icon decode.
            TaskbarLog.Debug($"TABS prefetch {bundleId}: {tabs.Count} in {sw.ElapsedMilliseconds}ms");
            return rows;
        }
        catch (Exception ex)
        {
            TaskbarLog.Debug($"TABS prefetch failed for {bundleId}: {ex.GetType().Name} after {sw.ElapsedMilliseconds}ms");
            return Array.Empty<TaskButtonMenu.TabMenuRow>();   // timeout / app gone — menu opens without Tabs
        }
    }

    private Bitmap? Icon(AppTab tab)
    {
        if (tab.IconPng is not { Length: > 0 } png) return null;
        var key = ShellModel.Fnv1a64(png);
        if (_icons.TryGetValue(key, out var cached)) return cached;
        if (_decode(png) is not { } bmp) return null;
        var winner = _icons.GetOrAdd(key, bmp);
        if (!ReferenceEquals(winner, bmp)) bmp.Dispose();   // lost a decode race — free it now
        return winner;
    }

    /// <summary>DecodeToWidth(16) so a 192px cache blob isn't decoded at full size for a 16px row; a
    /// blob that fails to decode is simply an icon-less row.</summary>
    private static Bitmap? DecodeFavicon(byte[] png)
    {
        try { return Bitmap.DecodeToWidth(new System.IO.MemoryStream(png), 16); }
        catch { return null; }
    }

    /// <summary>The favicons are Skia-backed unmanaged memory the GC cannot see, and they outlive
    /// individual menus on purpose — so the owner (the view, on unload) is what frees them.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var icon in _icons.Values) icon.Dispose();
        _icons.Clear();
        _slots.Clear();
    }
}
