using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.FileManager.Components;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// A marquee must highlight as it sweeps, not on release (bevel-oovi).
///
/// <para>The band was drawn live but the selection was applied once, in the pointer-RELEASED handler, so
/// a sweep showed no highlight at all until you let go — you could not see what you were about to
/// select. Windows 2000 and Finder both highlight live, and this is squarely the "colours + feel +
/// function" bar the project is held to; the feel was wrong.</para>
///
/// <para>Driven through the real headless pointer so the assertions sit on the same handlers a user
/// hits: these fail against the release-only implementation.</para>
/// </summary>
public sealed class MarqueeSelectionTests
{
    private sealed class Node : IVfsNode
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

    private static IVfsNode N(string name) =>
        new Node { Path = new VfsPath("test", name), DisplayName = name };

    private sealed record Fixture(ItemView View, Window Window);

    private static Fixture Realize(int count, ViewMode mode = ViewMode.Details)
    {
        var view = new ItemView { ViewMode = mode };
        var w = new Window { Content = view, Width = 600, Height = 500 };
        w.Show();
        view.Items = Enumerable.Range(0, count).Select(i => N($"f{i:D3}.txt")).ToArray();
        Dispatcher.UIThread.RunJobs();
        w.MouseMove(new Point(1, 1));
        Dispatcher.UIThread.RunJobs();
        return new Fixture(view, w);
    }

