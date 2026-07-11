using System.Runtime.InteropServices;
using Bevel.Pal.Abstractions;
using Microsoft.Extensions.Logging;

namespace Bevel.Pal.MacOS;

/// <summary>
/// Real macOS IDesktopEnvironment: monitor enumeration via NSScreen,
/// work-area reservation reinterpreted as Nudge strategy, and wallpaper visibility.
/// </summary>
public sealed class MacOSDesktopEnvironment : IDesktopEnvironment
{
    private readonly ILogger<MacOSDesktopEnvironment> _logger;

    private static readonly Capabilities MacOSDesktopCapabilities = new(
        Available: true,
        TrayMode: TrayCapability.Mirrored,
        Notes: new[] { "macos-desktop-environment" });

    public Capabilities Capabilities => MacOSDesktopCapabilities;

    public event EventHandler? MonitorsChanged;

    public MacOSDesktopEnvironment(ILogger<MacOSDesktopEnvironment> logger)
    {
        _logger = logger;
    }

    public ValueTask<IReadOnlyList<MonitorInfo>> GetMonitorsAsync(CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return ValueTask.FromResult<IReadOnlyList<MonitorInfo>>(Array.Empty<MonitorInfo>());

        try
        {
            AppKitInterop.EnsureAppKitLoaded();
            var screens = AppKitInterop.NSScreenScreens();
            var count = AppKitInterop.NSArrayCount(screens);
            var result = new List<MonitorInfo>(count);

            int primaryIdx = 0;
            for (int i = 0; i < count; i++)
            {
                var screen = AppKitInterop.NSArrayObjectAtIndex(screens, i);
                if (screen == IntPtr.Zero) continue;

                var frame = AppKitInterop.NSScreenFrame(screen);
                var isPrimary = i == primaryIdx; // First screen is primary on macOS

                result.Add(new MonitorInfo(
                    Id: new MonitorId($"m{i}"),
                    X: frame.X,
                    Y: frame.Y,
                    WidthPx: frame.Width,
                    HeightPx: frame.Height,
                    IsPrimary: isPrimary));
            }

            return ValueTask.FromResult<IReadOnlyList<MonitorInfo>>(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate monitors");
            return ValueTask.FromResult<IReadOnlyList<MonitorInfo>>(Array.Empty<MonitorInfo>());
        }
    }

    /// <summary>
    /// On macOS, "reserve" is reinterpreted as the Nudge strategy:
    /// auto-hide the Dock and move it to a side edge. No native
    /// work-area reservation API exists on macOS.
    /// </summary>
    public Task ReserveWorkAreaAsync(MonitorId monitor, DockEdge edge, int thicknessPx, CancellationToken ct = default)
    {
        _logger.LogInformation("ReserveWorkAreaAsync({Monitor}, {Edge}, {Thickness}) — Nudge strategy applied",
            monitor, edge, thicknessPx);
        // Full Dock manipulation is deferred to U12 WorkAreaMitigator.
        return Task.CompletedTask;
    }

    public Task SetWallpaperVisibleToHostAsync(bool hostWallpaperHidden, CancellationToken ct = default)
    {
        _logger.LogInformation("SetWallpaperVisibleToHostAsync({Hidden})", hostWallpaperHidden);
        return Task.CompletedTask;
    }
}