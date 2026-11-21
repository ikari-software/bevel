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

    // bevel-0esz: arrows are visual. In vertical flow (List) Right crosses to the next column and
    // Down moves one item; past the last item a move stays put.
    [Fact]
    public void Vertical_flow_right_moves_one_column_and_down_moves_one_item()
    {
        var l = WrapLayout.Compute(30, 180, 180, 18, horizontal: false);
        Assert.Equal(10, l.PerLine);
        Assert.Equal(10, l.Move(0, 1, 0, rtl: false));
        Assert.Equal(1, l.Move(0, 0, 1, rtl: false));
        Assert.Equal(0, l.Move(0, -1, 0, rtl: false));
        Assert.Equal(25, l.Move(25, 1, 0, rtl: false));
        Assert.Equal(25, l.Move(15, 1, 0, rtl: false));

        var shortLast = WrapLayout.Compute(27, 180, 180, 18, horizontal: false);
        Assert.Equal(26, shortLast.Move(18, 1, 0, rtl: false));   // next column exists but is shorter

        Assert.Equal(10, l.Move(0, -1, 0, rtl: true));
        Assert.Equal(0, l.Move(0, 1, 0, rtl: true));
    }

    [Fact]
    public void Horizontal_flow_up_in_the_first_row_and_down_in_the_last_row_stay_put()
    {
        var l = WrapLayout.Compute(10, 320, 80, 60, horizontal: true);   // 4 per row
        Assert.Equal(1, l.Move(1, 0, -1, rtl: false));
        Assert.Equal(9, l.Move(9, 0, 1, rtl: false));
        Assert.Equal(9, l.Move(5, 0, 1, rtl: false));
        Assert.Equal(6, l.Move(2, 0, 1, rtl: false));
    }

    [Fact]
    public void PageSize_is_the_items_in_one_viewport_along_the_scrolling_axis()
    {
        var icons = WrapLayout.Compute(100, 320, 80, 60, horizontal: true);   // 4 per row
        Assert.Equal(4 * 5, icons.PageSize(new Size(320, 300)));
        var list = WrapLayout.Compute(100, 180, 180, 18, horizontal: false);  // 10 per column
        Assert.Equal(10 * 3, list.PageSize(new Size(600, 180)));
        Assert.Equal(10, list.PageSize(new Size(50, 180)));                    // at least one line
    }

    [Fact]
    public void SingleColumn_is_one_item_per_row()
    {
        var l = WrapLayout.SingleColumn(5, 400, 22);
        Assert.Equal(1, l.PerLine);
        Assert.Equal(5, l.Lines);
        Assert.Equal(new Rect(0, 44, 400, 22), l.RectFor(2, 400, rtl: false));
        Assert.Equal(3, l.Move(2, 0, 1, rtl: false));
        Assert.Equal(3, l.Move(2, 1, 0, rtl: false));   // Details keeps Left/Right = ±1 item
    }
}
