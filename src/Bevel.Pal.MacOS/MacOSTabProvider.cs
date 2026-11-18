using System.Diagnostics;
using System.Globalization;
using Bevel.Pal.Abstractions;

namespace Bevel.Pal.MacOS;

/// <summary>
/// Tab provider (bevel-a40b): enumerates and activates tabs in the apps whose scripting
/// dictionaries expose them — iTerm2, the Chromium family (Chrome/Arc/Edge/Brave/…) and Safari —
/// plus the Gecko family (Zen/Firefox), which has no tab dictionary and is served through the
/// accessibility tree by <see cref="GeckoTabEngine"/> instead (bevel-osad).
/// Runs <c>/usr/bin/osascript</c> as a child process rather than in-proc OSAKit: the child
/// inherits Bevel as its TCC responsible process (so Automation consent prompts and grants key to
/// Bevel, not to a script host), and a hung target app can only stall the child, which the timeout
/// then kills — never a thread of the shell.
///
/// Script output is machine-framed with ASCII RS (0x1E) between rows and US (0x1F) between fields,
/// so titles containing commas / newlines / quotes never break parsing. Failures (app gone, consent
/// denied → AppleScript error -1743, timeout) all collapse to an empty list per the contract.
/// </summary>
public sealed class MacOSTabProvider : ITabProvider
{
    internal enum Dialect { ITerm, Chromium, Safari }

    // Field separators mirrored between the generated scripts (character id 31/30) and the parser.
    private const char Us = '\u001f';
    private const char Rs = '\u001e';

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    // The bundle-id → dialect map lives in TabBrowserRegistry now (bevel-gxrq). This is a script
    // dialect only if the registry entry carries one (Gecko entries return null → served via AX).
    private static Dialect? ScriptDialectFor(string bundleId) => TabBrowserRegistry.For(bundleId)?.ScriptDialect;

    public Capabilities Capabilities { get; } = new(
        Available: true,
        TrayMode: TrayCapability.Mirrored,
        Notes: new[] { "tabs: apple-events (iTerm2, Chromium family, Safari) + ax (Zen, Firefox)" });

    public bool SupportsApp(string? bundleId) =>
        bundleId is not null && IsSafeBundleId(bundleId)
        && (ScriptDialectFor(bundleId) is not null || GeckoTabEngine.Supports(bundleId));

    public async ValueTask<IReadOnlyList<AppTab>> GetTabsAsync(string bundleId, CancellationToken ct = default)
    {
        if (!SupportsApp(bundleId)) return Array.Empty<AppTab>();

        // Positive dispatch per mechanism — no implicit else, so a future third mechanism added to
        // SupportsApp can't silently fall into the wrong engine.
        // Gecko (Zen/Firefox) has no AppleScript tab dictionary at all — that family enumerates
        // through the accessibility tree instead (bevel-osad, see GeckoTabEngine).
        if (GeckoTabEngine.Supports(bundleId))
        {
            var geckoTabs = await GeckoTabEngine.GetTabsAsync(bundleId, ct).ConfigureAwait(false);
            return await EnrichAsync(bundleId, geckoTabs, ct).ConfigureAwait(false);
        }
        if (ScriptDialectFor(bundleId) is { } dialect)
        {
            var raw = await RunOsaScriptAsync(EnumerationScript(bundleId, dialect), ct).ConfigureAwait(false);
            var tabs = Parse(raw, bundleId, hasUrl: dialect != Dialect.ITerm);
            return await EnrichAsync(bundleId, tabs, ct).ConfigureAwait(false);
        }
        return Array.Empty<AppTab>();
    }

    public async Task ActivateAsync(AppTab tab, CancellationToken ct = default)
    {
        // WindowRef/TabIndex round-trip from our own enumeration, but they cross a process boundary
        // as strings — re-validate as integers so nothing non-numeric can reach a script body.
        // Both refs are 1-based indexes (Chromium/Safari/Gecko AX windows) or positive AppleScript
        // ids (iTerm2 — verified live: `id of window` is an integer), so anything < 1 is refused too.
        if (!SupportsApp(tab.BundleId)
            || !int.TryParse(tab.WindowRef, NumberStyles.Integer, CultureInfo.InvariantCulture, out var windowRef)
            || windowRef < 1 || tab.TabIndex < 1)
            return;

        if (GeckoTabEngine.Supports(tab.BundleId))
        {
            await GeckoTabEngine.ActivateAsync(tab, windowRef, ct).ConfigureAwait(false);
            return;
        }
        if (ScriptDialectFor(tab.BundleId) is { } dialect)
            await RunOsaScriptAsync(ActivationScript(tab.BundleId, dialect, windowRef, tab.TabIndex),
                ActivationArguments(tab), ct).ConfigureAwait(false);
    }

