using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
        // ...and the long name trims inside its row rather than just not widening it (review #5).
        int i = view.ItemsControl.Items.Cast<ItemViewModel>().ToList().FindIndex(v => v.DisplayName == LongName);
        Assert.True(Assert.IsType<NameLabel>(Label(view, i)).IsTrimmed);
        Assert.True(LabelRight(view, i) <= Container(view, i).Bounds.Width,
            $"label right {LabelRight(view, i)} > row {Container(view, i).Bounds.Width}");
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

    // ── bevel-m9jy: icon-view names stay inside their cell ───────────────

    [AvaloniaTheory]
    [InlineData(ViewMode.LargeIcons, 80, 72)]
    [InlineData(ViewMode.Thumbnails, 120, 142)]
    public void Long_name_stays_inside_its_icon_cell(ViewMode mode, double cellW, double cellH)
    {
        const string longName = "A really quite long file name with many words in it for wrapping.txt";
        var (view, _) = Show(mode, 600, 400, items: new[] { Node(longName), Node("short.txt") });
        int i = view.ItemsControl.Items.Cast<ItemViewModel>().ToList().FindIndex(v => v.DisplayName == longName);
        var c = Container(view, i);
        Assert.Equal(new Size(cellW, cellH), c.Bounds.Size);
        var label = Label(view, i);
        double bottom = label.TranslatePoint(new Point(0, label.Bounds.Height), c)!.Value.Y;
        Assert.True(bottom <= c.Bounds.Height, $"label bottom {bottom} > cell {c.Bounds.Height}");
    }

    // ── bevel-m9jy: the one selected item shows its whole name ───────────

    const string RevealName = "A really quite long file name with many words in it for wrapping.txt";

    static (ItemView View, Window Window) FocusedIcons(ViewMode mode, int count)
    {
        var items = new[] { Node(RevealName) }.Concat(Nodes(count - 1)).ToArray();   // RevealName sorts first
        var (view, w) = Show(mode, 600, 500, items: items);
        view.Focus();
        Dispatcher.UIThread.RunJobs();
        w.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.None);   // select item 0, the long name
        Dispatcher.UIThread.RunJobs();
        return (view, w);
    }

    [AvaloniaFact]
    public void Selecting_one_item_reveals_its_whole_name()
    {
        var (view, w) = FocusedIcons(ViewMode.LargeIcons, 10);
        Assert.Equal(RevealName, view.SelectedItems.Single().DisplayName);
        Assert.True(Label(view, 0).TextLayout.TextLines.Count > 2, "the selected name is shown in full");
        Assert.Equal(1, Container(view, 0).ZIndex);

        w.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Shift);   // two selected: no reveal
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, view.SelectedItems.Count);
        Assert.Equal(2, Label(view, 0).TextLayout.TextLines.Count);
        Assert.Equal(0, Container(view, 0).ZIndex);
    }

    [AvaloniaFact]
    public void Revealed_name_is_hidden_again_when_the_view_loses_focus()
    {
        var view = new ItemView { ViewMode = ViewMode.LargeIcons };
        var other = new TextBox();
        DockPanel.SetDock(other, Dock.Top);
        var w = new Window { Width = 600, Height = 500, Content = new DockPanel { Children = { other, view } } };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        view.Items = new[] { Node(RevealName) }.Concat(Nodes(9)).ToArray();
        Dispatcher.UIThread.RunJobs();
        view.Focus();
        Dispatcher.UIThread.RunJobs();
        w.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.SelectedItems.Single().ShowFullName);

        other.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.False(view.SelectedItems.Single().ShowFullName);
    }

    /// <summary>A point inside the revealed label's 2 px left padding on its 4th line, which lies over the
    /// next row's cell in Thumbnails (for hit-testing: it is inside item 5's container rect).</summary>
    static Point PaddingOnFourthLine(ItemView view, Visual relativeTo)
    {
        var label = Label(view, 0);
        var highlight = (Control)label.GetVisualParent()!;
        var labelTop = label.TranslatePoint(new Point(0, 0), highlight)!.Value.Y;
        return highlight.TranslatePoint(new Point(1, labelTop + 15 * 3 + 7), relativeTo)!.Value;
    }

    [AvaloniaFact]
    public void Revealed_name_paints_over_the_row_below()
    {
        var (view, w) = FocusedIcons(ViewMode.Thumbnails, 10);   // 5 per row: item 5 sits under item 0
        var label = Label(view, 0);
        var lines = label.TextLayout.TextLines;
        Assert.True(lines.Count >= 4);

        // Sample the highlight fill to the LEFT of the short, centred last line (no glyph there), at a
        // spot that lies over the next row's opaque thumbnail frame. Without the ZIndex binding the
        // next row paints its frame over the revealed name there.
        var last = lines[^1];
        double lastTop = 0;
        for (int i = 0; i < lines.Count - 1; i++) lastTop += lines[i].Height;
        double lineStart = (label.Bounds.Width - last.WidthIncludingTrailingWhitespace) / 2;
        var under = ThumbFrame(view, 5);
        var underInLabel = new Rect(under.TranslatePoint(new Point(0, 0), label)!.Value, under.Bounds.Size).Deflate(2);
        var sampleInLabel = new Point(underInLabel.X + 1, lastTop + last.Height / 2);
        Assert.True(sampleInLabel.X < lineStart - 1, $"sample x {sampleInLabel.X} must be left of the last line's text ({lineStart})");
        Assert.True(underInLabel.Contains(sampleInLabel), $"sample {sampleInLabel} must lie over the next row's frame {underInLabel}");
        var p = label.TranslatePoint(sampleInLabel, w)!.Value;

        var frame = w.CaptureRenderedFrame()!;
        var expected = ((ISolidColorBrush)Application.Current!.FindResource(Bevel.UI.ThemeTokens.BrushHighlight)!).Color;
        Assert.Equal(expected, Pixel(frame, (int)p.X, (int)p.Y));
    }

    static Color Pixel(Avalonia.Media.Imaging.WriteableBitmap bmp, int x, int y)
    {
        using var fb = bmp.Lock();
        int v = System.Runtime.InteropServices.Marshal.ReadInt32(fb.Address, y * fb.RowBytes + x * 4);
        byte b = (byte)v, g = (byte)(v >> 8), r = (byte)(v >> 16), a = (byte)(v >> 24);
        return fb.Format == Avalonia.Platform.PixelFormat.Rgba8888 ? Color.FromArgb(a, b, g, r) : Color.FromArgb(a, r, g, b);
    }

    [AvaloniaFact]
    public void Click_on_the_revealed_name_keeps_that_item_selected()
    {
        var (view, w) = FocusedIcons(ViewMode.Thumbnails, 10);
        var p = PaddingOnFourthLine(view, w);
        w.MouseDown(p, MouseButton.Left);
        w.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(RevealName, view.SelectedItems.Single().DisplayName);
    }

    // ── bevel-2ncr: an RTL List view opens at its reading start ──────────

    static void AssertFirstColumnVisible(ItemView view)
    {
        Assert.True(view.ItemsScroller.Extent.Width > view.ItemsScroller.Viewport.Width, "needs a scrolling list");
        var vp = new Rect(view.ItemsScroller.Viewport);
        var r = InScroller(view, Container(view, 0));
        Assert.True(vp.Contains(r), $"item 0 at {r} is outside the viewport {vp} (offset {view.ItemsScroller.Offset})");
    }

    [AvaloniaFact]
    public void Rtl_list_view_opens_at_the_reading_start()
    {
        var (view, _) = Show(ViewMode.List, 830, 400, FlowDirection.RightToLeft, Nodes(200));
        AssertFirstColumnVisible(view);
    }

    [AvaloniaFact]
    public void Rtl_list_view_stays_at_the_reading_start_while_items_stream_in()
    {
        var (view, _) = Show(ViewMode.List, 830, 400, FlowDirection.RightToLeft);
        view.ResetItems();
        view.AddItems(Nodes(200, "a"));
        Dispatcher.UIThread.RunJobs();
        view.AddItems(Nodes(200, "b"));
        Dispatcher.UIThread.RunJobs();
        AssertFirstColumnVisible(view);
    }

    [AvaloniaFact]
    public void Rtl_list_view_keeps_a_position_the_user_scrolled_to()
    {
        var (view, _) = Show(ViewMode.List, 830, 400, FlowDirection.RightToLeft);
        view.ResetItems();
        view.AddItems(Nodes(200, "a"));
        Dispatcher.UIThread.RunJobs();
        view.ItemsScroller.Offset = new Vector(0, 0);   // the user scrolled to the reading end
        Dispatcher.UIThread.RunJobs();
        view.AddItems(Nodes(200, "b"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, view.ItemsScroller.Offset.X);
    }

    // ── Thumbnails: the frame holds only the picture (owner feedback on bevel-m9jy) ──

    /// <summary>The framed box around the thumbnail picture in the container at <paramref name="index"/>.</summary>
    static Border ThumbFrame(ItemView view, int index)
        => Container(view, index).GetVisualDescendants().OfType<Border>().First(b => b.BorderThickness == new Thickness(1));

    [AvaloniaFact]
    public void Thumbnail_caption_sits_below_the_frame_not_inside_it()
    {
        var (view, _) = Show(ViewMode.Thumbnails, 600, 400, items: new[] { Node("photo.jpg") });
        var c = Container(view, 0);
        var frame = ThumbFrame(view, 0);
        Assert.Null(frame.GetVisualDescendants().OfType<NameLabel>().FirstOrDefault());
        double frameBottom = frame.TranslatePoint(new Point(0, frame.Bounds.Height), c)!.Value.Y;
        double labelTop = Label(view, 0).TranslatePoint(new Point(0, 0), c)!.Value.Y;
        Assert.True(labelTop >= frameBottom, $"caption top {labelTop} is inside the frame (bottom {frameBottom})");
    }

    [AvaloniaFact]
    public void Thumbnail_frame_uses_the_light_face_line_not_a_dark_one()
    {
        // WindowFrame (black) and ButtonShadow (#808080 in the classic scheme) both read as a hard box
        // on the white window; the owner asked for a soft line, which is the ButtonFace grey.
        var (view, _) = Show(ViewMode.Thumbnails, 600, 400, items: new[] { Node("photo.jpg") });
        var expected = Application.Current!.FindResource(Bevel.UI.ThemeTokens.BrushButtonFace);
        Assert.Same(expected, ThumbFrame(view, 0).BorderBrush);
    }

    // ── Review #2: RTL List keeps its reading start across a resize ──────

    [AvaloniaFact]
    public void Rtl_list_view_keeps_the_reading_start_when_the_window_narrows()
    {
        var (view, w) = Show(ViewMode.List, 830, 400, FlowDirection.RightToLeft, Nodes(200));
        AssertFirstColumnVisible(view);
        w.Width = 500;
        Dispatcher.UIThread.RunJobs();
        AssertFirstColumnVisible(view);
        w.Width = 700;
        Dispatcher.UIThread.RunJobs();
        AssertFirstColumnVisible(view);
    }

    [AvaloniaFact]
    public void Rtl_list_view_keeps_a_user_position_across_a_resize()
    {
        var (view, w) = Show(ViewMode.List, 830, 400, FlowDirection.RightToLeft, Nodes(200));
        view.ItemsScroller.Offset = new Vector(0, 0);   // the user scrolled to the reading end
        Dispatcher.UIThread.RunJobs();
        w.Width = 500;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, view.ItemsScroller.Offset.X);
    }

    // ── Review #3: a drop on the revealed caption targets the folder underneath ──

    [AvaloniaFact]
    public void Drop_on_the_revealed_caption_targets_the_folder_underneath()
    {
        var folder = new LayoutTestNode
        {
            Path = new VfsPath("test", "f05-folder"), DisplayName = "f05-folder", Kind = VfsNodeKind.Folder,
            TypeDescription = "File Folder", MightHaveChildren = true,
        };
        // Folders sort first, so give the long-named item a folder too and keep it at index 0.
        var items = new IVfsNode[] { new LayoutTestNode
        {
            Path = new VfsPath("test", RevealName), DisplayName = RevealName, Kind = VfsNodeKind.Folder,
            TypeDescription = "File Folder", MightHaveChildren = true,
        } }.Concat(Enumerable.Range(1, 4).Select(i => (IVfsNode)new LayoutTestNode
        {
            Path = new VfsPath("test", $"f0{i}-folder"), DisplayName = $"f0{i}-folder", Kind = VfsNodeKind.Folder,
            TypeDescription = "File Folder", MightHaveChildren = true,
        })).Append(folder).Concat(Nodes(4)).ToArray();
        var (view, w) = Show(ViewMode.Thumbnails, 600, 500, items: items);
        view.Focus();
        Dispatcher.UIThread.RunJobs();
        w.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.SelectedItems.Single().ShowFullName);

        var p = PaddingOnFourthLine(view, view.ItemsControl);   // inside item 5's cell, on the revealed caption
        Assert.Equal("f05-folder", Assert.IsType<ItemViewModel>(view.ItemsControl.ContainerFromIndex(5)!.DataContext).DisplayName);
        Assert.Equal(folder.Path, view.DropTargetAt(p)?.Path);
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
