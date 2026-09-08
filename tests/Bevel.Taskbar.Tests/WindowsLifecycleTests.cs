using System;
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
}
