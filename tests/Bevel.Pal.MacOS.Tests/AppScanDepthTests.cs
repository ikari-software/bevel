using Bevel.Pal.MacOS;
using Xunit;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>
/// The installed-app scan must be depth-bounded (bevel-5erm).
///
/// <para>EnumerateAppsInDirectory documented "recurse one level deep" and then recursed with no limit
/// at all. On a real machine that walked 76,348 entries to find 340 apps — one vendor folder supplying
/// most of them — and the walk runs on the core's boot seed, on every EnumerateInstalledApps RPC, and
/// again 250 ms after any filesystem event under a scan root.</para>
///
/// <para>Built against synthetic trees rather than /Applications so the assertions mean the same thing
/// on any machine and in CI, where the installed set is nothing like a developer's.</para>
/// </summary>
public sealed class AppScanDepthTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bevel-appscan-" + Guid.NewGuid().ToString("N")[..8]);

    public AppScanDepthTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void MakeApp(params string[] segments)
    {
        var dir = Path.Combine(new[] { _root }.Concat(segments).ToArray());
        Directory.CreateDirectory(dir);
    }

    private async Task<string[]> Scan()
    {
        var env = new MacOSAppEnvironment(new[] { _root });
        var apps = await env.EnumerateInstalledAppsAsync(CancellationToken.None);
        return apps.Select(a => a.DisplayName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Everything within the cap is found: the root itself, a Utilities-style folder, and one
    /// more level (which is where CrossOver-style bottles put their apps).</summary>
    [Fact]
    public async Task Finds_apps_up_to_the_cap()
    {
        MakeApp("Top.app");
        MakeApp("Utilities", "Nested.app");
        MakeApp("Vendor", "Bottle", "Deep.app");

        Assert.Equal(new[] { "Deep", "Nested", "Top" }, await Scan());
    }

    /// <summary>Beyond the cap nothing is collected. On a real machine this is what keeps Adobe's
    /// uninstallers and Illustrator's bundled sample droplets out of the Programs list.</summary>
    [Fact]
    public async Task Ignores_apps_below_the_cap()
    {
        MakeApp("Top.app");
        MakeApp("a", "b", "c", "TooDeep.app");
        MakeApp("a", "b", "c", "d", "e", "WayTooDeep.app");

        Assert.Equal(new[] { "Top" }, await Scan());
    }

    /// <summary>A bundle's own contents are never walked: an .app is recorded and not descended into,
    /// so an app that ships helper apps inside itself contributes one entry, not several.</summary>
    [Fact]
    public async Task Does_not_descend_into_bundles()
    {
        MakeApp("Host.app", "Contents", "Helpers", "Inner.app");

        Assert.Equal(new[] { "Host" }, await Scan());
    }

    /// <summary>A symlink pointing back at an ancestor must not be followed.
    ///
    /// <para>The old scan tested directories with <c>Directory.Exists</c>, which follows symlinks, and
    /// had no depth limit — so this shape could recurse until it ran out of stack or path length. The
    /// depth cap alone bounds it; skipping reparse points avoids the wasted walk entirely.</para></summary>
    [Fact]
    public async Task Does_not_follow_a_symlink_loop()
    {
        MakeApp("Real.app");
        var loop = Path.Combine(_root, "loop");
        try { Directory.CreateSymbolicLink(loop, _root); }
        catch (Exception) { return; }   // filesystem/platform refuses symlinks — nothing to assert

        var names = await Scan();

        // "Real" is reachable at depth 0 and again through the link; what must not happen is the scan
        // hanging or stacking up duplicates from repeated traversals of the cycle.
        Assert.Contains("Real", names);
        Assert.True(names.Length <= 2, $"symlink cycle was traversed repeatedly: [{string.Join(", ", names)}]");
    }
}