    /// <summary>Post-enumeration enrichment (bevel-l17f): Gecko tabs first get their url by exact
    /// title match against the profile's session store (the AX tree exposes none), then every tab
    /// with a url gets its favicon from the browser's on-disk cache. File + SQLite work, so it runs
    /// off-thread; wholly best-effort — the un-enriched list is always an acceptable result, and
    /// that INCLUDES budget expiry: the caller's token both cancels the work cooperatively and,
    /// via WaitAsync, abandons a wedged enrichment so the tabs still reach the menu bare. Without
    /// this, a cold 68 MB Firefox favicon DB copy could hold the await far past the 1.5 s budget.</summary>
    private static async Task<IReadOnlyList<AppTab>> EnrichAsync(
        string bundleId, IReadOnlyList<AppTab> tabs, CancellationToken ct)
    {
        if (tabs.Count == 0 || ct.IsCancellationRequested) return tabs;
        try
        {
            return await Task.Run(() =>
            {
                var enriched = tabs;
                if (GeckoTabEngine.Supports(bundleId) && !ct.IsCancellationRequested)
                {
                    var urlByTitle = GeckoSessionStore.TitleToUrl(bundleId);
                    if (urlByTitle.Count > 0)
                    {
                        var withUrls = new AppTab[enriched.Count];
                        for (var i = 0; i < enriched.Count; i++)
                        {
                            var tab = enriched[i];
                            withUrls[i] = tab.Url is null && urlByTitle.TryGetValue(tab.Title, out var url)
                                ? tab with { Url = url }
                                : tab;
                        }
                        enriched = withUrls;
                    }
                }
                return TabFaviconStore.Enrich(bundleId, enriched, ct);
            }).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return tabs;   // over budget — bare tabs beat no tabs
        }
    }

    // ── Script generation (internal for tests) ───────────────────────────────

    /// <summary>Whether <paramref name="bundleId"/> is served by an AppleScript dialect (internal
    /// for tests: pins that the Gecko family routes to the AX engine, never to script generation).</summary>
    internal static bool HasScriptDialect(string bundleId) => ScriptDialectFor(bundleId) is not null;

    /// <summary>Bundle ids are embedded in script source, so only pass the charset LaunchServices
    /// allows in practice — anything else is refused rather than escaped.</summary>
    internal static bool IsSafeBundleId(string bundleId) =>
        bundleId.Length > 0 && bundleId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-');

    internal static string EnumerationScript(string bundleId, Dialect dialect) => dialect switch
    {
        // iTerm2: window identity is the stable AppleScript `id` (survives window re-ordering);
        // a tab's display text is its current session's name. Per-tab reads are deliberate here:
        // `current session of every tab` does NOT batch (verified live — returns nothing), and
        // terminal windows hold tens of tabs, not an Arc sidebar's hundreds.
        Dialect.ITerm => Prologue(bundleId) + $"""
            tell application id "{bundleId}"
                repeat with w in windows
                    set wid to id of w
                    set ti to 0
                    repeat with t in tabs of w
                        set ti to ti + 1
                        try
                            set end of rows to (wid as text) & us & ti & us & (name of current session of t)
                        end try
                    end repeat
                end repeat
            end tell
            """ + Epilogue,

        // Chromium dictionary (shared verbatim by Chrome, Arc, Edge, Brave, Vivaldi): windows are
        // 1-indexed by z-order; activation is `active tab index`. Index refs are only stable until
        // the user re-orders windows — fine for the menu-open→click lifetime (see AppTab docs).
        // Properties are fetched with `every tab` — ONE Apple Event per window per property. The
        // per-tab form costs a round-trip (~24 ms) per property per tab: measured 8.9 s on a
        // 182-tab Arc sidebar (Arc's one scripting window aggregates every Space's pinned + Today
        // tabs) vs 0.17 s batched. Never regress this to a per-tab read.
        Dialect.Chromium => Prologue(bundleId) + $"""
            tell application id "{bundleId}"
                repeat with wi from 1 to (count windows)
                    try
                        set ts to title of every tab of window wi
                        set ls to URL of every tab of window wi
                        repeat with ti from 1 to (count ts)
                            try
                                set end of rows to (wi as text) & us & ti & us & (item ti of ts) & us & (item ti of ls)
                            end try
                        end repeat
                    end try
                end repeat
            end tell
            """ + Epilogue,

        // Safari: same window-index model and the same batched reads, but tab text is `name` and
        // selection is `current tab`.
        Dialect.Safari => Prologue(bundleId) + $"""
            tell application id "{bundleId}"
                repeat with wi from 1 to (count windows)
                    try
                        set ts to name of every tab of window wi
                        set ls to URL of every tab of window wi
                        repeat with ti from 1 to (count ts)
                            try
                                set end of rows to (wi as text) & us & ti & us & (item ti of ts) & us & (item ti of ls)
                            end try
                        end repeat
                    end try
                end repeat
            end tell
            """ + Epilogue,

        _ => throw new ArgumentOutOfRangeException(nameof(dialect)),
    };

