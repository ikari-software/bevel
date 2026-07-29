using Bevel.Pal.MacOS;
using Xunit;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>
/// Security tests for HelperProcessHost.ResolveHelperBinary (SEC1 / bevel-tyv): the helper is the
/// IPC trust anchor, so its path must be an absolute, existing binary — never an unqualified name
/// resolved off $PATH, and never an unvalidated env override in a shipped build. The resolver
/// takes an injected file-existence probe so these cases are exercised without a real filesystem.
/// </summary>
public class HelperBinaryResolutionTests
{
    // Rooted fixture paths built with Path APIs (not hardcoded '/'-strings) so the resolver's
    // Path.GetFullPath/Combine round-trips identically on every OS — POSIX '/', Windows drive + '\'.
    // These are probe fixtures for the injected existence check, never a real filesystem.
    private static readonly string AppRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "bevel-fixture-app"));
    private static readonly string BaseDir = Path.Combine(AppRoot, "Contents", "Resources");
    // Exactly what ResolveHelperBinary computes from BaseDir, so the expectation can't drift from it.
    private static readonly string BundlePath = Path.GetFullPath(Path.Combine(BaseDir, "..", "MacOS", "BevelHelper"));

    private static Func<string, bool> Exists(params string[] present)
    {
        var set = new HashSet<string>(present);
        return p => set.Contains(p);
    }

    [Fact]
    public void Falls_back_to_the_app_bundle_and_returns_an_absolute_path()
    {
        var path = HelperProcessHost.ResolveHelperBinary(
            envPath: null, BaseDir, allowEnvOverride: false, Exists(BundlePath));

        Assert.Equal(BundlePath, path);
        Assert.True(Path.IsPathRooted(path));
    }

    [Fact]
    public void Throws_instead_of_falling_back_to_PATH_when_nothing_is_found()
    {
        // The old code returned the bare name "BevelHelper" here, letting $PATH decide. Now it
        // must fail closed rather than risk exec'ing an attacker-controlled binary.
        Assert.Throws<InvalidOperationException>(() =>
            HelperProcessHost.ResolveHelperBinary(
                envPath: null, BaseDir, allowEnvOverride: false, Exists(/* nothing present */)));
    }

    [Fact]
    public void Honors_an_absolute_existing_env_override_in_dev_builds()
    {
        const string dev = "/opt/custom/BevelHelper";
        var path = HelperProcessHost.ResolveHelperBinary(
            envPath: dev, BaseDir, allowEnvOverride: true, Exists(dev, BundlePath));

        Assert.Equal(dev, path);
    }

    [Fact]
    public void Rejects_a_relative_env_override_even_when_it_exists()
    {
        // A relative path resolves against the attacker-influenced CWD — reject loudly rather
        // than honor it or silently fall through to the bundle.
        Assert.Throws<InvalidOperationException>(() =>
            HelperProcessHost.ResolveHelperBinary(
                envPath: "evil/BevelHelper", BaseDir, allowEnvOverride: true,
                Exists("evil/BevelHelper", BundlePath)));
    }

    [Fact]
    public void Rejects_an_absolute_env_override_that_does_not_exist()
    {
        Assert.Throws<InvalidOperationException>(() =>
            HelperProcessHost.ResolveHelperBinary(
                envPath: "/opt/missing/BevelHelper", BaseDir, allowEnvOverride: true,
                Exists(BundlePath)));
    }

    [Fact]
    public void Ignores_the_env_override_entirely_when_not_allowed()
    {
        // In a shipped (Release) build allowEnvOverride is false, so even a valid absolute
        // existing override is ignored and the bundle wins — a hostile env cannot redirect it.
        const string dev = "/opt/custom/BevelHelper";
        var path = HelperProcessHost.ResolveHelperBinary(
            envPath: dev, BaseDir, allowEnvOverride: false, Exists(dev, BundlePath));

        Assert.Equal(BundlePath, path);
    }

    [Fact]
    public void Finds_the_dev_build_output_by_walking_up_to_the_repo_root()
    {
        var repo = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "bevel-fixture-repo"));
        var exeDir = Path.Combine(repo, "src", "Bevel.App", "bin");
        var devDebug = Path.Combine(repo, "native", "helper-macos", ".build", "debug", "BevelHelper");

        var path = HelperProcessHost.ResolveHelperBinary(
            envPath: null, exeDir, allowEnvOverride: false,
            Exists(Path.Combine(repo, "Bevel.sln"), devDebug));

        Assert.Equal(devDebug, path);
    }
}
