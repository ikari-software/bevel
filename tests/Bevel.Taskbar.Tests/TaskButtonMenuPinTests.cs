using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-be86: the task-button context menu's Pin/Unpin row. It is keyed on the bundle id (so a
/// window button, an app group, and a windowless pinned button all get the same verb), shows
/// "_Pin to taskbar" / "Un_pin from taskbar" per the pinned state the caller reports, invokes the
/// toggle hook with exactly that bundle id, and is omitted when there is no bundle id to key on or
/// no hooks were wired (never a dead verb).
/// </summary>
public class TaskButtonMenuPinTests
{
    private sealed class NullWm : IWindowManager
    {
        public Capabilities Capabilities { get; } = new(Available: true, TrayMode: TrayCapability.Mirrored, Notes: Array.Empty<string>());
        public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(System.Threading.CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<ForeignWindow>>(Array.Empty<ForeignWindow>());
        public Task ActivateAsync(ForeignWindowId id, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task MinimizeAsync(ForeignWindowId id, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAsync(ForeignWindowId id, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAndActivateAsync(ForeignWindowId id, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync(ForeignWindowId id, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, PalRect? expected = null, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public event EventHandler<ForeignWindow>? WindowOpened;
        public event EventHandler<ForeignWindow>? WindowClosed;
        public event EventHandler<ForeignWindow>? WindowChanged;
        public event EventHandler<ForeignWindow>? ForegroundChanged;
    }

    private static TaskItemViewModel Vm(string? bundleId, string id = "w1") =>
        new(new ForeignWindow(new ForeignWindowId(id), "Fake App", "Fake", false, false, default,
            BundleId: bundleId), new NullWm());

    private static MenuItem? PinItem(MenuFlyout? flyout) =>
        (flyout?.ItemsSource as IEnumerable<object>)?.OfType<MenuItem>()
            .FirstOrDefault(m => (m.Header as string) is "_Pin to taskbar" or "Un_pin from taskbar");

    [AvaloniaFact]
    public void Pin_item_appears_and_invokes_the_toggle_with_the_exact_bundle_id()
    {
        string? toggled = null;
        var flyout = TaskButtonMenu.Build(Vm("fake.app"), null, null,
            isPinned: _ => false, onTogglePin: b => toggled = b);

        var item = PinItem(flyout);
        Assert.NotNull(item);
        Assert.Equal("_Pin to taskbar", item!.Header as string);

        item.Command!.Execute(null);
        Assert.Equal("fake.app", toggled);
    }

    [AvaloniaFact]
    public void Pinned_app_shows_the_unpin_label()
    {
        var flyout = TaskButtonMenu.Build(Vm("fake.app"), null, null,
            isPinned: b => b == "fake.app", onTogglePin: _ => { });

        Assert.Equal("Un_pin from taskbar", PinItem(flyout)?.Header as string);
    }

    [AvaloniaFact]
    public void Item_is_omitted_when_the_window_has_no_bundle_id()
    {
        // Nothing stable to key the pin on → no row, never a pin that can't persist.
        var flyout = TaskButtonMenu.Build(Vm(null), null, null,
            isPinned: _ => false, onTogglePin: _ => { });
        Assert.Null(PinItem(flyout));
    }

    [AvaloniaFact]
    public void Item_is_omitted_when_no_hooks_are_wired()
    {
        // A hook-less caller must not get a clickable item that no-ops on click.
        var flyout = TaskButtonMenu.Build(Vm("fake.app"), null, null);
        Assert.Null(PinItem(flyout));
    }

    [AvaloniaFact]
    public void Group_button_gets_the_same_row_keyed_on_its_bundle_id()
    {
        var group = new TaskGroupViewModel("Fake");
        group.SyncChildren(new[]
        {
            Vm("fake.app", "w1"),
            Vm("fake.app", "w2"),
        });

        string? toggled = null;
        var flyout = TaskButtonMenu.Build(group, null, null,
            isPinned: _ => true, onTogglePin: b => toggled = b);

        var item = PinItem(flyout);
        Assert.NotNull(item);
        Assert.Equal("Un_pin from taskbar", item!.Header as string);

        item.Command!.Execute(null);
        Assert.Equal("fake.app", toggled);
    }

    [AvaloniaFact]
    public void Windowless_pinned_button_offers_unpin()
    {
        // A pinned app with no window is itself a TaskItemViewModel (app-presence shape) — its menu
        // must offer Unpin, not Pin.
        var pinned = new TaskItemViewModel(
            new ForeignWindow(new ForeignWindowId("app:fake.app"), "Fake", "Fake", false, false, default,
                IsAppPresence: true, BundleId: "fake.app"), new NullWm());

        var flyout = TaskButtonMenu.Build(pinned, null, null,
            isPinned: b => b == "fake.app", onTogglePin: _ => { });

        Assert.Equal("Un_pin from taskbar", PinItem(flyout)?.Header as string);
    }
}
