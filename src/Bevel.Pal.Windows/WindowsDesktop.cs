using Bevel.Pal.Abstractions;

namespace Bevel.Pal.Windows;

/// <summary>U7 (bevel-ncfp.7) lands GetMonitors + work-area reservation (AppBar when Explorer serves it,
/// SPI_SETWORKAREA + startup reconciliation when Bevel is the shell) + MonitorsChanged via a message-only
/// window. Note IDesktopEnvironment has NO wallpaper get/set — its surface is monitors + reserve +
/// SetWallpaperVisibleToHost. Bootstrap stub for now.</summary>
public sealed class WindowsDesktopEnvironment : IDesktopEnvironment
{
    public Capabilities Capabilities => NotYet.Unavailable;

    public ValueTask<IReadOnlyList<MonitorInfo>> GetMonitorsAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<MonitorInfo>>(Array.Empty<MonitorInfo>());

    public Task ReserveWorkAreaAsync(MonitorId monitor, DockEdge edge, int thicknessPx, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetWallpaperVisibleToHostAsync(bool hostWallpaperHidden, CancellationToken ct = default) => Task.CompletedTask;

    public event EventHandler? MonitorsChanged;
}

/// <summary>U7: the Windows "dock" analogue is hide/restore of the native Shell_TrayWnd. Bootstrap stub.</summary>
public sealed class WindowsDockController : IDockController
{
    public Capabilities Capabilities => NotYet.Unavailable;

    public Task SetAutoHideAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
}
