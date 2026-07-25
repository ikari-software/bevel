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

    /// <summary>Default inline tray-icon count before overflow (bevel-m3.4); user-overridable
    /// live via <see cref="VisibleCap"/> / <see cref="Configure"/> (bevel-cust.tray).</summary>
    public const int DefaultVisibleCap = 8;

    // Tray tuning bounds (bevel-cust.tray). Keep in sync with OnboardingWindow.axaml's TrayCapSlider /
    // TrayIconSizeSlider Minimum/Maximum — the domain clamp and the UI slider must not drift apart.
    public const int MinOverflowCap = 1;
    public const int MaxOverflowCap = 20;
    public const int MinIconSize = 12;
    public const int MaxIconSize = 32;

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

    /// <summary>Effective inline capacity = the PER-ROW cap × the taskbar row count — a taller bar shows
    /// proportionally more icons before the overflow chevron.</summary>
    private int EffectiveCap => Math.Max(1, VisibleCap * Math.Max(1, _rows));

    /// <summary>Track the taskbar row count (bevel-m3). Reslices when it changes.</summary>
    public void SetRows(int rows)
    {
        rows = Math.Max(1, rows);
        if (_rows == rows) return;
        Rows = rows;
        Reslice();
    }

    /// <summary>Applies the user's tray tuning live (bevel-cust.tray): inline overflow cap + icon size.</summary>
    public void Configure(int overflowCap, int iconSize)
    {
        _iconSize = Math.Clamp(iconSize, MinIconSize, MaxIconSize);
        foreach (var it in Items) it.IconSize = _iconSize;
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
        return await (_tray?.ForwardClickAsync(id, button, modifiers) ?? Task.FromResult(false));
    }

    /// <summary>Moves an item into the last inline slot if it's currently overflowed — a used item
    /// earns its place in the visible strip without reshuffling the others.</summary>
    private void PromoteToVisible(TrayItemId id)
    {
        var index = -1;
        for (var i = 0; i < Items.Count; i++)
            if (Items[i].Id.Equals(id)) { index = i; break; }
        if (index >= EffectiveCap)
        {
            Items.Move(index, EffectiveCap - 1);
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
        Items.Add(new TrayItemViewModel(item) { IconSize = _iconSize, Ink = _ink });
        Reslice();
    }

    private void Remove(TrayItemId id)
    {
        var existing = Items.FirstOrDefault(i => i.Id.Equals(id));
        if (existing is null) return;
        Items.Remove(existing);
        Reslice();
    }

    /// <summary>Reconciles <see cref="VisibleItems"/> / <see cref="OverflowItems"/> to the first-N /
    /// rest of <see cref="Items"/> in place (shared VM instances, so bindings/icons survive).</summary>
    private void Reslice()
    {
        SyncTo(VisibleItems, Items.Take(EffectiveCap));
        SyncTo(OverflowItems, Items.Skip(EffectiveCap));
        HasOverflow = Items.Count > EffectiveCap;
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

    private byte[]? _png;
    private Color _ink = Colors.White;

    /// <summary>The bar's contrast ink (Bevel.Brush.TrayText). Template glyphs recolour to it; changing it
    /// (theme/variant switch) re-tints in place.</summary>
    public Color Ink { get => _ink; set { if (_ink != value) { _ink = value; Retint(); } } }

    private double _iconSize = 16;
    /// <summary>Target icon-content edge length (px), driven by the tray icon-size setting (bevel-cust.tray).</summary>
    public double IconSize
    {
        get => _iconSize;
        set { if (SetProperty(ref _iconSize, value)) OnPropertyChanged(nameof(CellHeight)); }
    }

    /// <summary>Height the tray Image is rendered at. The captured PNG is the whole menu-bar CELL (the
    /// glyph/text padded inside it, as the bar draws it — see TrayServiceImpl.pngFromCGImage), so it must
    /// render TALLER than the icon content: a typical glyph is ~0.6 of its cell, so rendering at ~1.7×
    /// IconSize lands the icon content at IconSize (matching the macOS menu bar) while text stays small
    /// with its padding. Clamped to the taskbar height so it can't overflow the bar.</summary>
    public double CellHeight => Math.Min(30.0, _iconSize * 1.7);

    public void Update(TrayItem item)
    {
        Tooltip = string.IsNullOrEmpty(item.Tooltip) ? (item.OwnerName ?? "") : item.Tooltip;
        _png = item.IconPng;
        Retint();
    }

    /// <summary>Re-run adaptive tinting for the current ink (macOS template model, see <see cref="TrayIconTint"/>).</summary>
    private void Retint() => IconSource = TrayIconTint.Process(_png, _ink)?.Image;
}
