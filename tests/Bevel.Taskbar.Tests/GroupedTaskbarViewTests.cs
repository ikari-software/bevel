using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Core;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// View-level coverage for XP window grouping (bevel-m2.10.3): with grouping on, the strip realizes
/// one grouped button (bound to a <see cref="TaskGroupViewModel"/>) per multi-window app plus normal
/// buttons for single windows, and the type-based template selection picks the group template. With
/// grouping off, the strip is unchanged — window buttons only.
/// </summary>
public class GroupedTaskbarViewTests
{
    private static TaskbarView BuildView(bool grouping, params ForeignWindow[] windows)
    {
        var model = new ShellModel(null, null, null);
        var vm = new TaskbarViewModel(model, new StartMenuViewModel(model));
        var wm = new GroupViewStubWm();
        var view = new TaskbarView { DataContext = vm };
        view.Initialize(null, null, buttonWidth: 150,
            grouping: grouping ? TaskbarGroupingMode.Always : TaskbarGroupingMode.Never);
        var window = new TaskbarWindow(null, rows: 1) { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        foreach (var w in windows)
            model.Windows.Add(new TaskItemViewModel(w, wm) { Width = 150, Opacity = 1 });
        Dispatcher.UIThread.RunJobs();
        return view;
    }

    private static ForeignWindow W(string id, string title, string? appId, bool focused = false)
        => new(new ForeignWindowId(id), title, appId, false, focused, default);

    [AvaloniaFact]
    public void Grouping_on_collapses_an_apps_windows_into_one_group_button()
    {
        var view = BuildView(grouping: true,
            W("c1", "Gmail", "com.google.Chrome", focused: true),
            W("c2", "Docs", "com.google.Chrome"),
            W("c3", "News", "com.google.Chrome"),
            W("f1", "Downloads", "com.apple.finder"));

        var vm = (TaskbarViewModel)view.DataContext!;

        // Items: [Chrome group(3), Finder single].
        Assert.Equal(2, vm.Items.Count);
        var group = Assert.IsType<TaskGroupViewModel>(vm.Items[0]);
        Assert.Equal("Chrome (3)", group.Label);
        Assert.Equal(3, group.Count);
        Assert.True(group.IsFocused);   // a child (Gmail) is focused → group shows active
        Assert.IsType<TaskItemViewModel>(vm.Items[1]);

        // The strip realized a ToggleButton bound to the group VM (the group template was selected).
        var groupButton = view.WindowButtonAreaControl.GetRealizedContainers()
            .Select(FindToggle)
            .FirstOrDefault(b => b?.DataContext is TaskGroupViewModel);
        Assert.NotNull(groupButton);
        Assert.NotNull(groupButton!.Flyout);   // the window-list flyout is attached

        // The group template actually rendered its caption — proves content (not just the container)
        // is present. (Its on-screen width can be 0 mid-transition under the frozen headless clock,
        // so we assert the visual content, not pixels.)
        var caption = groupButton.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Text == "Chrome (3)");
        Assert.NotNull(caption);
    }

    [AvaloniaFact]
    public void Grouping_off_shows_one_button_per_window()
    {
        var view = BuildView(grouping: false,
            W("c1", "Gmail", "com.google.Chrome"),
            W("c2", "Docs", "com.google.Chrome"));

        var vm = (TaskbarViewModel)view.DataContext!;
        Assert.Equal(2, vm.Items.Count);
        Assert.All(vm.Items, i => Assert.IsType<TaskItemViewModel>(i));

        // No group containers realized.
        var anyGroup = view.WindowButtonAreaControl.GetRealizedContainers()
            .Select(FindToggle).Any(b => b?.DataContext is TaskGroupViewModel);
        Assert.False(anyGroup);
    }

    private static ToggleButton? FindToggle(Control c)
        => c as ToggleButton ?? c.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault();

    private sealed class GroupViewStubWm : IWindowManager
    {
        public Capabilities Capabilities { get; } = new(true, TrayCapability.Mirrored, Array.Empty<string>(), true);
        public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<ForeignWindow>>(Array.Empty<ForeignWindow>());
        public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, CancellationToken ct = default) => Task.CompletedTask;
        public event EventHandler<ForeignWindow>? WindowOpened { add { } remove { } }
        public event EventHandler<ForeignWindow>? WindowClosed { add { } remove { } }
        public event EventHandler<ForeignWindow>? WindowChanged { add { } remove { } }
        public event EventHandler<ForeignWindow>? ForegroundChanged { add { } remove { } }
    }
}
