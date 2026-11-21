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
}
