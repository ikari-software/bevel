using Bevel.Pal.Abstractions;
using Bevel.Pal.Windows;
using Xunit;

namespace Bevel.Pal.Windows.Tests;

/// <summary>
/// U6 (bevel-ncfp.6): the real Win32 <see cref="WindowsAppEnvironment"/>. Split into portable
/// contracts (run on every CI OS — off Windows the native paths are guarded to empty/no-op) and
/// Windows-gated runtime behaviors (<c>if (!OperatingSystem.IsWindows()) return;</c>), so nothing
/// touches COM / P/Invoke on the macOS/Linux runners.
/// </summary>
public class WindowsAppEnvironmentTests
{
    // ── Portable: off Windows everything is an empty/no-op, never a throw ─────

    [Fact]
    public async Task GetRunningApps_never_throws_and_is_empty_off_windows()
    {
        using var env = new WindowsAppEnvironment();
        var running = await env.GetRunningAppsAsync();
        Assert.NotNull(running);
        if (!OperatingSystem.IsWindows())
            Assert.Empty(running);
    }

    [Fact]
    public async Task EnumerateInstalledApps_never_throws_and_is_empty_off_windows()
    {
        using var env = new WindowsAppEnvironment();
        var installed = await env.EnumerateInstalledAppsAsync();
        Assert.NotNull(installed);
        if (!OperatingSystem.IsWindows())
            Assert.Empty(installed);
    }

    [Fact]
    public async Task EnumerateInstalledApps_is_empty_off_windows_even_with_real_lnk_files()
    {
        // Even when the (test) Start-menu root exists and holds .lnk files, the OS guard keeps the
        // whole native path dormant off Windows — no COM, no throw.
        var dir = Directory.CreateTempSubdirectory("bevel-appenv-portable");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "Thing.lnk"), "not-a-real-shortcut");
            using var env = new WindowsAppEnvironment(new[] { dir.FullName });
            var installed = await env.EnumerateInstalledAppsAsync();
            if (!OperatingSystem.IsWindows())
                Assert.Empty(installed);
        }
        finally { TryDelete(dir.FullName); }
    }

    [Fact]
    public async Task Launch_off_windows_is_a_silent_no_op()
    {
        if (OperatingSystem.IsWindows())
            return; // the launch path is exercised only where it can actually spawn a process
        using var env = new WindowsAppEnvironment();
        await env.LaunchAsync("C:\\Windows\\System32\\notepad.exe"); // guarded → CompletedTask, no throw
    }

    [Fact]
    public async Task Launch_rejects_an_empty_id()
    {
        if (!OperatingSystem.IsWindows())
            return; // the argument guard sits past the OS guard, so it only runs on Windows
        using var env = new WindowsAppEnvironment();
        await Assert.ThrowsAsync<ArgumentException>(() => env.LaunchAsync("   "));
    }

    [Fact]
    public void Construction_and_dispose_touch_no_native_resources()
    {
        // Constructing + disposing must be inert on any OS (no watcher/COM/P-Invoke in the ctor).
        var env = new WindowsAppEnvironment();
        env.Dispose();
        env.Dispose(); // idempotent
    }

    // ── Windows-gated runtime behaviors ──────────────────────────────────────

    [Fact]
    public async Task RunningApps_entries_are_well_formed_on_windows()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var env = new WindowsAppEnvironment();
        var running = await env.GetRunningAppsAsync();
        // The test host may be headless (no visible top-level windows), so an empty list is legal.
        // Whatever IS returned must be well-formed and PID-deduped.
        var pids = new HashSet<int>();
        foreach (var app in running)
        {
            Assert.False(string.IsNullOrWhiteSpace(app.AppId));
            Assert.False(string.IsNullOrWhiteSpace(app.DisplayName));
            Assert.True(app.ProcessId >= 0);
            Assert.True(pids.Add(app.ProcessId), "running apps must be deduped by PID");
        }
    }

    [Fact]
    public async Task InstalledApps_enumerates_lnk_files_and_falls_back_to_the_lnk_path_on_windows()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var dir = Directory.CreateTempSubdirectory("bevel-appenv-win");
        try
        {
            // A bogus .lnk can't be COM-resolved → AppId falls back to the .lnk path, DisplayName = stem.
            var lnk = Path.Combine(dir.FullName, "My App.lnk");
            File.WriteAllText(lnk, "not-a-real-shortcut");

            using var env = new WindowsAppEnvironment(new[] { dir.FullName });
            var installed = await env.EnumerateInstalledAppsAsync();

            var hit = Assert.Single(installed, a => a.DisplayName == "My App");
            Assert.Equal(lnk, hit.AppId);
        }
        finally { TryDelete(dir.FullName); }
    }

    [Fact]
    public async Task Resolved_shortcut_keeps_the_lnk_as_AppId_on_windows()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var dir = Directory.CreateTempSubdirectory("bevel-appenv-reallnk");
        var lnk = Path.Combine(dir.FullName, "Notepad with args.lnk");
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            var shortcut = shell.CreateShortcut(lnk);
            shortcut.TargetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
            shortcut.Arguments = "/a";
            shortcut.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            shortcut.Save();

            using var env = new WindowsAppEnvironment(new[] { dir.FullName });
            var installed = await env.EnumerateInstalledAppsAsync();
            var hit = Assert.Single(installed, a => a.DisplayName == "Notepad with args");
            Assert.Equal(lnk, hit.AppId);
            Assert.False(hit.AppId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
                "resolving to the .exe would drop Arguments/WorkingDirectory (PR #1 #11)");
        }
        finally { TryDelete(dir.FullName); }
    }

    [Fact]
    public async Task Adding_a_shortcut_raises_InstalledAppsChanged_on_windows()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var dir = Directory.CreateTempSubdirectory("bevel-appenv-watch");
        try
        {
            using var env = new WindowsAppEnvironment(new[] { dir.FullName });
            // Arm the lazy watcher via the first enumeration.
            await env.EnumerateInstalledAppsAsync();

            var fired = new TaskCompletionSource<IReadOnlyList<InstalledApp>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            env.InstalledAppsChanged += (_, apps) => fired.TrySetResult(apps);

            File.WriteAllText(Path.Combine(dir.FullName, "New App.lnk"), "x");

            var done = await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(done == fired.Task, "InstalledAppsChanged should fire after a .lnk is added");
            Assert.Contains(fired.Task.Result, a => a.DisplayName == "New App");
        }
        finally { TryDelete(dir.FullName); }
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }
}
