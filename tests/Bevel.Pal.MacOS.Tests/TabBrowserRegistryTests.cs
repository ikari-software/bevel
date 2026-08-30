using System.Linq;
using Bevel.Pal.MacOS;
using Xunit;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>
/// The single browser registry (bevel-gxrq) replaced four parallel bundle-id tables. These pin its
/// internal consistency — a half-wired entry (script dialect but a nonsense config, or a Gecko
/// entry missing its data root) is exactly the drift the consolidation exists to prevent.
/// </summary>
public class TabBrowserRegistryTests
{
    [Fact]
    public void Every_entry_is_either_scriptable_or_gecko_never_both_never_neither()
    {
        foreach (var b in TabBrowserRegistry.ByBundleId.Values)
        {
            var scriptable = b.ScriptDialect is not null;
            Assert.True(scriptable ^ b.IsGecko,
                $"{b.BundleId}: must be scriptable XOR gecko (dialect={b.ScriptDialect}, gecko={b.IsGecko})");
        }
    }

    [Fact]
    public void Every_gecko_entry_has_a_favicon_slash_session_root()
    {
        foreach (var b in TabBrowserRegistry.ByBundleId.Values.Where(b => b.IsGecko))
            Assert.False(string.IsNullOrEmpty(b.FaviconRoot),
                $"{b.BundleId}: a Gecko browser needs a data root (favicon DB + session store)");
    }

    [Theory]
    [InlineData("app.zen-browser.zen", true)]
    [InlineData("org.mozilla.firefox", true)]
    [InlineData("com.google.Chrome", false)]
    [InlineData("com.apple.Safari", false)]
    [InlineData("com.unknown.app", false)]
    public void Gecko_membership_matches_the_old_hardcoded_set(string bundleId, bool isGecko)
        => Assert.Equal(isGecko, GeckoTabEngine.Supports(bundleId));

    [Theory]
    [InlineData("com.googlecode.iterm2", true)]   // ITerm dialect
    [InlineData("com.google.Chrome", true)]       // Chromium dialect
    [InlineData("com.apple.Safari", true)]        // Safari dialect
    [InlineData("app.zen-browser.zen", false)]    // gecko → no script dialect
    [InlineData("com.unknown.app", false)]
    public void Script_dialect_membership_matches_the_old_Apps_table(string bundleId, bool hasDialect)
        => Assert.Equal(hasDialect, MacOSTabProvider.HasScriptDialect(bundleId));

    [Fact]
    public void Case_insensitive_lookup()
        => Assert.NotNull(TabBrowserRegistry.For("COM.GOOGLE.CHROME"));
}
