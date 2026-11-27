using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bevel.Pal.Abstractions;
using Bevel.TestSupport;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-4nct: the task-button context menu's "Show in Finder" verb. It appears only when the
/// window carried an app bundle path (<see cref="ForeignWindow.BundlePath"/>) AND the caller wired
/// a reveal hook, sits directly above Quit, and invokes the hook with exactly that path. A group
/// button takes the first member's path, and <see cref="TaskItemViewModel.Update"/> keeps the path
/// current.
/// </summary>
public class TaskButtonMenuShowInFinderTests
{
    private static TaskItemViewModel Vm(string? bundlePath, string id = "w1") =>
        new(new ForeignWindow(new ForeignWindowId(id), "Fake App", "fake.app", false, false, default,
            BundleId: "fake.app", BundlePath: bundlePath), new TestWindowManager());

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

        // It sits in the bottom app-verb section, above Quit. bevel-d2j9's _Hide row now sits
        // directly above the Quit separator (macOS app-menu order), so the immediate successor is
        // "_Hide"; on a menu without it the successor is the Quit entry. Holding Option while the
        // menu opens on macOS relabels that entry "_Force Quit", so accept either.
        var headers = (flyout!.ItemsSource as IEnumerable<object>)
            .OfType<MenuItem>().Select(m => m.Header as string).ToList();
        Assert.Contains(headers[headers.IndexOf("Show in _Finder") + 1], new[] { "_Hide", "_Quit", "_Force Quit" });

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
    public void Item_omitted_when_no_reveal_hook_wired()
    {
        // A hook-less caller must not get a clickable item that no-ops on click.
        var flyout = TaskButtonMenu.Build(Vm("/Applications/Fake.app"), null, null);
        Assert.Null(ShowInFinderItem(flyout));
    }

    [AvaloniaFact]
    public void Group_menu_uses_first_member_with_a_path()
    {
        var g = new TaskGroupViewModel("fake.app");
        // The earlier member carries a DIFFERENT path, so this pins FirstOrDefault, not LastOrDefault.
        g.Windows.Add(Vm("/Applications/First.app", "w0"));
        g.Windows.Add(Vm(null, "w1"));
        g.Windows.Add(Vm("/Applications/Fake.app", "w2"));

        string? revealed = null;
        var flyout = TaskButtonMenu.Build(g, null, null, onShowInFinder: p => revealed = p);

        ShowInFinderItem(flyout)!.Command!.Execute(null);
        Assert.Equal("/Applications/First.app", revealed);
    }

    [AvaloniaFact]
    public void Group_menu_omits_item_when_no_member_has_a_path()
    {
        var g = new TaskGroupViewModel("fake.app");
        g.Windows.Add(Vm(null, "w1"));
        g.Windows.Add(Vm(null, "w2"));

        var flyout = TaskButtonMenu.Build(g, null, null, onShowInFinder: _ => { });
        Assert.Null(ShowInFinderItem(flyout));
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