    /// <summary>Centre of a named row, in window coordinates.</summary>
    private static Point RowCentre(Fixture f, string name)
    {
        var c = f.View.ItemsControl.GetRealizedContainers()
            .First(x => (x.DataContext as ItemViewModel)?.DisplayName == name);
        return c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), f.Window)!.Value;
    }

    /// <summary>A point below every row — the empty background a marquee starts from.</summary>
    private static Point Background(Fixture f)
    {
        var lowest = f.View.ItemsControl.GetRealizedContainers()
            .Max(c => c.TranslatePoint(new Point(0, c.Bounds.Height), f.Window)!.Value.Y);
        return new Point(20, lowest + 30);
    }

    private static string[] Selected(ItemView v) =>
        v.SelectedItems.Select(i => i.DisplayName).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static string[] Highlighted(ItemView v) =>
        v.ItemsControl.GetRealizedContainers()
            .Select(c => c.DataContext as ItemViewModel)
            .Where(vm => vm is { IsSelected: true })
            .Select(vm => vm!.DisplayName)
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();

    /// <summary>The core assertion: rows are highlighted while the button is still DOWN.</summary>
    [AvaloniaFact]
    public void Rows_highlight_while_the_band_is_still_being_dragged()
    {
        var f = Realize(5);
        var start = Background(f);

        f.Window.MouseDown(start, MouseButton.Left);
        f.Window.MouseMove(RowCentre(f, "f001.txt"));
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("f001.txt", Highlighted(f.View));
        Assert.Contains("f001.txt", Selected(f.View));   // and it is a real selection, not just a visual

        f.Window.MouseUp(RowCentre(f, "f001.txt"), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("f001.txt", Selected(f.View));   // ...which survives the release unchanged
    }

    /// <summary>Shrinking the band back off a row deselects it mid-gesture, so the highlight tracks
    /// the band in both directions rather than only accumulating.</summary>
    [AvaloniaFact]
    public void Shrinking_the_band_off_a_row_deselects_it_mid_gesture()
    {
        var f = Realize(6);
        var start = Background(f);

        f.Window.MouseDown(start, MouseButton.Left);
        f.Window.MouseMove(RowCentre(f, "f000.txt"));     // sweep up over everything
        Dispatcher.UIThread.RunJobs();
        var wide = Highlighted(f.View);
        Assert.Contains("f000.txt", wide);
        Assert.Contains("f004.txt", wide);

        f.Window.MouseMove(RowCentre(f, "f004.txt"));     // pull back down
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain("f000.txt", Highlighted(f.View));
        Assert.Contains("f004.txt", Highlighted(f.View));
    }

    /// <summary>SelectionChanged fires ONCE for the gesture, on release — it drives the info pane,
    /// which re-reads the whole selection, so firing it per pointer move would be its own regression.
    /// (The count is "once for the release"; the press may clear a previous selection, which is its
    /// own event and happens before the drag.)</summary>
    [AvaloniaFact]
    public void Selection_changed_fires_once_for_the_gesture_not_per_move()
    {
        var f = Realize(6);
        var start = Background(f);

        f.Window.MouseDown(start, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        var events = 0;
        f.View.SelectionChanged += () => events++;

        for (var i = 4; i >= 0; i--)
        {
            f.Window.MouseMove(RowCentre(f, $"f{i:D3}.txt"));
            Dispatcher.UIThread.RunJobs();
        }
        Assert.Equal(0, events);     // five moves, five selection changes applied, no event churn

        f.Window.MouseUp(RowCentre(f, "f000.txt"), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, events);
    }

    /// <summary>A jitter-sized band selects nothing — the live pass uses the same 4px gate as the band
    /// itself, so a plain click on the background can't sweep up the row under the cursor.</summary>
    [AvaloniaFact]
    public void A_click_sized_band_selects_nothing()
    {
        var f = Realize(5);
        var start = Background(f);

        f.Window.MouseDown(start, MouseButton.Left);
        f.Window.MouseMove(new Point(start.X + 2, start.Y + 2));
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(Highlighted(f.View));

        f.Window.MouseUp(new Point(start.X + 2, start.Y + 2), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(Selected(f.View));
    }

    /// <summary>An additive (ctrl) marquee keeps what was already selected. This fell out of diffing
    /// instead of clear-and-re-add: the old release-time pass began with ClearSel, so a ctrl marquee
    /// silently discarded the existing selection even though the press handler had deliberately
    /// preserved it.</summary>
    [AvaloniaFact]
    public void A_ctrl_marquee_adds_to_the_existing_selection()
    {
        var f = Realize(6);

        var first = RowCentre(f, "f000.txt");
        f.Window.MouseDown(first, MouseButton.Left);
        f.Window.MouseUp(first, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { "f000.txt" }, Selected(f.View));

        var start = Background(f);
        f.Window.MouseDown(start, MouseButton.Left, RawInputModifiers.Control);
        f.Window.MouseMove(RowCentre(f, "f004.txt"));
        Dispatcher.UIThread.RunJobs();
        f.Window.MouseUp(RowCentre(f, "f004.txt"), MouseButton.Left, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        var sel = Selected(f.View);
        Assert.Contains("f000.txt", sel);
        Assert.Contains("f004.txt", sel);
    }

    /// <summary>What a live pass costs, measured rather than assumed — this decides whether the pass
    /// needs a per-frame throttle at all. It walks the REALIZED containers only, so the bound is the
    /// viewport, not the folder; the budget is deliberately loose (a 60fps frame is 16.7ms) and exists
    /// to catch an implementation that became O(folder) instead.</summary>
    [AvaloniaFact]
    public void A_live_pass_costs_far_less_than_a_frame()
    {
        var f = Realize(2000);
        var start = Background(f);
        f.Window.MouseDown(start, MouseButton.Left);

        var top = new Point(20, 20);
        var sw = Stopwatch.StartNew();
        const int passes = 60;
        for (var i = 0; i < passes; i++)
        {
            // Alternate the band's height so every pass really does flip rows rather than no-op.
            f.Window.MouseMove(i % 2 == 0 ? top : new Point(20, start.Y - 40));
            Dispatcher.UIThread.RunJobs();
        }
        sw.Stop();

        var perPass = sw.Elapsed.TotalMilliseconds / passes;
        Assert.True(perPass < 16.0,
            $"a live marquee pass took {perPass:F2}ms on {f.View.ItemsControl.GetRealizedContainers().Count()} "
            + "realized rows — at that cost the pass needs throttling to one per frame");
    }
}
