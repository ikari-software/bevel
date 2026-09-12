using Bevel.Core;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>
/// The enforcement behind bevel-cfg-guard. Three call sites used to resolve <c>~/.config/bevel</c> as an
/// implicit default — <c>new SettingsService()</c>, <c>new ProgramUsageStore(null)</c> and
/// <c>AutomationSocket.DefaultPath</c> — so a test that forgot to pass a temp directory wrote to the
/// developer's live settings. The guard makes that accident impossible rather than discouraged; these
/// tests exist so it cannot be quietly removed.
/// </summary>
public class BevelConfigDirTests
{
    [Fact]
    public void IsTestHost_is_true_inside_the_test_runner()
        => Assert.True(BevelConfigDir.IsTestHost(),
            "the guard is inert unless the runner is detected — if this fails, every other guard here is a no-op");

    [Fact]
    public void Path_throws_rather_than_handing_a_test_the_real_config_dir()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BevelConfigDir.Path);
        // The message has to name the fix, not just the problem: whoever trips this is mid-test-write.
        Assert.Contains("~/.config/bevel", ex.Message);
        Assert.Contains("explicit directory", ex.Message);
    }

    [Fact]
    public void Real_still_exposes_the_production_location_for_the_composition_root()
    {
        // Naming the path must stay possible (the app needs it); only RESOLVING it as a default is barred.
        Assert.EndsWith(Path.Combine(".config", "bevel"), BevelConfigDir.Real);
    }

    [Fact]
    public void SettingsService_parameterless_ctor_is_refused_in_a_test_host()
        => Assert.Throws<InvalidOperationException>(() => new SettingsService());

    [Fact]
    public void SettingsService_with_an_explicit_directory_still_works()
    {
        var dir = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), "bevel-tests", Guid.NewGuid().ToString("n"))).FullName;
        using var svc = new SettingsService(dir);
        Assert.NotNull(svc.Current);   // the seam is unaffected — only the implicit default is barred
    }
}
