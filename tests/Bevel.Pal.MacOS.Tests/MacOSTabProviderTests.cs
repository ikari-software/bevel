using Bevel.Pal.MacOS;
using Xunit;

namespace Bevel.Pal.MacOS.Tests;

// Pure-logic coverage for the Apple Events tab provider (bevel-a40b): dialect routing, script
// generation, and output parsing. The osascript round-trip itself is exercised by the live rig
// (a real target app + consent state can't be faked in CI).
public class MacOSTabProviderTests
{
    private const char Us = '\u001f';
    private const char Rs = '\u001e';

    private readonly MacOSTabProvider _provider = new();

    // ── Dialect routing ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("com.googlecode.iterm2")]
    [InlineData("com.google.Chrome")]
    [InlineData("company.thebrowser.Browser")]   // Arc
    [InlineData("com.apple.Safari")]
    [InlineData("org.mozilla.firefox")]          // Gecko family — served via AX (bevel-osad)
    [InlineData("app.zen-browser.zen")]
    public void SupportsApp_knows_the_scriptable_apps(string bundleId) =>
        Assert.True(_provider.SupportsApp(bundleId));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("com.apple.finder")]
    public void SupportsApp_declines_everything_else(string? bundleId) =>
        Assert.False(_provider.SupportsApp(bundleId));

    [Theory]
    [InlineData("com.evil\" & (do shell script \"true\") & \".app")]   // script-source injection
    [InlineData("com.spaced bundle")]
    [InlineData("bundle\nid")]
    public void Unsafe_bundle_ids_are_refused_not_escaped(string bundleId)
    {
        Assert.False(MacOSTabProvider.IsSafeBundleId(bundleId));
        Assert.False(_provider.SupportsApp(bundleId));
    }

    // ── Script generation ────────────────────────────────────────────────────

    [Fact]
    public void Enumeration_scripts_guard_against_launching_a_quit_app()
    {
        foreach (var dialect in new[]
        {
            MacOSTabProvider.Dialect.ITerm, MacOSTabProvider.Dialect.Chromium, MacOSTabProvider.Dialect.Safari,
        })
        {
            var script = MacOSTabProvider.EnumerationScript("com.example.app", dialect);
            // The guard must appear BEFORE the tell block — mentioning an app object launches it.
            var guard = script.IndexOf("is not running then return", StringComparison.Ordinal);
            var tell = script.IndexOf("tell application id", StringComparison.Ordinal);
            Assert.True(guard >= 0 && tell > guard, $"missing or misplaced guard for {dialect}");
        }
    }

    [Fact]
    public void ITerm_dialect_reads_session_names_and_window_ids()
    {
        var script = MacOSTabProvider.EnumerationScript("com.googlecode.iterm2", MacOSTabProvider.Dialect.ITerm);
        Assert.Contains("name of current session of t", script);
        Assert.Contains("id of w", script);
        Assert.DoesNotContain("URL", script);
    }

    [Fact]
    public void Chromium_and_safari_dialects_batch_property_reads_per_window()
    {
        // Batched `every tab` reads are a hard requirement, not a style choice: per-tab reads cost
        // one Apple Event per property per tab — measured 8.9 s on a 182-tab Arc sidebar vs
        // 0.17 s batched. This test pins the shape so a refactor can't quietly regress it.
        var chrome = MacOSTabProvider.EnumerationScript("com.google.Chrome", MacOSTabProvider.Dialect.Chromium);
        Assert.Contains("title of every tab of window wi", chrome);
        Assert.Contains("URL of every tab of window wi", chrome);

        var safari = MacOSTabProvider.EnumerationScript("com.apple.Safari", MacOSTabProvider.Dialect.Safari);
        Assert.Contains("name of every tab of window wi", safari);
        Assert.Contains("URL of every tab of window wi", safari);
    }

    [Fact]
    public void Safari_activation_sets_current_tab_chromium_sets_active_index()
    {
        Assert.Contains("set current tab to tab 3",
            MacOSTabProvider.ActivationScript("com.apple.Safari", MacOSTabProvider.Dialect.Safari, 2, 3));
        Assert.Contains("set active tab index of window 2 to 3",
            MacOSTabProvider.ActivationScript("com.google.Chrome", MacOSTabProvider.Dialect.Chromium, 2, 3));
        Assert.Contains("select tab 3 of window id 77",
            MacOSTabProvider.ActivationScript("com.googlecode.iterm2", MacOSTabProvider.Dialect.ITerm, 77, 3));
    }

    // ── Output parsing ───────────────────────────────────────────────────────

    [Fact]
    public void Parse_reads_rs_us_framed_rows()
    {
        var raw = $"1{Us}1{Us}Welcome{Us}https://a.test{Rs}1{Us}2{Us}Docs{Us}https://b.test{Rs}2{Us}1{Us}Other{Us}https://c.test{Rs}";
        var tabs = MacOSTabProvider.Parse(raw, "com.google.Chrome", hasUrl: true);

        Assert.Equal(3, tabs.Count);
        Assert.Equal(new Abstractions.AppTab("com.google.Chrome", "1", 2, "Docs", "https://b.test"), tabs[1]);
        Assert.Equal("2", tabs[2].WindowRef);
    }

    [Fact]
    public void Parse_survives_titles_containing_quotes_commas_and_newlines()
    {
        var title = "\"Quoted\", comma'd,\nand multi-line — Slack";
        var raw = $"4{Us}7{Us}{title}{Rs}";
        var tabs = MacOSTabProvider.Parse(raw, "com.googlecode.iterm2", hasUrl: false);

        var tab = Assert.Single(tabs);
        Assert.Equal(title, tab.Title);
        Assert.Equal(7, tab.TabIndex);
        Assert.Null(tab.Url);
    }

    [Fact]
    public void Parse_falls_back_to_url_when_a_loading_tab_has_no_title()
    {
        var raw = $"1{Us}1{Us}{Us}https://loading.test{Rs}";
        var tab = Assert.Single(MacOSTabProvider.Parse(raw, "com.google.Chrome", hasUrl: true));
        Assert.Equal("https://loading.test", tab.Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData("no separators at all")]
    public void Parse_returns_empty_for_junk(string raw) =>
        Assert.Empty(MacOSTabProvider.Parse(raw, "com.google.Chrome", hasUrl: true));

    [Fact]
    public void Parse_skips_malformed_rows_keeps_good_ones()
    {
        var raw = $"only-two-fields{Us}x{Rs}1{Us}notanint{Us}T{Rs}2{Us}5{Us}Good{Rs}";
        var tab = Assert.Single(MacOSTabProvider.Parse(raw, "com.googlecode.iterm2", hasUrl: false));
        Assert.Equal("Good", tab.Title);
        Assert.Equal(5, tab.TabIndex);
    }

    // ── Activation input hardening ───────────────────────────────────────────

    [Theory]
    [InlineData("com.google.Chrome", "1; do shell script", 1)]   // non-numeric ref (injection shape)
    [InlineData("com.google.Chrome", "0", 1)]                    // window index below 1
    [InlineData("com.google.Chrome", "-3", 1)]                   // negative ref parses but is refused
    [InlineData("com.google.Chrome", "1", 0)]                    // tab index below 1
    [InlineData("com.google.Chrome", "1", -1)]
    [InlineData("com.apple.finder", "1", 1)]                     // unsupported app never scripts
    public async Task Activate_refuses_invalid_refs(string bundleId, string windowRef, int tabIndex)
    {
        // Invalid refs never reach script generation (and thus osascript): ActivateAsync validates
        // and returns without spawning. Completing instantly (no 4 s osascript timeout) is the
        // observable proof the guard short-circuited.
        var tab = new Abstractions.AppTab(bundleId, windowRef, tabIndex, "t");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _provider.ActivateAsync(tab);
        Assert.True(sw.ElapsedMilliseconds < 500, $"invalid ref ({windowRef}/{tabIndex}) should short-circuit, not spawn osascript");
    }
}
