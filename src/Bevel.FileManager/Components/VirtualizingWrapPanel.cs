using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Bevel.FileManager.Components;

/// <summary>
/// Wrap panel for icon grid and list-column modes, on Avalonia's <see cref="VirtualizingPanel"/>
/// (bevel-rzz): only the viewport plus a one-viewport buffer of lines is realized, so a 100k-entry
/// folder costs a few dozen containers instead of 100k. Cells are uniform, so all geometry comes
/// from <see cref="Layout"/> (<see cref="WrapLayout"/>), and realization/recycling follows Avalonia's
/// own VirtualizingStackPanel pattern (<c>ItemContainerGenerator.NeedsContainer/CreateContainer/
/// PrepareItemContainer/ItemContainerPrepared/ClearItemContainer</c> + a keyed recycle pool).
///
/// <see cref="Orientation"/> Horizontal = Large/Small Icons, Thumbnails (items flow left-to-right,
/// wrap down). Vertical = List view (items flow top-to-bottom, wrap into columns).
///
/// The panel always fills its slot: ArrangeOverride returns the slot, not the item extent, because
/// Avalonia centres a Stretch-aligned control that reports less than its slot (bevel-w912: a short
/// List view used to sit in the middle of the pane).
///
/// Honours the ambient <see cref="Visual.FlowDirection"/>: under RightToLeft the horizontal axis
/// is mirrored, so items start at the right edge.
/// </summary>
public class VirtualizingWrapPanel : VirtualizingPanel
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

    /// <summary>Index → realized container. Only the current <see cref="WrapLayout.RangeFor"/> window is in it.</summary>
    private readonly Dictionary<int, Control> _realized = new();
    private readonly Dictionary<object, Stack<Control>> _recyclePool = new();
    private Rect _viewport;
    private bool _hasViewport;

    // A container that was focused when it was recycled is kept out of the pool and reused for its
    // index (Avalonia's VirtualizingStackPanel pattern), so focus — and with it the container's
    // bindings, e.g. the ShowFullName ZIndex — survives scrolling away and back.
    private int _focusedIndex = -1;
    private Control? _focusedElement;

    private static readonly AttachedProperty<object?> RecycleKeyProperty =
        AvaloniaProperty.RegisterAttached<VirtualizingWrapPanel, Control, object?>("RecycleKey");
    private static readonly object s_itemIsItsOwnContainer = new();

    public VirtualizingWrapPanel()
    {
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        _viewport = e.EffectiveViewport.Intersect(new Rect(Bounds.Size));
        _hasViewport = true;

        // Re-measure only when the buffered range actually moved, so the viewport event fired by our
        // own layout pass does not loop.
        if (Layout.Count > 0 && _realized.Count > 0)
        {
            var (first, last) = RangeToRealize();
            var (rf, rl) = (_realized.Keys.Min(), _realized.Keys.Max());
            if (first == rf && last == rl) return;
        }
        InvalidateMeasure();
    }

    /// <summary>The item range to realize for the current viewport: the viewport plus a one-viewport
    /// buffer along the scrolling axis (RangeFor enforces a two-line minimum). Before the first
    /// effective viewport, just that minimum around the origin.</summary>
    private (int First, int Last) RangeToRealize()
    {
        bool horiz = Orientation == Orientation.Horizontal;
        Rect vp;
        if (_hasViewport)
        {
            vp = _viewport;
            // Under RightToLeft the horizontal axis is mirrored (index 0 sits at the right edge), so
            // the visible line range must be read from the mirrored viewport — otherwise a List view
            // scrolled to its reading start would realize the far (left) columns and not item 0.
            if (FlowDirection == Avalonia.Media.FlowDirection.RightToLeft && !horiz)
                vp = new Rect(Bounds.Width - vp.Right, vp.Top, vp.Width, vp.Height);
        }
        else
        {
            // No viewport yet: realize the minimum at the origin; the first real viewport fixes it.
            vp = new Rect(0, 0, horiz ? double.PositiveInfinity : 0, horiz ? 0 : double.PositiveInfinity);
        }
        double buffer = horiz ? Math.Max(vp.Height, ItemHeight) : Math.Max(vp.Width, ItemWidth);
        return Layout.RangeFor(vp, buffer);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var items = Items;
        int count = items.Count;
        if (count == 0 || ItemWidth <= 0 || ItemHeight <= 0)
        {
            Layout = default;
            RecycleAll();
            return default;
        }

        bool horiz = Orientation == Orientation.Horizontal;
        // Wrapping is driven by the CROSS extent: width for horizontal flow, height for vertical.
        // A ScrollViewer that allows scrolling on that axis measures us at infinity; WrapLayout guards
        // it, and the caller disables the matching scrollbar (see ItemView.ApplyViewMode).
        Layout = WrapLayout.Compute(count, horiz ? availableSize.Width : availableSize.Height, ItemWidth, ItemHeight, horiz);

        var (first, last) = RangeToRealize();

        foreach (var kv in _realized.ToArray())
            if (kv.Key < first || kv.Key > last)
            {
                RecycleElement(kv.Value, kv.Key);
                _realized.Remove(kv.Key);
            }
        for (int i = first; i <= last; i++)
            if (!_realized.ContainsKey(i))
                _realized[i] = GetOrCreateElement(items, i);

        var cell = new Size(ItemWidth, ItemHeight);
        foreach (var c in _realized.Values)
            c.Measure(cell);

        return Layout.Extent;
    }

    protected override Size ArrangeOverride(Size final)
    {
        bool rtl = FlowDirection == Avalonia.Media.FlowDirection.RightToLeft;
        foreach (var (i, c) in _realized)
            c.Arrange(Layout.RectFor(i, final.Width, rtl));

        // Fill the slot (see the class comment); Max keeps the result valid if a parent ever
        // arranges us smaller than we measured.
        var e = Layout.Extent;
        return new Size(Math.Max(final.Width, e.Width), Math.Max(final.Height, e.Height));
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(items, e);
        InvalidateMeasure();

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                Shift(e.NewStartingIndex, e.NewItems!.Count);
                break;
            case NotifyCollectionChangedAction.Remove:
                for (int i = e.OldStartingIndex; i < e.OldStartingIndex + e.OldItems!.Count; i++)
                    if (_realized.Remove(i, out var c)) RecycleElementOnItemRemoved(c);
                Shift(e.OldStartingIndex, -e.OldItems.Count);
                break;
            case NotifyCollectionChangedAction.Replace:
                for (int i = e.NewStartingIndex; i < e.NewStartingIndex + e.NewItems!.Count; i++)
                    if (_realized.Remove(i, out var c)) RecycleElementOnItemRemoved(c);
                break;
            default: // Move, Reset
                RecycleAll();
                break;
        }
    }

    /// <summary>Shift the realized indices at or after <paramref name="from"/> by <paramref name="delta"/>,
    /// telling the container generator so it keeps its container→index map in step.</summary>
    private void Shift(int from, int delta)
    {
        if (delta == 0 || _realized.Count == 0) return;
        var generator = ItemContainerGenerator;
        foreach (var kv in _realized.ToArray())
            if (kv.Key >= from)
            {
                _realized.Remove(kv.Key);
                int newIndex = kv.Key + delta;
                _realized[newIndex] = kv.Value;
                generator?.ItemContainerIndexChanged(kv.Value, kv.Key, newIndex);
            }
    }

    private void RecycleAll()
    {
        foreach (var kv in _realized)
            RecycleElementOnItemRemoved(kv.Value);
        _realized.Clear();
    }

    // ── VirtualizingPanel abstract members ──────────────────────────────

    protected override Control? ScrollIntoView(int index)
    {
        var items = Items;
        if (index < 0 || index >= items.Count) return null;
        if (!_realized.TryGetValue(index, out var el))
        {
            el = GetOrCreateElement(items, index);
            _realized[index] = el;
            el.Measure(new Size(ItemWidth, ItemHeight));
        }
        return el;
    }

    protected override Control? ContainerFromIndex(int index)
        => _realized.TryGetValue(index, out var c) ? c : null;

    protected override int IndexFromContainer(Control container)
    {
        foreach (var kv in _realized)
            if (ReferenceEquals(kv.Value, container)) return kv.Key;
        return -1;
    }

    protected override IEnumerable<Control>? GetRealizedContainers() => _realized.Values;


    // ItemView owns keyboard navigation itself; the panel reports none.
    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap) => null;

    // ── realize / recycle (pattern copied from Avalonia's VirtualizingStackPanel, 11.3.18) ──

    private Control GetOrCreateElement(IReadOnlyList<object?> items, int index)
    {
        // A container stashed as focused when it was recycled gets its index back first.
        if (_focusedIndex == index && _focusedElement is { } focused)
        {
            _focusedIndex = -1;
            _focusedElement = null;
            return focused;
        }

        var item = items[index];
        var generator = ItemContainerGenerator!;

        if (generator.NeedsContainer(item, index, out var recycleKey))
            return GetRecycledElement(item, index, recycleKey) ?? CreateElement(item, index, recycleKey);

        return GetItemAsOwnContainer(item, index);
    }

    private Control GetItemAsOwnContainer(object? item, int index)
    {
        var controlItem = (Control)item!;
        var generator = ItemContainerGenerator!;

        if (!controlItem.IsSet(RecycleKeyProperty))
        {
            generator.PrepareItemContainer(controlItem, controlItem, index);
            AddInternalChild(controlItem);
            controlItem.SetValue(RecycleKeyProperty, s_itemIsItsOwnContainer);
            generator.ItemContainerPrepared(controlItem, item, index);
        }

        controlItem.SetCurrentValue(Visual.IsVisibleProperty, true);
        return controlItem;
    }

    private Control? GetRecycledElement(object? item, int index, object? recycleKey)
    {
        if (recycleKey is null) return null;
        var generator = ItemContainerGenerator!;

        if (_recyclePool.TryGetValue(recycleKey, out var pool) && pool.Count > 0)
        {
            var recycled = pool.Pop();
            recycled.SetCurrentValue(Visual.IsVisibleProperty, true);
            generator.PrepareItemContainer(recycled, item, index);
            AddInternalChild(recycled);
            generator.ItemContainerPrepared(recycled, item, index);
            return recycled;
        }
        return null;
    }

    private Control CreateElement(object? item, int index, object? recycleKey)
    {
        var generator = ItemContainerGenerator!;
        var container = generator.CreateContainer(item, index, recycleKey);

        container.SetValue(RecycleKeyProperty, recycleKey);
        generator.PrepareItemContainer(container, item, index);
        AddInternalChild(container);
        generator.ItemContainerPrepared(container, item, index);
        return container;
    }

    private void RecycleElement(Control element, int index)
    {
        var recycleKey = element.GetValue(RecycleKeyProperty);

        if (recycleKey is null)
        {
            ItemContainerGenerator!.ClearItemContainer(element);
            RemoveInternalChild(element);
        }
        else if (ReferenceEquals(recycleKey, s_itemIsItsOwnContainer))
        {
            element.SetCurrentValue(Visual.IsVisibleProperty, false);
        }
        else if (KeyboardNavigation.GetTabOnceActiveElement(ItemsControl) == element)
        {
            _focusedElement = element;
            _focusedIndex = index;
        }
        else
        {
            ItemContainerGenerator!.ClearItemContainer(element);
            PushToRecyclePool(recycleKey, element);
            element.SetCurrentValue(Visual.IsVisibleProperty, false);
            RemoveInternalChild(element);
        }
    }

    private void RecycleElementOnItemRemoved(Control element)
    {
        var recycleKey = element.GetValue(RecycleKeyProperty);

        if (recycleKey is null)
        {
            ItemContainerGenerator!.ClearItemContainer(element);
            RemoveInternalChild(element);
        }
        else if (ReferenceEquals(recycleKey, s_itemIsItsOwnContainer))
        {
            RemoveInternalChild(element);
        }
        else
        {
            ItemContainerGenerator!.ClearItemContainer(element);
            PushToRecyclePool(recycleKey, element);
            element.SetCurrentValue(Visual.IsVisibleProperty, false);
            RemoveInternalChild(element);
        }
    }

    private void PushToRecyclePool(object recycleKey, Control element)
    {
        if (!_recyclePool.TryGetValue(recycleKey, out var pool))
            _recyclePool[recycleKey] = pool = new Stack<Control>();
        pool.Push(element);
    }
}
