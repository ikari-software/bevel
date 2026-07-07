using System.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.FileManager.Components;
using Xunit;

namespace Bevel.FileManager.Tests;

public class ItemViewTests
{
    private static IVfsNode Node(string name, VfsNodeKind kind = VfsNodeKind.File, long? size = null)
    {
        var path = new VfsPath("test", name);
        return new TestNode
        {
            Path = path, DisplayName = name, Kind = kind, Size = size,
            Modified = kind == VfsNodeKind.Folder ? null : DateTimeOffset.UtcNow,
            TypeDescription = kind == VfsNodeKind.Folder ? "File Folder" : "Text Document",
            MightHaveChildren = kind == VfsNodeKind.Folder,
        };
    }

    static void PumpAndShow(Avalonia.Controls.Control view, int w = 600, int h = 400)
    {
        new Avalonia.Controls.Window { Content = view, Width = w, Height = h }.Show();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Details_sets_columns_visible()
    {
        var view = new ItemView { ViewMode = ViewMode.Details };
        PumpAndShow(view);
        Assert.True(view.ColumnHeaderBorder.IsVisible);
    }

    [AvaloniaFact]
    public void LargeIcons_hides_columns()
    {
        var view = new ItemView { ViewMode = ViewMode.LargeIcons };
        PumpAndShow(view);
        Assert.False(view.ColumnHeaderBorder.IsVisible);
    }

    [AvaloniaFact]
    public void Items_populates_source()
    {
        var view = new ItemView { ViewMode = ViewMode.Details };
        PumpAndShow(view);

        view.Items = new[] { Node("a.txt"), Node("b.txt"), Node("dir", VfsNodeKind.Folder) };
        Dispatcher.UIThread.RunJobs();

        var src = (System.Collections.IList)view.ItemsControl.ItemsSource!;
        Assert.Equal(3, src.Count);
        Assert.Equal("a.txt", ((ItemViewModel)src[0]!).DisplayName);
    }

    [AvaloniaFact]
    public void Switch_view_mode_keeps_items()
    {
        var view = new ItemView { ViewMode = ViewMode.LargeIcons };
        PumpAndShow(view);

        view.Items = new[] { Node("x.txt"), Node("y.txt") };
        Dispatcher.UIThread.RunJobs();
        view.ViewMode = ViewMode.Details;
        Dispatcher.UIThread.RunJobs();

        var src = (System.Collections.IList)view.ItemsControl.ItemsSource!;
        Assert.Equal(2, src.Count);
        Assert.True(view.ColumnHeaderBorder.IsVisible);
    }

    [AvaloniaFact]
    public void Sort_default_name_ascending()
    {
        var view = new ItemView { ViewMode = ViewMode.Details };
        PumpAndShow(view);

        view.Items = new[] { Node("ccc.txt"), Node("aaa.txt"), Node("bbb.txt") };
        Dispatcher.UIThread.RunJobs();

        var src = (System.Collections.IList)view.ItemsControl.ItemsSource!;
        Assert.Equal("aaa.txt", ((ItemViewModel)src[0]!).DisplayName);
    }

    [AvaloniaFact]
    public void Empty_items_clears()
    {
        var view = new ItemView { ViewMode = ViewMode.Details };
        PumpAndShow(view);

        view.Items = Array.Empty<IVfsNode>();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty((System.Collections.IList)view.ItemsControl.ItemsSource!);
    }

    // ── Differential reload (keyed reconcile — no flicker) ─────────────

    static ItemViewModel[] Rows(ItemView view)
        => ((System.Collections.IList)view.ItemsControl.ItemsSource!).Cast<ItemViewModel>().ToArray();

    [AvaloniaFact]
    public void ReconcileItems_keeps_row_identity_when_unchanged()
    {
        var view = new ItemView { ViewMode = ViewMode.Details };
        PumpAndShow(view);
        view.Items = new[] { Node("a.txt"), Node("b.txt"), Node("c.txt") };
        Dispatcher.UIThread.RunJobs();
        var before = Rows(view);

        // A real re-enumeration yields fresh node instances with the same paths.
        view.ReconcileItems(new[] { Node("a.txt"), Node("b.txt"), Node("c.txt") });
        Dispatcher.UIThread.RunJobs();
        var after = Rows(view);

        Assert.Equal(3, after.Length);
        // Same objects survive → nothing is torn down and rebuilt, so the list can't flicker.
        Assert.Same(before.Single(v => v.DisplayName == "a.txt"), after.Single(v => v.DisplayName == "a.txt"));
        Assert.Same(before.Single(v => v.DisplayName == "b.txt"), after.Single(v => v.DisplayName == "b.txt"));
        Assert.Same(before.Single(v => v.DisplayName == "c.txt"), after.Single(v => v.DisplayName == "c.txt"));
    }

    [AvaloniaFact]
    public void ReconcileItems_adds_new_and_drops_missing_keeping_survivors()
    {
        var view = new ItemView { ViewMode = ViewMode.Details };
        PumpAndShow(view);
        view.Items = new[] { Node("a.txt"), Node("b.txt"), Node("c.txt") };
        Dispatcher.UIThread.RunJobs();
        var b = Rows(view).Single(v => v.DisplayName == "b.txt");

        // a.txt vanished, d.txt appeared; b and c persist.
        view.ReconcileItems(new[] { Node("b.txt"), Node("c.txt"), Node("d.txt") });
        Dispatcher.UIThread.RunJobs();
        var after = Rows(view);

        Assert.Equal(new[] { "b.txt", "c.txt", "d.txt" }, after.Select(v => v.DisplayName));
        Assert.Same(b, after.Single(v => v.DisplayName == "b.txt")); // survivor kept its identity
    }
}

file sealed class TestNode : IVfsNode
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
    public IReadOnlyDictionary<string, object?> ExtraColumns { get; init; } = new Dictionary<string, object?>();
}