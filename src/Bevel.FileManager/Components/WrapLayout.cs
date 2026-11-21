using Avalonia;

namespace Bevel.FileManager.Components;

/// <summary>
/// Uniform-cell geometry for the wrap views (bevel-w912): the only place that knows where item
/// <c>i</c> sits. <see cref="VirtualizingWrapPanel"/> arranges from it; keyboard navigation and
/// scroll-into-view read it. Pure arithmetic: no controls, no realized containers.
/// </summary>
/// <param name="Count">Number of items.</param>
/// <param name="PerLine">Items along one wrapping line (per row when <paramref name="Horizontal"/>,
/// per column otherwise).</param>
/// <param name="Lines">Number of lines.</param>
/// <param name="ItemWidth">Cell width.</param>
/// <param name="ItemHeight">Cell height.</param>
/// <param name="Horizontal">True: items flow left to right and wrap down (icon views). False:
/// items flow top to bottom and wrap into columns (List).</param>
internal readonly record struct WrapLayout(int Count, int PerLine, int Lines, double ItemWidth, double ItemHeight, bool Horizontal)
{
    /// <summary>Lay out <paramref name="count"/> cells along a line of length <paramref name="lineSpan"/>
    /// (the width for horizontal flow, the height for vertical flow). An infinite or NaN span (a
    /// ScrollViewer measuring on its scrolling axis) puts every item on one line instead of saturating
    /// <c>(int)(∞ / cell)</c> to int.MaxValue.</summary>
    public static WrapLayout Compute(int count, double lineSpan, double itemWidth, double itemHeight, bool horizontal)
    {
        double cell = horizontal ? itemWidth : itemHeight;
        int perLine = double.IsInfinity(lineSpan) || double.IsNaN(lineSpan)
            ? Math.Max(1, count)
            : Math.Max(1, (int)(lineSpan / cell));
        int lines = (int)Math.Ceiling((double)count / perLine);
        return new WrapLayout(count, perLine, lines, itemWidth, itemHeight, horizontal);
    }

    /// <summary>The size the cells cover.</summary>
    public Size Extent => Horizontal
        ? new Size(PerLine * ItemWidth, Lines * ItemHeight)
        : new Size(Lines * ItemWidth, PerLine * ItemHeight);

    /// <summary>The cell of item <paramref name="index"/>. Under <paramref name="rtl"/> the horizontal
    /// axis is mirrored inside <paramref name="finalWidth"/>, so index 0 sits at the right edge.</summary>
    public Rect RectFor(int index, double finalWidth, bool rtl)
    {
        int line = index / PerLine;   // row (horizontal) or column (vertical)
        int pos = index % PerLine;    // position along that line
        double x = Horizontal ? pos * ItemWidth : line * ItemWidth;
        double y = Horizontal ? line * ItemHeight : pos * ItemHeight;
        if (rtl) x = finalWidth - ItemWidth - x;
        return new Rect(x, y, ItemWidth, ItemHeight);
    }

    /// <summary>One item per row, for Details (rows of <paramref name="rowHeight"/> across
    /// <paramref name="width"/>): Up/Down move a row, Left/Right move one item.</summary>
    public static WrapLayout SingleColumn(int count, double width, double rowHeight)
        => new(count, 1, count, width, rowHeight, Horizontal: true);

    /// <summary>Where an arrow key goes from <paramref name="index"/>. <paramref name="dx"/>/<paramref name="dy"/>
    /// are the arrow's VISUAL direction (-1, 0 or 1; exactly one non-zero), so under
    /// <paramref name="rtl"/> Right moves toward reading-start (bevel-3cd). Moving along the flow
    /// continues across line ends (Down at the foot of a List column goes to the top of the next);
    /// moving across lines keeps the position in the line, lands on the last item when the next line
    /// is shorter, and stays put at the edges (Explorer behaviour: no clamping to item 0 or the end).</summary>
    public int Move(int index, int dx, int dy, bool rtl)
    {
        // An empty panel's layout is default (PerLine 0): a key pressed after the first batch arrives but
        // before the next layout pass lands here, and must not divide by zero (review #1).
        if (PerLine <= 0 || Count <= 0) return index;
        int ahead = rtl ? -dx : dx;
        int along = Horizontal ? ahead : dy;
        int across = Horizontal ? dy : ahead;

        if (along != 0)
        {
            int t = index + along;
            return t >= 0 && t < Count ? t : index;
        }
        if (across != 0)
        {
            int t = index + across * PerLine;
            if (t >= 0 && t < Count) return t;
            if (across > 0 && index / PerLine < Lines - 1) return Count - 1;   // shorter last line
        }
        return index;
    }

    /// <summary>Items in one full <paramref name="viewport"/> along the scrolling axis (PageUp/PageDown):
    /// whole rows for horizontal flow, whole columns for vertical flow, at least one line.</summary>
    public int PageSize(Size viewport)
        => PerLine * Math.Max(1, (int)(Horizontal ? viewport.Height / ItemHeight : viewport.Width / ItemWidth));
}
