using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Bevel.FileManager.Components;
using Xunit;
using static Bevel.FileManager.Tests.ItemViewLayoutTests;

namespace Bevel.FileManager.Tests;

/// <summary>
/// Arrow keys move by the VISUAL grid in every view (bevel-0esz): in List (items flow down, then
/// wrap into columns) Right/Left cross columns and Up/Down move one item; in the icon views a move
/// past the first or last row stays put; End/PageDown scroll the selection into view on the axis the
/// view scrolls.
/// </summary>
public sealed class ItemViewKeyboardTests
{
    static void Press(Window w, PhysicalKey key)
    {
        w.KeyPressQwerty(key, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    static (ItemView View, Window Window) Focused(ViewMode mode, int count, int w, int h,
        FlowDirection flow = FlowDirection.LeftToRight)
    {
        var (view, window) = Show(mode, w, h, flow, Nodes(count));
        view.Focus();
        Dispatcher.UIThread.RunJobs();
        Press(window, PhysicalKey.Home);   // cursor on item 0
        return (view, window);
    }

    static string Selected(ItemView view) => view.SelectedItems.Single().DisplayName;

    static WrapLayout PanelLayout(ItemView view)
        => Assert.IsType<VirtualizingWrapPanel>(view.ItemsControl.ItemsPanelRoot).Layout;

    [AvaloniaFact]
    public void List_right_moves_a_column_and_down_moves_an_item()
    {
        var (view, w) = Focused(ViewMode.List, 60, 600, 220);
        int perCol = PanelLayout(view).PerLine;
        Assert.InRange(perCol, 2, 30);

        Press(w, PhysicalKey.ArrowRight);
        Assert.Equal($"f{perCol:D2}.txt", Selected(view));
        Press(w, PhysicalKey.ArrowDown);
        Assert.Equal($"f{perCol + 1:D2}.txt", Selected(view));
        Press(w, PhysicalKey.ArrowLeft);
        Assert.Equal("f01.txt", Selected(view));
    }

    [AvaloniaFact]
    public void List_end_scrolls_the_selection_into_view()
    {
        var (view, w) = Focused(ViewMode.List, 60, 600, 220);
        Press(w, PhysicalKey.End);
        Assert.Equal("f59.txt", Selected(view));

        var vp = new Rect(view.ItemsScroller.Viewport);
        var r = InScroller(view, Container(view, 59));
        Assert.True(view.ItemsScroller.Offset.X > 0, $"offset {view.ItemsScroller.Offset}");
        Assert.True(vp.Contains(r), $"selection {r} outside viewport {vp}");
    }

    [AvaloniaFact]
    public void Large_icons_up_in_the_first_row_keeps_the_selection()
    {
        var (view, w) = Focused(ViewMode.LargeIcons, 10, 600, 400);
        Press(w, PhysicalKey.ArrowRight);
        Assert.Equal("f01.txt", Selected(view));
        Press(w, PhysicalKey.ArrowUp);
        Assert.Equal("f01.txt", Selected(view));
    }

    [AvaloniaFact]
    public void List_left_moves_a_column_under_rtl()
    {
        var (view, w) = Focused(ViewMode.List, 60, 600, 220, FlowDirection.RightToLeft);
        int perCol = PanelLayout(view).PerLine;
        Press(w, PhysicalKey.ArrowLeft);
        Assert.Equal($"f{perCol:D2}.txt", Selected(view));
    }
}
