using System.Collections.Generic;
using System.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The notification-area tray view-model (bevel-m3.1): pulls the initial item set from
/// <see cref="ISystemTrayHost"/> and reconciles the add/remove/update events in place (keyed by id),
/// marshalling each onto the UI thread.
/// </summary>
public class TrayViewModelTests
{
    private static TrayItem Item(string id, string tooltip) => new(new TrayItemId(id), tooltip);

    [AvaloniaFact]
    public void Pulls_initial_snapshot_and_reconciles_events_in_place()
    {
        var alpha = Item("1:10", "Alpha");
        var beta = Item("2:20", "Beta");
        var host = new StubTray(alpha, beta);
        var vm = new TrayViewModel(host);

        vm.Start();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, vm.Items.Count);

        // A new item arrives → appended.
        host.RaiseAdded(Item("3:30", "Gamma"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, vm.Items.Count);

        // Update to an existing id → no duplicate, tooltip refreshed.
        host.RaiseUpdated(Item("1:10", "Alpha!"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, vm.Items.Count);
        Assert.Contains(vm.Items, i => i.Tooltip == "Alpha!");

        // Remove → gone.
        host.RaiseRemoved(alpha);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, vm.Items.Count);
        Assert.DoesNotContain(vm.Items, i => i.Id.Value == "1:10");
    }

    [AvaloniaFact]
    public void Null_host_yields_an_empty_tray_without_faulting()
    {
        var vm = new TrayViewModel(null);
        vm.Start();
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.Items);
    }

    private sealed class StubTray : ISystemTrayHost
    {
        private readonly List<TrayItem> _items;
        public StubTray(params TrayItem[] items) => _items = items.ToList();

        public Capabilities Capabilities => Capabilities.None;
        public ValueTask<IReadOnlyList<TrayItem>> GetItemsAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<TrayItem>>(_items.ToArray());
        public Task SetNativeTrayHiddenAsync(bool hidden, CancellationToken ct = default) => Task.CompletedTask;

        public event EventHandler<TrayItem>? ItemAdded;
        public event EventHandler<TrayItem>? ItemRemoved;
        public event EventHandler<TrayItem>? ItemUpdated;

        public void RaiseAdded(TrayItem i) => ItemAdded?.Invoke(this, i);
        public void RaiseRemoved(TrayItem i) => ItemRemoved?.Invoke(this, i);
        public void RaiseUpdated(TrayItem i) => ItemUpdated?.Invoke(this, i);
    }
}
