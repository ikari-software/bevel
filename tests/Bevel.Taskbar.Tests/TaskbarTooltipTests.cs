using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

public sealed class TaskbarTooltipTests
{
    [AvaloniaFact]
    public void Window_buttons_do_not_use_avalonian_tooltip_popup()
    {
        var model = new ShellModel(null, null, null, usage: TestUsage.Scratch());
        var vm = new TaskbarViewModel(model, new StartMenuViewModel(model));
        var view = new TaskbarView { DataContext = vm };
        var window = new TaskbarWindow(null) { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var fw = new ForeignWindow(new ForeignWindowId("w1"), "Sample window title", "App", false, false, default);
        model.Windows.Add(new TaskItemViewModel(fw, new NoOpWindowManager()) { Width = 120, Opacity = 1 });
        Dispatcher.UIThread.RunJobs();

        var button = view.WindowButtonAreaControl.GetVisualDescendants().OfType<ToggleButton>().Single();
        Assert.Null(ToolTip.GetTip(button));
        Assert.NotNull(view.FindControl<Popup>("TooltipPopup"));
        Assert.NotNull(view.FindControl<TextBlock>("TaskbarTooltipText"));
    }

    [AvaloniaFact]
    public void Preview_frame_has_a_background_behind_the_thumbnail()
    {
        var model = new ShellModel(null, null, null, usage: TestUsage.Scratch());
        var vm = new TaskbarViewModel(model, new StartMenuViewModel(model));
        var view = new TaskbarView { DataContext = vm };
        // The frame binds its backing to Bevel.Brush.InfoWindow (the info-tooltip face); inject it so the
        // DynamicResource resolves in the headless test, then assert the binding actually points at it.
        view.Resources["Bevel.Brush.InfoWindow"] = Brushes.Magenta;
        var window = new TaskbarWindow(null) { Content = view, Width = 800, Height = 40 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var popup = view.FindControl<Popup>("TooltipPopup");
        popup!.IsOpen = true;   // realize the popup content so its bindings resolve
        Dispatcher.UIThread.RunJobs();

        // A captured macOS window thumbnail has transparent rounded corners (and can letterbox inside a
        // stretched frame); without a fill those regions show through the popup. The frame must fill behind
        // the thumbnail, bound to the same panel face brush.
        var frame = view.FindControl<Border>("PreviewFrame");
        Assert.NotNull(frame);
        Assert.Same(Brushes.Magenta, frame!.Background);
    }

    [AvaloniaFact]
    public void Tooltip_popup_opens_with_placement_target()
    {
        var model = new ShellModel(null, null, null, usage: TestUsage.Scratch());
        var vm = new TaskbarViewModel(model, new StartMenuViewModel(model));
        var view = new TaskbarView { DataContext = vm };
        var window = new TaskbarWindow(null) { Content = view, Width = 800, Height = 40 };

        var fw = new ForeignWindow(new ForeignWindowId("w1"), "Sample window title", "App", false, false, default);
        model.Windows.Add(new TaskItemViewModel(fw, new NoOpWindowManager()) { Width = 120, Opacity = 1 });

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var button = view.WindowButtonAreaControl.GetVisualDescendants().OfType<ToggleButton>().Single();
        var popup = view.FindControl<Popup>("TooltipPopup");
        var text = view.FindControl<TextBlock>("TaskbarTooltipText");
        Assert.NotNull(popup);
        Assert.NotNull(text);

        text!.Text = "Sample window title — Open (click to activate)";
        popup!.PlacementTarget = button;
        popup.IsOpen = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(popup.IsOpen);
        Assert.Same(button, popup.PlacementTarget);
    }

    private sealed class NoOpWindowManager : IWindowManager
    {
        public Capabilities Capabilities { get; } = new(true, TrayCapability.Mirrored, []);
        public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<ForeignWindow>>([]);
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
