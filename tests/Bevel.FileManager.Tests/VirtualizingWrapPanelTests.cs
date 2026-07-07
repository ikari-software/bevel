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
        var p = new VirtualizingWrapPanel { ItemWidth = w, ItemHeight = h, FlowDirection = flow };
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
}
