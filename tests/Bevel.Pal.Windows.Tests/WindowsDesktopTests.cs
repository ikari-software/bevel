using Bevel.Pal.Abstractions;
using Bevel.Pal.Windows;
using Xunit;

namespace Bevel.Pal.Windows.Tests;

/// <summary>
/// U7 (bevel-ncfp.7): the real <see cref="WindowsDesktopEnvironment"/> + <see cref="WindowsDockController"/>.
///
/// Two tiers of test. The <b>portable</b> facts run on every runner (macOS/Linux CI included): off
/// Windows every P/Invoke path is guarded to a no-op, so GetMonitorsAsync must be empty and every
/// apply-call must return without throwing. The <b>Windows-gated</b> facts early-return on non-Windows
/// (there is no OS-skip attribute wired into this project) and exercise the live Win32 surface when run
/// on the box — real monitor geometry, a reserve/reset round-trip, and best-effort taskbar hide/restore.
/// </summary>
public class WindowsDesktopTests
{
    // ── Portable (run everywhere) ─────────────────────────────────────────────

    [Fact]
    public async Task GetMonitors_is_empty_off_windows()
    {
        if (OperatingSystem.IsWindows())
            return; // the meaningful Windows behavior is asserted in the gated fact below
        var de = new WindowsDesktopEnvironment();
        Assert.Empty(await de.GetMonitorsAsync());
    }

    [Fact]
    public async Task Reserve_and_wallpaper_and_reset_never_throw_off_windows()
    {
        if (OperatingSystem.IsWindows())
            return;
        var de = new WindowsDesktopEnvironment();
        await de.ReserveWorkAreaAsync(new MonitorId(@"\\.\DISPLAY1"), DockEdge.Bottom, 40);
        await de.ReserveWorkAreaAsync(new MonitorId("does-not-exist"), DockEdge.Top, 32);
        await de.SetWallpaperVisibleToHostAsync(true);
        await de.SetWallpaperVisibleToHostAsync(false);
        de.ResetWorkAreasToFull();
        de.Dispose();
    }

    [Fact]
    public void Capabilities_track_the_platform()
    {
        var de = new WindowsDesktopEnvironment();
        var dock = new WindowsDockController();
        Assert.Equal(OperatingSystem.IsWindows(), de.Capabilities.Available);
        Assert.Equal(OperatingSystem.IsWindows(), dock.Capabilities.Available);
    }

    [Fact]
    public async Task Dock_setautohide_never_throws_off_windows()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var dock = new WindowsDockController();
        await dock.SetAutoHideAsync(true);
        await dock.SetAutoHideAsync(false);
        dock.Dispose(); // second dispose must be a no-op (bevel-injy)
    }

    [Fact]
    public void Subscribing_to_MonitorsChanged_off_windows_starts_no_pump()
    {
        if (OperatingSystem.IsWindows())
            return;
        var de = new WindowsDesktopEnvironment();
        EventHandler h = (_, _) => { };
        de.MonitorsChanged += h;   // lazy-start is guarded off-Windows: must be inert, not throw
        de.MonitorsChanged -= h;
        de.Dispose();
    }

    // ── Windows-gated (exercise the live Win32 surface on the box) ────────────

    [Fact]
    public async Task GetMonitors_returns_a_primary_with_positive_bounds_on_windows()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var de = new WindowsDesktopEnvironment();
        var monitors = await de.GetMonitorsAsync();
        Assert.NotEmpty(monitors);
        Assert.Single(monitors, m => m.IsPrimary);
        foreach (var m in monitors)
        {
            Assert.True(m.WidthPx > 0, "monitor width should be positive");
            Assert.True(m.HeightPx > 0, "monitor height should be positive");
            Assert.False(string.IsNullOrEmpty(m.Id.Value), "monitor id should be the device name");
        }
    }

    [Fact]
    public async Task Reserve_then_reset_restores_full_work_area_on_windows()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var de = new WindowsDesktopEnvironment();
        var primary = (await de.GetMonitorsAsync()).First(m => m.IsPrimary);
        Assert.True(WindowsDesktopEnvironment.TryGetWorkArea(primary.Id, out var original, out var full));
        try
        {
            await de.ReserveWorkAreaAsync(primary.Id, DockEdge.Bottom, 40);
            Assert.True(WindowsDesktopEnvironment.TryGetWorkArea(primary.Id, out var reserved, out var fullAfter));
            // SPI_SETWORKAREA must shrink rcWork below rcMonitor. Explorer's AppBar may claim a few
            // extra pixels on CI, so require ≥ thickness rather than an exact full-40 height.
            Assert.True(reserved.Height < fullAfter.Height,
                $"reserved height {reserved.Height} should be below full {fullAfter.Height}");
            Assert.True(fullAfter.Height - reserved.Height >= 40,
                $"expected ≥40px bottom carve, got {fullAfter.Height - reserved.Height}");
            Assert.Equal(fullAfter.Y, reserved.Y);
            Assert.Equal(fullAfter.X, reserved.X);
            Assert.Equal(fullAfter.Width, reserved.Width);
            Assert.Equal(full, fullAfter); // monitor bounds themselves must not move
        }
        finally
        {
            WindowsDesktopEnvironment.RestoreWorkArea(original);
            Assert.True(WindowsDesktopEnvironment.TryGetWorkArea(primary.Id, out var restored, out _));
            Assert.Equal(original, restored);
        }
    }

    [Fact]
    public async Task Dock_hide_restore_round_trips_the_native_taskbar_on_windows()
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (!WindowsDockController.IsNativeTaskbarVisible())
            return; // session-0 / no Explorer — nothing to hide
        using var dock = new WindowsDockController();
        await dock.SetAutoHideAsync(true);  // hide Shell_TrayWnd, capturing prior state
        Assert.False(WindowsDockController.IsNativeTaskbarVisible());
        await dock.SetAutoHideAsync(false); // restore it symmetrically
        Assert.True(WindowsDockController.IsNativeTaskbarVisible());
    }

    [Fact]
    public async Task Dock_dispose_restores_the_native_taskbar_on_windows()
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (!WindowsDockController.IsNativeTaskbarVisible())
            return;

        var dock = new WindowsDockController();
        await dock.SetAutoHideAsync(true);
        Assert.False(WindowsDockController.IsNativeTaskbarVisible());
        dock.Dispose();
        Assert.True(WindowsDockController.IsNativeTaskbarVisible());
    }
}
