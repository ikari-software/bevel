using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bevel.Pal.Abstractions;
using Bevel.TestSupport;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The per-window "Ma_ximize" verb in the task-button context menu (bevel-kpkx): the Windows
/// submenu row order (Activate / Minimize / Restore / Maximize / Close) and the menu command
/// reaching IWindowManager.ZoomAsync with the exact window id.
/// </summary>
public class TaskButtonMenuZoomTests
{
    private static (TaskItemViewModel Vm, TestWindowManager Wm) SingleWindowVm()
    {
        var wm = new TestWindowManager();
        var vm = new TaskItemViewModel(
            new ForeignWindow(new ForeignWindowId("w1"), "Doc", "fake.app", false, false,
                new PalRect(100, 100, 800, 600), BundleId: "fake.app"),
            wm);
        return (vm, wm);
    }

    private static MenuItem? WindowsMenu(MenuFlyout? flyout) =>
        (flyout?.ItemsSource as IEnumerable<object>)?.OfType<MenuItem>()
            .FirstOrDefault(m => (m.Header as string) == "_Windows");

    private static List<MenuItem> WindowRows(MenuFlyout? flyout) =>
        WindowsMenu(flyout)!.Items.OfType<MenuItem>().ToList();

    [AvaloniaFact]
    public void Per_window_submenu_has_maximize_between_restore_and_close()
    {
        var (vm, _) = SingleWindowVm();
        var flyout = TaskButtonMenu.Build(vm, tabs: null, tabProvider: null);

        var row = Assert.Single(WindowRows(flyout));
        var headers = row.Items.OfType<MenuItem>().Select(m => m.Header as string).ToList();
        Assert.Equal(new[] { "_Activate", "Mi_nimize", "_Restore", "Ma_ximize", "_Close" }, headers);
    }

    [AvaloniaFact]
    public void Maximize_command_calls_ZoomAsync_with_the_window_id()
    {
        var (vm, wm) = SingleWindowVm();
        var flyout = TaskButtonMenu.Build(vm, tabs: null, tabProvider: null);

        var maximize = WindowRows(flyout).Single().Items.OfType<MenuItem>()
            .Single(m => (m.Header as string) == "Ma_ximize");
        maximize.Command!.Execute(null);

        // AsyncRelayCommand.Execute is async-void, but the handler records synchronously before its
        // first await; poll briefly anyway so the assertion cannot race a threadpool hop.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (wm.Zooms.Count == 0 && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Assert.Equal(new[] { new ForeignWindowId("w1") }, wm.Zooms);
    }

    [AvaloniaFact]
    public void Group_menu_window_rows_carry_the_maximize_verb_too()
    {
        var wm = new TestWindowManager();
        var group = new TaskGroupViewModel("fake.app");
        group.Windows.Add(new TaskItemViewModel(
            new ForeignWindow(new ForeignWindowId("w1"), "One", "fake.app", false, false,
                new PalRect(0, 0, 800, 600), BundleId: "fake.app"), wm));
        group.Windows.Add(new TaskItemViewModel(
            new ForeignWindow(new ForeignWindowId("w2"), "Two", "fake.app", false, false,
                new PalRect(50, 50, 800, 600), BundleId: "fake.app"), wm));

        var flyout = TaskButtonMenu.Build(group, tabs: null, tabProvider: null);
        var rows = WindowRows(flyout);
        Assert.Equal(2, rows.Count);
        foreach (var row in rows)
        {
            var headers = row.Items.OfType<MenuItem>().Select(m => m.Header as string).ToList();
            Assert.Equal(new[] { "_Activate", "Mi_nimize", "_Restore", "Ma_ximize", "_Close" }, headers);
        }

        rows[1].Items.OfType<MenuItem>().Single(m => (m.Header as string) == "Ma_ximize").Command!.Execute(null);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (wm.Zooms.Count == 0 && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Assert.Equal(new[] { new ForeignWindowId("w2") }, wm.Zooms);
    }
}
