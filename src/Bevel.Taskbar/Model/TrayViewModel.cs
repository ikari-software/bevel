using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Bevel.Pal.Abstractions;

namespace Bevel.Taskbar;

/// <summary>
/// The notification-area tray: mirrors the host's menu-bar status items (bevel-m3.1) into an
/// observable strip the taskbar renders. Fed by <see cref="ISystemTrayHost"/> — an initial
/// <see cref="ISystemTrayHost.GetItemsAsync"/> pull plus the add/remove/update events (which arrive
/// on a gRPC stream thread, so every mutation hops to the UI thread). Keyed by item id so a
/// re-emitted snapshot updates in place rather than duplicating.
/// </summary>
public sealed class TrayViewModel : ObservableObject, IDisposable
{
    private readonly ISystemTrayHost? _tray;
    private bool _started;

    public TrayViewModel(ISystemTrayHost? tray) => _tray = tray;

    /// <summary>Strategy C (bevel-7hf4): consolidate the real macOS menu bar into this tray (hide the
    /// real items) or reveal it. Drives the helper's control item via the tray host; fire-and-forget
    /// (failures are logged host-side, bounded by a deadline).</summary>
    public void SetConsolidated(bool consolidated)
    {
        // Dedup HERE so BOTH callers benefit — the settings-poll path (TaskbarViewModel) and the
        // in-process live-settings path (TaskbarView.ApplyLiveSettings). Previously only the former
        // deduped, so an unchanged value from the live path re-fired the native hide (ce-review:
        // maintainability; the two-path pattern this taskbar has a history of).
        if (_consolidated == consolidated) return;
        _consolidated = consolidated;
        _ = _tray?.SetNativeTrayHiddenAsync(consolidated);
    }

    private bool _consolidated;

    /// <summary>Default per-row overflow budget in BOX-WIDTHS (bevel-cust.tray): the tray shows up to this
    /// many box-slots of icons per taskbar row before the rest move under the overflow chevron.</summary>
    public const int DefaultVisibleCap = 8;

    /// <summary>One box-slot's width in points — the unit the overflow budget counts. A single native
    /// menu-bar icon is ~one box; a wide strip (iStat) spans several (<see cref="TrayItemViewModel.BoxSpan"/>).</summary>
    public const double BoxWidth = 24;

    // Tray tuning bounds (bevel-cust.tray). Keep in sync with OnboardingWindow.axaml's TrayCapSlider /
    // TrayIconSizeSlider Minimum/Maximum — the domain clamp and the UI slider must not drift apart.
    public const int MinOverflowCap = 1;
    public const int MaxOverflowCap = 20;
    public const int MinIconSize = 12;
    public const int MaxIconSize = 32;

    /// <summary>Vertical chrome between a taskbar row and the tray icons inside it, in points: the
    /// <c>TrayArea</c> StackPanel's 1pt top+bottom margin plus the sunken tray well's 2pt top+bottom
    /// border (Bevel.Metric.TrayWellBorder; Luna flattens it to 0, so this is the worst case).</summary>
    internal const double TrayWellInset = 6;

    /// <summary>The tallest a tray icon may render (points) on a bar of <paramref name="rows"/> rows.
    /// Native menu-bar status items are NOT bounded by the taskbar — their on-screen height is whatever
    /// the host's menu bar is (the helper admits 8–40pt, and a notched Mac's band is ~37pt) and the user's
    /// size slider scales it further — so without this clamp the tray strip renders taller than the bar
    /// it lives in and the icons spill past it (bevel-xpfl). Derived from the live
    /// <see cref="TaskbarTheme"/> metrics so the Small/Normal/Large button tier moves it too.</summary>
    internal static double MaxIconHeight(int rows)
    {
        rows = Math.Max(1, rows);
        var content = TaskbarTheme.HeightForRows(rows) - TrayWellInset;
        return Math.Max(8, content / rows);
    }

    private int _iconSize = 16;
    private Color _ink = Colors.White;

    /// <summary>Inline tray-icon count before the overflow chevron. Set live via <see cref="Configure"/>.</summary>
    public int VisibleCap { get; private set; } = DefaultVisibleCap;

    /// <summary>The bar's contrast ink (Bevel.Brush.TrayText — dark on bright bars, light on dark). Template
    /// glyphs are recoloured to it; called on theme/variant change so mirrored icons re-adapt in place.</summary>
    public void SetInk(Color ink)
    {
        if (_ink == ink) return;
        _ink = ink;
        foreach (var it in Items) it.Ink = ink;
    }

