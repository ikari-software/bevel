using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Core;
using Bevel.Pal.Abstractions;
using Bevel.Pal.Fake;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Unread/attention badges on task buttons (bevel-ijln). The count is whatever the PLATFORM publishes
/// (macOS: the Dock's <c>AXStatusLabel</c>), matched onto buttons by bundle id or app name and shown as
/// a theme-tokened pill before the title. The load-bearing invariant these tests pin down is the honest
/// one: a Slack-style "(7)" in a window TITLE must never become a badge — no source, no badge.
/// </summary>
public class TaskBadgeTests
{
    private const string SlackBundle = "com.tinyspeck.slackmacgap";

    private static ForeignWindow W(string id, string title, string? appId, string? bundleId = null)
        => new(new ForeignWindowId(id), title, appId, false, false, default, BundleId: bundleId);

    // ── Matching (pure) ─────────────────────────────────────────────────

    [Fact]
    public void Index_keys_a_badge_by_both_bundle_id_and_app_name()
    {
        var index = TaskBadges.Index(new[] { new AppBadge(SlackBundle, "Slack", "7") });

        Assert.Equal("7", index[SlackBundle]);
        Assert.Equal("7", index["Slack"]);
        // The Dock's localized title and the enumeration's AppId can differ in case.
        Assert.Equal("7", index["slack"]);
    }

    [Fact]
    public void Index_drops_blank_labels_because_there_is_no_such_thing_as_an_empty_badge()
    {
        var index = TaskBadges.Index(new[]
        {
            new AppBadge("com.a", "Alpha", ""),
            new AppBadge("com.b", "Beta", "   "),
            new AppBadge("com.c", "Gamma", " 4 "),
        });

        Assert.False(index.ContainsKey("com.a"));
        Assert.False(index.ContainsKey("com.b"));
        Assert.Equal("4", index["com.c"]);   // trimmed
    }

    [Fact]
    public void Bundle_id_wins_over_app_name_when_a_button_carries_both()
    {
        // Two apps whose display names collide; only the bundle id disambiguates them.
        var index = TaskBadges.Index(new[]
        {
            new AppBadge(SlackBundle, "Slack", "7"),
            new AppBadge("com.impostor.slack", "Slack", "99"),
        });

        var slack = new TaskItemViewModel(W("s1", "general - Acme - Slack", "Slack", SlackBundle), new StubWm());
        Assert.Equal("7", TaskBadges.Lookup(slack, index));
    }

    [Fact]
    public void App_name_is_the_fallback_key_when_no_bundle_id_is_known()
    {
        var index = TaskBadges.Index(new[] { new AppBadge(BundleId: null, AppName: "Slack", Label: "3") });
        var slack = new TaskItemViewModel(W("s1", "Acme - Slack", "Slack"), new StubWm());

        Assert.Equal("3", TaskBadges.Lookup(slack, index));
    }

    // ── Honesty: titles are never a badge source ────────────────────────

    [Theory]
    [InlineData("(7) general - Acme - Slack")]
    [InlineData("7 · general - Acme - Slack")]
    [InlineData("• general - Acme - Slack")]
    [InlineData("Inbox (12) - Mail")]
    [InlineData("general - Acme - Slack")]
    public void A_count_in_the_window_title_is_never_turned_into_a_badge(string title)
    {
        // Slack-like fixtures: the shell must NOT parse these. Only IAppBadgeSource publishes badges,
        // so with an empty snapshot every one of these buttons is badge-free.
        var vm = new TaskItemViewModel(W("s1", title, "Slack", SlackBundle), new StubWm());
        TaskBadges.Apply(new[] { vm }, TaskBadges.Index(Array.Empty<AppBadge>()));

        Assert.False(vm.HasBadge);
        Assert.Null(vm.BadgeText);
        Assert.Null(vm.BadgeCount);
    }

    // ── View-model surface ──────────────────────────────────────────────

    [Fact]
    public void Badge_text_parses_to_a_count_only_when_it_actually_is_one()
    {
        var vm = new TaskItemViewModel(W("s1", "Acme - Slack", "Slack", SlackBundle), new StubWm());

        vm.ApplyBadge("7");
        Assert.True(vm.HasBadge);
        Assert.Equal(7, vm.BadgeCount);

        // macOS ellipsizes a wide Dock label: the label still shows, but there is no honest number.
        vm.ApplyBadge("..82");
        Assert.True(vm.HasBadge);
        Assert.Equal("..82", vm.BadgeText);
        Assert.Null(vm.BadgeCount);

        // A dot badge is a real badge with no count.
        vm.ApplyBadge("•");
        Assert.True(vm.HasBadge);
        Assert.Null(vm.BadgeCount);

        // Clearing is the app stopping badging — not a zero.
        vm.ApplyBadge(null);
        Assert.False(vm.HasBadge);
        Assert.Null(vm.BadgeText);
    }

