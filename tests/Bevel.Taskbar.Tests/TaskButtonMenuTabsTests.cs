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
        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, PalRect? expected = null, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public event EventHandler<ForeignWindow>? WindowOpened;
        public event EventHandler<ForeignWindow>? WindowClosed;
        public event EventHandler<ForeignWindow>? WindowChanged;
        public event EventHandler<ForeignWindow>? ForegroundChanged;
    }

    private static TaskItemViewModel BrowserVm() =>
        new(new ForeignWindow(new ForeignWindowId("w1"), "Fake Browser", App, false, false, default), new NullWm());

    // Same shape but with the machine key set — jump-list actions key on BundleId, not AppId.
    private static TaskItemViewModel BrowserVmWithBundle() =>
        new(new ForeignWindow(new ForeignWindowId("w1"), "Fake Browser", App, false, false, default, BundleId: App), new NullWm());

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

    // ── bevel-bw1v: activation outcome reporting ────────────────────────────

    [AvaloniaFact]
    public void A_not_found_activation_invokes_the_refresh_callback()
    {
        var provider = new FakeTabProvider { NextOutcome = TabActivationResult.NotFound };
        var failed = 0;
        var menu = TabsMenu(TaskButtonMenu.Build(BrowserVm(), MakeTabs(2), provider,
            onActivationFailed: () => failed++))!;
        menu.Items.OfType<MenuItem>().First().Command!.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, failed);
    }

    [AvaloniaFact]
    public void A_successful_activation_does_not_invoke_the_refresh_callback()
    {
        var provider = new FakeTabProvider();   // Activated by default
        var failed = 0;
        var menu = TabsMenu(TaskButtonMenu.Build(BrowserVm(), MakeTabs(2), provider,
            onActivationFailed: () => failed++))!;
        menu.Items.OfType<MenuItem>().First().Command!.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, failed);
        Assert.NotNull(provider.LastActivated);
    }

    // ── bevel-s5x: per-app jump-list actions under the header ───────────────────

    [AvaloniaFact]
    public void App_actions_appear_under_the_header_and_invoke()
    {
        var invoked = 0;
        Func<string?, IReadOnlyList<TaskButtonMenu.TaskMenuAction>> provider = bundle =>
            bundle == App
                ? new[] { new TaskButtonMenu.TaskMenuAction("New _Window", () => invoked++) }
                : Array.Empty<TaskButtonMenu.TaskMenuAction>();

        var flyout = TaskButtonMenu.Build(BrowserVmWithBundle(), null, null, appActions: provider)!;
        var items = (flyout.ItemsSource as IEnumerable<object>)!.ToList();
        // header, separator, action, separator, then the Windows section.
        var action = Assert.IsType<MenuItem>(items[2]);
        Assert.Equal("New _Window", action.Header);
        Assert.True(action.IsEnabled);
        Assert.IsType<Separator>(items[3]);
        Assert.Equal("_Windows", (Assert.IsType<MenuItem>(items[4])).Header);

        action.Command!.Execute(null);
        Assert.Equal(1, invoked);
    }

    [AvaloniaFact]
    public void Null_app_actions_leave_the_menu_shape_unchanged()
    {
        var flyout = TaskButtonMenu.Build(BrowserVm(), null, null)!;
        var items = (flyout.ItemsSource as IEnumerable<object>)!.ToList();
        // header, separator, Windows — no action row, no extra separator.
        Assert.Equal("_Windows", (Assert.IsType<MenuItem>(items[2])).Header);
        Assert.DoesNotContain(items, i => i is MenuItem { Header: "New _Window" });
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

    private static TaskGroupViewModel GroupVmWithTwoWindows()
    {
        var group = new TaskGroupViewModel(App);
        group.Windows.Add(new TaskItemViewModel(
            new ForeignWindow(new ForeignWindowId("w1"), "One", App, false, false, default), new NullWm()));
        group.Windows.Add(new TaskItemViewModel(
            new ForeignWindow(new ForeignWindowId("w2"), "Two", App, false, false, default), new NullWm()));
        return group;
    }

    private static List<MenuItem> TopLevelItems(MenuFlyout? flyout) =>
        (flyout?.ItemsSource as IEnumerable<object>)!.OfType<MenuItem>().ToList();

    [AvaloniaFact]
    public void Group_menu_offers_minimize_restore_close_all()
    {
        // bevel-apm: the group context menu surfaces the (previously dead) all-window verbs, and each
        // per-window submenu gains Restore.
        var flyout = TaskButtonMenu.Build(GroupVmWithTwoWindows(), null, null);
        var top = TopLevelItems(flyout);
        var headers = top.Select(m => m.Header as string).ToList();

        Assert.Contains("Minimize _All", headers);
        Assert.Contains("R_estore All", headers);
        Assert.Contains("Close A_ll", headers);

        var windows = top.First(m => (m.Header as string) == "_Windows");
        var per = windows.Items.OfType<MenuItem>().First();
        var verbs = per.Items.OfType<MenuItem>().Select(m => m.Header as string).ToList();
        // bevel-kpkx: each per-window submenu also carries the Maximize/Zoom verb.
        Assert.Equal(new[] { "_Activate", "Mi_nimize", "_Restore", "Ma_ximize", "_Close" }, verbs);
    }

    [AvaloniaFact]
    public void Single_window_menu_has_restore_but_no_all_verbs()
    {
        // The All-verbs are group-only (2+ windows); a lone window button must not offer them.
        var top = TopLevelItems(TaskButtonMenu.Build(BrowserVm(), null, null));
        var headers = top.Select(m => m.Header as string).ToList();

        Assert.DoesNotContain("Minimize _All", headers);
        Assert.DoesNotContain("R_estore All", headers);
        Assert.DoesNotContain("Close A_ll", headers);

        var windows = top.First(m => (m.Header as string) == "_Windows");
        var verbs = windows.Items.OfType<MenuItem>().First().Items.OfType<MenuItem>()
            .Select(m => m.Header as string).ToList();
        // bevel-kpkx: each per-window submenu also carries the Maximize/Zoom verb.
        Assert.Equal(new[] { "_Activate", "Mi_nimize", "_Restore", "Ma_ximize", "_Close" }, verbs);
    }

    // ── bevel-d2j9: the app-level Hide verb in the button menu ──────────────

    private sealed class HideRecWm : IWindowManager
    {
        public List<string> Hidden { get; } = new();
        public Capabilities Capabilities { get; } = new(Available: true, TrayMode: TrayCapability.Mirrored, Notes: Array.Empty<string>());
        public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(System.Threading.CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<ForeignWindow>>(Array.Empty<ForeignWindow>());
        public Task ActivateAsync(ForeignWindowId id, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task MinimizeAsync(ForeignWindowId id, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAsync(ForeignWindowId id, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync(ForeignWindowId id, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, PalRect? expected = null, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task HideAppAsync(string bundleId, System.Threading.CancellationToken ct = default) { Hidden.Add(bundleId); return Task.CompletedTask; }
        public event EventHandler<ForeignWindow>? WindowOpened;
        public event EventHandler<ForeignWindow>? WindowClosed;
        public event EventHandler<ForeignWindow>? WindowChanged;
        public event EventHandler<ForeignWindow>? ForegroundChanged;
    }

    [AvaloniaFact]
    public void Menu_offers_hide_directly_above_quit_and_it_hides_the_app()
    {
        // bevel-d2j9: "_Hide" sits immediately before the separator + Quit at the menu bottom (the
        // macOS app-menu order), and its command rides HideAppAsync with the bundle id.
        var wm = new HideRecWm();
        var vm = new TaskItemViewModel(
            new ForeignWindow(new ForeignWindowId("w1"), "Doc", "Safari", false, false, default, BundleId: "com.apple.Safari"), wm);

        var flyout = TaskButtonMenu.Build(vm, null, null)!;
        var items = (flyout.ItemsSource as IEnumerable<object>)!.ToList();
        var hideIdx = items.FindIndex(i => i is MenuItem { Header: "_Hide" });
        Assert.True(hideIdx >= 0, "menu has no _Hide item");
        Assert.IsType<Separator>(items[hideIdx + 1]);
        Assert.Equal("_Quit", (Assert.IsType<MenuItem>(items[hideIdx + 2]).Header as string));

        (items[hideIdx] as MenuItem)!.Command!.Execute(null);
        Assert.Equal(new[] { "com.apple.Safari" }, wm.Hidden);
    }

    [AvaloniaFact]
    public void Group_menu_hide_delegates_to_a_member_window()
    {
        // The group's HideCommand acts on the whole app via any member's bundle id.
        var wm = new HideRecWm();
        var group = new TaskGroupViewModel(App);
        group.Windows.Add(new TaskItemViewModel(
            new ForeignWindow(new ForeignWindowId("w1"), "One", App, false, false, default, BundleId: App), wm));
        group.Windows.Add(new TaskItemViewModel(
            new ForeignWindow(new ForeignWindowId("w2"), "Two", App, false, false, default, BundleId: App), wm));

        var top = TopLevelItems(TaskButtonMenu.Build(group, null, null));
        var hide = top.First(m => (m.Header as string) == "_Hide");
        hide.Command!.Execute(null);

        Assert.Equal(new[] { App }, wm.Hidden);
    }


    [AvaloniaFact]
    public void Rows_without_a_favicon_show_the_session_tint_swatch(/* bevel-l17f */)
    {
        var tinted = new AppTab(App, "7", 1, "zsh", SessionId: "SID-9", Tint: "#FF0000");
        var plain = new AppTab(App, "7", 2, "docs", SessionId: "SID-10", Tint: null);
        var rows = new List<TaskButtonMenu.TabMenuRow>
        {
            new(tinted, null),
            new(plain, null),
        };
        var tabs = TabsMenu(TaskButtonMenu.Build(BrowserVm(), rows, new FakeTabProvider()));
        var icons = tabs!.Items.OfType<MenuItem>().Select(m => m.Icon).ToList();

        var swatch = Assert.IsType<Border>(icons[0]);
        Assert.Equal(Avalonia.Media.Color.FromRgb(0xFF, 0, 0),
            (swatch.Background as Avalonia.Media.SolidColorBrush)?.Color);
        Assert.Null(icons[1]);   // no favicon, no tint → no icon
    }
}
