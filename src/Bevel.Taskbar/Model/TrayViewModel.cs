using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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

    /// <summary>Inline tray-icon count before the overflow chevron. Set live via <see cref="Configure"/>.</summary>
    public int VisibleCap { get; private set; } = DefaultVisibleCap;

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
        if (index >= VisibleCap)
        {
            Items.Move(index, VisibleCap - 1);
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
        Items.Add(new TrayItemViewModel(item) { IconSize = _iconSize });
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
        SyncTo(VisibleItems, Items.Take(VisibleCap));
        SyncTo(OverflowItems, Items.Skip(VisibleCap));
        HasOverflow = Items.Count > VisibleCap;
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

    private double _iconSize = 16;
    /// <summary>Displayed icon edge length (px), driven by the tray icon-size setting (bevel-cust.tray).</summary>
    public double IconSize { get => _iconSize; set => SetProperty(ref _iconSize, value); }

    public void Update(TrayItem item)
    {
        Tooltip = string.IsNullOrEmpty(item.Tooltip) ? (item.OwnerName ?? "") : item.Tooltip;
        IconSource = Decode(item.IconPng);
    }

    private static Bitmap? Decode(byte[]? png)
    {
        if (png is null || png.Length == 0) return null;
        try { using var ms = new MemoryStream(png); return new Bitmap(ms); }
        catch { return null; }
    }
}