    /// <summary>Shared script head: separator constants, the row accumulator, and the is-running
    /// guard — merely mentioning an app object in a `tell` LAUNCHES a quit app, so the guard must
    /// run first.</summary>
    private static string Prologue(string bundleId) => $$"""
        set us to character id 31
        set rs to character id 30
        set rows to {}
        if application id "{{bundleId}}" is not running then return ""

        """;

    /// <summary>Rows are collected in a list and joined once — repeated `out & row` text concat is
    /// quadratic in AppleScript and visibly slow at Arc-sidebar tab counts (a sidebar aggregates every Space).</summary>
    private const string Epilogue = """

        set AppleScript's text item delimiters to rs
        return rows as text
        """;

    /// <summary>The wanted title and url, in the order the activation script reads them from
    /// <c>argv</c> (item 1, item 2; "" when the tab has no url). Foreign text — it travels as
    /// osascript arguments and never enters script source.</summary>
    internal static IReadOnlyList<string> ActivationArguments(AppTab tab) => new[] { tab.Title, tab.Url ?? "" };

    /// <summary>Shared AppleScript handlers for the activation scripts. <c>isMatch</c> mirrors
    /// <see cref="Parse"/>: the title and url are trimmed and a blank title falls back to the url,
    /// and the comparison is case-SENSITIVE (AppleScript's default `is` ignores case).</summary>
    private const string MatchHandlers = """
        on trimmed(s)
            if s is missing value then return ""
            set t to s as text
            set ws to {" ", tab, return, linefeed}
            repeat while (count of t) > 0 and ws contains (character 1 of t)
                if (count of t) is 1 then
                    set t to ""
                else
                    set t to text 2 thru -1 of t
                end if
            end repeat
            repeat while (count of t) > 0 and ws contains (character -1 of t)
                if (count of t) is 1 then
                    set t to ""
                else
                    set t to text 1 thru -2 of t
                end if
            end repeat
            return t
        end trimmed

        on isMatch(rawTitle, rawUrl, wantTitle, wantUrl)
            set t to my trimmed(rawTitle)
            set u to my trimmed(rawUrl)
            if t is "" then set t to u
            considering case
                if t is not wantTitle then return false
                if wantUrl is not "" and u is not wantUrl then return false
            end considering
            return true
        end isMatch

        """;

    /// <summary>Activation by IDENTITY, not position (bevel-6asm). Row indexes go stale the moment a
    /// tab closes or reorders, so the script receives the wanted title and url as argv and: if tab
    /// <paramref name="tabIndex"/> of window <paramref name="windowRef"/> still matches, selects it;
    /// else searches EVERY window (window indexes are z-order and go stale too); exactly one match
    /// is selected, zero or 2+ do nothing. `activate` runs last and only after a successful select.
    /// Returns "ok" / "none" / "gone" (informational; the caller ignores it).</summary>
    internal static string ActivationScript(string bundleId, Dialect dialect, int windowRef, int tabIndex) => dialect switch
    {
        // iTerm2 has no url; its tab text is the current session's name.
        Dialect.ITerm => $$"""
            {{MatchHandlers}}
            on run argv
                set wantTitle to item 1 of argv
                set wantUrl to item 2 of argv
                if application id "{{bundleId}}" is not running then return "gone"
                set hitW to 0
                set hitT to 0
                tell application id "{{bundleId}}"
                    try
                        if my isMatch(name of current session of tab {{tabIndex}} of window id {{windowRef}}, "", wantTitle, "") then
                            set hitW to {{windowRef}}
                            set hitT to {{tabIndex}}
                        end if
                    end try
                    if hitW is 0 then
                        set n to 0
                        repeat with w in windows
                            set wid to id of w
                            set ti to 0
                            repeat with t in tabs of w
                                set ti to ti + 1
                                try
                                    if my isMatch(name of current session of t, "", wantTitle, "") then
                                        set n to n + 1
                                        set hitW to wid
                                        set hitT to ti
                                    end if
                                end try
                            end repeat
                        end repeat
                        if n is not 1 then return "none"
                    end if
                    select tab hitT of window id hitW
                    activate
                end tell
                return "ok"
            end run
            """,

        Dialect.Chromium => $$"""
            {{MatchHandlers}}
            on run argv
                set wantTitle to item 1 of argv
                set wantUrl to item 2 of argv
                if application id "{{bundleId}}" is not running then return "gone"
                set hitW to 0
                set hitT to 0
                tell application id "{{bundleId}}"
                    try
                        if my isMatch(title of tab {{tabIndex}} of window {{windowRef}}, URL of tab {{tabIndex}} of window {{windowRef}}, wantTitle, wantUrl) then
                            set hitW to {{windowRef}}
                            set hitT to {{tabIndex}}
                        end if
                    end try
                    if hitW is 0 then
                        set n to 0
                        repeat with wi from 1 to (count of windows)
                            try
                                set ts to title of every tab of window wi
                                set ls to URL of every tab of window wi
                                repeat with ti from 1 to (count ts)
                                    if my isMatch(item ti of ts, item ti of ls, wantTitle, wantUrl) then
                                        set n to n + 1
                                        set hitW to wi
                                        set hitT to ti
                                    end if
                                end repeat
                            end try
                        end repeat
                        if n is not 1 then return "none"
                    end if
                    set active tab index of window hitW to hitT
                    set index of window hitW to 1
                    activate
                end tell
                return "ok"
            end run
            """,

        Dialect.Safari => $$"""
            {{MatchHandlers}}
            on run argv
                set wantTitle to item 1 of argv
                set wantUrl to item 2 of argv
                if application id "{{bundleId}}" is not running then return "gone"
                set hitW to 0
                set hitT to 0
                tell application id "{{bundleId}}"
                    try
                        if my isMatch(name of tab {{tabIndex}} of window {{windowRef}}, URL of tab {{tabIndex}} of window {{windowRef}}, wantTitle, wantUrl) then
                            set hitW to {{windowRef}}
                            set hitT to {{tabIndex}}
                        end if
                    end try
                    if hitW is 0 then
                        set n to 0
                        repeat with wi from 1 to (count of windows)
                            try
                                set ts to name of every tab of window wi
                                set ls to URL of every tab of window wi
                                repeat with ti from 1 to (count ts)
                                    if my isMatch(item ti of ts, item ti of ls, wantTitle, wantUrl) then
                                        set n to n + 1
                                        set hitW to wi
                                        set hitT to ti
                                    end if
                                end repeat
                            end try
                        end repeat
                        if n is not 1 then return "none"
                    end if
                    tell window hitW to set current tab to tab hitT
                    set index of window hitW to 1
                    activate
                end tell
                return "ok"
            end run
            """,

        _ => throw new ArgumentOutOfRangeException(nameof(dialect)),
    };

