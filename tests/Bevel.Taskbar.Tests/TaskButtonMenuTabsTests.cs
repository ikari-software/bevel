using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bevel.Pal.Abstractions;
using Bevel.Pal.Fake;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The Tabs submenu of the task-button context menu (bevel-a40b/l17f): scrollable full row list
/// with favicon icons, foreign title hygiene (access-key escaping, surrogate-safe ellipsis), and
/// the row command reaching ITabProvider.ActivateAsync with the exact prefetched AppTab.
/// </summary>
public class TaskButtonMenuTabsTests
{
    private const string App = FakeTabProvider.TabApp;

    private sealed class NullWm : IWindowManager
    {
        public Capabilities Capabilities { get; } = new(Available: true, TrayMode: TrayCapability.Mirrored, Notes: Array.Empty<string>());
        public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(System.Threading.CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<ForeignWindow>>(Array.Empty<ForeignWindow>());
        public Task ActivateAsync(ForeignWindowId id, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task MinimizeAsync(ForeignWindowId id, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAsync(ForeignWindowId id, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync(ForeignWindowId id, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public event EventHandler<ForeignWindow>? WindowOpened;
        public event EventHandler<ForeignWindow>? WindowClosed;
        public event EventHandler<ForeignWindow>? WindowChanged;
        public event EventHandler<ForeignWindow>? ForegroundChanged;
    }

    private static TaskItemViewModel BrowserVm() =>
        new(new ForeignWindow(new ForeignWindowId("w1"), "Fake Browser", App, false, false, default), new NullWm());

    private static IReadOnlyList<TaskButtonMenu.TabMenuRow> MakeTabs(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new TaskButtonMenu.TabMenuRow(new AppTab(App, "1", i, $"Tab {i}"), null))
            .ToList();

    /// <summary>The "_Tabs" MenuItem of a built flyout, or null when the section was omitted.</summary>
    private static MenuItem? TabsMenu(MenuFlyout? flyout) =>
        (flyout?.ItemsSource as IEnumerable<object>)?.OfType<MenuItem>()
            .FirstOrDefault(m => (m.Header as string) == "_Tabs");

    [AvaloniaFact]
    public void Every_tab_gets_a_row_up_to_the_sanity_ceiling()
    {
        // The 25-row cap is gone (bevel-l17f): overflow scrolls. A 300-row sanity ceiling remains
        // because the Classic submenu panel is non-virtualizing — rows past it collapse to a tail.
        var tabs = TabsMenu(TaskButtonMenu.Build(BrowserVm(), MakeTabs(120), new FakeTabProvider()));
        Assert.Equal(120, tabs!.Items.OfType<MenuItem>().Count());

        var capped = TabsMenu(TaskButtonMenu.Build(BrowserVm(), MakeTabs(320), new FakeTabProvider()));
        var rows = capped!.Items.OfType<MenuItem>().ToList();
        Assert.Equal(301, rows.Count);
        Assert.Equal("… 20 more", rows[^1].Header);
        Assert.False(rows[^1].IsEnabled);
    }

    [AvaloniaFact]
    public void Tabs_submenu_style_EVALUATES_and_caps_the_realized_scroll_viewer()
    {
        // Regression pin for a reproduced hard failure: a Style whose selector throws during
        // template application (x.Nesting() at the root of Control.Styles) killed the WHOLE menu,
        // and the old test passed because it only inspected the Style object graph. This test
        // realizes the popup for real: show, open the submenu, and assert MaxHeight landed on the
        // template's ScrollViewer. If the selector is invalid, ShowAt/layout throws right here.
        var window = new Window { Width = 800, Height = 600 };
        var anchor = new Button { DataContext = BrowserVm() };
        window.Content = anchor;
        window.Show();
        var flyout = TaskButtonMenu.TryShow(anchor, anchor.DataContext, MakeTabs(40), new FakeTabProvider());
        Assert.NotNull(flyout);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var tabsItem = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<MenuItem>().First(m => (m.Header as string) == "_Tabs");
        tabsItem.IsSubMenuOpen = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var capped = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<ScrollViewer>().Where(s => s.MaxHeight == 480).ToList();
        Assert.NotEmpty(capped);

        // Dismiss popups BEFORE closing the window — tearing down a window with an open nested
        // submenu popup trips an Avalonia detach-ordering crash unrelated to what's under test.
        tabsItem.IsSubMenuOpen = false;
        flyout!.Hide();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.Close();
    }

    [AvaloniaFact]
    public void Rows_carry_their_decoded_favicon_as_the_menu_icon()
    {
        using var ms = new System.IO.MemoryStream(FakeTabProvider.FaviconPng);
        var fav = new Avalonia.Media.Imaging.Bitmap(ms);
        var rows = new[]
        {
            new TaskButtonMenu.TabMenuRow(new AppTab(App, "1", 1, "With icon"), fav),
            new TaskButtonMenu.TabMenuRow(new AppTab(App, "1", 2, "Without icon"), null),
        };
        var menu = TabsMenu(TaskButtonMenu.Build(BrowserVm(), rows, new FakeTabProvider()))!;

        var items = menu.Items.OfType<MenuItem>().ToList();
        var image = Assert.IsType<Image>(items[0].Icon);
        Assert.Same(fav, image.Source);
        Assert.Null(items[1].Icon);
    }

    [AvaloniaFact]
    public void No_tabs_or_no_provider_omits_the_section()
    {
        Assert.Null(TabsMenu(TaskButtonMenu.Build(BrowserVm(), Array.Empty<TaskButtonMenu.TabMenuRow>(), new FakeTabProvider())));
        Assert.Null(TabsMenu(TaskButtonMenu.Build(BrowserVm(), null, new FakeTabProvider())));
        Assert.Null(TabsMenu(TaskButtonMenu.Build(BrowserVm(), MakeTabs(3), tabProvider: null)));
    }

    [AvaloniaFact]
    public void Row_command_activates_the_exact_prefetched_tab()
    {
        var provider = new FakeTabProvider();
        var tabs = MakeTabs(3);
        var menu = TabsMenu(TaskButtonMenu.Build(BrowserVm(), tabs, provider))!;

        var second = menu.Items.OfType<MenuItem>().ElementAt(1);
        second.Command!.Execute(null);

        Assert.Same(tabs[1].Tab, provider.LastActivated);
    }

    [Fact]
    public void EscapeHeader_doubles_underscores_so_titles_keep_them_visible()
        => Assert.Equal("a__b____c", TaskButtonMenu.EscapeHeader("a_b__c"));

    [Fact]
    public void Ellipsize_caps_long_titles_with_a_single_ellipsis()
    {
        var s = TaskButtonMenu.Ellipsize(new string('x', 80));
        Assert.Equal(70, s.Length);
        Assert.EndsWith("…", s);
        Assert.Equal(new string('x', 40), TaskButtonMenu.Ellipsize(new string('x', 40)));
    }

    [Fact]
    public void Ellipsize_never_splits_a_surrogate_pair()
    {
        // 34 astral emoji = 68 chars; "ab" pushes past 70 and puts a HIGH surrogate at the naive
        // cut index 69 — a char slice there would strand half the pair.
        var title = string.Concat(Enumerable.Repeat("\U0001F600", 34)) + "ab";
        var cut = TaskButtonMenu.Ellipsize(title);
        // The invariant: every high surrogate in the result is followed by its low surrogate.
        for (var i = 0; i < cut.Length; i++)
            if (char.IsHighSurrogate(cut[i]))
                Assert.True(i + 1 < cut.Length && char.IsLowSurrogate(cut[i + 1]), $"lone high surrogate at {i}");
    }
}