    private int _rows = 1;

    /// <summary>Taskbar row count. Bound by the tray panel (a UniformGrid) so it lays out exactly this many
    /// rows — robust to per-icon pixel heights, unlike a height-driven WrapPanel.</summary>
    public int Rows { get => _rows; private set => SetProperty(ref _rows, value); }

    /// <summary>Total inline capacity in BOX-WIDTHS = the per-row box budget × the taskbar row count — a
    /// taller bar shows proportionally more before the overflow chevron.</summary>
    private int BoxBudget => Math.Max(1, VisibleCap * Math.Max(1, _rows));

    /// <summary>Track the taskbar row count (bevel-m3). Reslices when it changes.</summary>
    public void SetRows(int rows)
    {
        rows = Math.Max(1, rows);
        if (_rows == rows) return;
        Rows = rows;
        ApplyIconHeightCap();
        Reslice();
    }

    /// <summary>Pushes the current per-row height budget onto every item, so no icon can render taller
    /// than the tray strip it sits in (bevel-xpfl). Re-applied whenever the row count, the button tier or
    /// the user's size slider changes, and on every newly mirrored item.</summary>
    private void ApplyIconHeightCap()
    {
        var max = MaxIconHeight(_rows);
        foreach (var it in Items) it.SetMaxHeight(max);
    }

    /// <summary>Applies the user's tray tuning live (bevel-cust.tray): inline overflow cap + icon size.</summary>
    public void Configure(int overflowCap, int iconSize)
    {
        _iconSize = Math.Clamp(iconSize, MinIconSize, MaxIconSize);
        var scale = _iconSize / 16.0;   // 16 == Native (1.0×); the slider scales native size uniformly
        var max = MaxIconHeight(_rows);
        foreach (var it in Items) { it.SetMaxHeight(max); it.SetScale(scale); }
        VisibleCap = Math.Clamp(overflowCap, MinOverflowCap, MaxOverflowCap);
        Reslice();
    }

    private bool _hasOverflow;
    private bool _hasAnyItems;

    /// <summary>The full mirrored item set, in host order (left-to-right menu-bar order on macOS).</summary>
    public ObservableCollection<TrayItemViewModel> Items { get; } = new();

    /// <summary>The items shown inline in the tray strip (first <see cref="VisibleCap"/>).</summary>
    public ObservableCollection<TrayItemViewModel> VisibleItems { get; } = new();

    /// <summary>The items past the cap, reached through the overflow (») flyout.</summary>
    public ObservableCollection<TrayItemViewModel> OverflowItems { get; } = new();

    /// <summary>True when there are more items than fit inline — drives the overflow chevron.</summary>
    public bool HasOverflow { get => _hasOverflow; private set => SetProperty(ref _hasOverflow, value); }

    /// <summary>True when the tray has any mirrored items — gates the dark mirror strip so an empty
    /// tray shows nothing rather than a bare dark chip.</summary>
    public bool HasAnyItems { get => _hasAnyItems; private set => SetProperty(ref _hasAnyItems, value); }

    /// <summary>Forwards a click on a mirrored item to the real status item (spec §5.5), and promotes
    /// it into the visible set (light LRU) so an item you use stays reachable inline.</summary>
    public async Task<bool> Forward(TrayItemId id, TrayButton button, TrayModifiers modifiers)
    {
        PromoteToVisible(id);
        // Reveal-on-click (C2): with overlay-hide (bevel-7hf4) landed, the real items are never moved —
        // they stay on-screen under the level-26 cover, so this click AX-presses their live coordinates
        // directly and the resulting system menu pops above the overlay. No collapse/reveal/rehide dance.
        return await (_tray?.ForwardClickAsync(id, button, modifiers) ?? Task.FromResult(false));
    }

    /// <summary>Moves an item into the last inline slot if it's currently overflowed — a used item
    /// earns its place in the visible strip without reshuffling the others.</summary>
    private void PromoteToVisible(TrayItemId id)
    {
        var index = -1;
        for (var i = 0; i < Items.Count; i++)
            if (Items[i].Id.Equals(id)) { index = i; break; }
        // If the used item isn't currently inline (it's under the overflow flyout), hoist it to the front
        // so it earns an inline slot — a light LRU that keeps an item you actually use reachable.
        if (index > 0 && !VisibleItems.Any(v => v.Id.Equals(id)))
        {
            Items.Move(index, 0);
            Reslice();
        }
    }

