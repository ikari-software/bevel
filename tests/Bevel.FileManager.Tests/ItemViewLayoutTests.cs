using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Core.Vfs;
using Bevel.FileManager.Components;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// Where the item views put things on screen (the Filer view-layout set: bevel-w912 onward).
/// Rule for these tests: show the window and run jobs BEFORE adding items, so the view mode's panel
/// is in place for the first layout pass (one test breaks that rule on purpose).
/// </summary>
public sealed class ItemViewLayoutTests
{
    internal static IVfsNode Node(string name) => new LayoutTestNode
    {
        Path = new VfsPath("test", name),
        DisplayName = name,
        Kind = VfsNodeKind.File,
        Size = 1,
        Modified = DateTimeOffset.UtcNow,
        TypeDescription = "Text Document",
    };

    internal static IVfsNode[] Nodes(int count, string prefix = "f")
        => Enumerable.Range(0, count).Select(i => Node($"{prefix}{i:D2}.txt")).ToArray();

    internal static (ItemView View, Window Window) Show(ViewMode mode, int w, int h,
        FlowDirection flow = FlowDirection.LeftToRight, IVfsNode[]? items = null)
    {
        var view = new ItemView { ViewMode = mode };
        var window = new Window { Content = view, Width = w, Height = h, FlowDirection = flow };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        if (items is not null)
        {
            view.Items = items;
            Dispatcher.UIThread.RunJobs();
        }
        return (view, window);
    }

    /// <summary>The realized container for the item at sorted position <paramref name="index"/>.</summary>
    internal static Control Container(ItemView view, int index)
        => view.ItemsControl.ContainerFromIndex(index)
           ?? throw new InvalidOperationException($"item {index} is not realized");

    /// <summary>A container's rect in the scroll viewer's coordinates (what the user sees).</summary>
    internal static Rect InScroller(ItemView view, Control c)
    {
        var origin = c.TranslatePoint(new Point(0, 0), view.ItemsScroller)
                     ?? throw new InvalidOperationException("not in the scroller's tree");
        return new Rect(origin, c.Bounds.Size);
    }

    [AvaloniaFact]
    public void Short_list_view_starts_at_the_left_edge_of_the_pane()
    {
        var (view, _) = Show(ViewMode.List, 830, 400, items: Nodes(5));
        Assert.Equal(0, InScroller(view, Container(view, 0)).X);
    }

    [AvaloniaFact]
    public void Short_list_view_starts_at_the_right_edge_under_rtl()
    {
        var (view, _) = Show(ViewMode.List, 830, 400, FlowDirection.RightToLeft, Nodes(5));
        Assert.Equal(view.ItemsScroller.Viewport.Width, InScroller(view, Container(view, 0)).Right);
    }

    [AvaloniaFact]
    public void Icons_start_at_the_top_left_when_items_arrive_before_load()
    {
        var view = new ItemView { ViewMode = ViewMode.LargeIcons };
        var window = new Window { Content = view, Width = 830, Height = 400 };
        window.Show();
        view.Items = Nodes(5);   // deliberately before the first RunJobs (see the class comment)
        Dispatcher.UIThread.RunJobs();

        var r = InScroller(view, Container(view, 0));
        Assert.Equal(new Point(0, 0), r.Position);
    }

    [AvaloniaFact]
    public void Details_rows_are_the_cell_height()
    {
        var (view, _) = Show(ViewMode.Details, 600, 400, items: Nodes(3));
        Assert.Equal(ItemView.CellSize(ViewMode.Details).Height, Container(view, 0).Bounds.Height);
    }

    // ── bevel-3fgd: a name never draws outside its cell ─────────────────

    internal static readonly string LongName = new string('x', 100) + ".txt";

    /// <summary>The name label of the container at <paramref name="index"/>.</summary>
    internal static TextBlock Label(ItemView view, int index)
        => Container(view, index).GetVisualDescendants().OfType<TextBlock>().First();

    /// <summary>The label's right edge in its container's coordinates.</summary>
    static double LabelRight(ItemView view, int index)
    {
        var label = Label(view, index);
        return label.TranslatePoint(new Point(label.Bounds.Width, 0), Container(view, index))!.Value.X;
    }

    [AvaloniaTheory]
    [InlineData(ViewMode.List)]
    [InlineData(ViewMode.SmallIcons)]
    public void Long_name_stays_inside_its_cell(ViewMode mode)
    {
        var (view, _) = Show(mode, 600, 400, items: new[] { Node("b.txt"), Node(LongName) });
        int i = view.ItemsControl.Items.Cast<ItemViewModel>().ToList().FindIndex(v => v.DisplayName == LongName);
        Assert.True(LabelRight(view, i) <= Container(view, i).Bounds.Width,
            $"label right {LabelRight(view, i)} > cell {Container(view, i).Bounds.Width}");
        Assert.True(Assert.IsType<NameLabel>(Label(view, i)).IsTrimmed);
    }

    [AvaloniaFact]
    public void Long_name_does_not_widen_details_rows()
    {
        var (view, _) = Show(ViewMode.Details, 600, 400, items: new[] { Node("b.txt"), Node(LongName) });
        foreach (var c in view.ItemsControl.GetRealizedContainers())
            Assert.True(c.Bounds.Width <= view.ItemsScroller.Viewport.Width,
                $"row {c.Bounds.Width} wider than viewport {view.ItemsScroller.Viewport.Width}");
    }

    static double ListColumnWidth(ItemView view)
        => Assert.IsType<VirtualizingWrapPanel>(view.ItemsControl.ItemsPanelRoot).Layout.ItemWidth;

    [AvaloniaFact]
    public void List_columns_fit_short_names()
    {
        var (view, _) = Show(ViewMode.List, 600, 400,
            items: new[] { Node("a.txt"), Node("b.txt"), Node("c.txt"), Node("d.txt"), Node("e.txt") });
        Assert.Equal(100, ListColumnWidth(view));
    }

    [AvaloniaFact]
    public void List_columns_grow_but_never_shrink_while_streaming()
    {
        var (view, _) = Show(ViewMode.List, 600, 400);
        view.ResetItems();
        view.AddItems(Nodes(5));
        Dispatcher.UIThread.RunJobs();
        double first = ListColumnWidth(view);

        var longOne = Node(new string('m', 40) + ".txt");
        view.AddItems(new[] { longOne });
        Dispatcher.UIThread.RunJobs();
        double grown = ListColumnWidth(view);
        Assert.True(grown > first, $"{grown} should exceed {first}");

        view.ReconcileItems(Nodes(5));   // the long name is gone, but a refresh must not jitter narrower
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(grown, ListColumnWidth(view));

        view.ResetItems();               // a new folder starts again from its own names
        view.AddItems(Nodes(5));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(first, ListColumnWidth(view));
    }
}

file sealed class LayoutTestNode : IVfsNode
{
    public VfsPath Path { get; init; }
    public string DisplayName { get; init; } = "";
    public VfsNodeKind Kind { get; init; }
    public bool MightHaveChildren { get; init; }
    public long? Size { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public string TypeDescription { get; init; } = "";
    public IconKey IconKey { get; init; }
    public VfsCapabilities Caps { get; init; }
}
