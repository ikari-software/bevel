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
    public void IconOnly_mode_never_shows_labels_even_when_roomy()
    {
        // Roomy strip that Auto would fully label: IconOnly stays icon-sized and unlabelled.
        var (width, showLabel) = TaskbarView.ComputeButtonLayout(
            TaskbarButtonWidthMode.ShrinkToFit, 1000, 2, 1, Max, Min, TaskbarButtonLabels.IconOnly);
        Assert.False(showLabel);
        Assert.Equal(40, width);            // clamped to the icon-only max
    }

    [Fact]
    public void IconOnly_with_fixed_width_is_icon_sized_and_unlabelled()
    {
        var (width, showLabel) = TaskbarView.ComputeButtonLayout(
            TaskbarButtonWidthMode.Fixed, 1000, 5, 1, Max, Min, TaskbarButtonLabels.IconOnly);
        Assert.False(showLabel);
        Assert.Equal(40, width);   // clamped to the icon-only max, never the full Fixed max
    }

    [Fact]
    public void IconOnly_with_zero_count_is_icon_sized_and_unlabelled()
    {
        var (width, showLabel) = TaskbarView.ComputeButtonLayout(
            TaskbarButtonWidthMode.ShrinkToFit, 1000, 0, 1, Max, Min, TaskbarButtonLabels.IconOnly);
        Assert.False(showLabel);
        Assert.Equal(40, width);
    }

    [Fact]
    public void Always_mode_keeps_the_label_and_floor_even_when_crowded()
    {
        // Crowding that drops Auto to icon-only stays labelled at the text floor under Always.
        var (width, showLabel) = TaskbarView.ComputeButtonLayout(
            TaskbarButtonWidthMode.ShrinkToFit, 150, 5, 1, Max, Min, TaskbarButtonLabels.Always);
        Assert.True(showLabel);
        Assert.Equal(Min, width);
    }

    /// <summary>
    /// bevel-c54t: the icon-only floor/cap scale with the tier's glyph. A 32px Big-tier icon in a
    /// 24–40px slot would be clipped, so the slot grows with it (floor 40, cap 56) — while the default
    /// 16px glyph must still produce the exact pre-c54t 24/40 numbers the tests above assert.
    /// </summary>
    [Theory]
    [InlineData(16, 24, 40)]   // Small/Normal — unchanged
    [InlineData(24, 32, 48)]   // Large
    [InlineData(32, 40, 56)]   // Big icons
    public void Icon_only_slot_scales_with_the_tier_glyph(int iconSize, double expectedFloor, double expectedMax)
    {
        // Roomy: clamped to the cap.
        var roomy = TaskbarView.ComputeButtonLayout(
            TaskbarButtonWidthMode.ShrinkToFit, 1000, 2, 1, Max, Min, TaskbarButtonLabels.IconOnly, iconSize);
        Assert.Equal(expectedMax, roomy.Width);
        Assert.False(roomy.ShowLabel);

        // Extreme crowding: clamped to the floor, never below the glyph.
        var crowded = TaskbarView.ComputeButtonLayout(
            TaskbarButtonWidthMode.ShrinkToFit, 40, 20, 1, Max, Min, TaskbarButtonLabels.IconOnly, iconSize);
        Assert.Equal(expectedFloor, crowded.Width);
        Assert.True(crowded.Width >= iconSize, "the slot must never be narrower than the glyph it holds");
    }

    /// <summary>Auto mode's crowded tier also respects the bigger glyph: it may shrink to the tier's
    /// icon-only floor, never past it (bevel-c54t).</summary>
    [Fact]
    public void Auto_mode_crowded_floor_respects_a_big_glyph()
    {
        var (width, showLabel) = TaskbarView.ComputeButtonLayout(
            TaskbarButtonWidthMode.ShrinkToFit, 50, 12, 1, Max, Min, TaskbarButtonLabels.Auto, iconSize: 32);
        Assert.False(showLabel);
        Assert.Equal(40, width);   // 32 + 8, not the 16px tier's 24
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