    [Fact]
    public void Badge_changes_raise_the_bindings_the_view_and_a_screen_reader_depend_on()
    {
        var vm = new TaskItemViewModel(W("s1", "Acme - Slack", "Slack", SlackBundle), new StubWm());
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        vm.ApplyBadge("7");
        Assert.Contains(nameof(TaskItemViewModel.BadgeText), raised);
        Assert.Contains(nameof(TaskItemViewModel.HasBadge), raised);
        Assert.Contains(nameof(TaskItemViewModel.BadgeCount), raised);
        Assert.Contains(nameof(TaskItemViewModel.StatusText), raised);
        Assert.Contains("7 unread", vm.StatusText);

        // Idempotent: the 2s poll re-pushing the same label must not churn bindings.
        raised.Clear();
        vm.ApplyBadge("7");
        Assert.Empty(raised);
    }

    // ── Grouped button ──────────────────────────────────────────────────

    [Fact]
    public void Group_badge_is_the_apps_unread_not_the_window_count()
    {
        var wm = new StubWm();
        var group = new TaskGroupViewModel(SlackBundle);
        var w1 = new TaskItemViewModel(W("s1", "general", SlackBundle, SlackBundle), wm);
        var w2 = new TaskItemViewModel(W("s2", "random", SlackBundle, SlackBundle), wm);
        group.SyncChildren(new[] { w1, w2 });

        Assert.False(group.HasBadge);          // two windows, no unread → no pill (not "2")
        Assert.Equal(2, group.Count);

        w1.ApplyBadge("7");
        Assert.Equal("7", group.BadgeText);    // the app's unread, not the member count
        Assert.Equal(7, group.BadgeCount);
        Assert.Equal(2, group.Count);          // glomming count (R-TB-6) untouched
        Assert.Contains("7 unread", group.StatusText);

        w1.ApplyBadge(null);
        Assert.False(group.HasBadge);
    }

    // ── End to end through the poll loop ────────────────────────────────

    [AvaloniaFact]
    public async Task Badges_flow_from_the_pal_source_onto_buttons_and_clear_again()
    {
        var wm = new StubWm
        {
            Live = new[]
            {
                W("s1", "general - Acme - Slack", "Slack", SlackBundle),
                W("f1", "Documents", "Finder", "com.apple.finder"),
            },
        };
        var badges = new FakeAppBadgeSource();
        badges.Set(SlackBundle, "7");

        using var model = new ShellModel(wm, null, null, usage: TestUsage.Scratch(), badges: badges);
        model.Start();

        var slack = await Settle(model, m => m.Windows.FirstOrDefault(v => v.Id.Value == "s1")?.HasBadge == true);
        Assert.Equal("7", slack.BadgeText);
        Assert.Equal(7, slack.BadgeCount);

        // The unbadged app stays clean — badges are per app, never smeared across the strip.
        Assert.False(model.Windows.Single(v => v.Id.Value == "f1").HasBadge);

        // The user reads their messages: the source stops publishing and the pill must GO. A stale
        // count is worse than none.
        badges.Clear();
        await Settle(model, m => m.Windows.All(v => !v.HasBadge));
        Assert.Null(slack.BadgeText);
    }

    /// <summary>Pumps the dispatcher until the model reaches <paramref name="until"/> (the reconcile
    /// loop's first pass is immediate; later passes are 2 s apart, so this only ever waits on the first).</summary>
    private static async Task<TaskItemViewModel> Settle(ShellModel model, Func<ShellModel, bool> until)
    {
        for (var i = 0; i < 300; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (until(model)) break;
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(until(model), "model did not reach the expected badge state");
        return model.Windows.FirstOrDefault() ?? throw new InvalidOperationException("no buttons");
    }

    private sealed class StubWm : IWindowManager
    {
        public IReadOnlyList<ForeignWindow> Live { get; set; } = Array.Empty<ForeignWindow>();
        public Capabilities Capabilities { get; } = new(true, TrayCapability.Mirrored, Array.Empty<string>());
        public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
            => ValueTask.FromResult(Live);
        public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, CancellationToken ct = default) => Task.CompletedTask;
        public event EventHandler<ForeignWindow>? WindowOpened { add { } remove { } }
        public event EventHandler<ForeignWindow>? WindowClosed { add { } remove { } }
        public event EventHandler<ForeignWindow>? WindowChanged { add { } remove { } }
        public event EventHandler<ForeignWindow>? ForegroundChanged { add { } remove { } }
    }
}

/// <summary>
/// View-level coverage for the badge pill (bevel-ijln): it is realized only when the app actually
/// badges, sits before the title, and takes its colours from the active skin's SELECTION tokens rather
/// than a hardcoded macOS red — so it follows a Win2000 colour scheme or a Luna variant for free.
/// </summary>
[Collection("TaskbarTheme")]   // realizes the strip against Application.Current's resources
public class TaskBadgeViewTests
{
    private static ForeignWindow W(string id, string title, string appId, string bundleId)
        => new(new ForeignWindowId(id), title, appId, false, false, default, BundleId: bundleId);

