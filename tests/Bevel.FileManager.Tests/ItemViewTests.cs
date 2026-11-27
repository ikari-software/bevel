using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Core.Vfs;
using Bevel.FileManager.Components;
using Bevel.UI;
using Xunit;
using Path = Avalonia.Controls.Shapes.Path;

namespace Bevel.FileManager.Tests;

[Collection("TaskbarTheme")]   // one test overrides an Application-level icon token
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
        // Folders sort ahead of files (Filer/Finder), then files by name.
        Assert.Equal(new[] { "dir", "a.txt", "b.txt" },
            src.Cast<ItemViewModel>().Select(v => v.DisplayName).ToArray());
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

    [AvaloniaFact]
    public void ArrowDown_advances_the_selection_when_focused()
    {
        var view = new ItemView { ViewMode = ViewMode.Details };
        var w = new Avalonia.Controls.Window { Content = view, Width = 400, Height = 300 };
        w.Show();
        view.Items = new[] { Node("a.txt"), Node("b.txt"), Node("c.txt") };
        Dispatcher.UIThread.RunJobs();
        view.SelectPath(new VfsPath("test", "a.txt"));
        view.Focus();
        Dispatcher.UIThread.RunJobs();

        w.KeyPressQwerty(Avalonia.Input.PhysicalKey.ArrowDown, Avalonia.Input.RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("b.txt", view.SelectedItems.Single().DisplayName);

        w.KeyPressQwerty(Avalonia.Input.PhysicalKey.End, Avalonia.Input.RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("c.txt", view.SelectedItems.Single().DisplayName);
    }

    [AvaloniaFact]
    public void TypeAhead_selects_by_typed_text_and_cycles_on_repeat()
    {
        // bevel-p3v3: type-ahead now runs off TextInput through the shared TypeToFind service — so it selects
        // forward (the old loop wrapped backwards) and repeating the same letter cycles (the old code searched
        // "cc"). Rapid KeyTextInput calls stay inside the reset window, so the two 'c's are a cycle, not "cc".
        var view = new ItemView { ViewMode = ViewMode.Details };
        var w = new Avalonia.Controls.Window { Content = view, Width = 400, Height = 300 };
        w.Show();
        view.Items = new[] { Node("apple.txt"), Node("cherry.txt"), Node("cranberry.txt"), Node("cucumber.txt") };
        Dispatcher.UIThread.RunJobs();
        view.Focus();
        Dispatcher.UIThread.RunJobs();

        w.KeyTextInput("c");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("cherry.txt", view.SelectedItems.Single().DisplayName);   // forward, not the backwards wrap

        w.KeyTextInput("c");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("cranberry.txt", view.SelectedItems.Single().DisplayName); // repeat cycles to the next match
    }

    [AvaloniaFact]
    public void Column_width_is_shared_between_header_and_rows()
    {
        var view = new ItemView { ViewMode = ViewMode.Details };
        PumpAndShow(view);
        view.Items = new[] { Node("a.txt", size: 10) };
        Dispatcher.UIThread.RunJobs();

        // The header's Size column starts at the default width...
        Assert.Equal(80, view.DetailsHeaderGrid.ColumnDefinitions[1].Width.Value);
        // ...and a resize flows through the shared property to the header column (rows bind the same).
        view.SizeColWidth = new Avalonia.Controls.GridLength(150);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(150, view.DetailsHeaderGrid.ColumnDefinitions[1].Width.Value);
    }

    [AvaloniaFact]
    public void SelectAll_and_InvertSelection_toggle_rows_without_dupes()
    {
        var view = new ItemView { ViewMode = ViewMode.Details };
        PumpAndShow(view);
        view.Items = new[] { Node("a.txt"), Node("b.txt"), Node("c.txt") };
        Dispatcher.UIThread.RunJobs();

        view.SelectAll();
        view.SelectAll();                       // twice — must not accumulate duplicates
        Assert.Equal(3, view.SelectedItems.Count);

        view.InvertSelection();                 // all → none
        Assert.Empty(view.SelectedItems);

        view.SelectPath(new VfsPath("test", "b.txt"));
        view.InvertSelection();                 // b → a,c
        Assert.Equal(new[] { "a.txt", "c.txt" }, view.SelectedItems.Select(v => v.DisplayName).OrderBy(x => x));
    }

    [AvaloniaFact]
    public void SelectPath_selects_the_matching_row()
    {
        var view = new ItemView { ViewMode = ViewMode.Details };
        PumpAndShow(view);
        view.Items = new[] { Node("a.txt"), Node("b.txt") };
        Dispatcher.UIThread.RunJobs();

        view.SelectPath(new VfsPath("test", "b.txt"));

        Assert.Single(view.SelectedItems);
        Assert.Equal("b.txt", view.SelectedItems[0].DisplayName);
        Assert.True(view.SelectedItems[0].IsSelected);
    }

    [AvaloniaTheory]
    [InlineData(ViewMode.LargeIcons)]
    [InlineData(ViewMode.SmallIcons)]
    [InlineData(ViewMode.List)]
    [InlineData(ViewMode.Details)]
    [InlineData(ViewMode.Thumbnails)]
    public void Row_icons_are_GlyphIcon_hosts_and_rebuild_on_invalidation(ViewMode mode)
    {
        // A row icon is a GlyphIcon (bevel-yfkw), not a one-shot Glyphs.Icon() snapshot: a snapshot holds
        // the brushes it was built with forever, so a live theme/scheme/colourway switch never recoloured
        // the listing until it was re-listed.
        var view = new ItemView { ViewMode = mode };
        var window = new Avalonia.Controls.Window { Content = view, Width = 600, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            view.Items = new IVfsNode[]
            {
                new TestNode { Path = new VfsPath("test", "dir"), DisplayName = "dir", Kind = VfsNodeKind.Folder,
                               MightHaveChildren = true, TypeDescription = "File Folder", IconKey = IconKey.Folder() },
            };
            Dispatcher.UIThread.RunJobs();

            var icon = Assert.Single(view.GetVisualDescendants().OfType<GlyphIcon>());
            Assert.Equal("folder", icon.Glyph);
            var before = icon.Child;
            var original = BackTop(icon);
            Assert.NotEqual(Colors.Red, original);

            var resources = Application.Current!.Resources;
            resources[ThemeTokens.ColorIconFolderBackTop] = Colors.Red;
            try
            {
                Glyphs.InvalidateThemeCache();
                Dispatcher.UIThread.RunJobs();

                Assert.NotSame(before, icon.Child);   // rebuilt, not the stale snapshot with the evicted brush
                Assert.Equal(Colors.Red, BackTop(icon));
            }
            finally
            {
                resources.Remove(ThemeTokens.ColorIconFolderBackTop);
                Glyphs.InvalidateThemeCache();
                Dispatcher.UIThread.RunJobs();
            }
            Assert.Equal(original, BackTop(icon));
        }
        finally { window.Close(); }   // a failed assert must not leave subscribed GlyphIcons alive
    }

    // ── bevel-5rel: selection colours are live theme resources ───────────

    static Avalonia.Controls.Border NameCellOf(ItemView view, string name)
        => view.ItemsControl.GetRealizedContainers()
            .First(c => (c.DataContext as ItemViewModel)?.DisplayName == name)
            .GetVisualDescendants().OfType<Avalonia.Controls.Border>().First(b => b.Classes.Contains("namecell"));

    /// <summary>Overwrite an Application-level brush token for the duration of <paramref name="body"/>.</summary>
    static void WithToken(string key, IBrush brush, Action body)
    {
        var resources = Application.Current!.Resources;
        bool had = resources.TryGetValue(key, out var old);
        resources[key] = brush;
        try { Dispatcher.UIThread.RunJobs(); body(); }
        finally
        {
            if (had) resources[key] = old; else resources.Remove(key);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Selection_colours_follow_a_live_token_change_without_reselecting()
    {
        var view = new ItemView { ViewMode = ViewMode.Details };
        PumpAndShow(view);
        view.Items = new[] { Node("a.txt"), Node("b.txt") };
        Dispatcher.UIThread.RunJobs();
        view.SelectPath(new VfsPath("test", "a.txt"));
        Dispatcher.UIThread.RunJobs();

        var red = new SolidColorBrush(Colors.Red);
        WithToken(ThemeTokens.BrushHighlight, red, () =>
            Assert.Same(red, NameCellOf(view, "a.txt").Background));
    }

    [AvaloniaFact]
    public void Unselected_text_follows_a_live_token_change()
    {
        var view = new ItemView { ViewMode = ViewMode.Details };
        PumpAndShow(view);
        view.Items = new[] { Node("a.txt"), Node("b.txt") };
        Dispatcher.UIThread.RunJobs();

        var green = new SolidColorBrush(Colors.Green);
        WithToken(ThemeTokens.BrushWindowText, green, () =>
            Assert.Same(green, NameCellOf(view, "b.txt").GetVisualDescendants().OfType<NameLabel>().Single().Foreground));
    }

    [AvaloniaFact]
    public void Selected_text_uses_the_highlight_text_token()
    {
        var view = new ItemView { ViewMode = ViewMode.Details };
        PumpAndShow(view);
        view.Items = new[] { Node("a.txt"), Node("b.txt") };
        Dispatcher.UIThread.RunJobs();
        view.SelectPath(new VfsPath("test", "a.txt"));
        Dispatcher.UIThread.RunJobs();

        var yellow = new SolidColorBrush(Colors.Yellow);
        WithToken(ThemeTokens.BrushHighlightText, yellow, () =>
            Assert.Same(yellow, NameCellOf(view, "a.txt").GetVisualDescendants().OfType<NameLabel>().Single().Foreground));
    }

    private static Color BackTop(GlyphIcon icon)
    {
        var back = icon.GetVisualDescendants().OfType<Path>().First();
        return Assert.IsType<LinearGradientBrush>(back.Fill).GradientStops[0].Color;
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
}