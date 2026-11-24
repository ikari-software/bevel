using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.FileManager.Components;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// The icon-grid layout: horizontal flow wraps to the available WIDTH, vertical flow (List)
/// wraps to the available HEIGHT, and an unconstrained (infinite) cross-extent must not blow up
/// into int.MaxValue lines — the bug that made Large Icons render as one endless row.
///
/// bevel-rzz: the panel is now an Avalonia <see cref="VirtualizingPanel"/>, so tests host it in an
/// ItemsControl (its containers come from the item container generator, not Children.Add), and a
/// 100k-entry folder realizes only the viewport plus buffer, not every item.
/// </summary>
public sealed class VirtualizingWrapPanelTests
{
    private sealed class Item
    {
        public int Index { get; init; }
    }

    /// <summary>Host the panel in an ItemsControl inside a ScrollViewer inside a window, the same
    /// shape ItemView uses. Horizontal flow disables the horizontal scrollbar (so the width is
    /// finite and the grid wraps), vertical flow disables the vertical one. Returns the window and
    /// the panel; <paramref name="rtl"/> mirrors the horizontal axis.</summary>
    private static (Window W, VirtualizingWrapPanel Panel, ItemsControl Items) Host(
        int count, double cellW, double cellH, Orientation flow, double winW, double winH, bool rtl = false)
    {
        var panel = new VirtualizingWrapPanel { ItemWidth = cellW, ItemHeight = cellH, Orientation = flow };
        var items = new ItemsControl { ItemsPanel = new FuncTemplate<Panel?>(() => panel) };
        items.ItemsSource = Enumerable.Range(0, count).Select(i => new Item { Index = i }).ToList();
        var scroll = new ScrollViewer
        {
            Content = items,
            HorizontalScrollBarVisibility = flow == Orientation.Horizontal ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = flow == Orientation.Horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
        };
        var w = new Window { Content = scroll, Width = winW, Height = winH };
        if (rtl) w.FlowDirection = Avalonia.Media.FlowDirection.RightToLeft;
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return (w, panel, items);
    }

    [AvaloniaFact]
    public void Horizontal_flow_wraps_to_the_available_width()
    {
        // 320/80 = 4 per row -> ceil(10/4) = 3 rows
        var (_, p, _) = Host(10, 80, 60, Orientation.Horizontal, 320, 1000);
        Assert.Equal(new Size(4 * 80, 3 * 60), p.DesiredSize);
    }

