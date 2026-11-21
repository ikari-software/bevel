using Avalonia;
using Bevel.FileManager.Components;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// The pure uniform-cell geometry behind every wrap view (bevel-w912): how many items fit on a
/// line, how many lines there are, and where item <c>i</c> sits, in both flows and both reading
/// directions. No Avalonia app is needed.
/// </summary>
public sealed class WrapLayoutTests
{
    [Fact]
    public void Horizontal_flow_fills_rows_to_the_span()
    {
        var l = WrapLayout.Compute(10, 320, 80, 60, horizontal: true);
        Assert.Equal(4, l.PerLine);
        Assert.Equal(3, l.Lines);
        Assert.Equal(new Size(320, 180), l.Extent);
    }

    [Fact]
    public void Infinite_span_puts_everything_on_one_line()
    {
        var l = WrapLayout.Compute(5, double.PositiveInfinity, 80, 60, horizontal: true);
        Assert.Equal(5, l.PerLine);
        Assert.Equal(1, l.Lines);
        Assert.Equal(new Size(400, 60), l.Extent);
    }

    [Fact]
    public void Vertical_flow_fills_columns_to_the_span()
    {
        var l = WrapLayout.Compute(10, 80, 180, 20, horizontal: false);
        Assert.Equal(4, l.PerLine);
        Assert.Equal(3, l.Lines);
        Assert.Equal(new Size(540, 80), l.Extent);
    }

    [Fact]
    public void No_items_means_no_lines()
    {
        var l = WrapLayout.Compute(0, 320, 80, 60, horizontal: true);
        Assert.Equal(0, l.Lines);
        Assert.Equal(new Size(l.PerLine * 80, 0), l.Extent);
    }

    [Fact]
    public void RectFor_places_items_along_rows_in_horizontal_flow()
    {
        var l = WrapLayout.Compute(10, 320, 80, 60, horizontal: true);
        Assert.Equal(new Rect(0, 0, 80, 60), l.RectFor(0, 320, rtl: false));
        Assert.Equal(new Rect(80, 60, 80, 60), l.RectFor(5, 320, rtl: false));
        Assert.Equal(new Rect(240, 0, 80, 60), l.RectFor(0, 320, rtl: true));
        Assert.Equal(new Rect(160, 60, 80, 60), l.RectFor(5, 320, rtl: true));
    }

    [Fact]
    public void RectFor_places_items_down_columns_in_vertical_flow()
    {
        var l = WrapLayout.Compute(10, 80, 180, 20, horizontal: false);
        Assert.Equal(new Rect(0, 0, 180, 20), l.RectFor(0, 800, rtl: false));
        Assert.Equal(new Rect(180, 20, 180, 20), l.RectFor(5, 800, rtl: false));
        Assert.Equal(new Rect(620, 0, 180, 20), l.RectFor(0, 800, rtl: true));
        Assert.Equal(new Rect(440, 20, 180, 20), l.RectFor(5, 800, rtl: true));
    }
}
