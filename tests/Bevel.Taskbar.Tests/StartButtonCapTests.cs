using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The Start button must never be taller than the bar it sits in (bevel-kclq).
///
/// This is a PLACEMENT guard wearing a sizing test's clothes. The Start menu's popup is anchored above
/// the Start button, so the button's height decides where the menu's bottom edge lands. The cap used to
/// be a flat HeightForRows(StartMaxRows) — 86 for the 3-row cap — which passes any test that only asks
/// "is the bar the right height?", because 86 &gt; 58 means it never binds on the BAR. It bound on the
/// menu: measured on device at rows=2, the popup's bottom landed at y=1355 — exactly HeightForRows(3)
/// off the screen bottom, plus the Win2000 skin's 1px button margin — where the bar's top is 1383. One
/// RowHeight of wallpaper showed between the menu and the taskbar.
///
/// Nothing caught it because nothing asserts popup placement at all: RenderLunaStartMenuTest detaches
/// the popup's content into its own window before capturing, so the menu is only ever rendered in
/// isolation. Until placement itself is testable headlessly, the button's cap is the closest proxy —
/// it is the value the anchor is derived from.
/// </summary>
[Collection("TaskbarTheme")]
public class StartButtonCapTests
{
    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Start_button_is_never_taller_than_the_bar(int rows)
    {
        var view = new TaskbarView();
        var window = new TaskbarWindow(null, rows: rows) { Content = view, Width = 1200 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var start = view.GetVisualDescendants().OfType<Button>().First(b => b.Name == "StartButton");
        var barHeight = TaskbarTheme.HeightForRows(window.Rows);

        Assert.True(start.MaxHeight <= barHeight,
            $"rows={window.Rows}: Start button capped at {start.MaxHeight} in a {barHeight}-tall bar — " +
            "the Start menu anchors above this button, so a cap above the bar floats the menu off it");
        Assert.True(start.Bounds.Height <= barHeight, $"rows={window.Rows}: Start button arranged taller than the bar");
    }

    /// <summary>The cap still honours its own ceiling: past StartMaxRows the button stops growing.</summary>
    [AvaloniaFact]
    public void Start_button_stops_growing_past_its_row_ceiling()
    {
        var view = new TaskbarView();
        var window = new TaskbarWindow(null, rows: 4) { Content = view, Width = 1200 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var start = view.GetVisualDescendants().OfType<Button>().First(b => b.Name == "StartButton");
        if (window.Rows > 3)
            Assert.Equal(TaskbarTheme.HeightForRows(3), start.MaxHeight);
    }
}
