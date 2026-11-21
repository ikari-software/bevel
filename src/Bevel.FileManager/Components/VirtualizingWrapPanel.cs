using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Bevel.FileManager.Components;

/// <summary>
/// Wrap panel for icon grid and list-column modes.
/// <see cref="Orientation"/> Horizontal = Large/Small Icons, Thumbnails (items flow left-to-right,
/// wrap down). Vertical = List view (items flow top-to-bottom, wrap into columns).
///
/// All geometry comes from <see cref="Layout"/> (<see cref="WrapLayout"/>), so keyboard navigation and
/// scroll-into-view agree with what is drawn. The panel always fills its slot: ArrangeOverride returns
/// the slot, not the item extent, because Avalonia centres a Stretch-aligned control that reports less
/// than its slot (bevel-w912: a short List view used to sit in the middle of the pane).
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

    static VirtualizingWrapPanel() =>
        AffectsMeasure<VirtualizingWrapPanel>(ItemWidthProperty, ItemHeightProperty, OrientationProperty);

    public double ItemWidth { get => GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }
    public Orientation Orientation { get => GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }

    /// <summary>The geometry of the last measure pass.</summary>
    internal WrapLayout Layout { get; private set; }

    protected override Size MeasureOverride(Size available)
    {
        var children = Children;
        int count = children.Count;
        if (count == 0 || ItemWidth <= 0 || ItemHeight <= 0)
        {
            Layout = default;
            return default;
        }

        bool horiz = Orientation == Orientation.Horizontal;
        // Wrapping is driven by the CROSS extent: width for horizontal flow, height for vertical.
        // A ScrollViewer that allows scrolling on that axis measures us at infinity; WrapLayout guards
        // it, and the caller disables the matching scrollbar (see ItemView.ApplyViewMode).
        Layout = WrapLayout.Compute(count, horiz ? available.Width : available.Height, ItemWidth, ItemHeight, horiz);

        for (int i = 0; i < count; i++)
            children[i].Measure(new Size(ItemWidth, ItemHeight));

        return Layout.Extent;
    }

    protected override Size ArrangeOverride(Size final)
    {
        var children = Children;
        bool rtl = FlowDirection == Avalonia.Media.FlowDirection.RightToLeft;
        var layout = Layout;
        if (layout.PerLine > 0)
            for (int i = 0; i < children.Count; i++)
                children[i].Arrange(layout.RectFor(i, final.Width, rtl));

        // Fill the slot (see the class comment); Max keeps the result valid if a parent ever
        // arranges us smaller than we measured.
        var e = layout.Extent;
        return new Size(Math.Max(final.Width, e.Width), Math.Max(final.Height, e.Height));
    }
}