    /// <summary>Subscribes to the host and pulls the initial snapshot. Idempotent.</summary>
    public async void Start()
    {
        if (_started || _tray is null) return;
        _started = true;

        _tray.ItemAdded += OnItemAdded;
        _tray.ItemUpdated += OnItemUpdated;
        _tray.ItemRemoved += OnItemRemoved;

        try
        {
            var items = await _tray.GetItemsAsync();
            Dispatcher.UIThread.Post(() => { foreach (var i in items) Upsert(i); });
        }
        catch
        {
            // Best-effort initial pull; the Changes stream still delivers items as events.
        }
    }

    private void OnItemAdded(object? sender, TrayItem item) => Post(() => Upsert(item));
    private void OnItemUpdated(object? sender, TrayItem item) => Post(() => Upsert(item));
    private void OnItemRemoved(object? sender, TrayItem item) => Post(() => Remove(item.Id));

    private static void Post(Action a) => Dispatcher.UIThread.Post(a);

    private void Upsert(TrayItem item)
    {
        var existing = Items.FirstOrDefault(i => i.Id.Equals(item.Id));
        if (existing is not null) { existing.Update(item); return; } // in-place update — no reslice needed
        var vm = new TrayItemViewModel(item) { Ink = _ink };
        vm.SetMaxHeight(MaxIconHeight(_rows));
        vm.SetScale(_iconSize / 16.0);
        vm.PropertyChanged += OnItemIconStateChanged;
        Items.Add(vm);
        Reslice();
        RepokeConsolidation();
        QueueIconStateSummary();
    }

    private void Remove(TrayItemId id)
    {
        var existing = Items.FirstOrDefault(i => i.Id.Equals(id));
        if (existing is null) return;
        existing.PropertyChanged -= OnItemIconStateChanged;
        Items.Remove(existing);
        Reslice();
        RepokeConsolidation();
        QueueIconStateSummary();
    }

    // ── Icon-state diagnostics (bevel-yduf) ─────────────────────────────

    private void OnItemIconStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TrayItemViewModel.HasIcon) or nameof(TrayItemViewModel.IsLive))
            QueueIconStateSummary();
    }

    private bool _summaryQueued;
    private string? _lastSummary;

    /// <summary>Coalesces the per-item icon-state changes (a snapshot burst decodes N icons off-thread and
    /// lands N Apply calls) into ONE summary line per settled state — see <see cref="DescribeIconState"/>.</summary>
    private void QueueIconStateSummary()
    {
        if (_summaryQueued) return;
        _summaryQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _summaryQueued = false;
            var summary = DescribeIconState(
                total: Items.Count,
                live: Items.Count(i => i.IsLive && i.HasIcon),
                appIcons: Items.Count(i => !i.IsLive && i.HasIcon),
                placeholders: Items.Count(i => !i.HasIcon));
            if (summary == _lastSummary) return;
            _lastSummary = summary;
            if (summary is not null) TaskbarLog.Info(summary);
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// The one line that says what the tray is actually showing (bevel-yduf). Three kinds of slot exist
    /// and they look alike from a distance — a LIVE capture of the real status item, the owning app's
    /// icon (the helper's limited mode, §5.5), and a placeholder (the host sent no icon bytes at all, or
    /// they did not decode) — and the difference between "mirroring works" and "the helper has no
    /// Screen Recording grant" is invisible in the bar. Logged only when the counts change, so the 2 s
    /// poll doesn't spam. Null when the tray is empty (nothing to say). Pure, so it is testable.
    /// </summary>
    internal static string? DescribeIconState(int total, int live, int appIcons, int placeholders)
    {
        if (total == 0) return null;
        var line = $"tray: {total} mirrored item(s) — {live} live capture(s), {appIcons} app-icon fallback(s), {placeholders} placeholder(s)";
        if (live == 0)
            line += " — no item captured live: the helper has no Screen Recording grant (keyed to the helper binary; " +
                    "a rebuild changes its cdhash — see packaging/macos/dev-sign.sh) or its self-test disabled live " +
                    "mirroring; look for the helper's '[BEVEL-TRAY] self-test' / 'capture' lines above";
        return line;
    }

    /// <summary>Menu-bar reflow (bevel-7hf4, overlay-hide risk 5): adding/removing a real status item
    /// shifts its neighbours by its full width, moving the covered span. Re-apply the hide so the host
    /// recomputes the cover frame from its fresh bounds cache. Cheap: the overlay no-ops unless the frame
    /// actually changed, so the frame-diff is its own debounce. Runs on the UI thread (callers already Post).</summary>
    private void RepokeConsolidation()
    {
        if (_consolidated) _ = _tray?.SetNativeTrayHiddenAsync(true);
    }

    /// <summary>Reconciles <see cref="VisibleItems"/> / <see cref="OverflowItems"/> to the first-N /
    /// rest of <see cref="Items"/> in place (shared VM instances, so bindings/icons survive).</summary>
    private void Reslice()
    {
        // Fill the inline strip GREEDILY by BOX-WIDTH budget: add each item whose box-span fits the
        // REMAINING budget, and skip (overflow) any too wide to fit — so one very wide item (e.g. a 269pt
        // combined strip = 12 boxes) drops to the flyout instead of truncating every item after it.
        var budget = BoxBudget;
        var vis = new List<TrayItemViewModel>();
        var over = new List<TrayItemViewModel>();
        var used = 0;
        foreach (var it in Items)
        {
            var span = it.BoxSpan;
            if (used + span <= budget) { vis.Add(it); used += span; }
            else over.Add(it);
        }
        SyncTo(VisibleItems, vis);
        SyncTo(OverflowItems, over);
        HasOverflow = over.Count > 0;
        HasAnyItems = Items.Count > 0;
    }

    private static void SyncTo(ObservableCollection<TrayItemViewModel> target, IEnumerable<TrayItemViewModel> desired)
    {
        var want = desired.ToList();
        for (var i = target.Count - 1; i >= 0; i--)
            if (!want.Contains(target[i])) target.RemoveAt(i);
        for (var i = 0; i < want.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], want[i])) continue;
            var cur = target.IndexOf(want[i]);
            if (cur >= 0) target.Move(cur, i);
            else target.Insert(i, want[i]);
        }
    }

    public void Dispose()
    {
        if (_tray is null) return;
        _tray.ItemAdded -= OnItemAdded;
        _tray.ItemUpdated -= OnItemUpdated;
        _tray.ItemRemoved -= OnItemRemoved;
    }
}

