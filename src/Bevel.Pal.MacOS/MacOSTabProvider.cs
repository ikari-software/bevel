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

    public bool SupportsApp(string? bundleId) => IsSupported(bundleId);

    private static bool IsSupported(string? bundleId) =>
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
            var tabs = Parse(raw, bundleId, hasUrl: dialect != Dialect.ITerm, hasSessionId: dialect == Dialect.ITerm);
            return await EnrichAsync(bundleId, tabs, ct).ConfigureAwait(false);
        }
        return Array.Empty<AppTab>();
    }

    public async Task ActivateAsync(AppTab tab, CancellationToken ct = default)
    {
        if (!TryWindowRef(tab, out var windowRef)) return;

        if (GeckoTabEngine.Supports(tab.BundleId))
        {
            await GeckoTabEngine.ActivateAsync(tab, windowRef, ct).ConfigureAwait(false);
            return;
        }
        if (ActivationStartInfo(tab) is { } psi)
            await RunOsaScriptAsync(psi, ct).ConfigureAwait(false);
    }

    /// <summary>Validates the row's refs. WindowRef/TabIndex round-trip from our own enumeration, but
    /// they cross a process boundary as strings — re-validate as integers so nothing non-numeric can
    /// reach a script body. Both refs are 1-based indexes (Chromium/Safari/Gecko AX windows) or
    /// positive AppleScript ids (iTerm2 — verified live: `id of window` is an integer), so anything
    /// &lt; 1 is refused too.</summary>
    private static bool TryWindowRef(AppTab tab, out int windowRef)
    {
        windowRef = 0;
        return IsSupported(tab.BundleId)
            && int.TryParse(tab.WindowRef, NumberStyles.Integer, CultureInfo.InvariantCulture, out windowRef)
            && windowRef >= 1 && tab.TabIndex >= 1;
    }

    /// <summary>The exact osascript invocation that activates <paramref name="tab"/> (internal for
    /// tests), or null when no script should run: invalid refs, unsupported or Gecko app, or an
    /// iTerm2 row without a session id (cannot be identified — never guessed).</summary>
    internal static ProcessStartInfo? ActivationStartInfo(AppTab tab)
    {
        if (!TryWindowRef(tab, out var windowRef)) return null;
        if (GeckoTabEngine.Supports(tab.BundleId)) return null;
        if (ScriptDialectFor(tab.BundleId) is not { } dialect) return null;
        if (dialect == Dialect.ITerm && string.IsNullOrEmpty(tab.SessionId)) return null;
        return BuildOsaStartInfo(ActivationScript(tab.BundleId, dialect, windowRef, tab.TabIndex), ActivationArguments(tab));
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

    /// <summary>Raw string literals take the line endings of the SOURCE FILE (CRLF on a Windows
    /// checkout), so every generated script is normalised to "\n": the text must be identical on
    /// every platform even though it only ever runs on macOS.</summary>
    internal static string EnumerationScript(string bundleId, Dialect dialect) =>
        EnumerationScriptRaw(bundleId, dialect).ReplaceLineEndings("\n");

    private static string EnumerationScriptRaw(string bundleId, Dialect dialect) => dialect switch
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
                            set end of rows to (wid as text) & us & ti & us & (name of current session of t) & us & (unique id of current session of t)
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
                                set tt to item ti of ts
                                if tt is missing value then set tt to ""
                                set u to item ti of ls
                                if u is missing value then set u to ""
                                set end of rows to (wi as text) & us & ti & us & tt & us & u
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
                                set tt to item ti of ts
                                if tt is missing value then set tt to ""
                                set u to item ti of ls
                                if u is missing value then set u to ""
                                set end of rows to (wi as text) & us & ti & us & tt & us & u
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

    /// <summary>Marks every foreign osascript argument. osascript parses options with getopt, so a
    /// title like <c>-e&lt;payload&gt;</c> or <c>-zsh</c> would otherwise be read as an OPTION (extra
    /// script source / "illegal option"). Defence in depth: <see cref="BuildOsaStartInfo"/> also
    /// ends option parsing with <c>--</c>, and the script strips this one character (<c>unwrap</c>).</summary>
    internal const string ArgSentinel = "=";

    /// <summary>The wanted title, url and session id, sentinel-prefixed, in the order the activation
    /// script reads them from <c>argv</c> (items 1, 2, 3; just the sentinel when absent). Foreign
    /// text — it travels as osascript arguments and never enters script source.</summary>
    internal static IReadOnlyList<string> ActivationArguments(AppTab tab) =>
        new[] { ArgSentinel + tab.Title, ArgSentinel + (tab.Url ?? ""), ArgSentinel + (tab.SessionId ?? "") };

    /// <summary>The osascript command line (internal for tests): <c>-e script</c>, then — only when
    /// there are foreign args — <c>--</c> and the args, so none can be parsed as an option.</summary>
    internal static ProcessStartInfo BuildOsaStartInfo(string script, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo("/usr/bin/osascript")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,   // swallowed: -1743 consent denials etc. are non-errors here
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(script);
        if (args.Count > 0)
        {
            psi.ArgumentList.Add("--");
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        return psi;
    }

    /// <summary>Shared AppleScript handlers for the activation scripts — the in-app twin of
    /// <see cref="TabIdentity"/>: <c>trimmed</c> strips exactly <see cref="TabIdentity.TrimChars"/>,
    /// and <c>isMatch</c> is url-first (title only when the wanted url is empty), case-SENSITIVE
    /// (AppleScript's default `is` ignores case).</summary>
    private static readonly string MatchHandlers = $$"""
        on unwrap(s)
            if (count of s) < 2 then return ""
            return text 2 thru -1 of s
        end unwrap

        on trimmed(s)
            if s is missing value then return ""
            set t to s as text
            set ws to {{TabIdentity.TrimCharsAppleScriptList()}}
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
            set u to my trimmed(rawUrl)
            if wantUrl is not "" then
                considering case
                    return u is wantUrl
                end considering
            end if
            set t to my trimmed(rawTitle)
            if t is "" then set t to u
            considering case
                return t is wantTitle
            end considering
        end isMatch

        """;

    private const string ArgvHead = """
        on run argv
            set wantTitle to my unwrap(item 1 of argv)
            set wantUrl to my unwrap(item 2 of argv)
            set wantSession to my unwrap(item 3 of argv)
        """;

    /// <summary>Activation by IDENTITY, not position (bevel-6asm). Row indexes go stale the moment a
    /// tab closes or reorders, so the script receives the wanted identity as (sentinel-prefixed) argv
    /// and: if tab <paramref name="tabIndex"/> of window <paramref name="windowRef"/> still matches,
    /// selects it; else searches EVERY window (window indexes are z-order and go stale too); exactly
    /// one match is selected, zero or 2+ do nothing. Browsers match by url (title when the url is
    /// empty); iTerm2 by session unique id. `activate` runs last, only after a successful select.
    /// Returns "ok" / "none" / "gone" (informational; the caller ignores it). The decision is mirrored
    /// by <see cref="TabIdentity.Resolve"/>.</summary>
    internal static string ActivationScript(string bundleId, Dialect dialect, int windowRef, int tabIndex) =>
        ActivationScriptRaw(bundleId, dialect, windowRef, tabIndex).ReplaceLineEndings("\n");

    private static string ActivationScriptRaw(string bundleId, Dialect dialect, int windowRef, int tabIndex) => dialect switch
    {
        Dialect.ITerm => $$"""
            {{MatchHandlers}}
            {{ArgvHead}}
                if wantSession is "" then return "none"
                if application id "{{bundleId}}" is not running then return "gone"
                set hitW to 0
                set hitT to 0
                tell application id "{{bundleId}}"
                    try
                        repeat with s in (sessions of tab {{tabIndex}} of window id {{windowRef}})
                            if (unique id of s) is wantSession then
                                set hitW to {{windowRef}}
                                set hitT to {{tabIndex}}
                                exit repeat
                            end if
                        end repeat
                    end try
                    if hitW is 0 then
                        set n to 0
                        repeat with w in windows
                            set wid to id of w
                            set ti to 0
                            repeat with t in tabs of w
                                set ti to ti + 1
                                try
                                    repeat with s in (sessions of t)
                                        if (unique id of s) is wantSession then
                                            set n to n + 1
                                            set hitW to wid
                                            set hitT to ti
                                            exit repeat
                                        end if
                                    end repeat
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
            {{ArgvHead}}
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
            {{ArgvHead}}
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

    internal static IReadOnlyList<AppTab> Parse(string raw, string bundleId, bool hasUrl, bool hasSessionId = false)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<AppTab>();
        var tabs = new List<AppTab>();
        foreach (var row in raw.Split(Rs, StringSplitOptions.RemoveEmptyEntries))
        {
            var f = row.Split(Us);
            if (f.Length < 3 || !int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tabIndex)) continue;
            var title = TabIdentity.Normalize(f[2]);
            var url = hasUrl && f.Length >= 4 ? TabIdentity.Normalize(f[3]) : null;
            var sessionId = hasSessionId && f.Length >= 4 ? f[3].Trim() : null;
            // A tab with no title at all falls back to its URL (browsers mid-load), else app name-less "".
            if (title.Length == 0 && !string.IsNullOrEmpty(url)) title = url;
            tabs.Add(new AppTab(bundleId, f[0].Trim(), tabIndex, title, string.IsNullOrEmpty(url) ? null : url,
                SessionId: string.IsNullOrEmpty(sessionId) ? null : sessionId));
        }
        return tabs;
    }

    // ── osascript runner ─────────────────────────────────────────────────────

    private static Task<string> RunOsaScriptAsync(string script, CancellationToken ct) =>
        RunOsaScriptAsync(BuildOsaStartInfo(script, Array.Empty<string>()), ct);

    private static async Task<string> RunOsaScriptAsync(ProcessStartInfo psi, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) return "";

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
