using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Core;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The Start button auto-sizes to its configured caption (bevel-6x9z). It used to be a fixed
/// Width="74" with ClipToBounds="True", so a longer caption was simply cut off and a shorter one left
/// dead space. The divider that follows it was positioned by a hardcoded Margin encoding that same 74,
/// so it detached the moment the button changed size.
/// </summary>
[Collection("TaskbarTheme")]   // ApplyLiveSettings mutates the shared TaskbarTheme metrics
public class StartButtonSizingTests
{
    private static (TaskbarView View, Button Start) Bar()
    {
        var model = new ShellModel(null, null, null, usage: TestUsage.Scratch());
        var vm = new TaskbarViewModel(model, new StartMenuViewModel(model));
        var view = new TaskbarView { DataContext = vm };
        var window = new TaskbarWindow(null, rows: 1) { Content = view, Width = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, view.StartButtonControl);
    }

    private static double WidthFor(TaskbarView view, Button start, string caption)
    {
        view.ApplyLiveSettings(new BevelSettings { TaskbarStartLabel = caption });
        Dispatcher.UIThread.RunJobs();
        start.Measure(Avalonia.Size.Infinity);
        start.Arrange(new Avalonia.Rect(start.DesiredSize));
        Dispatcher.UIThread.RunJobs();
        return start.Bounds.Width;
    }

    [AvaloniaFact]
    public void A_longer_caption_makes_a_wider_button()
    {
        var (view, start) = Bar();

        var shortWidth = WidthFor(view, start, "Go");
        var longWidth = WidthFor(view, start, "Applications and Places");

        Assert.True(longWidth > shortWidth,
            $"caption did not grow the button: '{"Go"}' = {shortWidth}, long caption = {longWidth}");
    }

    [AvaloniaFact]
    public void A_short_or_empty_caption_never_shrinks_below_the_themed_minimum()
    {
        var (view, start) = Bar();

        // MinWidth comes from the theme (Bevel.Metric.StartButtonMinWidth), so read it rather than
        // hardcoding a number that would drift the moment a skin retunes its pill.
        var min = start.MinWidth;
        Assert.True(min > 0, "the theme published no Start-button minimum width");

        Assert.True(WidthFor(view, start, "") >= min, "logo-only caption fell below the minimum");
        Assert.True(WidthFor(view, start, "B") >= min, "one-character caption fell below the minimum");
    }

    [AvaloniaFact]
    public void The_caption_is_not_clipped_by_the_button()
    {
        var (view, start) = Bar();
        WidthFor(view, start, "Applications and Places");

        // The content has to FIT: a button narrower than what its content wants is the old clipping
        // bug wearing a different hat (ClipToBounds hides it rather than reporting it).
        // DesiredSize INCLUDES the button's own margin (Win2000 publishes 1,1,0,1), so compare like
        // for like or every themed margin reads as a 1px clip.
        var marginX = start.Margin.Left + start.Margin.Right;
        Assert.True(start.Bounds.Width + 0.5 >= start.DesiredSize.Width - marginX,
            $"button {start.Bounds.Width} is narrower than its content " +
            $"{start.DesiredSize.Width - marginX} (desired {start.DesiredSize.Width} less {marginX} margin)");
    }
}
