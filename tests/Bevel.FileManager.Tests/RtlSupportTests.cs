using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.FileManager.Components;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// RTL support in the file-manager chrome (bevel-3cd): arrow keys are visual (Right moves
/// toward reading-start — a lower index — when the layout is mirrored), the details header
/// mirrors with the ambient FlowDirection, and the address-bar PATH text stays LeftToRight
/// (filesystem paths are LTR strings) while its chrome mirrors.
/// </summary>
public class RtlSupportTests
{
    private static IVfsNode Node(string name)
        => new FakeNode
        {
            Path = new VfsPath("test", name),
            DisplayName = name,
            Kind = VfsNodeKind.File,
            Size = 1,
            TypeDescription = "Text Document",
        };

    private static (ItemView View, Window Window) BuildItemView(FlowDirection flow, ViewMode mode = ViewMode.LargeIcons)
    {
        var view = new ItemView { ViewMode = mode };
        var window = new Window { Content = view, Width = 600, Height = 400, FlowDirection = flow };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        view.Items = new[] { Node("a.txt"), Node("b.txt"), Node("c.txt"), Node("d.txt") };
        Dispatcher.UIThread.RunJobs();
        return (view, window);
    }

    private static void Press(ItemView view, Key key)
    {
        view.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            Source = view,
        });
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Ltr_right_arrow_advances_in_reading_order()
    {
        var (view, _) = BuildItemView(FlowDirection.LeftToRight);

        Press(view, Key.Right);
        Press(view, Key.Right);

        Assert.Equal("c.txt", view.SelectedItem!.DisplayName);
    }

    [AvaloniaFact]
    public void Rtl_right_arrow_moves_toward_reading_start_and_left_advances()
    {
        var (view, _) = BuildItemView(FlowDirection.RightToLeft);

        // Index 0 sits at the VISUAL right under RTL, so Right cannot advance past it…
        Press(view, Key.Right);
        Press(view, Key.Right);
        Assert.Equal("a.txt", view.SelectedItem!.DisplayName);

        // …and Left (visually toward the items that follow in reading order) advances.
        Press(view, Key.Left);
        Press(view, Key.Left);
        Assert.Equal("c.txt", view.SelectedItem!.DisplayName);
    }

    [AvaloniaFact]
    public void Details_header_mirrors_with_the_ambient_flow_direction()
    {
        var (ltrView, ltrWindow) = BuildItemView(FlowDirection.LeftToRight, ViewMode.Details);
        var (rtlView, rtlWindow) = BuildItemView(FlowDirection.RightToLeft, ViewMode.Details);

        var ltrX = ltrView.SortName.TranslatePoint(new Point(0, 0), ltrWindow)!.Value.X;
        var rtlX = rtlView.SortName.TranslatePoint(new Point(0, 0), rtlWindow)!.Value.X;

        // The Name column (the widest, first Grid column) hugs the visual LEFT edge under LTR
        // and the visual RIGHT half under RTL.
        Assert.True(ltrX < 50, $"LTR Name header at x={ltrX}, expected near the left edge");
        Assert.True(rtlX > 300, $"RTL Name header at x={rtlX}, expected in the right half");
    }

    [AvaloniaFact]
    public void Address_path_text_stays_left_to_right_under_rtl_chrome()
    {
        var bar = new AddressBar();
        var window = new Window { Content = bar, Width = 600, Height = 60, FlowDirection = FlowDirection.RightToLeft };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The bar's chrome inherits RTL, but the path field is pinned LTR — paths are LTR text.
        Assert.Equal(FlowDirection.RightToLeft, bar.FlowDirection);
        Assert.Equal(FlowDirection.LeftToRight, bar.AddressBox.FlowDirection);
    }
}
