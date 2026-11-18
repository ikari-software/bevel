using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Bevel.App;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Start-menu folder opens must take the SUPERVISED spawn path (bevel-hsvx).
///
/// <para><c>App.OpenFilerAt</c>/<c>OpenFilerSearch</c> — Start ▸ My Documents / Pictures / Music /
/// Computer / Search — called <c>Program.SpawnFiler</c> directly, which CODEMAP calls the
/// unsupervised dev fallback: a bare <c>Process.Start</c>. Only the automation path
/// (<see cref="SpawningShellSurface"/>) went through the launcher, so the commonest "open a folder"
/// click paid a full process cold start instead of claiming the pre-warmed <c>--park</c> window that
/// bevel-t48y built, and the resulting Filer was unsupervised: no crash respawn, not torn down on
/// Quit, not restarted by RestartAll — so it kept running the old binary after an update.</para>
///
/// <para>The regression is silent in both directions: a Filer still opens either way, and nothing in
/// the UI says whether the launcher served it. Hence a source-level guard rather than a behavioural
/// one — the failure mode is a future edit reaching for <c>Program.SpawnFiler</c> again because it is
/// the shorter call, exactly as the original did.</para>
/// </summary>
public sealed class StartMenuFilerSpawnTests
{
    /// <summary>The composition must offer the launcher-backed spawner, for every role. It
    /// self-degrades to the in-process spawner when unsupervised, which is why one registration is
    /// enough and why <c>OpenFilerAt</c> can depend on it unconditionally.</summary>
    [Theory]
    [InlineData(ShellRole.Taskbar)]
    [InlineData(ShellRole.Desktop)]
    [InlineData(ShellRole.Filer)]
    public void Every_role_can_resolve_the_launcher_backed_spawner(ShellRole role)
    {
        var services = new ServiceCollection();
        services.AddBevelModules(role);

        // Inspect the descriptor rather than building the provider: the surrounding graph needs a real
        // PAL, and this registration is a dependency-free factory, so invoking it directly is both
        // sufficient and isolated (same approach as AutomationHostRoutingTests).
        var descriptor = Assert.Single(services.Where(d => d.ServiceType == typeof(IFilerSpawner)));
        var spawner = descriptor.ImplementationFactory!(null!);

        Assert.IsType<LauncherFilerSpawner>(spawner);
    }

    /// <summary>App.axaml.cs must route every spawn through <see cref="IFilerSpawner"/>.
    ///
    /// <para><c>Program.SpawnFiler</c> is not forbidden outright — <see cref="ProcessFilerSpawner"/>
    /// is its one legitimate caller, and it lives in SpawningShellSurface.cs. What must not come back
    /// is App.axaml.cs calling it directly and so stepping around the launcher.</para></summary>
    [Fact]
    public void App_never_calls_the_unsupervised_spawn_directly()
    {
        var src = StripComments(Path.Combine(RepoRoot(), "src", "Bevel.App", "App.axaml.cs"));

        var direct = Regex.Matches(src, @"Program\s*\.\s*SpawnFiler\s*\(").Count;

        Assert.True(direct == 0,
            $"App.axaml.cs calls Program.SpawnFiler directly {direct}x. That is the unsupervised " +
            "dev fallback: it skips the launcher, so the Filer is never respawned after a crash, " +
            "never torn down on Quit, never restarted by RestartAll, and never claims the parked " +
            "window. Resolve IFilerSpawner and call Spawn instead (see SpawnFilerSupervised).");
    }

    /// <summary>The spawn must not run inline on the UI thread.
    ///
    /// <para><c>LauncherControl.TrySendPayload</c> ends in <c>.GetAwaiter().GetResult()</c> on a
    /// 3s-budgeted round trip. Its own internal <c>Task.Run</c> only detaches the
    /// SynchronizationContext to avoid a deadlock — it does NOT make the call non-blocking for the
    /// caller. Every caller here is a Start-menu click handler on the UI thread, so an inline
    /// <c>Spawn</c> would trade a cold start for a UI freeze of up to three seconds whenever the
    /// control socket is stale: a worse bug than the one this fixed.</para></summary>
    [Fact]
    public void The_supervised_spawn_is_dispatched_off_the_calling_thread()
    {
        var src = StripComments(Path.Combine(RepoRoot(), "src", "Bevel.App", "App.axaml.cs"));

        // Deliberately NOT a brace-matching regex: the body nests a lambda inside a try/catch and
        // contains an interpolated "{ex.Message}", so brace counting mis-pairs. A line window from the
        // declaration is cruder but cannot be fooled by either.
        var lines = src.Split('\n');
        var decl = Array.FindIndex(lines, l => l.Contains("void SpawnFilerSupervised("));

        Assert.True(decl >= 0, "SpawnFilerSupervised not found in App.axaml.cs — if it was renamed, "
                               + "update this guard rather than deleting it.");

        var window = string.Join('\n', lines.Skip(decl).Take(12));
        Assert.True(window.Contains("Task.Run"),
            "SpawnFilerSupervised must dispatch off the calling thread: LauncherControl.TrySendPayload "
            + "blocks its caller for up to 3s, and every caller is a Start-menu click on the UI thread.");
    }

    private static string StripComments(string path)
    {
        var text = File.ReadAllText(path);
        text = Regex.Replace(text, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return string.Join('\n', text.Split('\n').Select(l => Regex.Replace(l, @"//.*$", string.Empty)));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bevel.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
