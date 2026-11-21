using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Bevel.FileManager.Components;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// The icon-grid layout: horizontal flow wraps to the available WIDTH, vertical flow (List)
/// wraps to the available HEIGHT, and an unconstrained (infinite) cross-extent must not blow up
/// into int.MaxValue lines — the bug that made Large Icons render as one endless row.
/// </summary>
public sealed class VirtualizingWrapPanelTests
{
    private static VirtualizingWrapPanel Panel(int count, double w, double h, Orientation flow)
    {
        var p = new VirtualizingWrapPanel { ItemWidth = w, ItemHeight = h, Orientation = flow };
        for (var i = 0; i < count; i++) p.Children.Add(new Border());
        return p;
    }

    [AvaloniaFact]
    public void Horizontal_flow_wraps_to_the_available_width()
    {
        var p = Panel(10, 80, 60, Orientation.Horizontal);
        p.Measure(new Size(320, 1000));           // 320/80 = 4 per row -> ceil(10/4) = 3 rows
        Assert.Equal(new Size(4 * 80, 3 * 60), p.DesiredSize);
    }

    [AvaloniaFact]
    public void Infinite_width_does_not_explode_into_one_giant_row()
    {
        var p = Panel(5, 80, 60, Orientation.Horizontal);
        p.Measure(new Size(double.PositiveInfinity, 1000)); // guard: fall back to a single line
        Assert.Equal(new Size(5 * 80, 60), p.DesiredSize);   // one row of 5, NOT int.MaxValue cols
    }

    [AvaloniaFact]
    public void Vertical_flow_wraps_to_the_available_height_into_columns()
    {
        var p = Panel(10, 180, 20, Orientation.Vertical);
        p.Measure(new Size(1000, 80));            // 80/20 = 4 per column -> ceil(10/4) = 3 columns
        Assert.Equal(new Size(3 * 180, 4 * 20), p.DesiredSize);
    }

    [AvaloniaFact]
    public void RightToLeft_mirrors_horizontal_placement()
    {
        var p = Panel(3, 80, 60, Orientation.Horizontal);
        p.FlowDirection = Avalonia.Media.FlowDirection.RightToLeft;   // ambient reading order
        p.Measure(new Size(320, 1000));                              // 4 columns wide
        p.Arrange(new Rect(0, 0, 320, 1000));

        // First item hugs the right edge; subsequent items march leftward.
        Assert.Equal(320 - 80, p.Children[0].Bounds.X);   // rightmost slot
        Assert.Equal(320 - 160, p.Children[1].Bounds.X);
        Assert.Equal(320 - 240, p.Children[2].Bounds.X);
        Assert.Equal(0, p.Children[0].Bounds.Y);          // still the first row
    }

    // bevel-w912: the panel must fill its slot. Returning only the item extent from ArrangeOverride
    // made Avalonia centre a Stretch-aligned panel, so a short List view sat mid-pane.
    private static VirtualizingWrapPanel Hosted(VirtualizingWrapPanel p, double w, double h)
    {
        var host = new Decorator { Child = p };
        host.Measure(new Size(w, h));
        host.Arrange(new Rect(0, 0, w, h));
        return p;
    }

    [AvaloniaFact]
    public void Columns_narrower_than_the_slot_start_at_the_left_edge()
    {
        var p = Hosted(Panel(5, 180, 18, Orientation.Vertical), 800, 40);   // 2 per column, 3 columns
        Assert.Equal(0, p.Bounds.X);
        Assert.Equal(800, p.Bounds.Width);
        Assert.Equal(0, p.Children[0].Bounds.X);
        Assert.Equal(180, p.Children[2].Bounds.X);
    }

    [AvaloniaFact]
    public void Columns_start_at_the_right_edge_under_rtl()
    {
        var p = Panel(5, 180, 18, Orientation.Vertical);
        p.FlowDirection = Avalonia.Media.FlowDirection.RightToLeft;
        Hosted(p, 800, 40);
        Assert.Equal(0, p.Bounds.X);   // child bounds are panel-relative; the panel itself must not shift
        Assert.Equal(620, p.Children[0].Bounds.X);
        Assert.Equal(440, p.Children[2].Bounds.X);
    }

    [AvaloniaFact]
    public void Changing_item_width_relays_out()
    {
        var p = Panel(5, 180, 18, Orientation.Vertical);
        var host = new Decorator { Child = p };
        host.Measure(new Size(800, 40));
        host.Arrange(new Rect(0, 0, 800, 40));
        p.ItemWidth = 100;
        // Re-measure the panel itself: a detached host has no layout manager to pass the
        // invalidation up, and Measure with an unchanged size is skipped unless the panel's own
        // measure was invalidated (which AffectsMeasure does).
        p.Measure(new Size(800, 40));
        p.Arrange(new Rect(0, 0, 800, 40));
        Assert.Equal(100, p.Children[2].Bounds.X);
    }

    [AvaloniaFact]
    public void Rows_keep_the_spare_width_on_the_trailing_side()
    {
        var p = Hosted(Panel(3, 80, 60, Orientation.Horizontal), 830, 1000);
        Assert.Equal(0, p.Bounds.X);
        Assert.Equal(0, p.Children[0].Bounds.X);
        Assert.Equal(0, p.Children[0].Bounds.Y);
    }
}