    // ── Output parsing (internal for tests) ──────────────────────────────────

    internal static IReadOnlyList<AppTab> Parse(string raw, string bundleId, bool hasUrl)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<AppTab>();
        var tabs = new List<AppTab>();
        foreach (var row in raw.Split(Rs, StringSplitOptions.RemoveEmptyEntries))
        {
            var f = row.Split(Us);
            if (f.Length < 3 || !int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tabIndex)) continue;
            var title = f[2].Trim();
            var url = hasUrl && f.Length >= 4 ? f[3].Trim() : null;
            // A tab with no title at all falls back to its URL (browsers mid-load), else app name-less "".
            if (title.Length == 0 && !string.IsNullOrEmpty(url)) title = url;
            tabs.Add(new AppTab(bundleId, f[0].Trim(), tabIndex, title, string.IsNullOrEmpty(url) ? null : url));
        }
        return tabs;
    }

    // ── osascript runner ─────────────────────────────────────────────────────

    private static Task<string> RunOsaScriptAsync(string script, CancellationToken ct) =>
        RunOsaScriptAsync(script, Array.Empty<string>(), ct);

    /// <summary>Runs <paramref name="script"/> with <paramref name="args"/> delivered to its
    /// <c>on run argv</c> handler. Each arg is its own <c>ArgumentList</c> entry after
    /// <c>-e script</c>, so foreign text can never be re-parsed as script source or shell.</summary>
    private static async Task<string> RunOsaScriptAsync(string script, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) return "";
        var psi = new ProcessStartInfo("/usr/bin/osascript")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,   // swallowed: -1743 consent denials etc. are non-errors here
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(script);
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return "";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            var stdout = p.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = p.StandardError.ReadToEndAsync(cts.Token);   // drain so a chatty child can't block on a full pipe
            try
            {
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                return p.ExitCode == 0 ? await stdout.ConfigureAwait(false) : "";
            }
            catch (OperationCanceledException)
            {
                return "";   // timeout / caller cancelled — child is reaped in finally
            }
            finally
            {
                // ANY exit from the post-Start region must reap the child (Dispose alone doesn't
                // terminate it, and a hung osascript can outlive us blocked on its target app) and
                // let both pipe reads settle BEFORE Dispose closes the streams under them —
                // otherwise an in-flight ReadToEndAsync faults unobserved on the disposed stream.
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch { /* cancelled / broken pipe */ }
            }
        }
        catch (Exception)
        {
            return "";   // osascript missing / spawn refused — same contract as "no tabs"
        }
    }
}
