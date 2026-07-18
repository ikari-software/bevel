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

    [AvaloniaFact]
    public async Task Forward_passes_the_item_button_and_modifiers_to_the_host()
    {
        var host = new StubTray(Item("9:90", "Clock"));
        var vm = new TrayViewModel(host);

        var ok = await vm.Forward(new TrayItemId("9:90"), TrayButton.Right,
            TrayModifiers.Option | TrayModifiers.Command);

        Assert.True(ok);
        Assert.Equal("9:90", host.LastForward?.Id.Value);
        Assert.Equal(TrayButton.Right, host.LastForward?.Button);
        Assert.Equal(TrayModifiers.Option | TrayModifiers.Command, host.LastForward?.Modifiers);
    }

    [AvaloniaFact]
    public async Task Forward_with_null_host_is_a_safe_false()
        => Assert.False(await new TrayViewModel(null).Forward(new TrayItemId("x"), TrayButton.Left, TrayModifiers.None));

    [AvaloniaFact]
    public void Overflow_caps_the_visible_strip_and_the_flyout_holds_the_rest()
    {
        var host = new StubTray(Enumerable.Range(0, 10).Select(i => Item($"{i}:{i}0", $"T{i}")).ToArray());
        var vm = new TrayViewModel(host);
        vm.Start();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(10, vm.Items.Count);
        Assert.Equal(TrayViewModel.DefaultVisibleCap, vm.VisibleItems.Count);
        Assert.Equal(10 - TrayViewModel.DefaultVisibleCap, vm.OverflowItems.Count);
        Assert.True(vm.HasOverflow);
    }

    [AvaloniaFact]
    public async Task Using_an_overflowed_item_promotes_it_into_the_visible_set()
    {
        var host = new StubTray(Enumerable.Range(0, 10).Select(i => Item($"{i}:{i}0", $"T{i}")).ToArray());
        var vm = new TrayViewModel(host);
        vm.Start();
        Dispatcher.UIThread.RunJobs();

        var overflowId = vm.OverflowItems.Last().Id;
        Assert.DoesNotContain(vm.VisibleItems, i => i.Id.Equals(overflowId));

        await vm.Forward(overflowId, TrayButton.Left, TrayModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(vm.VisibleItems, i => i.Id.Equals(overflowId));      // promoted in
        Assert.Equal(TrayViewModel.DefaultVisibleCap, vm.VisibleItems.Count);       // still capped
    }

    [AvaloniaFact]
    public void Configure_changes_the_overflow_cap_and_icon_size_live()
    {
        var host = new StubTray(Enumerable.Range(0, 10).Select(i => Item($"{i}:{i}0", $"T{i}")).ToArray());
        var vm = new TrayViewModel(host);
        vm.Start();
        Dispatcher.UIThread.RunJobs();

        vm.Configure(overflowCap: 3, iconSize: 24);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, vm.VisibleItems.Count);
        Assert.Equal(7, vm.OverflowItems.Count);
        Assert.All(vm.VisibleItems, i => Assert.Equal(24, i.IconSize));
    }

    private sealed class StubTray : ISystemTrayHost
    {
        private readonly List<TrayItem> _items;
        public StubTray(params TrayItem[] items) => _items = items.ToList();

        public (TrayItemId Id, TrayButton Button, TrayModifiers Modifiers)? LastForward { get; private set; }

        public Capabilities Capabilities => Capabilities.None;
        public ValueTask<IReadOnlyList<TrayItem>> GetItemsAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<TrayItem>>(_items.ToArray());
        public Task SetNativeTrayHiddenAsync(bool hidden, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ForwardClickAsync(TrayItemId id, TrayButton button, TrayModifiers modifiers, CancellationToken ct = default)
        {
            LastForward = (id, button, modifiers);
            return Task.FromResult(true);
        }

        public event EventHandler<TrayItem>? ItemAdded;
        public event EventHandler<TrayItem>? ItemRemoved;
        public event EventHandler<TrayItem>? ItemUpdated;

        public void RaiseAdded(TrayItem i) => ItemAdded?.Invoke(this, i);
        public void RaiseRemoved(TrayItem i) => ItemRemoved?.Invoke(this, i);
        public void RaiseUpdated(TrayItem i) => ItemUpdated?.Invoke(this, i);
    }
}
