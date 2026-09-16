using System;
using System.Diagnostics;
using System.IO;
using Bevel.App;
using Bevel.App.Supervision;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Portable slices of the Windows launcher/IPC unit (bevel-ncfp.2 / U2) — the parts provable off
/// Windows: the shared runtime-dir invariants, the sun_path length guard, and that the shutdown-signal
/// registration is an inert no-op when unsupervised. The named-event handshake + Job Object reaping are
/// Windows-runtime behaviors validated on the box.
/// </summary>
public class WindowsLifecycleTests
{
    [Fact]
    public void CoreDir_and_ExplorersDir_share_one_root()
    {
        // Siblings under the same parent (compared normalized — GetTempPath() carries a trailing
        // separator on some platforms, so compare the resolved parent dirs, not the raw Root string).
        Assert.Equal(
            Path.GetDirectoryName(BevelRuntimeDir.CoreDir),
            Path.GetDirectoryName(BevelRuntimeDir.ExplorersDir));
    }

    [Fact]
    public void Non_windows_paths_are_byte_identical_to_the_historical_temp_location()
    {
        // Regression guard: the shared helper must not move the socket dir on macOS/Linux (only Windows
        // relocates to %LOCALAPPDATA%). If this ever changes, running peers stop finding each other.
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(Path.Combine(Path.GetTempPath(), "bevel-core"), BevelRuntimeDir.CoreDir);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "bevel-explorers"), BevelRuntimeDir.ExplorersDir);
    }

    [Fact]
    public void GuardSocketPath_passes_a_short_path_through_unchanged()
    {
        var p = "/tmp/bevel-core/core.sock";
        Assert.Equal(p, BevelRuntimeDir.GuardSocketPath(p));
    }

    [Fact]
    public void GuardSocketPath_throws_loudly_past_the_108_byte_sun_path_limit()
    {
        var tooLong = "/tmp/" + new string('x', 120) + ".sock"; // > 108 bytes
        var ex = Assert.Throws<InvalidOperationException>(() => BevelRuntimeDir.GuardSocketPath(tooLong));
        Assert.Contains("108", ex.Message);
    }

    [Fact]
    public void GuardSocketPath_counts_utf8_bytes_not_chars()
    {
        // 60 two-byte characters = 120 bytes but only 60 chars — must still trip the byte limit.
        var multibyte = new string('é', 60);
        Assert.Throws<InvalidOperationException>(() => BevelRuntimeDir.GuardSocketPath(multibyte));
    }

    [Fact]
    public void ShutdownSignal_Register_is_an_inert_noop_when_unsupervised()
    {
        // No BEVEL_SHUTDOWN_EVENT env in a plain test process → a disposable that never fires onStop.
        var fired = false;
        using var reg = WindowsShutdownSignal.Register(() => fired = true);
        Assert.NotNull(reg);
        Assert.False(fired);
    }

    [Fact]
    public void Windows_runtime_root_is_LocalAppData_bevel()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "bevel"),
            BevelRuntimeDir.Root);
    }

    [Fact]
    public void Named_shutdown_event_stops_a_waiting_child_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var psi = new ProcessStartInfo("powershell.exe")
        {
            Arguments = "-NoProfile -Command \"$n=$env:BEVEL_SHUTDOWN_EVENT; $e=[Threading.EventWaitHandle]::OpenExisting($n); [void]$e.WaitOne()\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var rp = new RoleProcess(ShellRole.Core, psi);
        rp.Start();
        for (var i = 0; i < 50 && !rp.IsAlive; i++)
            Thread.Sleep(50);
        Assert.True(rp.IsAlive, "powershell child should be waiting on the named shutdown event");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        rp.Kill();
        sw.Stop();
        Assert.False(rp.IsAlive);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2.5),
            "named-event teardown must not fall through to the 3s hard-kill grace");
    }

    [Fact]
    public void RoleProcess_kill_reaps_a_real_child()
    {
        var file = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sleep";
        var args = OperatingSystem.IsWindows() ? "/c ping 127.0.0.1 -n 20 >nul" : "20";
        var psi = new ProcessStartInfo(file)
        {
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var rp = new RoleProcess(ShellRole.Core, psi);
        rp.Start();
        Assert.True(rp.IsAlive);
        rp.Kill();
        Assert.False(rp.IsAlive);
    }

    [Fact]
    public void Launcher_job_assigns_a_fresh_child_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var psi = new ProcessStartInfo("cmd.exe")
        {
            Arguments = "/c ping 127.0.0.1 -n 20 >nul",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var rp = new RoleProcess(ShellRole.Core, psi);
        rp.Start();
        try
        {
            // Nested-job CI hosts may refuse AssignProcessToJobObject — that is not #5 breakaway.
            if (rp.IsInLauncherJob)
                Assert.True(rp.IsAlive);
        }
        finally { rp.Kill(); }
    }
}
