using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Bevel.FileManager.Components;

/// <summary>
/// Wrap panel for icon grid and list-column modes.
/// <see cref="Orientation"/> Horizontal = Large/Small Icons, Thumbnails (items flow left-to-right,
/// wrap down). Vertical = List view (items flow top-to-bottom, wrap into columns).
///
/// Honours the ambient <see cref="Visual.FlowDirection"/>: under RightToLeft the horizontal axis
/// is mirrored, so items start at the right edge (row items go right-to-left; columns fill from
/// the right). This is the inherited property we used to shadow — the wrapping axis is
/// <see cref="Orientation"/> (WrapPanel's term), which is a different concept from reading order.
///
/// TODO(M2): Upgrade to full VirtualizingPanel for 100k-entry performance.
/// </summary>
public class VirtualizingWrapPanel : Panel
{
    public static readonly StyledProperty<double> ItemWidthProperty =
        AvaloniaProperty.Register<VirtualizingWrapPanel, double>(nameof(ItemWidth), 96);

    public static readonly StyledProperty<double> ItemHeightProperty =
        AvaloniaProperty.Register<VirtualizingWrapPanel, double>(nameof(ItemHeight), 96);

    /// <summary>The wrapping axis: Horizontal flows into rows, Vertical into columns.
    /// (Named like WrapPanel.Orientation; distinct from the inherited FlowDirection/reading order.)</summary>
    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<VirtualizingWrapPanel, Orientation>(nameof(Orientation), Orientation.Horizontal);

    public double ItemWidth { get => GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }
    public Orientation Orientation { get => GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }

    // _perLine = items along the wrapping line (per row when horizontal, per column when
    // vertical); _lines = number of such lines.
    private int _perLine;
    private int _lines;

    protected override Size MeasureOverride(Size available)
    {
        var children = Children;
        int count = children.Count;
        if (count == 0 || ItemWidth <= 0 || ItemHeight <= 0)
            return default;

        bool horiz = Orientation == Orientation.Horizontal;
        // Wrapping is driven by the CROSS extent: width for horizontal flow, height for vertical.
        // A ScrollViewer that allows scrolling on that axis measures us at infinity — guard it, or
        // (int)(∞ / cell) saturates to int.MaxValue and everything lands on one line. The caller
        // disables the matching scrollbar (see ItemView.ApplyViewMode) so this stays finite.
        double lineSpan = horiz ? available.Width : available.Height;
        double cell = horiz ? ItemWidth : ItemHeight;
        _perLine = double.IsInfinity(lineSpan) || double.IsNaN(lineSpan)
            ? count
            : Math.Max(1, (int)(lineSpan / cell));
        _lines = (int)Math.Ceiling((double)count / _perLine);

        for (int i = 0; i < count; i++)
            children[i].Measure(new Size(ItemWidth, ItemHeight));

        return Extent();
    }

    protected override Size ArrangeOverride(Size final)
    {
        var children = Children;
        bool horiz = Orientation == Orientation.Horizontal;
        bool rtl = FlowDirection == Avalonia.Media.FlowDirection.RightToLeft;

        for (int i = 0; i < children.Count; i++)
        {
            int line = i / _perLine;   // row (horizontal) or column (vertical)
            int pos = i % _perLine;    // position along that line
            double x = horiz ? pos * ItemWidth : line * ItemWidth;
            double y = horiz ? line * ItemHeight : pos * ItemHeight;
            if (rtl)
                x = final.Width - ItemWidth - x;   // mirror the horizontal axis: origin at the right
            children[i].Arrange(new Rect(x, y, ItemWidth, ItemHeight));
        }
        return Extent();
    }

    private Size Extent() => Orientation == Orientation.Horizontal
        ? new Size(_perLine * ItemWidth, _lines * ItemHeight)
        : new Size(_lines * ItemWidth, _perLine * ItemHeight);
}
