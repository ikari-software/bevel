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
/// bevel-4nct: the task-button context menu's "Show in Finder" verb. It appears only when the
/// window carried an app bundle path (<see cref="ForeignWindow.BundlePath"/>), sits directly above
/// Quit, and invokes the caller's reveal hook with exactly that path. A group button takes the
/// first member's path, and <see cref="TaskItemViewModel.Update"/> keeps the path current.
/// </summary>
public class TaskButtonMenuShowInFinderTests
{
    private sealed class NullWm : IWindowManager
    {
        public Capabilities Capabilities { get; } = new(Available: true, TrayMode: TrayCapability.Mirrored, Notes: Array.Empty<string>());
        public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(System.Threading.CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<ForeignWindow>>(Array.Empty<ForeignWindow>());
        public Task ActivateAsync(ForeignWindowId id, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task MinimizeAsync(ForeignWindowId id, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAsync(ForeignWindowId id, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync(ForeignWindowId id, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, PalRect? expected = null, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public event EventHandler<ForeignWindow>? WindowOpened;
        public event EventHandler<ForeignWindow>? WindowClosed;
        public event EventHandler<ForeignWindow>? WindowChanged;
        public event EventHandler<ForeignWindow>? ForegroundChanged;
    }

    private static TaskItemViewModel Vm(string? bundlePath, string id = "w1") =>
        new(new ForeignWindow(new ForeignWindowId(id), "Fake App", "fake.app", false, false, default,
            BundleId: "fake.app", BundlePath: bundlePath), new NullWm());

    private static MenuItem? ShowInFinderItem(MenuFlyout? flyout) =>
        (flyout?.ItemsSource as IEnumerable<object>)?.OfType<MenuItem>()
            .FirstOrDefault(m => (m.Header as string) == "Show in _Finder");

    [AvaloniaFact]
    public void Item_appears_when_bundle_path_known_and_invokes_hook_with_exact_path()
    {
        string? revealed = null;
        var flyout = TaskButtonMenu.Build(Vm("/Applications/Fake.app"), null, null,
            onShowInFinder: p => revealed = p);

        var item = ShowInFinderItem(flyout);
        Assert.NotNull(item);

        // It sits directly above the Quit item (the last two entries of the menu).
        var headers = (flyout!.ItemsSource as IEnumerable<object>)
            .OfType<MenuItem>().Select(m => m.Header as string).ToList();
        Assert.Equal("_Quit", headers[headers.IndexOf("Show in _Finder") + 1]);

        item!.Command!.Execute(null);
        Assert.Equal("/Applications/Fake.app", revealed);
    }

    [AvaloniaFact]
    public void Item_omitted_when_bundle_path_unknown()
    {
        // No path on the window → no verb: never guessed from the bundle id alone.
        var flyout = TaskButtonMenu.Build(Vm(null), null, null, onShowInFinder: _ => { });
        Assert.Null(ShowInFinderItem(flyout));
    }

    [AvaloniaFact]
    public void Group_menu_uses_first_member_with_a_path()
    {
        var g = new TaskGroupViewModel("fake.app");
        g.Windows.Add(Vm(null, "w1"));
        g.Windows.Add(Vm("/Applications/Fake.app", "w2"));

        string? revealed = null;
        var flyout = TaskButtonMenu.Build(g, null, null, onShowInFinder: p => revealed = p);

        ShowInFinderItem(flyout)!.Command!.Execute(null);
        Assert.Equal("/Applications/Fake.app", revealed);
    }

    [AvaloniaFact]
    public void Update_refreshes_bundle_path_on_the_live_vm()
    {
        var vm = Vm("/Applications/Fake.app");
        Assert.Equal("/Applications/Fake.app", vm.BundlePath);

        // A later snapshot that lost the path clears the verb's data; one that gains it sets it.
        vm.Update(new ForeignWindow(new ForeignWindowId("w1"), "Fake App", "fake.app", false, false, default,
            BundleId: "fake.app"));
        Assert.Null(vm.BundlePath);

        vm.Update(new ForeignWindow(new ForeignWindowId("w1"), "Fake App", "fake.app", false, false, default,
            BundleId: "fake.app", BundlePath: "/Applications/Other.app"));
        Assert.Equal("/Applications/Other.app", vm.BundlePath);
    }
}
