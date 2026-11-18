using Bevel.Pal.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>
/// The seed-grade contract of the real macOS window manager and tray host (bevel-wheq / bevel-ri8c): when the
/// helper cannot be asked, <c>TrySeed*</c> must say "no answer" (null), never "nothing there" (empty) — the
/// core publishes an answered seed as an authoritative snapshot, and the taskbar closes every button / icon a
/// snapshot omits. The UI-facing <c>EnumerateAsync</c> / <c>GetItemsAsync</c> keep folding that into empty.
/// No helper needed: a disposed instance is the portable "cannot be asked" shape (runs on every OS).
/// </summary>
public sealed class SeedContractTests
{
    [Fact]
    public async Task A_disposed_window_manager_reports_no_answer_to_the_seed_and_empty_to_the_UI()
    {
        using var lifecycle = new HelperLifecycle(NullLogger<HelperLifecycle>.Instance);
        var wm = new MacOSWindowManager(lifecycle, NullLogger<MacOSWindowManager>.Instance);
        wm.Dispose();

        Assert.Null(await wm.TrySeedWindowsAsync());
        Assert.Null(await ((IWindowManager)wm).TrySeedWindowsAsync());   // the override, not the default method
        Assert.Empty(await wm.EnumerateAsync());
    }

    [Fact]
    public async Task A_disposed_tray_host_reports_no_answer_to_the_seed_and_empty_to_the_UI()
    {
        using var lifecycle = new HelperLifecycle(NullLogger<HelperLifecycle>.Instance);
        var tray = new MacOSSystemTrayHost(lifecycle, NullLogger<MacOSSystemTrayHost>.Instance);
        tray.Dispose();

        Assert.Null(await ((ISystemTrayHost)tray).TrySeedItemsAsync());
        Assert.Empty(await tray.GetItemsAsync());
    }
}
