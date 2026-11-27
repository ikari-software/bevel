using Bevel.App.Uninstall;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The <c>Bevel --uninstall</c> plan (ENG-040, bevel-q3l): a PURE plan over an
/// <see cref="UninstallEnvironment"/> rooted in a temp dir (never BevelConfigDir.Path — it throws in
/// a test host), plus the executor's dry-run guarantee. Order matters: stop the shell first, then
/// hand back the Dock/login item/LaunchServices/TCC, then delete what Bevel wrote.
/// </summary>
public sealed class UninstallPlanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bevel-uninstall-" + Guid.NewGuid().ToString("N")[..10]);

    public UninstallPlanTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private UninstallEnvironment Env(bool keepSettings = false, bool withMarker = true)
    {
        var config = Path.Combine(_root, "config");
        var bevelDir = Path.Combine(_root, ".bevel");
        var runtime = Path.Combine(_root, "tmp");
        var bundle = Path.Combine(_root, "Bevel.app");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(bevelDir);
        Directory.CreateDirectory(runtime);
        Directory.CreateDirectory(bundle);
        if (withMarker)
            File.WriteAllText(Path.Combine(bevelDir, "dock-claimed.json"), """{"autohide": true}""");
        return new UninstallEnvironment
        {
            ConfigDir = config,
            DockMarkerFile = Path.Combine(bevelDir, "dock-claimed.json"),
            RuntimeRoot = runtime,
            AppBundlePath = bundle,
            KeepSettings = keepSettings,
        };
    }

    [Fact]
    public void Plan_lists_every_location_bevel_writes_in_order()
    {
        var env = Env();
        // Runtime state the plan must name: the four runtime dirs + the restart/filer logs.
        foreach (var d in new[] { "bevel-core", "bevel-filers", "bevel-health", "bevel-helper" })
            Directory.CreateDirectory(Path.Combine(env.RuntimeRoot, d));
        File.WriteAllText(Path.Combine(env.RuntimeRoot, "bevel-restart.log"), "x");
        File.WriteAllText(Path.Combine(env.RuntimeRoot, "bevel-filer-123.log"), "x");

        var plan = UninstallPlan.Build(env);
        var kinds = plan.Select(a => a.Kind).ToArray();

        Assert.Equal(
            new[]
            {
                UninstallActionKind.StopShell,
                UninstallActionKind.RestoreDock,
                UninstallActionKind.DisableLoginItem,
                UninstallActionKind.UnregisterBundle,
                UninstallActionKind.ResetTcc,
                UninstallActionKind.DeletePath,   // ~/.config/bevel
                UninstallActionKind.DeletePath,   // ~/.bevel
                UninstallActionKind.DeletePath,   // bevel-core
                UninstallActionKind.DeletePath,   // bevel-filers
                UninstallActionKind.DeletePath,   // bevel-health
                UninstallActionKind.DeletePath,   // bevel-helper
                UninstallActionKind.DeletePath,   // bevel-restart.log
                UninstallActionKind.DeletePath,   // bevel-filer-*.log
                UninstallActionKind.PrintHint,
            },
            kinds);

        var targets = plan.Select(a => a.Target).ToArray();
        Assert.Contains(Path.Combine(env.ConfigDir, "shell.lock.pid"), targets);
        Assert.Contains(env.DockMarkerFile, targets);
        Assert.Contains(env.ConfigDir, targets);
        Assert.Contains(Path.Combine(env.RuntimeRoot, "bevel-core"), targets);
        Assert.Contains(Path.Combine(env.RuntimeRoot, "bevel-restart.log"), targets);
        Assert.Contains(Path.Combine(env.RuntimeRoot, "bevel-filer-*.log"), targets);
        Assert.Contains(UninstallEnvironment.BundleId, plan.Single(a => a.Kind == UninstallActionKind.ResetTcc).Target);
        Assert.Contains(UninstallEnvironment.HelperBundleId, plan.Single(a => a.Kind == UninstallActionKind.ResetTcc).Target);
    }

    [Fact]
    public void Keep_settings_omits_the_config_dir()
    {
        var env = Env(keepSettings: true);

        var plan = UninstallPlan.Build(env);

        Assert.DoesNotContain(plan, a => a.Kind == UninstallActionKind.DeletePath && a.Target == env.ConfigDir);
    }

    [Fact]
    public void No_dock_marker_means_no_dock_restore()
    {
        var env = Env(withMarker: false);

        var plan = UninstallPlan.Build(env);

        Assert.DoesNotContain(plan, a => a.Kind == UninstallActionKind.RestoreDock);
    }

    [Fact]
    public async Task Dry_run_executes_nothing()
    {
        var env = Env();
        File.WriteAllText(Path.Combine(env.ConfigDir, "settings.db"), "x");
        var plan = UninstallPlan.Build(env);

        await Uninstaller.RunAsync(plan, dryRun: true);

        Assert.True(File.Exists(Path.Combine(env.ConfigDir, "settings.db")), "dry-run must not delete");
        Assert.True(File.Exists(env.DockMarkerFile), "dry-run must not remove the marker");
    }
}
