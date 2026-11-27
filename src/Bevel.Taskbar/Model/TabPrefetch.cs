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
/// <para><b>Thread-safe.</b> The Start menu's tabs search warms the same cache from thread-pool queries
/// (bevel-vtc6) while the UI thread warms on pointer-enter, so the slot check-and-set is locked and
/// <see cref="Fetches"/> is an <see cref="Interlocked"/> counter — concurrent <see cref="Warm"/> calls
/// for one app share a single fetch.</para>
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

    /// <summary>Guards <see cref="_slots"/> — Warm runs on the UI thread (pointer-enter) AND on
    /// thread-pool search queries (bevel-vtc6), so the reuse check-and-set must be atomic.</summary>
    private readonly object _slotsGate = new();
    private readonly Dictionary<string, (Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>> Work, long StartedAt)>
        _slots = new(StringComparer.Ordinal);

    /// <summary>Decoded favicons keyed by a content hash of the PNG — the same pattern and reason as
    /// <c>ShellModel</c>'s window-icon cache. Two things fall out: many tabs of one site share a
    /// favicon, so a 100-tab window decodes a handful of images rather than 100; and a row no longer
    /// OWNS its bitmap, which is what makes it safe to hand the same rows to a second menu. The old
    /// per-open decode had to dispose on close, which is exactly what made a reusable prefetch
    /// impossible.
    ///
    /// <para>The cache is an LRU capped at <see cref="MaxIcons"/> entries (a long-lived taskbar warming
    /// a new site per hover would otherwise grow it forever); the evicted bitmap is disposed, since
    /// it is Skia-backed unmanaged memory the GC cannot see. All access is under
    /// <see cref="_iconsGate"/> — decodes happen OUTSIDE the lock, and a lost decode race disposes
    /// its own bitmap, exactly as before.</para></summary>
    private const int MaxIcons = 512;
    private readonly object _iconsGate = new();
    private readonly Dictionary<long, Bitmap> _icons = new();
    /// <summary>Most-recently-used first; <see cref="_iconLruIndex"/> points at each key's node.</summary>
    private readonly LinkedList<long> _iconLru = new();
    private readonly Dictionary<long, LinkedListNode<long>> _iconLruIndex = new();

    private int _fetches;
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
    /// test assert that a warm slot does NOT spawn a second enumeration. An <see cref="Interlocked"/>
    /// counter — Warm is called from the UI thread and from thread-pool search queries.</summary>
    public int Fetches => Volatile.Read(ref _fetches);

    /// <summary>Live favicon cache entries (never above <see cref="MaxIcons"/>). Exposed for tests.</summary>
    internal int IconCount { get { lock (_iconsGate) return _icons.Count; } }

    /// <summary>Starts a tab enumeration for <paramref name="bundleId"/>, or returns the one already
    /// running or recently finished. Null when there is no provider or the app has no tab dialect, so
    /// an unsupported app never pays a query (or a consent prompt). Thread-safe: the view's pointer
    /// and context handlers call this on the UI thread while the Start menu's tabs search warms from
    /// thread-pool queries (bevel-vtc6) — concurrent callers for one app share a single fetch.</summary>
    public Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>? Warm(string? bundleId)
    {
        if (_provider is null || !_provider.SupportsApp(bundleId)) return null;

        var now = _nowMs();
        Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>> work;
        lock (_slotsGate)
        {
            if (_slots.TryGetValue(bundleId!, out var slot)
                // Reuse while fresh, and reuse a stale-but-UNFINISHED fetch too: it is about to answer
                // (or time out) either way, and a second osascript for the same app would make the slow
                // case slower, not faster.
                && (now - slot.StartedAt <= (long)_freshness.TotalMilliseconds || !slot.Work.IsCompleted))
                return slot.Work;

            work = FetchAsync(bundleId!);
            _slots[bundleId!] = (work, now);
        }
        Interlocked.Increment(ref _fetches);
        return work;
    }

    /// <summary>bevel-bw1v: drops the slot for <paramref name="bundleId"/> so the next
    /// <see cref="Warm"/> re-enumerates. Called when an activation reported NotFound/Failed — the
    /// cached list no longer reflects the app, and the next menu open must show the truth.</summary>
    public void Invalidate(string? bundleId)
    {
        if (bundleId is null) return;
        lock (_slotsGate) _slots.Remove(bundleId);
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
        var key = Bevel.Core.ContentHash.Fnv1a64(png);
        lock (_iconsGate)
        {
            if (_icons.TryGetValue(key, out var hit))
            {
                TouchLocked(key);
                return hit;
            }
        }
        // Decode OUTSIDE the lock (never-block rule) — a lost race disposes its own bitmap below.
        if (_decode(png) is not { } bmp) return null;
        Bitmap winner;
        var lostRace = false;
        lock (_iconsGate)
        {
            if (_icons.TryGetValue(key, out winner)) lostRace = true;   // another thread decoded it first
            else
            {
                StoreLocked(key, bmp);
                winner = bmp;
            }
        }
        if (lostRace) bmp.Dispose();   // lost a decode race — free it now
        return winner;
    }

    /// <summary>Records a cache hit as most-recently-used. Caller must hold <see cref="_iconsGate"/>.</summary>
    private void TouchLocked(long key)
    {
        if (_iconLruIndex.TryGetValue(key, out var node))
        {
            _iconLru.Remove(node);
            _iconLru.AddFirst(node);
        }
    }

    /// <summary>Inserts a fresh entry, evicting (and disposing) the least-recently-used bitmap when the
    /// cache is at <see cref="MaxIcons"/>. Caller must hold <see cref="_iconsGate"/>.</summary>
    private void StoreLocked(long key, Bitmap bmp)
    {
        while (_icons.Count >= MaxIcons)
        {
            var oldest = _iconLru.Last!;
            _iconLru.RemoveLast();
            _iconLruIndex.Remove(oldest.Value);
            if (_icons.Remove(oldest.Value, out var evicted)) evicted.Dispose();
        }
        var node = _iconLru.AddFirst(key);
        _iconLruIndex[key] = node;
        _icons[key] = bmp;
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
        lock (_iconsGate)
        {
            foreach (var icon in _icons.Values) icon.Dispose();
            _icons.Clear();
            _iconLru.Clear();
            _iconLruIndex.Clear();
        }
        lock (_slotsGate) _slots.Clear();
    }
}
