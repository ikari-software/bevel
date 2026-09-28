using Bevel.Pal.MacOS;
using Xunit;

namespace Bevel.Pal.MacOS.Tests;

// Pure-logic coverage for the Gecko AX tab engine (bevel-osad). The AX walk itself is inherently
// live; Zen_live_enumeration below exercises it when the runner has both an AX grant and a running
// Zen, and no-ops cleanly otherwise (macos-window-test-permission-gate pattern).
public class GeckoTabEngineTests
{
    // ── SelectTarget: the press-safety policy ────────────────────────────────
    // Never a bare-index press: a stale menu must either hit exactly what it promised or do nothing.

    private static readonly string[] Strip = { "Mail", "Docs", "Build log", "Docs", "News" };

    [Fact]
    public void Exact_index_and_title_match_presses_that_index()
        => Assert.Equal(2, GeckoTabEngine.SelectTarget(Strip, 3, "Build log"));

    [Fact]
    public void Shifted_strip_falls_back_to_a_unique_title_match()
        // A tab closed before ours: index 4 now says "Docs", but "News" exists exactly once.
        => Assert.Equal(4, GeckoTabEngine.SelectTarget(Strip, 4, "News"));

    [Fact]
    public void Duplicate_titles_after_a_shift_refuse_to_guess()
        // Index 1 no longer matches and "Docs" appears twice — pressing either could be wrong.
        => Assert.Null(GeckoTabEngine.SelectTarget(Strip, 1, "Docs"));

    [Fact]
    public void Vanished_tab_is_a_no_op()
        => Assert.Null(GeckoTabEngine.SelectTarget(Strip, 2, "Closed page"));

    [Fact]
    public void Out_of_range_index_still_finds_a_unique_title()
        => Assert.Equal(0, GeckoTabEngine.SelectTarget(Strip, 99, "Mail"));

    [Fact]
    public void Empty_strip_is_a_no_op()
        => Assert.Null(GeckoTabEngine.SelectTarget(System.Array.Empty<string>(), 1, "Mail"));

    // A truncated walk yields a PREFIX of the strip; a title duplicated beyond the cut would look
    // unique in the prefix, so the fallback must be off — only the exact index+title match presses.

    [Fact]
    public void Truncated_strip_disables_the_unique_title_fallback()
        // Full strip: ..."News" at 5 AND at 90; prefix only shows the first — "unique" is a lie.
        => Assert.Null(GeckoTabEngine.SelectTarget(Strip, 90, "News", allowFallback: false));

    [Fact]
    public void Truncated_strip_still_presses_an_exact_index_and_title_match()
        => Assert.Equal(2, GeckoTabEngine.SelectTarget(Strip, 3, "Build log", allowFallback: false));

    // ── Routing ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("app.zen-browser.zen", true)]
    [InlineData("org.mozilla.firefox", true)]
    [InlineData("com.google.Chrome", false)]   // Chromium stays on the AppleScript dialect
    [InlineData("com.apple.finder", false)]
    public void Supports_covers_the_gecko_family_only(string bundleId, bool expected)
        => Assert.Equal(expected, GeckoTabEngine.Supports(bundleId));

    [Theory]
    [InlineData("app.zen-browser.zen")]
    [InlineData("org.mozilla.firefox")]
    public void Gecko_apps_route_to_the_ax_engine_never_to_script_generation(string bundleId)
    {
        // The provider dispatches positively: Gecko first, then the script dialects. These two
        // being disjoint proves a Gecko bundle id can never reach osascript source.
        Assert.True(GeckoTabEngine.Supports(bundleId));
        Assert.False(MacOSTabProvider.HasScriptDialect(bundleId));
        Assert.True(new MacOSTabProvider().SupportsApp(bundleId));
    }

    // ── CoreFoundation ownership balance (permission-gated) ──────────────────
    // Added with the interop consolidation (bevel-uat): this path calls CFRetain/CFRelease and had no
    // balance coverage, while MacOSAppBadgeSource had a 20x repeat for exactly this. An unbalanced
    // CFRetain leaks; an unbalanced CFRelease corrupts and typically faults on a LATER access, so one
    // pass proves nothing — twenty passes is what turns an over-release into a visible failure.
    // No-ops cleanly without an AX grant or a running Zen, like the enumeration test below.

    [Fact]
    public async System.Threading.Tasks.Task Repeated_enumeration_keeps_cf_ownership_balanced()
    {
        if (!System.OperatingSystem.IsMacOS()) return;

        for (var i = 0; i < 20; i++)
        {
            var tabs = await GeckoTabEngine.GetTabsAsync(
                "app.zen-browser.zen", System.Threading.CancellationToken.None);
            // Empty is the contract when Zen is absent or the grant is missing; the point of the loop is
            // that twenty real trips through the retain/release pairs neither throw nor fault the process.
            Assert.NotNull(tabs);
        }
    }

    // ── Live enumeration (permission-gated) ──────────────────────────────────

    [Fact]
    public async System.Threading.Tasks.Task Zen_live_enumeration_lists_titled_tabs_when_available()
    {
        if (!System.OperatingSystem.IsMacOS()) return;

        var tabs = await GeckoTabEngine.GetTabsAsync("app.zen-browser.zen", System.Threading.CancellationToken.None);

        // Zen not running, or the test host lacks the AX grant → empty by contract; nothing to assert.
        if (tabs.Count == 0) return;

        Assert.All(tabs, t =>
        {
            Assert.Equal("app.zen-browser.zen", t.BundleId);
            Assert.True(int.TryParse(t.WindowRef, out var w) && w >= 1, $"window ref {t.WindowRef}");
            Assert.True(t.TabIndex >= 1);
        });
        // Tab indices are 1..n within each window, in strip order.
        foreach (var byWindow in System.Linq.Enumerable.GroupBy(tabs, t => t.WindowRef))
            Assert.Equal(
                System.Linq.Enumerable.Range(1, System.Linq.Enumerable.Count(byWindow)),
                System.Linq.Enumerable.Select(byWindow, t => t.TabIndex));
    }
}
