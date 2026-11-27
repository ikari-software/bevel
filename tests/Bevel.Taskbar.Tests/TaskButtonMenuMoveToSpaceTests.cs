using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The per-window "Move to _Space" submenu in the task-button context menu (bevel-44vg, dev-flagged
/// at the call site): present only when the caller supplies a Space list (fetched before the menu
/// opens), one row per Space with the current Space disabled, and the command reaching
/// IWindowManager.MoveWindowToSpaceAsync with the exact window id + Space id.
/// </summary>
public class TaskButtonMenuMoveToSpaceTests
{
    private sealed class RecordingWm : IWindowManager
    {
        public List<(ForeignWindowId Window, ulong Space)> Moves { get; } = [];
        public Capabilities Capabilities { get; } = new(Available: true, TrayMode: TrayCapability.Mirrored, Notes: Array.Empty<string>());
        public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<ForeignWindow>>(Array.Empty<ForeignWindow>());
        public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, PalRect? expected = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveWindowToSpaceAsync(ForeignWindowId id, ulong spaceId, CancellationToken ct = default)
        {
            lock (Moves) Moves.Add((id, spaceId));
            return Task.CompletedTask;
        }
        public event EventHandler<ForeignWindow>? WindowOpened;
        public event EventHandler<ForeignWindow>? WindowClosed;
        public event EventHandler<ForeignWindow>? WindowChanged;
        public event EventHandler<ForeignWindow>? ForegroundChanged;
    }

    private static readonly IReadOnlyList<TaskButtonMenu.SpaceMenuRow> Spaces =
    [
        new(100, "Desktop 1", true),
        new(250, "Desktop 2", false),
    ];

    private static (TaskItemViewModel Vm, RecordingWm Wm) SingleWindowVm()
    {
        var wm = new RecordingWm();
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

    private static MenuItem? MoveToSpaceSubmenu(MenuItem windowRow) =>
        windowRow.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string) == "Move to _Space");

    [AvaloniaFact]
    public void No_submenu_when_no_spaces_supplied()
    {
        var (vm, _) = SingleWindowVm();
        var flyout = TaskButtonMenu.Build(vm, tabs: null, tabProvider: null);

        var row = Assert.Single(WindowRows(flyout));
        var headers = row.Items.OfType<MenuItem>().Select(m => m.Header as string).ToList();
        Assert.Equal(new[] { "_Activate", "Mi_nimize", "_Restore", "Ma_ximize", "_Close" }, headers);
        Assert.Null(MoveToSpaceSubmenu(row));
    }

    [AvaloniaFact]
    public void Submenu_lists_every_space_after_close_with_current_disabled()
    {
        var (vm, _) = SingleWindowVm();
        var flyout = TaskButtonMenu.Build(vm, tabs: null, tabProvider: null, spaces: Spaces);

        var row = Assert.Single(WindowRows(flyout));
        var headers = row.Items.OfType<MenuItem>().Select(m => m.Header as string).ToList();
        Assert.Equal(new[] { "_Activate", "Mi_nimize", "_Restore", "Ma_ximize", "_Close", "Move to _Space" }, headers);

        var submenu = MoveToSpaceSubmenu(row)!;
        var rows = submenu.Items.OfType<MenuItem>().ToList();
        Assert.Equal(new[] { "Desktop 1", "Desktop 2" }, rows.Select(m => m.Header as string).ToList());
        Assert.False(rows[0].IsEnabled);   // current Space: moving there is a no-op
        Assert.True(rows[1].IsEnabled);
    }

    [AvaloniaFact]
    public void Space_command_calls_MoveWindowToSpaceAsync_with_window_and_space_id()
    {
        var (vm, wm) = SingleWindowVm();
        var flyout = TaskButtonMenu.Build(vm, tabs: null, tabProvider: null, spaces: Spaces);

        var row = Assert.Single(WindowRows(flyout));
        MoveToSpaceSubmenu(row)!.Items.OfType<MenuItem>()
            .Single(m => (m.Header as string) == "Desktop 2")
            .Command!.Execute(null);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (wm.Moves.Count == 0 && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Assert.Equal(new[] { (new ForeignWindowId("w1"), 250UL) }, wm.Moves);
    }

    [AvaloniaFact]
    public void Group_menu_window_rows_carry_the_submenu_too()
    {
        var wm = new RecordingWm();
        var group = new TaskGroupViewModel("fake.app");
        group.Windows.Add(new TaskItemViewModel(
            new ForeignWindow(new ForeignWindowId("w1"), "One", "fake.app", false, false,
                new PalRect(0, 0, 800, 600), BundleId: "fake.app"), wm));
        group.Windows.Add(new TaskItemViewModel(
            new ForeignWindow(new ForeignWindowId("w2"), "Two", "fake.app", false, false,
                new PalRect(50, 50, 800, 600), BundleId: "fake.app"), wm));

        var flyout = TaskButtonMenu.Build(group, tabs: null, tabProvider: null, spaces: Spaces);
        var rows = WindowRows(flyout);
        Assert.Equal(2, rows.Count);
        foreach (var row in rows) Assert.NotNull(MoveToSpaceSubmenu(row));

        MoveToSpaceSubmenu(rows[1])!.Items.OfType<MenuItem>()
            .Single(m => (m.Header as string) == "Desktop 2")
            .Command!.Execute(null);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (wm.Moves.Count == 0 && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Assert.Equal(new[] { (new ForeignWindowId("w2"), 250UL) }, wm.Moves);
    }
}