    [AvaloniaFact]
    public void Infinite_width_does_not_explode_into_one_giant_row()
    {
        var panel = new VirtualizingWrapPanel { ItemWidth = 80, ItemHeight = 60, Orientation = Orientation.Horizontal };
        var items = new ItemsControl { ItemsPanel = new FuncTemplate<Panel?>(() => panel) };
        items.ItemsSource = Enumerable.Range(0, 5).Select(i => new Item { Index = i }).ToList();
        // No scrollbar constraint on the width: the panel is measured at infinite width.
        var w = new Window { Content = items, Width = 400, Height = 200 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        panel.Measure(new Size(double.PositiveInfinity, 1000)); // guard: fall back to a single line
        Assert.Equal(new Size(5 * 80, 60), panel.DesiredSize);   // one row of 5, NOT int.MaxValue cols
        w.Close();
    }

    [AvaloniaFact]
    public void Vertical_flow_wraps_to_the_available_height_into_columns()
    {
        // 80/20 = 4 per column -> ceil(10/4) = 3 columns; window content height 80.
        var (_, p, _) = Host(10, 180, 20, Orientation.Vertical, 1000, 80);
        Assert.Equal(new Size(3 * 180, 4 * 20), p.DesiredSize);
    }

    [AvaloniaFact]
    public void RightToLeft_mirrors_horizontal_placement()
    {
        var (_, p, items) = Host(3, 80, 60, Orientation.Horizontal, 320, 1000, rtl: true);

        // First item hugs the right edge; subsequent items march leftward.
        Assert.Equal(320 - 80, items.ContainerFromIndex(0)!.Bounds.X);   // rightmost slot
        Assert.Equal(320 - 160, items.ContainerFromIndex(1)!.Bounds.X);
        Assert.Equal(320 - 240, items.ContainerFromIndex(2)!.Bounds.X);
        Assert.Equal(0, items.ContainerFromIndex(0)!.Bounds.Y);          // still the first row
    }

    // bevel-w912: the panel must fill its slot. Returning only the item extent from ArrangeOverride
    // made Avalonia centre a Stretch-aligned panel, so a short List view sat mid-pane.
    [AvaloniaFact]
    public void Columns_narrower_than_the_slot_start_at_the_left_edge()
    {
        // 2 per column, 3 columns of 180 in an 800-wide pane
        var (_, p, items) = Host(5, 180, 18, Orientation.Vertical, 800, 40);
        Assert.Equal(0, p.Bounds.X);
        Assert.Equal(800, p.Bounds.Width);
        Assert.Equal(0, items.ContainerFromIndex(0)!.Bounds.X);
        Assert.Equal(180, items.ContainerFromIndex(2)!.Bounds.X);
    }

    [AvaloniaFact]
    public void Columns_start_at_the_right_edge_under_rtl()
    {
        var (_, p, items) = Host(5, 180, 18, Orientation.Vertical, 800, 40, rtl: true);
        Assert.Equal(0, p.Bounds.X);   // child bounds are panel-relative; the panel itself must not shift
        Assert.Equal(620, items.ContainerFromIndex(0)!.Bounds.X);
        Assert.Equal(440, items.ContainerFromIndex(2)!.Bounds.X);
    }

    [AvaloniaFact]
    public void Changing_item_width_relays_out()
    {
        var (_, p, items) = Host(5, 180, 18, Orientation.Vertical, 800, 40);
        p.ItemWidth = 100;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(100, items.ContainerFromIndex(2)!.Bounds.X);
    }

    [AvaloniaFact]
    public void Rows_keep_the_spare_width_on_the_trailing_side()
    {
        var (_, p, items) = Host(3, 80, 60, Orientation.Horizontal, 830, 1000);
        Assert.Equal(0, p.Bounds.X);
        Assert.Equal(0, items.ContainerFromIndex(0)!.Bounds.X);
        Assert.Equal(0, items.ContainerFromIndex(0)!.Bounds.Y);
    }

    [AvaloniaFact]
    public void Items_changed_in_place_keep_container_indices_in_step()
    {
        var panel = new VirtualizingWrapPanel { ItemWidth = 180, ItemHeight = 18, Orientation = Orientation.Vertical };
        var items = new ItemsControl { ItemsPanel = new FuncTemplate<Panel?>(() => panel) };
        var list = new System.Collections.ObjectModel.ObservableCollection<Item>(
            Enumerable.Range(0, 5).Select(i => new Item { Index = i }));
        items.ItemsSource = list;
        var scroll = new ScrollViewer
        {
            Content = items,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var w = new Window { Content = scroll, Width = 800, Height = 40 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        list.Insert(1, new Item { Index = 99 });
        Dispatcher.UIThread.RunJobs();
        // 2 items per column: index 3 sits in the second column (indices 2,3).
        Assert.Equal(180, items.ContainerFromIndex(3)!.Bounds.X);
        Assert.Equal(99, ((Item)items.ContainerFromIndex(1)!.DataContext!).Index);
        w.Close();
    }

    // bevel-rzz: a huge folder must not realize every item — only the viewport plus a one-viewport
    // buffer of lines.
    [AvaloniaFact]
    public void Large_folder_realizes_only_the_viewport()
    {
        var view = new ItemView { ViewMode = ViewMode.LargeIcons };
        var w = new Window { Content = view, Width = 600, Height = 500 };
        w.Show();
        view.Items = Enumerable.Range(0, 100_000)
            .Select(i => (IVfsNode)new PanelTestNode
            {
                Path = new VfsPath("test", $"file{i}.txt"),
                DisplayName = $"file{i}.txt",
                Kind = VfsNodeKind.File,
            })
            .ToList();
        Dispatcher.UIThread.RunJobs();

        var realized = view.ItemsControl.GetRealizedContainers().Count();
        Assert.True(realized < 400, $"expected fewer than 400 realized containers, got {realized}");
        Assert.True(realized > 0);
        w.Close();
    }
}

file sealed class PanelTestNode : IVfsNode
{
    public VfsPath Path { get; init; }
    public string DisplayName { get; init; } = "";
    public VfsNodeKind Kind { get; init; }
    public bool MightHaveChildren { get; init; }
    public long? Size { get; init; }
    public System.DateTimeOffset? Modified { get; init; }
    public string TypeDescription { get; init; } = "";
    public IconKey IconKey { get; init; }
    public VfsCapabilities Caps { get; init; }
}
