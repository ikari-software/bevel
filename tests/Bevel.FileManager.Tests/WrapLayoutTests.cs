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

    // bevel-rzz: virtualization realizes only the viewport plus a buffer of lines around it.
    [Fact]
    public void RangeFor_returns_only_viewport_lines_plus_buffer()
    {
        // 100 000 items, 80x60 cells, 800 px wide → 10 per line; viewport y=6000..6600 (600 px tall).
        var l = WrapLayout.Compute(100_000, 800, 80, 60, horizontal: true);
        Assert.Equal(10, l.PerLine);

        var (first, last) = l.RangeFor(new Rect(0, 6000, 800, 600), buffer: 600);

        Assert.True(last - first + 1 < 400, $"range {first}..{last} is {last - first + 1} indices, must be < 400");
        Assert.True(first <= 1000 && last >= 1009, "line 100 (indices 1000..1009) must be inside the range");
        Assert.True(first >= 900, "buffer reaches at most one viewport (10 lines) above the viewport");
    }

    [Fact]
    public void RangeFor_covers_everything_when_the_buffer_is_the_whole_extent()
    {
        var l = WrapLayout.Compute(10, 320, 80, 60, horizontal: true);   // 3 lines
        var (first, last) = l.RangeFor(new Rect(0, 0, 320, 60), buffer: 10_000);
        Assert.Equal((0, 9), (first, last));
    }

    [Fact]
    public void RangeFor_in_vertical_flow_scrolls_on_x()
    {
        var l = WrapLayout.Compute(1000, 200, 180, 18, horizontal: false);  // 11 per column
        var (first, last) = l.RangeFor(new Rect(400, 0, 200, 180), buffer: 200);
        // columns are 180 wide: viewport x=400..600 → columns 2..3, buffer ±1 column → 1..4
        Assert.Equal(11, first);
        Assert.Equal(5 * 11 - 1, last);   // columns 1..4 → items 11..54
    }

    [Fact]
    public void LinesIn_reports_the_rows_a_rect_touches()
    {
        var l = WrapLayout.Compute(100, 320, 80, 60, horizontal: true);   // 4 per row, 25 rows
        Assert.Equal((2, 5), l.LinesIn(new Rect(0, 120, 320, 180)));        // y 120..300 → rows 2..5 (300/60=5)
        Assert.Equal((0, 0), l.LinesIn(new Rect(0, 0, 320, 30)));
        Assert.Equal((0, -1), default(WrapLayout).LinesIn(new Rect(0, 0, 10, 10)));
    }

    // Review #1: an empty panel's layout is default (PerLine 0). Keys pressed after the first batch
    // arrives but before the next layout pass reach Move with it and must not divide by zero.
    [Fact]
    public void Move_on_an_empty_layout_stays_put()
    {
        var l = default(WrapLayout);
        Assert.Equal(0, l.Move(0, 0, 1, rtl: false));
        Assert.Equal(0, l.Move(0, 1, 0, rtl: false));
        Assert.Equal(3, l.Move(3, 0, -1, rtl: true));
        var vertical = new WrapLayout(0, 0, 0, 180, 18, Horizontal: false);
        Assert.Equal(0, vertical.Move(0, 1, 0, rtl: false));
    }
}
