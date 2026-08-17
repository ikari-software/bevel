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
/// The Tabs submenu of the task-button context menu (bevel-a40b): row cap + overflow summary, foreign
/// title hygiene (access-key escaping, surrogate-safe ellipsis), and the row command reaching
/// ITabProvider.ActivateAsync with the exact prefetched AppTab.
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

    private static IReadOnlyList<AppTab> MakeTabs(int count) =>
        Enumerable.Range(1, count).Select(i => new AppTab(App, "1", i, $"Tab {i}")).ToList();

    /// <summary>The "_Tabs" MenuItem of a built flyout, or null when the section was omitted.</summary>
    private static MenuItem? TabsMenu(MenuFlyout? flyout) =>
        (flyout?.ItemsSource as IEnumerable<object>)?.OfType<MenuItem>()
            .FirstOrDefault(m => (m.Header as string) == "_Tabs");

    [AvaloniaFact]
    public void Thirty_tabs_collapse_to_25_rows_plus_disabled_summary()
    {
        var tabs = TabsMenu(TaskButtonMenu.Build(BrowserVm(), MakeTabs(30), new FakeTabProvider()));
        Assert.NotNull(tabs);

        var rows = tabs!.Items.OfType<MenuItem>().ToList();
        Assert.Equal(26, rows.Count);
        Assert.All(rows.Take(25), r => Assert.True(r.IsEnabled));
        Assert.Equal("… 5 more", rows[^1].Header);
        Assert.False(rows[^1].IsEnabled);
    }

    [AvaloniaFact]
    public void No_tabs_or_no_provider_omits_the_section()
    {
        Assert.Null(TabsMenu(TaskButtonMenu.Build(BrowserVm(), Array.Empty<AppTab>(), new FakeTabProvider())));
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

        Assert.Same(tabs[1], provider.LastActivated);
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