/// <summary>One mirrored tray icon: its hover text and decoded 16×16 icon. The icon PNG is the live
/// capture or the owning app's icon (limited mode, spec §5.5).</summary>
public sealed class TrayItemViewModel : ObservableObject
{
    private string _tooltip = "";
    private Bitmap? _iconSource;

    public TrayItemViewModel(TrayItem item)
    {
        Id = item.Id;
        Update(item);
    }

    public TrayItemId Id { get; }

    public string Tooltip { get => _tooltip; private set => SetProperty(ref _tooltip, value); }
    public Bitmap? IconSource { get => _iconSource; private set => SetProperty(ref _iconSource, value); }

    private bool _hasIcon;
    private bool _isLive;

    /// <summary>True once a decoded icon is showing. False = the slot draws a PLACEHOLDER (bevel-yduf):
    /// the host sent no icon bytes (limited mode with no resolvable app icon) or they failed to decode.
    /// Never a blank cell — a blank at native width looks like nothing is mirrored at all.</summary>
    public bool HasIcon { get => _hasIcon; private set => SetProperty(ref _hasIcon, value); }

    /// <summary>True when the icon bytes are a live capture of the real status item, false when they
    /// are the owning app's icon (the helper's limited mode, §5.5). Diagnostic — see
    /// <see cref="TrayViewModel.DescribeIconState"/>.</summary>
    public bool IsLive { get => _isLive; private set => SetProperty(ref _isLive, value); }

    private byte[]? _png;
    private byte[]? _loggedUndecodable;
    private Color _ink = Colors.White;

    /// <summary>The bar's contrast ink (Bevel.Brush.TrayText). Template glyphs recolour to it; changing it
    /// (theme/variant switch) re-tints in place.</summary>
    public Color Ink { get => _ink; set { if (_ink != value) { _ink = value; Retint(); } } }

    // NATIVE size + a single tray-wide uniform SCALE (bevel-7hf4). The mirrored icon renders at its true
    // macOS on-screen size (from bounds, points) × the scale — default 1.0 = "Native", so a mac icon stays
    // mac-sized. The scale is uniform across every item, so it never reintroduces per-item size drift.
    private double _nativeW = 24, _nativeH = 22, _scale = 1.0, _maxH;

    /// <summary>Shrink factor that keeps the rendered height inside the tray strip's per-row budget
    /// (<see cref="SetMaxHeight"/>). 1.0 whenever the icon already fits; applied to BOTH axes so a
    /// clamped icon scales down uniformly instead of squashing (bevel-xpfl).</summary>
    private double Fit
    {
        get
        {
            var h = _nativeH * _scale;
            return _maxH > 0 && h > _maxH ? _maxH / h : 1.0;
        }
    }

