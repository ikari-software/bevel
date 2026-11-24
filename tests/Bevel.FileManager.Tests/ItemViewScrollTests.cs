using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Core.Vfs;
using Bevel.FileManager.Components;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// bevel-rzz: type-ahead scrolls smoothly (ease-out over 120 ms, driven by the render loop) when the
/// match is within three viewports, and jumps when it is further away; arrows jump. The animation is
/// driven deterministically in tests with <see cref="AvaloniaHeadlessPlatform.ForceRenderTimerTick"/> —
/// no sleeps.
/// </summary>
public sealed class ItemViewScrollTests
{
    private static IVfsNode Node(string name)
        => new ScrollTestNode { Path = new VfsPath("test", name), DisplayName = name, Kind = VfsNodeKind.File };

    private static (Window W, ItemView View, ScrollViewer Scroller) Host(ViewMode mode, int count, double w, double h)
    {
        var view = new ItemView { ViewMode = mode };
        var win = new Window { Content = view, Width = w, Height = h };
        win.Show();
        view.Items = Enumerable.Range(0, count).Select(i => Node($"n{i:D3}.txt")).ToList();
        Dispatcher.UIThread.RunJobs();
        view.Focus();
        Dispatcher.UIThread.RunJobs();
        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().Single();
        return (win, view, scroller);
    }

    private static void PumpFrames(int frames)
    {
        for (int i = 0; i < frames; i++)
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(16);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void TypeAhead_in_Details_animates_the_scroll_and_lands_on_the_match()
    {
        var (w, view, scroller) = Host(ViewMode.Details, 100, 400, 300);
        view.SelectPath(new VfsPath("test", "n000.txt"));
        Dispatcher.UIThread.RunJobs();

        w.KeyTextInput("n04");   // matches n040.txt, index 40 — about two viewports down: smooth
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("n040.txt", view.SelectedItems.Single().DisplayName);
        Assert.True(view.ScrollAnimationRunning, "a nearby match must take the animated path, not jump");

        PumpFrames(12);                                // drive the render loop past the 120 ms ease-out
        double endY = scroller.Offset.Y;
        Assert.True(endY > 0, $"expected the view to have scrolled, got {endY}");

        var c = view.ItemsControl.ContainerFromIndex(40);
        Assert.NotNull(c);
        Assert.True(c!.Bounds.Y >= endY - 0.5 && c.Bounds.Bottom <= endY + scroller.Viewport.Height + 0.5,
            $"item 40 (y={c.Bounds.Y}..{c.Bounds.Bottom}) must be inside the viewport at {endY}..{endY + scroller.Viewport.Height}");
        w.Close();
    }

    [AvaloniaFact]
    public void TypeAhead_beyond_three_viewports_jumps_instead_of_animating()
    {
        var (w, view, scroller) = Host(ViewMode.Details, 100, 400, 300);
        view.SelectPath(new VfsPath("test", "n000.txt"));
        Dispatcher.UIThread.RunJobs();

        w.KeyTextInput("n09");   // index 90 — far more than three viewports away
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("n090.txt", view.SelectedItems.Single().DisplayName);
        // Far target: lands one viewport short synchronously, then animates the rest — never a
        // whole-path animation. KeyTextInput pumps render frames internally, so by the time it
        // returns the animation may already have finished; either way the view must have moved far.
        Assert.True(scroller.Offset.Y > 3 * scroller.Viewport.Height - 2 * 22,
            $"a distant match must jump most of the way immediately, got {scroller.Offset.Y}");
        PumpFrames(12);
        var c = view.ItemsControl.ContainerFromIndex(90);
        Assert.NotNull(c);
        Assert.True(c!.Bounds.Y >= scroller.Offset.Y - 0.5 && c.Bounds.Bottom <= scroller.Offset.Y + scroller.Viewport.Height + 0.5,
            $"item 90 must be visible after the animation, viewport {scroller.Offset.Y}..{scroller.Offset.Y + scroller.Viewport.Height}, item {c.Bounds.Y}..{c.Bounds.Bottom}");
        w.Close();
    }

    [AvaloniaFact]
    public void TypeAhead_in_List_animates_the_horizontal_scroll()
    {
        var (w, view, scroller) = Host(ViewMode.List, 100, 200, 300);
        view.SelectPath(new VfsPath("test", "n000.txt"));
        Dispatcher.UIThread.RunJobs();

        w.KeyTextInput("n04");   // index 40 — a couple of columns over: smooth on the X axis
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("n040.txt", view.SelectedItems.Single().DisplayName);
        Assert.True(view.ScrollAnimationRunning, "List type-ahead within three viewports must animate on X");

        PumpFrames(12);
        double endX = scroller.Offset.X;
        Assert.True(endX > 0, $"expected the horizontal scroll to have run, got {endX}");

        var c = view.ItemsControl.ContainerFromIndex(40);
        Assert.NotNull(c);
        Assert.True(c!.Bounds.X >= endX && c.Bounds.Right <= endX + scroller.Viewport.Width + 0.5,
            $"item 40 (x={c.Bounds.X}..{c.Bounds.Right}) must be inside the viewport at {endX}..{endX + scroller.Viewport.Width}");
        w.Close();
    }

    [AvaloniaFact]
    public void Arrow_keys_jump_without_animation()
    {
        var (w, view, scroller) = Host(ViewMode.Details, 100, 400, 300);
        view.SelectPath(new VfsPath("test", "n000.txt"));
        view.Focus();
        Dispatcher.UIThread.RunJobs();

        w.KeyPressQwerty(Avalonia.Input.PhysicalKey.ArrowDown, Avalonia.Input.RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        w.KeyPressQwerty(Avalonia.Input.PhysicalKey.End, Avalonia.Input.RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("n099.txt", view.SelectedItems.Single().DisplayName);
        Assert.False(view.ScrollAnimationRunning, "arrows jump, they do not animate");
        Assert.True(scroller.Offset.Y > 0);   // End scrolled straight there
        w.Close();
    }
}

file sealed class ScrollTestNode : IVfsNode
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
