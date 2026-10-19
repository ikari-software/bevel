using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Core;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-zk4a live repro: the user's bar runs Luna + <c>taskbarGrouping=Always</c> + focus flowing
/// through <see cref="TaskItemViewModel.SetFocused"/> AFTER the group is built — and the active
/// app's button does NOT render pressed. The earlier item-button render test proved the ITEM path
/// and the checked THEME work; this test walks the GROUPED path with the live focus ordering and
/// pins down which link (group VM → IsChecked binding → checked template) breaks.
/// </summary>
[Collection("TaskbarTheme")] // serialize theme-mutating tests: they share Application.Current (bevel-hd05)
public class RenderGroupedFocusTest
{
    [AvaloniaFact]
    public void Grouped_focused_button_renders_checked()
    {
        try
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Blue2001);
            var model = new ShellModel(null, null, null, usage: TestUsage.Scratch());
            var vm = new TaskbarViewModel(model, new StartMenuViewModel(model));
            var wm = new StubWindowManager();
            var view = new TaskbarView { DataContext = vm };
            var window = new TaskbarWindow(null, rows: 1) { Content = view, Width = 900 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // The VIEW owns applying the grouping setting to the VM (TaskbarView line ~253/424) — the
            // test has no settings, so it resets grouping to Never; apply Always AFTER the view exists,
            // exactly as the live settings poll does.
            vm.SetGrouping(TaskbarGroupingMode.Always);
            Dispatcher.UIThread.RunJobs();

            // Two windows of the SAME app (they glom into one group) + one window of another app.
            model.Windows.Add(new TaskItemViewModel(new ForeignWindow(new ForeignWindowId("w1"), "Shell", "WezTerm", false, false, default), wm) { Width = 150, Opacity = 1 });
            model.Windows.Add(new TaskItemViewModel(new ForeignWindow(new ForeignWindowId("w2"), "Editor", "WezTerm", false, false, default), wm) { Width = 150, Opacity = 1 });
            model.Windows.Add(new TaskItemViewModel(new ForeignWindow(new ForeignWindowId("w3"), "Docs", "Other", false, false, default), wm) { Width = 150, Opacity = 1 });
            Dispatcher.UIThread.RunJobs();

            // The LIVE ordering: focus arrives AFTER the group exists (ShellModel.ApplyExclusiveFocus
            // writes the child, the group forwards).
            model.Windows[1].SetFocused(true);
            Dispatcher.UIThread.RunJobs();

            // Link 1 — the group VM's computed IsFocused actually forwards.
            var kinds = string.Join(",", vm.Items.Select(i => i is TaskGroupViewModel g
                ? $"group:{g.AppId}/{g.DisplayName}x{g.Count}" : $"item:{(i as TaskItemViewModel)?.Title}"));
            var group = vm.Items.OfType<TaskGroupViewModel>().SingleOrDefault(g => g.DisplayName == "WezTerm");
            Assert.True(group is not null, $"expected a WezTerm group; items=[{kinds}]");
            Assert.Equal(2, group.Count);
            Assert.True(group.IsFocused, "group VM should compute IsFocused from its focused child");

            // Link 2 — the group button's OneWay IsChecked binding reflects it.
            var button = view.GetVisualDescendants().OfType<TaskButton>()
                .Single(b => ReferenceEquals(b.DataContext, group));
            Assert.True(button.IsChecked == true, "group button IsChecked should follow IsFocused");

            // Link 3 — the checked template actually paints the darker matte gradient: sample the
            // button's face at mid-height; checked mid reads #2559BC (37,89,188), normal #215FD8
            // (33,95,216) — blue-dominant but a ~30R/6G gap, stable at 2dp tolerance.
            // Link 3 — the checked style actually activated on the template part: the border brush must
            // be the CHECKED one (#12358A), not the resting #1C4D9C.
            var face = button.GetVisualDescendants().OfType<Avalonia.Controls.Border>()
                .Single(b => b.Name == "Face");
            Assert.True(face.BorderBrush is Avalonia.Media.ISolidColorBrush checkedBorder
                && string.Equals(checkedBorder.Color.ToString(), "#FF12358A",
                    StringComparison.OrdinalIgnoreCase),
                $"group button face should carry the checked border, got {face.BorderBrush}");

            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var outPath = Environment.GetEnvironmentVariable("BEVEL_GROUPED_FOCUS_OUT")
                          ?? Path.Combine(Path.GetTempPath(), "bevel-grouped-focus.png");
            frame!.Save(outPath);

        }
        finally
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
        }
    }
}



/// <summary>Minimal window manager: a fixed live set (buttons are driven directly by the test).</summary>
internal sealed class StubWindowManager : IWindowManager
{
    public IReadOnlyList<ForeignWindow> Live { get; set; } = [];

    public Capabilities Capabilities { get; } =
        new(Available: true, TrayMode: TrayCapability.Mirrored, Notes: [], SupportsReposition: true);

    public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
        => ValueTask.FromResult(Live);

    public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
    public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
    public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
    public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
    public Task RepositionAsync(ForeignWindowId id, PalRect bounds, CancellationToken ct = default) => Task.CompletedTask;

    public event EventHandler<ForeignWindow>? WindowOpened { add { } remove { } }
    public event EventHandler<ForeignWindow>? WindowClosed { add { } remove { } }
    public event EventHandler<ForeignWindow>? WindowChanged { add { } remove { } }
    public event EventHandler<ForeignWindow>? ForegroundChanged;
}
