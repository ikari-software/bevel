using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Bevel.Core.Search;
using Bevel.Pal.Abstractions;
using Bevel.Pal.Fake;
using Bevel.Taskbar;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The Start menu's "Tabs" search section (bevel-vtc6): <see cref="TabsSearchProvider"/> ranks every
/// running <see cref="ITabProvider"/> app's tabs through the one TabPrefetch-style warm delegate, paints
/// cache-first, and re-resolves the clicked tab by Url on activation so a stale positional row can never
/// press the wrong tab.
/// </summary>
[Collection("TaskbarTheme")]   // the Start-menu integration case runs alongside theme-mutating tests
public sealed class TabsSearchProviderTests
{
    private const string App = "com.example.browser";

    /// <summary>Counting fake in the shape of <c>TabPrefetchTests.CountingTabs</c>: proves the
    /// SupportsApp gate (an unsupported app is never enumerated) and records activations.</summary>
    private sealed class CountingTabs : ITabProvider
    {
        private readonly HashSet<string> _supported;
        public int SupportsCalls;
        public int GetCalls;
        public readonly List<AppTab> Activated = [];
        public List<AppTab> Tabs = [];

        public CountingTabs(params string[] supported) => _supported = new(supported, StringComparer.Ordinal);

        public Capabilities Capabilities { get; } = new(true, TrayCapability.Mirrored, []);

        public bool SupportsApp(string? bundleId)
        {
            Interlocked.Increment(ref SupportsCalls);
            return bundleId is not null && _supported.Contains(bundleId);
        }

        public ValueTask<IReadOnlyList<AppTab>> GetTabsAsync(string bundleId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref GetCalls);
            return ValueTask.FromResult<IReadOnlyList<AppTab>>(Tabs.ToArray());
        }

