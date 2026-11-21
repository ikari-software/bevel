using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
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