    /// <summary>Rendered size (points) = native on-screen size × the tray scale, capped to the row height.</summary>
    public double IconW => _nativeW * _scale * Fit;
    public double IconH => _nativeH * _scale * Fit;

    /// <summary>The tallest this icon may render (points) — the tray strip's per-row content height. The
    /// host's menu-bar item height is outside Bevel's control (and the size slider multiplies it), so
    /// without this bound the tray measures taller than the bar and spills past it (bevel-xpfl).</summary>
    public void SetMaxHeight(double maxH)
    {
        if (Math.Abs(_maxH - maxH) < 0.001) return;
        _maxH = maxH;
        OnPropertyChanged(nameof(IconW));
        OnPropertyChanged(nameof(IconH));
    }

    /// <summary>How many box-slots this item spans in the overflow budget — its NATIVE width in
    /// <see cref="TrayViewModel.BoxWidth"/> units (scale-independent, so the "N boxes/row" cap is stable
    /// at any display scale). A wide strip (iStat) counts as several boxes; every item is at least 1.</summary>
    public int BoxSpan => Math.Max(1, (int)Math.Ceiling(_nativeW / TrayViewModel.BoxWidth));

    /// <summary>Sets the tray-wide uniform display scale (1.0 = native size).</summary>
    public void SetScale(double scale)
    {
        scale = scale <= 0 ? 1.0 : scale;
        if (Math.Abs(_scale - scale) < 0.001) return;
        _scale = scale;
        OnPropertyChanged(nameof(IconW));
        OnPropertyChanged(nameof(IconH));
    }

    public void Update(TrayItem item)
    {
        Tooltip = string.IsNullOrEmpty(item.Tooltip) ? (item.OwnerName ?? "") : item.Tooltip;
        // Native size: the item's true on-screen size (bounds are integer points).
        if (item.Bounds is { } b && b.Width >= 1 && b.Height >= 1)
        {
            _nativeW = b.Width;
            _nativeH = b.Height;
            OnPropertyChanged(nameof(IconW));
            OnPropertyChanged(nameof(IconH));
            OnPropertyChanged(nameof(BoxSpan));
        }
        _png = item.IconPng;
        IsLive = item.IsLive;
        Retint();
    }

    // Monotonic retint token: only the latest Process result is applied, so an out-of-order completion
    // (a stale ink/png that finished after a newer one) is discarded rather than clobbering the icon.
    // Read/written on the UI thread only (Retint runs there; the continuation Posts back there).
    private int _retintGen;

    /// <summary>Re-run adaptive tinting for the current ink (macOS template model, see <see cref="TrayIconTint"/>).
    /// The decode + per-pixel scan + recolour runs OFF the UI thread (bevel-dotj: it must never run inline in
    /// a tray ItemUpdated / ink change); only the finished bitmap is marshalled back.</summary>
    private void Retint()
    {
        var gen = ++_retintGen;
        var png = _png;
        var ink = _ink;
        System.Threading.Tasks.Task.Run(() =>
        {
            var img = TrayIconTint.Process(png, ink)?.Image;
            Dispatcher.UIThread.Post(() => Apply(img, gen, png));
        });
    }

    private void Apply(Bitmap? img, int gen, byte[]? png)
    {
        // Superseded by a newer retint while this one was off-thread — drop it (and its unmanaged memory).
        if (gen != _retintGen) { (img as IDisposable)?.Dispose(); return; }
        // A null here is the "blank slot" of bevel-yduf. Say WHY once per icon payload (an ItemUpdated
        // re-delivering the same bytes must not re-log), then let the view draw the placeholder.
        if (img is null && png is { Length: > 0 } && !ReferenceEquals(png, _loggedUndecodable))
        {
            _loggedUndecodable = png;
            TaskbarLog.Info($"tray: icon for '{Tooltip}' ({Id.Value}) did not decode ({png.Length} B PNG) — placeholder shown");
        }
        // Process returns a freshly-decoded, unshared Skia bitmap on every live ItemUpdated and every ink
        // change — dispose the outgoing one or it leaks unmanaged memory per repaint (ce-review; same
        // discipline as the hover-preview and tab-favicon paths).
        var old = IconSource;
        if (ReferenceEquals(old, img)) { HasIcon = img is not null; return; }
        IconSource = img;
        HasIcon = img is not null;
        (old as IDisposable)?.Dispose();
    }
}