        public Task<TabActivationResult> ActivateAsync(AppTab tab, CancellationToken ct = default)
        {
            lock (Activated) Activated.Add(tab);
            return Task.FromResult(TabActivationResult.Activated);
        }
    }

    private static AppTab Tab(string title, string? url, int index = 1) => new(App, "1", index, title, url);

    private static IReadOnlyList<TaskButtonMenu.TabMenuRow> Rows(params AppTab[] tabs)
        => tabs.Select(t => new TaskButtonMenu.TabMenuRow(t, null)).ToArray();

    private static Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>> RowsOf(params AppTab[] tabs)
        => Task.FromResult(Rows(tabs));

    /// <summary>A publish collector that is safe to call from the session's thread-pool queries.</summary>
    private sealed class PublishLog
    {
        private readonly List<IReadOnlyList<SearchHit>> _all = [];
        public void Add(IReadOnlyList<SearchHit> hits) { lock (_all) _all.Add(hits); }
        public IReadOnlyList<IReadOnlyList<SearchHit>> All { get { lock (_all) return _all.ToArray(); } }
        public IReadOnlyList<SearchHit> Last { get { lock (_all) return _all.Count > 0 ? _all[^1] : Array.Empty<SearchHit>(); } }
    }

    /// <summary>Cache-first paint: the first publish comes out synchronously from the last completed
    /// warm (before the first await), the refresh publish follows once the parallel warms finish.</summary>
    [Fact]
    public async Task Publishes_cached_rows_first_then_fresh_rows()
    {
        var tabs = new CountingTabs(App);
        var log = new PublishLog();
        var warmCalls = new List<string>();
        var gate = new TaskCompletionSource<IReadOnlyList<TaskButtonMenu.TabMenuRow>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> impl =
            _ => gate.Task;
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> warm = id =>
        {
            lock (warmCalls) warmCalls.Add(id);
            return impl(id);
        };
        var provider = new TabsSearchProvider(() => new[] { App }, warm, tabs);

        // First query, nothing cached yet: the synchronous first publish is empty…
        var q1 = provider.QueryAsync("docs", 8, log.Add, CancellationToken.None);
        Assert.Single(log.All);                 // ran before the first await
        Assert.Empty(log.Last);

        // …then the warm completes and the refresh publish carries the row.
        impl = _ => RowsOf(Tab("Docs", "https://example.test/docs"));
        gate.SetResult(Rows(Tab("Docs", "https://example.test/docs")));
        await q1;
        Assert.Equal(2, log.All.Count);
        Assert.Equal("Docs", Assert.Single(log.Last).Title);

        // Second query with the warm now hanging: the cache-first publish is ALREADY out, carrying the
        // cached row — the instant first paint does not wait on the app.
        var hang = new TaskCompletionSource<IReadOnlyList<TaskButtonMenu.TabMenuRow>>(TaskCreationOptions.RunContinuationsAsynchronously);
        impl = _ => hang.Task;
        var q2 = provider.QueryAsync("docs", 8, log.Add, CancellationToken.None);
        Assert.Equal(3, log.All.Count);
        Assert.Equal("Docs", Assert.Single(log.All[2]).Title);

        hang.SetResult(Rows(Tab("Docs", "https://example.test/docs"), Tab("Docs 2", "https://example.test/docs2", 2)));
        await q2;
        Assert.Equal(4, log.All.Count);
        Assert.Equal(2, log.All[3].Count);      // the fresher picture replaces the cached one
        lock (warmCalls) Assert.Equal([App, App], warmCalls);
    }

    /// <summary>A title match ranks above a Url-only match (rank+3), and the Url is shown as subtitle.</summary>
    [Fact]
    public async Task Matches_title_before_url()
    {
        var tabs = new CountingTabs(App);
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> warm = _ => RowsOf(
            Tab("Welcome", "https://example.test/docs"),          // matches "docs" only in the URL
            Tab("Docs Guide", "https://example.test/x"));          // title prefix match
        var provider = new TabsSearchProvider(() => new[] { App }, warm, tabs);
        var log = new PublishLog();

        await provider.QueryAsync("docs", 8, log.Add, CancellationToken.None);

        var hits = log.Last;
        Assert.Equal(["Docs Guide", "Welcome"], hits.Select(h => h.Title));
        Assert.Equal(0, hits[0].Rank);                              // name prefix
        Assert.Equal(4, hits[1].Rank);                              // url word-start (1) + 3
        Assert.Equal("https://example.test/docs", hits[1].Subtitle);
        // The hit carries the row itself as Tag, so the results view binds the favicon off it.
        Assert.IsType<TaskButtonMenu.TabMenuRow>(hits[0].Tag);
    }

    /// <summary>Activation re-warms the app and re-resolves by Url against the FRESH rows: a tab that
    /// moved index since enumeration is activated at its NEW index, not the stale remembered one.</summary>
    [Fact]
    public async Task Activation_re_resolves_the_tab_by_url_and_ignores_a_stale_index()
    {
        var tabs = new CountingTabs(App);
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> impl =
            _ => RowsOf(
                Tab("Alpha", "https://a.test/1", 1),
                Tab("Beta", "https://b.test/2", 2));                // Beta sits at index 2 in the stale list
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> warm = _ => impl(_);
        var provider = new TabsSearchProvider(() => new[] { App }, warm, tabs);
        var log = new PublishLog();

        await provider.QueryAsync("beta", 8, log.Add, CancellationToken.None);
        var hit = Assert.Single(log.Last);
        Assert.Equal("Beta", hit.Title);

        // The app re-enumerated meanwhile: Beta moved to index 1.
        impl = _ => RowsOf(
            Tab("Beta", "https://b.test/2", 1),
            Tab("Alpha", "https://a.test/1", 2));

        await hit.ActivateAsync();

        var activated = Assert.Single(tabs.Activated);
        Assert.Equal("https://b.test/2", activated.Url);
        Assert.Equal(1, activated.TabIndex);                        // fresh index, NOT the stale 2
    }

    /// <summary>When the fresh enumeration no longer contains the clicked tab (closed/moved away), the
    /// activation is a silent no-op — it never presses whatever tab now occupies the remembered index.</summary>
    [Fact]
    public async Task Activation_with_no_matching_fresh_row_is_a_no_op()
    {
        var tabs = new CountingTabs(App);
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> impl =
            _ => RowsOf(Tab("Beta", "https://b.test/2", 2));
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> warm = _ => impl(_);
        var provider = new TabsSearchProvider(() => new[] { App }, warm, tabs);
        var log = new PublishLog();

        await provider.QueryAsync("beta", 8, log.Add, CancellationToken.None);
        var hit = Assert.Single(log.Last);

        impl = _ => RowsOf(Tab("Alpha", "https://a.test/1", 2));     // Beta is gone; index 2 is now Alpha
        await hit.ActivateAsync();

        Assert.Empty(tabs.Activated);
    }

    /// <summary>The SupportsApp gate: an unsupported running app is never warmed (and therefore never
    /// enumerated) — no osascript, no consent prompt for an app with no tab dialect.</summary>
    [Fact]
    public async Task Unsupported_apps_are_never_queried()
    {
        var tabs = new CountingTabs("com.supported");
        var warmCalls = new List<string>();
        // The warm stands in for TabPrefetch: it only reaches GetTabsAsync for ids it is handed.
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> warm = async id =>
        {
            lock (warmCalls) warmCalls.Add(id);
            var got = await tabs.GetTabsAsync(id);
            return got.Select(t => new TaskButtonMenu.TabMenuRow(t, null)).ToArray();
        };
        var provider = new TabsSearchProvider(
            () => new[] { "com.supported", "com.other", "com.supported" },   // dupes collapse too
            warm, tabs);
        var log = new PublishLog();

        await provider.QueryAsync("tab", 8, log.Add, CancellationToken.None);

        lock (warmCalls) Assert.Equal(["com.supported"], warmCalls);
        Assert.Equal(1, tabs.GetCalls);                             // exactly one enumeration, supported app only
        Assert.Equal(3, tabs.SupportsCalls);                        // the gate was consulted per running id (incl. the dupe)
    }

    /// <summary>Empty-provider case: no running apps → the section publishes empty and never queries.</summary>
    [Fact]
    public async Task No_running_apps_publishes_an_empty_section_without_querying()
    {
        var tabs = new CountingTabs(App);
        var warmCalls = 0;
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> warm = _ => { warmCalls++; return RowsOf(Tab("Docs", "https://x.test")); };
        var provider = new TabsSearchProvider(() => Array.Empty<string>(), warm, tabs);
        var log = new PublishLog();

        await provider.QueryAsync("docs", 8, log.Add, CancellationToken.None);

        Assert.NotEmpty(log.All);
        Assert.All(log.All, p => Assert.Empty(p));
        Assert.Equal(0, warmCalls);
        Assert.Equal(0, tabs.GetCalls);
    }

    /// <summary>Empty-provider case: the warm itself reports "no dialect" (null, like TabPrefetch.Warm
    /// for an unsupported app) → empty section, no throw.</summary>
    [Fact]
    public async Task A_warm_returning_null_yields_an_empty_section_without_throwing()
    {
        var tabs = new CountingTabs();      // supports NOTHING
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> warm = _ => null;
        var provider = new TabsSearchProvider(() => new[] { App }, warm, tabs);
        var log = new PublishLog();

        await provider.QueryAsync("docs", 8, log.Add, CancellationToken.None);

        Assert.NotEmpty(log.All);
        Assert.All(log.All, p => Assert.Empty(p));
        Assert.Equal(0, tabs.GetCalls);
    }

    /// <summary>bevel-vtc6 review (publish storm): when the post-refresh rows are reference/sequence-
    /// identical to the cached ones, the second publish is skipped — a keystroke must not rebuild both
    /// results views twice.</summary>
    [Fact]
    public async Task The_refresh_publish_is_skipped_when_the_fresh_rows_are_identical_to_the_cached_ones()
    {
        var tabs = new CountingTabs(App);
        var rows = Rows(Tab("Docs", "https://example.test/docs"));
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> warm = _ => Task.FromResult(rows);
        var provider = new TabsSearchProvider(() => new[] { App }, warm, tabs);
        var log = new PublishLog();

        await provider.QueryAsync("docs", 8, log.Add, CancellationToken.None);
        Assert.Equal(2, log.All.Count);                 // empty first paint + the fresh publish
        Assert.Equal("Docs", Assert.Single(log.Last).Title);

        await provider.QueryAsync("docs", 8, log.Add, CancellationToken.None);
        Assert.Equal(3, log.All.Count);                 // cache-first publish ONLY — identical refresh skipped
        Assert.Equal("Docs", Assert.Single(log.Last).Title);
    }

    /// <summary>bevel-vtc6 review: a warm delegate that THROWS keeps the previous rows — the failure is
    /// per-app, and the section still shows what the last successful warm produced.</summary>
    [Fact]
    public async Task A_throwing_warm_keeps_the_previous_rows()
    {
        var tabs = new CountingTabs(App);
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> impl =
            _ => RowsOf(Tab("Docs", "https://example.test/docs"));
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> warm = id => impl(id);
        var provider = new TabsSearchProvider(() => new[] { App }, warm, tabs);
        var log = new PublishLog();

        await provider.QueryAsync("docs", 8, log.Add, CancellationToken.None);
        Assert.Equal("Docs", Assert.Single(log.Last).Title);

        impl = _ => throw new InvalidOperationException("app quit mid-query");
        await provider.QueryAsync("docs", 8, log.Add, CancellationToken.None);   // must not throw

        Assert.Equal("Docs", Assert.Single(log.Last).Title);   // the previous rows stand
    }

    /// <summary>The section is capped at MaxRows (8) even when the session allows more and more rows
    /// match — a browser can hold hundreds of tabs; the section stays scannable.</summary>
    [Fact]
    public async Task Results_are_capped_at_eight_rows_with_ten_matches()
    {
        var tabs = new CountingTabs(App);
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> warm = _ => RowsOf(
            Enumerable.Range(1, 10).Select(i => Tab($"Docs {i}", $"https://example.test/{i}", i)).ToArray());
        var provider = new TabsSearchProvider(() => new[] { App }, warm, tabs);
        var log = new PublishLog();

        await provider.QueryAsync("docs", 20, log.Add, CancellationToken.None);

        Assert.Equal(8, log.Last.Count);
        Assert.Equal("Docs 1", log.Last[0].Title);   // stable in row order within the same rank
    }

    /// <summary>bevel-vtc6 review (rank merge): two tab-capable apps are both warmed and their hits merge
    /// into ONE section ordered by rank — a title match in app B beats a Url-only match in app A.</summary>
    [Fact]
    public async Task Hits_from_two_apps_merge_in_rank_order()
    {
        const string A = "com.example.a", B = "com.example.b";
        var tabs = new CountingTabs(A, B);
        var warmCalls = new List<string>();
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> warm = id =>
        {
            lock (warmCalls) warmCalls.Add(id);
            return id == A
                ? RowsOf(new AppTab(A, "1", 1, "Welcome", "https://a.test/docs"))          // url-only match
                : RowsOf(new AppTab(B, "1", 1, "Docs Guide", "https://b.test/x"));         // title match
        };
        var provider = new TabsSearchProvider(() => new[] { A, B }, warm, tabs);
        var log = new PublishLog();

        await provider.QueryAsync("docs", 8, log.Add, CancellationToken.None);

        Assert.Equal(["Docs Guide", "Welcome"], log.Last.Select(h => h.Title));
        Assert.Equal(0, log.Last[0].Rank);
        Assert.True(log.Last[1].Rank > log.Last[0].Rank);
        lock (warmCalls) Assert.Equal(new[] { A, B }.OrderBy(x => x), warmCalls.OrderBy(x => x));
    }

    /// <summary>bevel-vtc6 review (title fallback): a stale row with NO Url re-resolves by Title against
    /// the fresh rows — but only on a UNIQUE title match, and it activates the tab at its NEW index.</summary>
    [Fact]
    public async Task Activation_falls_back_to_a_unique_title_match_only_when_the_stale_row_had_no_url()
    {
        var tabs = new CountingTabs(App);
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> impl =
            _ => RowsOf(new AppTab(App, "1", 3, "Shell"));               // terminal-ish row: no Url, index 3
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> warm = _ => impl(_);
        var provider = new TabsSearchProvider(() => new[] { App }, warm, tabs);
        var log = new PublishLog();

        await provider.QueryAsync("shell", 8, log.Add, CancellationToken.None);
        var hit = Assert.Single(log.Last);

        impl = _ => RowsOf(new AppTab(App, "1", 1, "Shell"));             // the same tab moved to index 1
        await hit.ActivateAsync();

        var activated = Assert.Single(tabs.Activated);
        Assert.Equal("Shell", activated.Title);
        Assert.Equal(1, activated.TabIndex);                              // fresh index, NOT the stale 3
    }

    /// <summary>bevel-vtc6 review: several fresh rows carrying the stale title cannot be told apart —
    /// the activation is a no-op rather than a guess at one of the twins.</summary>
    [Fact]
    public async Task Activation_with_several_title_matches_is_a_no_op()
    {
        var tabs = new CountingTabs(App);
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> impl =
            _ => RowsOf(new AppTab(App, "1", 1, "Shell"));
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> warm = _ => impl(_);
        var provider = new TabsSearchProvider(() => new[] { App }, warm, tabs);
        var log = new PublishLog();

        await provider.QueryAsync("shell", 8, log.Add, CancellationToken.None);
        var hit = Assert.Single(log.Last);

        impl = _ => RowsOf(
            new AppTab(App, "1", 1, "Shell"),
            new AppTab(App, "2", 1, "Shell"));                            // twins in two windows
        await hit.ActivateAsync();

        Assert.Empty(tabs.Activated);
    }

    /// <summary>bevel-vtc6 review: when the stale row HAD a Url, the Title is never consulted — a fresh
    /// row that merely shares the title is a different tab.</summary>
    [Fact]
    public async Task Activation_never_falls_back_to_title_when_the_stale_row_had_a_url()
    {
        var tabs = new CountingTabs(App);
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> impl =
            _ => RowsOf(Tab("Docs", "https://example.test/docs", 1));
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> warm = _ => impl(_);
        var provider = new TabsSearchProvider(() => new[] { App }, warm, tabs);
        var log = new PublishLog();

        await provider.QueryAsync("docs", 8, log.Add, CancellationToken.None);
        var hit = Assert.Single(log.Last);

        impl = _ => RowsOf(Tab("Docs", "https://example.test/other", 1));   // url changed, title same
        await hit.ActivateAsync();

        Assert.Empty(tabs.Activated);
    }

    /// <summary>bevel-vtc6 review: terminal rows carry a SessionId and that is their identity — the
    /// fresh row with the same session id is activated at its new position, even with a new title.</summary>
    [Fact]
    public async Task Activation_prefers_the_session_id_for_terminal_rows()
    {
        var tabs = new CountingTabs(App);
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> impl =
            _ => RowsOf(new AppTab(App, "1", 2, "build", Url: null, IconPng: null, SessionId: "s1"));
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> warm = _ => impl(_);
        var provider = new TabsSearchProvider(() => new[] { App }, warm, tabs);
        var log = new PublishLog();

        await provider.QueryAsync("build", 8, log.Add, CancellationToken.None);
        var hit = Assert.Single(log.Last);

        impl = _ => RowsOf(
            new AppTab(App, "1", 1, "other", Url: null, IconPng: null, SessionId: "s2"),
            new AppTab(App, "1", 5, "renamed", Url: null, IconPng: null, SessionId: "s1"));   // moved + retitled
        await hit.ActivateAsync();

        var activated = Assert.Single(tabs.Activated);
        Assert.Equal("s1", activated.SessionId);
        Assert.Equal(5, activated.TabIndex);
    }

    /// <summary>bevel-vtc6 review: the activation re-warm is bounded (WaitAsync budget) — a hung app must
    /// not hang the Enter handler; the activation just no-ops after the budget.</summary>
    [Fact]
    public async Task The_activation_re_warm_is_bounded_by_the_budget()
    {
        var tabs = new CountingTabs(App);
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> impl =
            _ => RowsOf(Tab("Beta", "https://b.test/2", 2));
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> warm = _ => impl(_);
        var provider = new TabsSearchProvider(() => new[] { App }, warm, tabs)
        {
            ActivateBudget = TimeSpan.FromMilliseconds(100),
        };
        var log = new PublishLog();

        await provider.QueryAsync("beta", 8, log.Add, CancellationToken.None);
        var hit = Assert.Single(log.Last);

        impl = _ => new TaskCompletionSource<IReadOnlyList<TaskButtonMenu.TabMenuRow>>(
            TaskCreationOptions.RunContinuationsAsynchronously).Task;   // hangs forever
        var started = Environment.TickCount64;
        await hit.ActivateAsync();                                        // must return, not hang

        Assert.True(Environment.TickCount64 - started < 5000, "the re-warm must be bounded by ActivateBudget");
        Assert.Empty(tabs.Activated);
    }

    /// <summary>Provider results appear through the real <see cref="SearchSession"/> plumbing: the
    /// "tabs" section fills and the tab hit becomes the TopHit when no app matches.</summary>
    [Fact]
    public async Task SearchSession_surfaces_tab_hits_in_the_tabs_section()
    {
        var tabs = new CountingTabs(App);
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> warm =
            _ => RowsOf(Tab("Docs", "https://example.test/docs"));
        var provider = new TabsSearchProvider(() => new[] { App }, warm, tabs);
        var session = new SearchSession(new ISearchProvider[] { provider }, a => a());
        var saw = new TaskCompletionSource<IReadOnlyList<SearchHit>>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.SectionChanged += (id, hits) => { if (id == "tabs" && hits.Count > 0) saw.TrySetResult(hits); };

        session.SetQuery("docs");

        var hits = await saw.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Docs", Assert.Single(hits).Title);
        Assert.Equal("Docs", Assert.Single(session.Section("tabs")).Title);
        Assert.Equal("Docs", session.TopHit!.Title);
    }

    // ── Start-menu integration ─────────────────────────────────────────

    /// <summary>End to end under the headless shell: with the tab plumbing handed to the Start menu,
    /// typing lists matching tabs under a "Tabs" header in the results view, and Enter dispatches the
    /// activation to the provider with the re-resolved tab.</summary>
    [AvaloniaFact]
    public async Task Typing_in_the_start_menu_lists_matching_tabs_and_enter_activates_them()
    {
        var appEnv = new StubAppEnvironment(new InstalledApp("com.calc", "Calculator", null));
        var model = new ShellModel(null, appEnv, null, usage: TestUsage.Scratch());
        model.Start();
        model.StartProgramsAsync();
        Bevel.TestSupport.Settle.Until(() => model.Programs.Count >= 1, "programs to load");

        var tabs = new FakeTabProvider();
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> warm = id =>
            id == FakeTabProvider.TabApp
                ? RowsOf(
                    new AppTab(FakeTabProvider.TabApp, "1", 1, "Welcome — Fake Browser", "https://example.test/welcome", FakeTabProvider.FaviconPng),
                    new AppTab(FakeTabProvider.TabApp, "1", 2, "Docs", "https://example.test/docs", FakeTabProvider.FaviconPng),
                    new AppTab(FakeTabProvider.TabApp, "2", 1, "Second Window Tab", "https://example.test/two"))
                : null;
        var menu = new StartMenu(null, null, programs: new StartMenuViewModel(model),
            runningTabBundleIds: () => new[] { FakeTabProvider.TabApp },
            warmTabs: warm,
            tabProvider: tabs);
        var button = new Button { Content = "Start" };
        var host = new Window { SystemDecorations = SystemDecorations.None, Content = button };
        host.Show();
        Dispatcher.UIThread.RunJobs();
        await menu.OpenAsync(button);
        Dispatcher.UIThread.RunJobs();

        try
        {
            var root = menu.FindControl<Panel>("PopupRoot")!;
            root.RaiseEvent(new TextInputEventArgs
            {
                RoutedEvent = InputElement.TextInputEvent,
                Text = "docs",
                Source = root,
            });

            var results = menu.FindControl<StartSearchResultsView>("ClassicResultsView")!;
            Bevel.TestSupport.Settle.Until(
                () => results.Rows.OfType<SearchSectionHeader>().Any(s => s.Title == "Tabs"),
                "the Tabs section to appear");

            // No installed program matches "docs", so the only section is Tabs with the one hit.
            Assert.Equal("Tabs", Assert.IsType<SearchSectionHeader>(results.Rows[0]).Title);
            var hit = Assert.IsType<SearchHit>(results.Rows[1]);
            Assert.Equal("Docs", hit.Title);
            Assert.Equal("https://example.test/docs", hit.Subtitle);
            Assert.IsType<TaskButtonMenu.TabMenuRow>(hit.Tag);

            // Enter dispatches the top hit (the tab): activation re-resolves by Url and reaches the provider.
            root.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = root });
            Bevel.TestSupport.Settle.Until(() => tabs.LastActivated is not null, "the tab activation");
            Assert.Equal("https://example.test/docs", tabs.LastActivated!.Url);
            Assert.False(menu.IsOpen);
        }
        finally
        {
            menu.Close();
            host.Close();
            model.Dispose();
        }
    }

    /// <summary>bevel-vtc6 review (wrong-row Enter): Enter must NOT launch the TopHit when the keystroke
    /// comes from a focused row of ANY section — a focused TAB row is a hit too, not just a Programs row.
    /// (Rows carry their SearchHit as DataContext.)</summary>
    [AvaloniaFact]
    public async Task Enter_on_a_focused_tab_row_leaves_the_rows_own_activation_alone()
    {
        var appEnv = new StubAppEnvironment(new InstalledApp("com.calc", "Calculator", null));
        var model = new ShellModel(null, appEnv, null, usage: TestUsage.Scratch());
        model.Start();
        model.StartProgramsAsync();
        Bevel.TestSupport.Settle.Until(() => model.Programs.Count >= 1, "programs to load");

        var tabs = new FakeTabProvider();
        Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> warm = id =>
            id == FakeTabProvider.TabApp
                ? RowsOf(new AppTab(FakeTabProvider.TabApp, "1", 1, "Docs", "https://example.test/docs"))
                : null;
        var menu = new StartMenu(null, null, programs: new StartMenuViewModel(model),
            runningTabBundleIds: () => new[] { FakeTabProvider.TabApp },
            warmTabs: warm,
            tabProvider: tabs);
        var button = new Button { Content = "Start" };
        var host = new Window { SystemDecorations = SystemDecorations.None, Content = button };
        host.Show();
        Dispatcher.UIThread.RunJobs();
        await menu.OpenAsync(button);
        Dispatcher.UIThread.RunJobs();

        try
        {
            var root = menu.FindControl<Panel>("PopupRoot")!;
            root.RaiseEvent(new TextInputEventArgs
            {
                RoutedEvent = InputElement.TextInputEvent,
                Text = "docs",
                Source = root,
            });

            var results = menu.FindControl<StartSearchResultsView>("ClassicResultsView")!;
            Bevel.TestSupport.Settle.Until(
                () => results.Rows.OfType<SearchHit>().Any(h => h.Title == "Docs"),
                "the tab hit to appear");
            var tabHit = results.Rows.OfType<SearchHit>().Single(h => h.Title == "Docs");

            // The tab hit is the TopHit (no program matches "docs") — but a focused TAB row must still
            // win over the top-match launch, exactly like a focused Programs row.
            Assert.True(menu.EnterShouldLaunchTopMatch(root));   // menu at large: the top hit launches
            Assert.False(menu.EnterShouldLaunchTopMatch(new MenuItem { DataContext = tabHit }));
            // A row that is not a hit at all is no reason to skip the top hit.
            Assert.True(menu.EnterShouldLaunchTopMatch(new MenuItem { Header = "Run…" }));
        }
        finally
        {
            menu.Close();
            host.Close();
            model.Dispose();
        }
    }
}
