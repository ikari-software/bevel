using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Bevel.Core;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The taskbar overflow chevrons (bevel-m2.10.2): when the wrapped button rows are taller than the
/// bar, up/down arrows appear and scroll the strip a row at a time. Fixed-width mode with many
/// windows forces the overflow (buttons don't shrink), so the rows exceed a single-row bar.
/// </summary>
public class OverflowChevronTests
{
    [AvaloniaFact]
    public void Chevrons_appear_on_overflow_and_scroll_the_rows()
    {
        var model = new ShellModel(null, null, null);
        var vm = new TaskbarViewModel(model, new StartMenuViewModel(model));
        var wm = new StubWm();

        var view = new TaskbarView { DataContext = vm };
        view.Initialize(new BevelSettings { TaskbarButtonWidth = 150, TaskbarButtonWidthMode = TaskbarButtonWidthMode.Fixed });
        var window = new TaskbarWindow(null, rows: 1) { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Enough fixed-width buttons to wrap well past a single row at any sane bar width.
        for (var i = 0; i < 40; i++)
        {
            var fw = new ForeignWindow(new ForeignWindowId($"w{i}"), $"Window {i + 1}", "App", false, false, default);
            model.Windows.Add(new TaskItemViewModel(fw, wm) { Width = 150, Opacity = 1 });
        }
        Dispatcher.UIThread.RunJobs();

        var chevrons = view.FindControl<StackPanel>("OverflowChevrons");
        var up = view.FindControl<RepeatButton>("ScrollUpBtn");
        var down = view.FindControl<RepeatButton>("ScrollDownBtn");
        var scroller = view.FindControl<ScrollViewer>("WindowButtonScroller");
        Assert.NotNull(chevrons);
        Assert.NotNull(up);
        Assert.NotNull(down);
        Assert.NotNull(scroller);

        // Rows overflow the 1-row bar → chevrons show; at the top, up is disabled, down enabled.
        Assert.True(chevrons!.IsVisible, "chevrons should be visible when rows overflow the bar");
        Assert.True(scroller!.Extent.Height - scroller.Viewport.Height > 0.5, "content must actually overflow");
        Assert.False(up!.IsEnabled);
        Assert.True(down!.IsEnabled);

        // Scroll down one row: offset advances and the up arrow becomes usable.
        var before = scroller.Offset.Y;
        down.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.True(scroller.Offset.Y > before, "scrolling down should advance the offset");
        Assert.True(up.IsEnabled, "after scrolling down, up should be enabled");
    }

    [AvaloniaFact]
    public void Chevrons_hidden_when_everything_fits()
    {
        var model = new ShellModel(null, null, null);
        var vm = new TaskbarViewModel(model, new StartMenuViewModel(model));
        var wm = new StubWm();

        var view = new TaskbarView { DataContext = vm };
        view.Initialize(new BevelSettings { TaskbarButtonWidth = 150, TaskbarButtonWidthMode = TaskbarButtonWidthMode.ShrinkToFit });
        var window = new TaskbarWindow(null, rows: 1) { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // A couple of shrink-to-fit buttons always fit a single row → no chevrons.
        for (var i = 0; i < 3; i++)
        {
            var fw = new ForeignWindow(new ForeignWindowId($"w{i}"), $"W{i}", "App", false, false, default);
            model.Windows.Add(new TaskItemViewModel(fw, wm) { Width = 150, Opacity = 1 });
        }
        Dispatcher.UIThread.RunJobs();

        var chevrons = view.FindControl<StackPanel>("OverflowChevrons");
        Assert.NotNull(chevrons);
        Assert.False(chevrons!.IsVisible, "chevrons should stay hidden when the buttons fit");
    }

    private sealed class StubWm : IWindowManager
    {
        public Capabilities Capabilities { get; } =
            new(Available: true, TrayMode: TrayCapability.Mirrored, Notes: Array.Empty<string>(), SupportsReposition: true);
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
