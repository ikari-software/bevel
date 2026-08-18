namespace Bevel.Pal.MacOS;

/// <summary>
/// The single source of truth for which browsers/terminals Bevel enumerates tabs from, and how
/// (bevel-gxrq). Previously four independent tables — MacOSTabProvider.Apps (script dialect),
/// GeckoTabEngine.BundleIds, TabFaviconStore.Apps (favicon root), GeckoSessionStore.DataRoots —
/// had to be edited in lockstep to add a browser, and a half-wired entry (in one table but not the
/// others) shipped silently. They are now filtered lookups over this one registry, so adding a
/// browser is a one-line edit here and a drift test pins cross-table consistency.
/// </summary>
internal static class TabBrowserRegistry
{
    /// <param name="ScriptDialect">The AppleScript tab dialect, or null when the app has no tab
    /// scripting dictionary (the Gecko family — served via accessibility instead).</param>
    /// <param name="IsGecko">Enumerated through <see cref="GeckoTabEngine"/> (AX), not osascript.</param>
    /// <param name="FaviconRoot">The app's data dir under ~/Library/Application Support, used by
    /// <see cref="TabFaviconStore"/> (and, for Gecko, also by <see cref="GeckoSessionStore"/> for the
    /// session store). Null when there is no readable favicon store — iTerm (none) and Safari (its
    /// cache is TCC-locked behind Full Disk Access, out of scope).</param>
    internal sealed record Browser(
        string BundleId,
        MacOSTabProvider.Dialect? ScriptDialect,
        bool IsGecko,
        string? FaviconRoot);

    internal static readonly IReadOnlyDictionary<string, Browser> ByBundleId =
        new[]
        {
            // ── Terminal (no favicon store) ──
            new Browser("com.googlecode.iterm2", MacOSTabProvider.Dialect.ITerm, false, null),

            // ── Chromium family (AppleScript + Chromium-schema favicon DB) ──
            new Browser("com.google.Chrome",           MacOSTabProvider.Dialect.Chromium, false, "Google/Chrome"),
            new Browser("com.google.Chrome.canary",    MacOSTabProvider.Dialect.Chromium, false, "Google/Chrome Canary"),
            new Browser("company.thebrowser.Browser",  MacOSTabProvider.Dialect.Chromium, false, "Arc/User Data"),   // Arc
            new Browser("com.microsoft.edgemac",       MacOSTabProvider.Dialect.Chromium, false, "Microsoft Edge"),
            new Browser("com.brave.Browser",           MacOSTabProvider.Dialect.Chromium, false, "BraveSoftware/Brave-Browser"),
            new Browser("com.brave.Browser.beta",      MacOSTabProvider.Dialect.Chromium, false, "BraveSoftware/Brave-Browser-Beta"),
            new Browser("com.vivaldi.Vivaldi",         MacOSTabProvider.Dialect.Chromium, false, "Vivaldi"),
            new Browser("org.chromium.Chromium",       MacOSTabProvider.Dialect.Chromium, false, "Chromium"),

            // ── Safari (AppleScript; favicon cache TCC-locked, so no store) ──
            new Browser("com.apple.Safari",                 MacOSTabProvider.Dialect.Safari, false, null),
            new Browser("com.apple.SafariTechnologyPreview", MacOSTabProvider.Dialect.Safari, false, null),

            // ── Gecko family (accessibility, not AppleScript; Gecko-schema favicon DB + session store) ──
            new Browser("app.zen-browser.zen",                  null, true, "zen"),
            new Browser("org.mozilla.firefox",                  null, true, "Firefox"),
            new Browser("org.mozilla.firefoxdeveloperedition",  null, true, "Firefox"),
            new Browser("org.mozilla.nightly",                  null, true, "Firefox"),
            new Browser("org.mozilla.librewolf",                null, true, "librewolf"),
        }.ToDictionary(b => b.BundleId, b => b, StringComparer.OrdinalIgnoreCase);

    internal static Browser? For(string? bundleId) =>
        bundleId is not null && ByBundleId.TryGetValue(bundleId, out var b) ? b : null;
}