    private static (TaskbarView View, ShellModel Model) BuildStrip(params TaskItemViewModel[] items)
    {
        var model = new ShellModel(null, null, null, usage: TestUsage.Scratch());
        var vm = new TaskbarViewModel(model, new StartMenuViewModel(model));
        var view = new TaskbarView { DataContext = vm };
        view.Initialize(new BevelSettings
        {
            TaskbarButtonWidth = 160,
            TaskbarGrouping = TaskbarGroupingMode.Never,
        });
        var window = new TaskbarWindow(null, rows: 1) { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        foreach (var i in items)
            model.Windows.Add(i);
        Dispatcher.UIThread.RunJobs();
        return (view, model);
    }

    private static IEnumerable<ContentControl> Pills(TaskbarView view) =>
        view.WindowButtonAreaControl.GetRealizedContainers()
            .SelectMany(c => c.GetSelfAndVisualDescendants())
            .OfType<ContentControl>()
            // Exactly ContentControl: the pill is the only plain one in a button (ToggleButton and the
            // rest derive from it, so a type test — not a Theme test — is what isolates it).
            .Where(cc => cc.GetType() == typeof(ContentControl) && cc.Theme is not null);

    [AvaloniaFact]
    public void The_pill_appears_only_for_a_badged_app_and_shows_the_platform_label()
    {
        var wm = new StubViewWm();
        var slack = new TaskItemViewModel(
            W("s1", "general - Acme - Slack", "Slack", "com.tinyspeck.slackmacgap"), wm) { Width = 160, Opacity = 1 };
        var finder = new TaskItemViewModel(
            W("f1", "Documents", "Finder", "com.apple.finder"), wm) { Width = 160, Opacity = 1 };

        var (view, _) = BuildStrip(slack, finder);

        // Nothing badges yet → no pill is visible anywhere on the strip.
        Assert.DoesNotContain(Pills(view), p => p.IsVisible);

        slack.ApplyBadge("7");
        Dispatcher.UIThread.RunJobs();

        var pill = Assert.Single(Pills(view), p => p.IsVisible);
        Assert.Equal("7", pill.Content);

        // It is realized INSIDE the badged button, not the other one.
        Assert.Same(slack, pill.FindAncestorOfType<ToggleButton>()!.DataContext);

        // …and before the title in the content row (inline layout, bevel-ijln).
        var row = (Panel)pill.Parent!;
        var title = row.Children.OfType<TextBlock>().First();
        Assert.True(row.Children.IndexOf(pill) < row.Children.IndexOf(title));
    }

    [AvaloniaFact]
    public void The_pill_is_painted_from_the_skins_selection_tokens_not_a_hardcoded_red()
    {
        var slack = new TaskItemViewModel(
            W("s1", "Acme - Slack", "Slack", "com.tinyspeck.slackmacgap"), new StubViewWm()) { Width = 160, Opacity = 1 };
        slack.ApplyBadge("7");

        var (view, _) = BuildStrip(slack);
        var pill = Assert.Single(Pills(view), p => p.IsVisible);

        var highlight = Assert.IsType<SolidColorBrush>(Token(view, "Bevel.Brush.Highlight"));
        var highlightText = Assert.IsType<SolidColorBrush>(Token(view, "Bevel.Brush.HighlightText"));

        Assert.Equal(highlight.Color, Assert.IsType<SolidColorBrush>(pill.Background).Color);
        Assert.Equal(highlightText.Color, Assert.IsType<SolidColorBrush>(pill.Foreground).Color);

        // Vector-only: the pill is a drawn Border, never an image.
        Assert.Empty(pill.GetVisualDescendants().OfType<Image>());
    }

    /// <summary>Resolves a theme token the way the running strip does (view scope, then the app's).</summary>
    private static object Token(TaskbarView view, string key)
    {
        if (view.TryFindResource(key, out var fromView) && fromView is not null) return fromView;
        Assert.True(Application.Current!.TryFindResource(key, out var fromApp), $"missing theme token {key}");
        return fromApp!;
    }

    private sealed class StubViewWm : IWindowManager
    {
        public Capabilities Capabilities { get; } = new(true, TrayCapability.Mirrored, Array.Empty<string>());
        public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<ForeignWindow>>(Array.Empty<ForeignWindow>());
        public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, CancellationToken ct = default) => Task.CompletedTask;
        public event EventHandler<ForeignWindow>? WindowOpened { add { } remove { } }
        public event EventHandler<ForeignWindow>? WindowClosed { add { } remove { } }
        public event EventHandler<ForeignWindow>? WindowChanged { add { } remove { } }
        public event EventHandler<ForeignWindow>? ForegroundChanged { add { } remove { } }
    }
}
