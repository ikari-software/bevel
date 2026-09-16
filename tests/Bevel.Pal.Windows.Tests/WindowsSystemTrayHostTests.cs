using System.Runtime.InteropServices;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Pal.Windows.Tests;

/// <summary>
/// U4 own-app-fallback contract (bevel-ncfp.4, KTD-5): the Windows tray host constructs everywhere, is
/// empty in v1 (no third-party mirroring), reports its mode honestly, and hides the native tray safely.
/// Portable facts run on any OS; the native-hide fact is Windows-gated.
/// </summary>
public class WindowsSystemTrayHostTests
{
    [Fact]
    public async Task GetItemsAsync_is_empty_in_the_own_app_fallback()
    {
        var host = new WindowsSystemTrayHost();
        Assert.Empty(await host.GetItemsAsync());
    }

    [Fact]
    public void Capabilities_are_off_windows_none_or_on_windows_own_app_authoritative()
    {
        var host = new WindowsSystemTrayHost();
        var caps = host.Capabilities;

        if (OperatingSystem.IsWindows())
        {
            Assert.True(caps.Available);
            Assert.Equal(TrayCapability.Authoritative, caps.TrayMode);
            // The honest own-app note is the whole point of the spike fallback.
            Assert.Contains(caps.Notes, n => n.Contains("own-app", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(caps.Notes, n => n.Contains("defer", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            // Feature-detect contract: unavailable off its platform (Capabilities.None).
            Assert.False(caps.Available);
        }
    }

    [Fact]
    public async Task SetNativeTrayHiddenAsync_never_throws_hide_then_show()
    {
        var host = new WindowsSystemTrayHost();
        // Off Windows this is a guarded no-op; on Windows it best-effort toggles TrayNotifyWnd. Either way
        // it must not throw — a settings-apply must never crash the shell.
        await host.SetNativeTrayHiddenAsync(true);
        await host.SetNativeTrayHiddenAsync(false);
    }

    [Fact]
    public async Task ForwardClickAsync_returns_false_in_own_app_mode()
    {
        // Default interface member (own-app mode doesn't override it): call through the interface.
        ISystemTrayHost host = new WindowsSystemTrayHost();
        Assert.False(await host.ForwardClickAsync(new TrayItemId("x"), TrayButton.Left, TrayModifiers.None));
    }

    [Fact]
    public async Task On_windows_hiding_and_restoring_the_native_tray_round_trips_cleanly()
    {
        // Windows-gated runtime smoke: off Windows there is nothing to exercise, so return early rather
        // than take a test-only package dependency (SkippableFact). On Windows, hide the native
        // notification area then restore it. Best-effort by contract (no-op if TrayNotifyWnd isn't found),
        // so the only assertion is that neither call throws.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var host = new WindowsSystemTrayHost();
        await host.SetNativeTrayHiddenAsync(true);
        await host.SetNativeTrayHiddenAsync(false);
    }
}
