using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.FileManager.Components;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>Clicking an item with the mouse must select it — in every view mode.</summary>
public sealed class ClickSelectionTests
{
    private static IVfsNode Node(string name)
        => new ClickNode { Path = new VfsPath("test", name), DisplayName = name, Kind = VfsNodeKind.File };

    private static void ClickSelects(ViewMode mode, bool rtl = false)
    {
        var view = new ItemView { ViewMode = mode };
        var w = new Window { Content = view, Width = 500, Height = 400 };
        if (rtl) w.FlowDirection = FlowDirection.RightToLeft;   // ambient reading order -> mirrored layout
        w.Show();
        view.Items = new[] { Node("a.txt"), Node("b.txt"), Node("c.txt") };
        Dispatcher.UIThread.RunJobs();
        w.MouseMove(new Point(1, 1));          // nudge a layout/render pass
        Dispatcher.UIThread.RunJobs();

        var target = view.ItemsControl.GetRealizedContainers()
            .First(c => (c.DataContext as ItemViewModel)?.DisplayName == "b.txt");
        var center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), w)!.Value;

        w.MouseDown(center, MouseButton.Left);
        w.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("b.txt", view.SelectedItems.Single().DisplayName);
    }

    // ── Agent/reveal-driven multi-selection (bevel-nwo) ─────────────────────

    [AvaloniaFact]
    public void SelectPaths_highlights_multiple_items()
    {
        var view = new ItemView { ViewMode = ViewMode.Details };
        var w = new Window { Content = view, Width = 500, Height = 400 };
        w.Show();
        view.Items = new[] { Node("a.txt"), Node("b.txt"), Node("c.txt") };
        Dispatcher.UIThread.RunJobs();

        view.SelectPaths(new[] { new VfsPath("test", "a.txt"), new VfsPath("test", "c.txt") });

        Assert.Equal(new[] { "a.txt", "c.txt" }, view.SelectedItems.Select(i => i.DisplayName).OrderBy(n => n));
        Assert.All(view.SelectedItems, i => Assert.True(i.IsSelected));
    }

    [AvaloniaFact]
    public void SelectPaths_clears_previous_and_skips_missing()
    {
        var view = new ItemView { ViewMode = ViewMode.Details };
        var w = new Window { Content = view, Width = 500, Height = 400 };
        w.Show();
        view.Items = new[] { Node("a.txt"), Node("b.txt") };
        Dispatcher.UIThread.RunJobs();

        view.SelectPaths(new[] { new VfsPath("test", "a.txt") });
        view.SelectPaths(new[] { new VfsPath("test", "b.txt"), new VfsPath("test", "nope.txt") });

        Assert.Equal("b.txt", view.SelectedItems.Single().DisplayName);   // reselected b, cleared a, skipped nope
    }

    [AvaloniaFact]
    public void Click_selects_after_switching_view_mode()
    {
        var view = new ItemView { ViewMode = ViewMode.Details };
        var w = new Window { Content = view, Width = 500, Height = 400 };
        w.Show();
        view.Items = new[] { Node("a.txt"), Node("b.txt"), Node("c.txt") };
        Dispatcher.UIThread.RunJobs();

        view.ViewMode = ViewMode.LargeIcons;      // switch views, then click
        // LargeIcons virtualization can lag a frame under CI load — poll until b.txt is realized.
        Control? target = null;
        for (var i = 0; i < 50 && target is null; i++)
        {
            Dispatcher.UIThread.RunJobs();
            target = view.ItemsControl.GetRealizedContainers()
                .FirstOrDefault(c => (c.DataContext as ItemViewModel)?.DisplayName == "b.txt");
            if (target is null)
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
        Assert.NotNull(target);

        w.MouseMove(new Point(1, 1));
        Dispatcher.UIThread.RunJobs();

        var center = target!.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), w)!.Value;
        w.MouseDown(center, MouseButton.Left);
        w.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("b.txt", view.SelectedItems.Single().DisplayName);
    }

    [AvaloniaFact] public void Details_click_selects() => ClickSelects(ViewMode.Details);
    [AvaloniaFact] public void LargeIcons_click_selects() => ClickSelects(ViewMode.LargeIcons);
    [AvaloniaFact] public void List_click_selects() => ClickSelects(ViewMode.List);
    [AvaloniaFact] public void LargeIcons_click_selects_under_rtl() => ClickSelects(ViewMode.LargeIcons, rtl: true);
}

file sealed class ClickNode : IVfsNode
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
