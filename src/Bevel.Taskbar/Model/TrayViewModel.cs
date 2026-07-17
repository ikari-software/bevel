using System;
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

    /// <summary>The mirrored status items, in host order (left-to-right menu-bar order on macOS).</summary>
    public ObservableCollection<TrayItemViewModel> Items { get; } = new();

    /// <summary>Forwards a click on a mirrored item to the real status item (spec §5.5).</summary>
    public Task<bool> Forward(TrayItemId id, TrayButton button, TrayModifiers modifiers)
        => _tray?.ForwardClickAsync(id, button, modifiers) ?? Task.FromResult(false);

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
        if (existing is not null) existing.Update(item);
        else Items.Add(new TrayItemViewModel(item));
    }

    private void Remove(TrayItemId id)
    {
        var existing = Items.FirstOrDefault(i => i.Id.Equals(id));
        if (existing is not null) Items.Remove(existing);
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
