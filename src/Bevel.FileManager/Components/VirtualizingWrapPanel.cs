using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Bevel.FileManager.Components;

/// <summary>
/// Wrap panel for icon grid and list-column modes.
/// Horizontal flow = Large/Small Icons, Thumbnails.
/// Vertical flow   = List view (top-to-bottom columns).
///
/// TODO(M2): Upgrade to full VirtualizingPanel for 100k-entry performance.
/// </summary>
public class VirtualizingWrapPanel : Panel
{
    public static readonly StyledProperty<double> ItemWidthProperty =
        AvaloniaProperty.Register<VirtualizingWrapPanel, double>(nameof(ItemWidth), 96);

    public static readonly StyledProperty<double> ItemHeightProperty =
        AvaloniaProperty.Register<VirtualizingWrapPanel, double>(nameof(ItemHeight), 96);

    public static readonly StyledProperty<Orientation> FlowDirectionProperty =
        AvaloniaProperty.Register<VirtualizingWrapPanel, Orientation>(
            nameof(FlowDirection), Orientation.Horizontal);

    public double ItemWidth { get => GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }
    public Orientation FlowDirection { get => GetValue(FlowDirectionProperty); set => SetValue(FlowDirectionProperty, value); }

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

        bool horiz = FlowDirection == Orientation.Horizontal;
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
        for (int i = 0; i < children.Count; i++)
        {
            Slot(i, out var x, out var y);
            children[i].Arrange(new Rect(x, y, ItemWidth, ItemHeight));
        }
        return Extent();
    }

    private Size Extent() => FlowDirection == Orientation.Horizontal
        ? new Size(_perLine * ItemWidth, _lines * ItemHeight)
        : new Size(_lines * ItemWidth, _perLine * ItemHeight);

    private void Slot(int index, out double x, out double y)
    {
        int line = index / _perLine;   // row (horizontal) or column (vertical)
        int pos = index % _perLine;    // position along that line
        if (FlowDirection == Orientation.Horizontal)
        {
            x = pos * ItemWidth;
            y = line * ItemHeight;
        }
        else
        {
            x = line * ItemWidth;
            y = pos * ItemHeight;
        }
    }
}
