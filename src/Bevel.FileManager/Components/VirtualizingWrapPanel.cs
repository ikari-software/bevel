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

    private int _cols;
    private int _rows;

    protected override Size MeasureOverride(Size available)
    {
        var children = Children;
        int count = children.Count;
        if (count == 0 || ItemWidth <= 0 || ItemHeight <= 0)
            return default;

        bool horiz = FlowDirection == Orientation.Horizontal;
        double span = horiz ? available.Width : available.Height;
        _cols = Math.Max(1, (int)(span / ItemWidth));
        _rows = (int)Math.Ceiling((double)count / _cols);

        for (int i = 0; i < count; i++)
        {
            children[i].Measure(new Size(ItemWidth, ItemHeight));
        }

        return new Size(_cols * ItemWidth, _rows * ItemHeight);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var children = Children;
        int count = children.Count;

        for (int i = 0; i < count; i++)
        {
            Slot(i, out var x, out var y);
            children[i].Arrange(new Rect(x, y, ItemWidth, ItemHeight));
        }

        return new Size(_cols * ItemWidth, _rows * ItemHeight);
    }

    private void Slot(int index, out double x, out double y)
    {
        int line = index / _cols;
        int pos = index % _cols;
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
