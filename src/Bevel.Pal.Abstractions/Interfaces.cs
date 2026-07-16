namespace Bevel.Pal.Abstractions;

// Capability-oriented, async PAL interfaces (PAL-01/03). These are M0 stubs: the
// signatures name the shell's needs, not any platform mechanism. Full contracts
// (events with snapshot+delta semantics, progress/undo/conflict jobs, thumbnails)
// are fleshed out per track and pinned by Bevel.Pal.ContractTests.

/// <summary>Foreign top-level windows: enumeration and control (taskbar backend).</summary>
public interface IWindowManager
{
    Capabilities Capabilities { get; }

    ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default);
    Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default);
    Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default);
    Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default);
    Task CloseAsync(ForeignWindowId id, CancellationToken ct = default);
    Task RepositionAsync(ForeignWindowId id, PalRect bounds, CancellationToken ct = default);

    event EventHandler<ForeignWindow>? WindowOpened;
    event EventHandler<ForeignWindow>? WindowClosed;
    event EventHandler<ForeignWindow>? WindowChanged;
    event EventHandler<ForeignWindow>? ForegroundChanged;
}

/// <summary>Host-OS tray/status-item capture and mirroring (macOS pillar 3).</summary>
public interface ISystemTrayHost
{
    Capabilities Capabilities { get; }

    ValueTask<IReadOnlyList<TrayItem>> GetItemsAsync(CancellationToken ct = default);
    Task SetNativeTrayHiddenAsync(bool hidden, CancellationToken ct = default);

    event EventHandler<TrayItem>? ItemAdded;
    event EventHandler<TrayItem>? ItemRemoved;
    event EventHandler<TrayItem>? ItemUpdated;
}

/// <summary>Wallpaper, monitors and work-area reservation.</summary>
public interface IDesktopEnvironment
{
    Capabilities Capabilities { get; }

    ValueTask<IReadOnlyList<MonitorInfo>> GetMonitorsAsync(CancellationToken ct = default);
    Task ReserveWorkAreaAsync(MonitorId monitor, DockEdge edge, int thicknessPx, CancellationToken ct = default);
    Task SetWallpaperVisibleToHostAsync(bool hostWallpaperHidden, CancellationToken ct = default);

    event EventHandler? MonitorsChanged;
}

/// <summary>Being (and stopping being) the user's shell; login/session integration.</summary>
public interface IShellSession
{
    Capabilities Capabilities { get; }

    ValueTask<bool> IsRegisteredAsShellAsync(CancellationToken ct = default);
    Task RegisterAsShellAsync(CancellationToken ct = default);
    Task UnregisterAsync(CancellationToken ct = default);
    Task SetRunAtLoginAsync(bool enabled, CancellationToken ct = default);
    Task LogOutAsync(LogoutKind kind, CancellationToken ct = default);

    event EventHandler? SessionChanged;
}

/// <summary>File operations backing the file manager (copy/move/delete/rename).</summary>
public interface IFileOperations
{
    Capabilities Capabilities { get; }

    Task CopyAsync(IReadOnlyList<string> sources, string destDir, CancellationToken ct = default);
    Task MoveAsync(IReadOnlyList<string> sources, string destDir, CancellationToken ct = default);
    Task DeleteAsync(IReadOnlyList<string> paths, DeleteMode mode, CancellationToken ct = default);
    Task RenameAsync(string path, string newName, CancellationToken ct = default);
}

/// <summary>Icons for files, folders, apps and devices at multiple sizes.</summary>
public interface IIconProvider
{
    ValueTask<PalImage> GetIconAsync(string pathOrExtension, int size, CancellationToken ct = default);

    event EventHandler? IconInvalidated;
}

/// <summary>App launching and the running/installed application registries.</summary>
public interface IAppEnvironment
{
    ValueTask<IReadOnlyList<RunningApp>> GetRunningAppsAsync(CancellationToken ct = default);
    ValueTask<IReadOnlyList<InstalledApp>> EnumerateInstalledAppsAsync(CancellationToken ct = default);
    Task LaunchAsync(string appIdOrPath, CancellationToken ct = default);

    event EventHandler<RunningApp>? AppLaunched;
    event EventHandler<RunningApp>? AppTerminated;
}

/// <summary>TCC / permission brokering — macOS-heavy, no-op elsewhere.</summary>
public interface IPermissionBroker
{
    ValueTask<PermissionState> GetStateAsync(ShellPermission permission, CancellationToken ct = default);
    Task<PermissionState> RequestAsync(ShellPermission permission, CancellationToken ct = default);

    event EventHandler<ShellPermission>? PermissionChanged;
}

/// <summary>Minimal audio playback for theme sounds (R10): play a WAV, respect mute.</summary>
public interface IAudioPlayback
{
    Capabilities Capabilities { get; }
    bool Muted { get; set; }

    Task PlayAsync(string wavPath, CancellationToken ct = default);
}

/// <summary>
/// <summary>Native display labels for mounted volumes (My Computer, bevel-1cc). .NET on Unix reports
/// DriveInfo.VolumeLabel as the mount path itself, so real labels ("Macintosh HD") need a
/// platform lookup. Implementations must be cheap and synchronous — called once per volume
/// during enumeration; cache anything slow.
/// </summary>
public interface IVolumeLabelSource
{
    /// <summary>The user-facing label of the volume mounted at <paramref name="mountPath"/>,
    /// or null when the platform has none (callers fall back to the mount path).</summary>
    string? LabelFor(string mountPath);
}

/// <summary>
/// macOS Dock control. Lets the shell claim the bottom edge by auto-hiding the Dock while the
/// taskbar is visible, restoring the user's original preference when the shell quits (bevel-3kz).
/// </summary>
public interface IDockController
{
    Capabilities Capabilities { get; }

    /// <summary>
    /// Enables (<paramref name="enabled"/>=true) or disables (false) Dock auto-hide. On enable,
    /// the current user preference is captured once and restored on the matching disable — so
    /// Bevel never leaves the Dock hidden if the user had it showing.
    /// </summary>
    Task SetAutoHideAsync(bool enabled, CancellationToken ct = default);
}

/// <summary>
/// Health of a split UI process's link to the shell-core owner. The taskbar reaches window
/// management and app actions over a single UDS connection; when that connection drops, commands
/// fail and the strip goes stale until it reconnects. The taskbar shell surfaces this as a tray
/// indicator. In the all-in-one process there is no link (window management is in-process), so a
/// stub reports <see cref="IsConnected"/>=true forever.
/// </summary>
public interface IShellConnectionStatus
{
    /// <summary>True while the link to the core is live. Read it for the initial state, then track
    /// <see cref="ConnectionChanged"/>. May be read from any thread.</summary>
    bool IsConnected { get; }

    /// <summary>Raised when the link goes down or comes back up (arg: connected?). Fires on a
    /// transport/reconnect thread — marshal onto your UI dispatcher before touching bound state.</summary>
    event EventHandler<bool>? ConnectionChanged;
}

/// <summary>Stub <see cref="IShellConnectionStatus"/> for processes with no core link (all-in-one,
/// fake PAL): always connected, never raises.</summary>
public sealed class AlwaysConnectedShellStatus : IShellConnectionStatus
{
    public bool IsConnected => true;
    public event EventHandler<bool>? ConnectionChanged { add { } remove { } }
}
