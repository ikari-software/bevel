using Bevel.Core;
using Bevel.Taskbar;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Unit coverage for the taskbar button width/label policy (bevel-m2.10). Exercises the pure
/// <see cref="TaskbarView.ComputeButtonLayout"/> seam directly — no visual tree — across the
/// display modes: Fixed, shrink-to-fit at various crowding, and the icon-only tier.
/// </summary>
public class ButtonLayoutTests
{
    private const double Max = 160;
    private const int Min = 80;      // default text floor
    private const double IconFloor = 24;

    private static (double Width, bool ShowLabel) Layout(
        double available, int count, int rows = 1, TaskbarButtonWidthMode mode = TaskbarButtonWidthMode.ShrinkToFit)
        => TaskbarView.ComputeButtonLayout(mode, available, count, rows, Max, Min);

    [Fact]
    public void Fixed_mode_always_uses_max_width_and_keeps_the_label()
    {
        // Even wildly overcrowded, Fixed stays at max (the strip scrolls the overflow).
        Assert.Equal((Max, true), Layout(120, 40, mode: TaskbarButtonWidthMode.Fixed));
        Assert.Equal((Max, true), Layout(2000, 1, mode: TaskbarButtonWidthMode.Fixed));
    }

    [Fact]
    public void Empty_strip_reports_max_width()
        => Assert.Equal((Max, true), Layout(1000, 0));

    [Fact]
    public void Roomy_strip_caps_button_width_at_the_max()
    {
        var (width, showLabel) = Layout(available: 1000, count: 2);
        Assert.Equal(Max, width);   // ideal 498 → capped to 160
        Assert.True(showLabel);
    }

    [Fact]
    public void Filling_strip_shrinks_buttons_but_keeps_labels_down_to_the_text_floor()
    {
        var (width, showLabel) = Layout(available: 600, count: 5);
        Assert.Equal(118, width, precision: 0);   // 600/5 - 2
        Assert.True(showLabel);

        // Right at the floor: still labelled.
        var atFloor = Layout(available: 410, count: 5);
        Assert.True(atFloor.Width >= Min - 1 && atFloor.Width <= Min + 1);
        Assert.True(atFloor.ShowLabel);
    }

    [Fact]
    public void Below_floor_but_above_label_threshold_shrinks_yet_stays_labelled()
    {
        // ideal ~70px: under the 80 text floor but above the 34 label threshold.
        var (width, showLabel) = Layout(available: 360, count: 5);
        Assert.True(width < Min);
        Assert.True(width >= 34);
        Assert.True(showLabel);
    }

    [Fact]
    public void Crowded_strip_drops_to_icon_only()
    {
        // ideal ~28px: below the label threshold → icon-only, but not below the icon floor.
        var (width, showLabel) = Layout(available: 150, count: 5);
        Assert.False(showLabel);
        Assert.True(width >= IconFloor);
        Assert.True(width < 34);
    }

    [Fact]
    public void Extreme_crowding_clamps_to_the_icon_floor()
    {
        var (width, showLabel) = Layout(available: 50, count: 12);
        Assert.Equal(IconFloor, width);
        Assert.False(showLabel);
    }

    [Fact]
    public void Rows_split_the_button_count_so_multi_row_buttons_stay_wider()
    {
        // 10 buttons over 2 rows = 5 per row: same width as 5 buttons on one row.
        var oneRow = Layout(available: 400, count: 5, rows: 1);
        var twoRows = Layout(available: 400, count: 10, rows: 2);
        Assert.Equal(oneRow.Width, twoRows.Width, precision: 3);
        Assert.Equal(oneRow.ShowLabel, twoRows.ShowLabel);
    }
}
