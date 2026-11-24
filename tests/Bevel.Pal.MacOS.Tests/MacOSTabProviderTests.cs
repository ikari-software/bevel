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

    // ── Activation argv layout (bevel-6asm fix round 1) ─────────────────────

    private static readonly string[] HostileTitles =
    {
        "-e x", "-zsh", "--", "-eproperty p : (do shell script \"id\")",
        "q\"uo\\te", "multi\nline\r\ttab", "", "x\" & (do shell script \"id\") & \"",
    };

    public static IEnumerable<object[]> HostileTitleData() => HostileTitles.Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(HostileTitleData))]
    public void Activation_argv_is_sentinel_prefixed_so_no_value_can_start_with_a_dash(string title)
    {
        var tab = new Abstractions.AppTab("com.google.Chrome", "2", 3, title, "-url", null, "-sid");
        var args = MacOSTabProvider.ActivationArguments(tab);
        Assert.Equal(new[] { "=" + title, "=-url", "=-sid" }, args);
        Assert.All(args, a => Assert.DoesNotMatch("^-", a));
        // No url / session id: still three elements, each just the sentinel.
        Assert.Equal(new[] { "=" + title, "=", "=" },
            MacOSTabProvider.ActivationArguments(new Abstractions.AppTab("com.google.Chrome", "2", 3, title)));
    }

    [Theory]
    [MemberData(nameof(HostileTitleData))]
    public void Osa_start_info_puts_double_dash_then_foreign_args_after_the_script(string title)
    {
        var args = MacOSTabProvider.ActivationArguments(new Abstractions.AppTab("com.google.Chrome", "2", 3, title, "https://e.test/"));
        var psi = MacOSTabProvider.BuildOsaStartInfo("SCRIPT", args);
        Assert.Equal("/usr/bin/osascript", psi.FileName);
        Assert.Equal(new[] { "-e", "SCRIPT", "--", "=" + title, "=https://e.test/", "=" }, psi.ArgumentList.ToArray());
        // Enumeration (no foreign text) stays a bare -e script.
        Assert.Equal(new[] { "-e", "SCRIPT" }, MacOSTabProvider.BuildOsaStartInfo("SCRIPT", Array.Empty<string>()).ArgumentList.ToArray());
    }

    [Fact]
    public void ActivateAsync_builds_exactly_script_plus_dash_dash_plus_encoded_tab()
    {
        var tab = new Abstractions.AppTab("com.google.Chrome", "7", 5, "-zsh", "", null);
        var psi = MacOSTabProvider.ActivationStartInfo(tab);
        Assert.NotNull(psi);
        var args = psi!.ArgumentList.ToArray();
        Assert.Equal(new[] { "-e", MacOSTabProvider.ActivationScript("com.google.Chrome", MacOSTabProvider.Dialect.Chromium, 7, 5),
            "--", "=-zsh", "=", "=" }, args);
        // iTerm without a session id cannot be identified: never guess, never spawn.
        Assert.Null(MacOSTabProvider.ActivationStartInfo(new Abstractions.AppTab("com.googlecode.iterm2", "77", 3, "-zsh")));
        var term = MacOSTabProvider.ActivationStartInfo(new Abstractions.AppTab("com.googlecode.iterm2", "77", 3, "-zsh", null, null, "SID-1"));
        Assert.Equal(new[] { "=-zsh", "=", "=SID-1" }, term!.ArgumentList.Skip(3).ToArray());
        // Invalid refs / unsupported apps / Gecko never produce a script invocation.
        Assert.Null(MacOSTabProvider.ActivationStartInfo(new Abstractions.AppTab("com.google.Chrome", "0", 5, "t")));
        Assert.Null(MacOSTabProvider.ActivationStartInfo(new Abstractions.AppTab("com.apple.finder", "1", 1, "t")));
        Assert.Null(MacOSTabProvider.ActivationStartInfo(new Abstractions.AppTab("org.mozilla.firefox", "1", 1, "t")));
    }

    [Theory]
    [InlineData("com.apple.Safari", "Safari")]
    [InlineData("com.google.Chrome", "Chromium")]
    [InlineData("com.googlecode.iterm2", "ITerm")]
    public void Activation_scripts_read_unwrapped_argv_and_never_embed_foreign_text(string bundleId, string dialectName)
    {
        var script = MacOSTabProvider.ActivationScript(bundleId, Enum.Parse<MacOSTabProvider.Dialect>(dialectName), 2, 3);
        Assert.Contains("on run argv", script);
        Assert.Contains("set wantTitle to my unwrap(item 1 of argv)", script);
        Assert.Contains("set wantUrl to my unwrap(item 2 of argv)", script);
        Assert.Contains("set wantSession to my unwrap(item 3 of argv)", script);
        Assert.Contains("on unwrap(s)", script);
    }

    [Theory]
    [InlineData("com.google.Chrome", "Chromium")]
    [InlineData("com.apple.Safari", "Safari")]
    [InlineData("com.googlecode.iterm2", "ITerm")]
    public void Generated_scripts_use_only_lf_line_endings(string bundleId, string dialectName)
    {
        var dialect = Enum.Parse<MacOSTabProvider.Dialect>(dialectName);
        Assert.DoesNotContain('\r', MacOSTabProvider.ActivationScript(bundleId, dialect, 2, 3));
        Assert.DoesNotContain('\r', MacOSTabProvider.EnumerationScript(bundleId, dialect));
    }

    [Fact]
    public void Chromium_activation_script_has_exact_remembered_position_and_ordering()
    {
        var s = MacOSTabProvider.ActivationScript("com.google.Chrome", MacOSTabProvider.Dialect.Chromium, 41, 97);
        Assert.Contains("title of tab 97 of window 41, URL of tab 97 of window 41, wantTitle, wantUrl", s);
        Assert.Contains("set hitW to 41", s);
        Assert.Contains("set hitT to 97", s);
        Assert.Contains("set active tab index of window hitW to hitT", s);
        // Order of the decisive steps, reported as expected-vs-actual on failure.
        var steps = new[]
        {
            "if n is not 1 then return \"none\"",
            "set active tab index of window hitW to hitT",
            "set index of window hitW to 1",
            "activate\n",
        };
        var actual = steps.OrderBy(x => s.IndexOf(x, StringComparison.Ordinal)).ToArray();
        Assert.All(steps, x => Assert.True(s.Contains(x, StringComparison.Ordinal), $"missing step: {x}"));
        Assert.Equal(steps, actual);
        Assert.Equal(1, s.Split("activate\n").Length - 1);
    }

    [Fact]
    public void Safari_and_iterm_activation_scripts_have_exact_remembered_position_and_select_call()
    {
        var safari = MacOSTabProvider.ActivationScript("com.apple.Safari", MacOSTabProvider.Dialect.Safari, 41, 97);
        Assert.Contains("name of tab 97 of window 41, URL of tab 97 of window 41", safari);
        Assert.Contains("tell window hitW to set current tab to tab hitT", safari);

        var iterm = MacOSTabProvider.ActivationScript("com.googlecode.iterm2", MacOSTabProvider.Dialect.ITerm, 4041, 97);
        Assert.Contains("tab 97 of window id 4041", iterm);
        Assert.Contains("set hitW to 4041", iterm);
        Assert.Contains("unique id of", iterm);
        Assert.Contains("select tab hitT of window id hitW", iterm);
        Assert.DoesNotContain("name of current session", iterm);   // identity is the session id, not the (volatile) job name
    }

    // ── Whitespace and missing-value normalisation ──────────────────────────

    [Fact]
    public void Script_and_csharp_trim_the_same_explicit_character_set()
    {
        var script = MacOSTabProvider.ActivationScript("com.google.Chrome", MacOSTabProvider.Dialect.Chromium, 1, 1);
        foreach (var c in TabIdentity.TrimChars)
            Assert.Contains($"character id {(int)c}", script);
        Assert.Contains('\u00a0', TabIdentity.TrimChars);                       // the NBSP case from the review
        Assert.Equal("Docs", TabIdentity.Normalize("\u00a0 Docs\u00a0"));
        Assert.Equal("Docs", MacOSTabProvider.Parse($"1{Us}1{Us}\u00a0Docs\u00a0{Us}https://a{Rs}", "com.google.Chrome", hasUrl: true)[0].Title);
    }

    [Theory]
    [InlineData(Dialect_Chromium)]
    [InlineData(Dialect_Safari)]
    public void Enumeration_normalises_missing_value_title_and_url_to_empty(string d)
    {
        var dialect = Enum.Parse<MacOSTabProvider.Dialect>(d);
        var e = MacOSTabProvider.EnumerationScript("com.apple.Safari", dialect);
        Assert.Contains("if u is missing value then set u to \"\"", e);
        Assert.Contains("if tt is missing value then set tt to \"\"", e);
        Assert.DoesNotContain("(item ti of ls)", e);   // raw concat of a missing value would drop the whole tab
    }

    private const string Dialect_Chromium = "Chromium";
    private const string Dialect_Safari = "Safari";

    [Fact]
    public void ITerm_enumeration_carries_the_session_unique_id_and_parse_reads_it()
    {
        var e = MacOSTabProvider.EnumerationScript("com.googlecode.iterm2", MacOSTabProvider.Dialect.ITerm);
        Assert.Contains("unique id of current session of t", e);
        var tab = Assert.Single(MacOSTabProvider.Parse($"77{Us}2{Us}-zsh{Us}SID-9{Rs}", "com.googlecode.iterm2", hasUrl: false, hasSessionId: true));
        Assert.Equal("SID-9", tab.SessionId);
        Assert.Null(tab.Url);
        Assert.Null(Assert.Single(MacOSTabProvider.Parse($"1{Us}1{Us}T{Us}https://a{Rs}", "com.google.Chrome", hasUrl: true)).SessionId);
    }

    // ── Matching rules (pure model mirroring the script's decision) ─────────

    private static TabIdentity.Live L(string w, int i, string title, string url, string? sid = null) => new(w, i, title, url, sid);

    private static Abstractions.AppTab Want(string w, int i, string title, string? url, string? sid = null,
        string bundle = "com.google.Chrome") => new(bundle, w, i, title, url, null, sid);

    [Fact]
    public void Resolve_same_position_still_matches()
    {
        var live = new[] { L("1", 1, "A", "https://a"), L("1", 2, "B", "https://b") };
        Assert.Equal(("1", 2), TabIdentity.Resolve(live, Want("1", 2, "B", "https://b")));
    }

    [Fact]
    public void Resolve_tab_moved_is_found_in_the_same_or_another_window()
    {
        var live = new[] { L("1", 1, "B", "https://b"), L("2", 4, "A", "https://a") };
        Assert.Equal(("1", 1), TabIdentity.Resolve(live, Want("1", 2, "B", "https://b")));
        Assert.Equal(("2", 4), TabIdentity.Resolve(live, Want("1", 1, "A", "https://a")));
    }

    [Fact]
    public void Resolve_closed_tab_is_nothing_never_the_neighbour()
    {
        var live = new[] { L("1", 1, "A", "https://a"), L("1", 2, "C", "https://c") };
        Assert.Null(TabIdentity.Resolve(live, Want("1", 2, "B", "https://b")));
    }

    [Fact]
    public void Resolve_twin_urls_elsewhere_is_ambiguous_but_the_remembered_position_still_wins()
    {
        var twins = new[] { L("1", 1, "T", "https://t"), L("1", 5, "T", "https://t") };
        Assert.Equal(("1", 5), TabIdentity.Resolve(twins, Want("1", 5, "T", "https://t")));   // K still matches
        Assert.Null(TabIdentity.Resolve(twins, Want("1", 3, "T", "https://t")));              // K gone, 2 candidates
    }

    [Fact]
    public void Resolve_changed_title_same_url_still_activates()
    {
        var live = new[] { L("1", 1, "(3) Inbox", "https://mail") };
        Assert.Equal(("1", 1), TabIdentity.Resolve(live, Want("1", 1, "Inbox", "https://mail")));
        Assert.Equal(("1", 1), TabIdentity.Resolve(live, Want("1", 4, "Inbox", "https://mail")));   // and after a move
    }

    [Fact]
    public void Resolve_empty_url_falls_back_to_title_and_trims()
    {
        var live = new[] { L("1", 1, "  Draft\u00a0", ""), L("1", 2, "Other", "") };
        Assert.Equal(("1", 1), TabIdentity.Resolve(live, Want("1", 3, "Draft", null)));
        Assert.Null(TabIdentity.Resolve(live, Want("1", 3, "draft", null)));   // case-sensitive
        Assert.Null(TabIdentity.Resolve(live, Want("1", 3, "Missing", "")));
    }

    [Fact]
    public void Resolve_different_url_same_title_does_not_match()
    {
        Assert.Null(TabIdentity.Resolve(new[] { L("1", 1, "Docs", "https://other") }, Want("1", 1, "Docs", "https://docs")));
    }

    [Fact]
    public void Resolve_iterm_uses_session_id_and_a_changed_job_name_does_not_matter()
    {
        var live = new[] { L("77", 1, "vim", "", "S1"), L("77", 2, "zsh", "", "S2") };
        Assert.Equal(("77", 2), TabIdentity.Resolve(live, Want("77", 1, "-zsh", null, "S2", "com.googlecode.iterm2")));
        Assert.Null(TabIdentity.Resolve(live, Want("77", 1, "zsh", null, "GONE", "com.googlecode.iterm2")));
        Assert.Null(TabIdentity.Resolve(live, Want("77", 2, "zsh", null, null, "com.googlecode.iterm2")));   // no id: never guess
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

    // ── bevel-bw1v: activation outcome mapping ──────────────────────────────

    [Theory]
    [InlineData("ok", Abstractions.TabActivationResult.Activated)]
    [InlineData("ok\n", Abstractions.TabActivationResult.Activated)]
    [InlineData("none", Abstractions.TabActivationResult.NotFound)]
    [InlineData("gone", Abstractions.TabActivationResult.NotFound)]
    [InlineData("", Abstractions.TabActivationResult.Failed)]
    [InlineData("garbage", Abstractions.TabActivationResult.Failed)]
    public void Activation_script_output_maps_to_an_outcome(string output, Abstractions.TabActivationResult expected)
        => Assert.Equal(expected, MacOSTabProvider.ParseOutcome(output));
}
