using Bevel.Pal.Abstractions;
using Bevel.Pal.Windows;
using Xunit;

namespace Bevel.Pal.Windows.Tests;

/// <summary>
/// U1 bootstrap contract (bevel-ncfp.1): the Windows PAL stubs construct and report themselves
/// unavailable (feature-detect contract), queries return empty rather than throw, and the surfaces
/// that DO throw are the genuine user actions a caller should gate on Capabilities.Available first.
/// These run on any OS — the stubs hold no Win32 P/Invoke yet, so nothing is macOS/Linux-guarded here.
/// </summary>
public class WindowsPalStubTests
{
    [Fact]
    public async Task WindowManager_reports_unavailable_and_enumerates_empty()
    {
        var wm = new WindowsWindowManager();
        Assert.False(wm.Capabilities.Available);
        Assert.Empty(await wm.EnumerateAsync());
    }

    [Fact]
    public async Task AppEnvironment_running_and_installed_are_empty()
    {
        var ae = new WindowsAppEnvironment();
        Assert.Empty(await ae.GetRunningAppsAsync());
        Assert.Empty(await ae.EnumerateInstalledAppsAsync());
    }

    [Fact]
    public async Task DesktopEnvironment_has_no_monitors_and_apply_calls_no_op()
    {
        var de = new WindowsDesktopEnvironment();
        Assert.Empty(await de.GetMonitorsAsync());
        await de.ReserveWorkAreaAsync(new MonitorId("m"), DockEdge.Bottom, 40);   // ambient apply: no throw
        await de.SetWallpaperVisibleToHostAsync(true);
    }

    [Fact]
    public async Task PermissionBroker_reports_NotApplicable_for_every_permission()
    {
        var pb = new WindowsPermissionBroker();
        foreach (var p in Enum.GetValues<ShellPermission>())
            Assert.Equal(PermissionState.NotApplicable, await pb.GetStateAsync(p));
    }

    [Fact]
    public async Task ShellSession_is_not_registered_and_run_at_login_is_off()
    {
        var s = new WindowsShellSession();
        Assert.False(await s.IsRegisteredAsShellAsync());
        Assert.False(await s.IsRunAtLoginEnabledAsync());
        await s.SetRunAtLoginAsync(true);   // ambient apply: no throw in the bootstrap stub
    }

    [Fact]
    public async Task FileOperations_throw_until_the_real_IFileOperation_lands()
    {
        var fo = new WindowsFileOperations();
        await Assert.ThrowsAsync<NotImplementedException>(() => fo.CopyAsync(new[] { "a" }, "b"));
        await Assert.ThrowsAsync<NotImplementedException>(() => fo.DeleteAsync(new[] { "a" }, DeleteMode.Trash));
    }

    [Fact]
    public async Task IconProvider_returns_a_non_null_blank_so_bound_images_do_not_crash()
    {
        var ip = new WindowsIconProvider();
        var img = await ip.GetIconAsync(".txt", 16);
        Assert.NotNull(img);
        Assert.Equal(4, img.Bgra.Length);
    }

    [Fact]
    public void VolumeLabelSource_returns_null_so_callers_fall_back_to_the_mount_path()
        => Assert.Null(new WindowsVolumeLabelSource().LabelFor("C:\\"));

    [Fact]
    public void TabProvider_supports_nothing_yet()
        => Assert.False(new WindowsTabProvider().SupportsApp("com.google.Chrome"));
}
