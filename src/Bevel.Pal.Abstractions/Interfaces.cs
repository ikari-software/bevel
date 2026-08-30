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

    /// <summary>De-miniaturize (if minimized) and activate in ONE atomic operation (bevel-nxic): the
    /// taskbar's minimized→click path. Backends that own the real activation collapse this into a single
    /// helper round-trip whose order is "raise the clicked window LAST", so it reliably ends frontmost
    /// instead of a two-RPC <see cref="RestoreAsync"/>+<see cref="ActivateAsync"/> racing the
    /// de-miniaturize animation. Default: the sequential fallback, correct for in-memory/test backends.</summary>
    async Task RestoreAndActivateAsync(ForeignWindowId id, CancellationToken ct = default)
    {
        await RestoreAsync(id, ct).ConfigureAwait(false);
        await ActivateAsync(id, ct).ConfigureAwait(false);
    }
    Task RepositionAsync(ForeignWindowId id, PalRect bounds, CancellationToken ct = default);

    /// <summary>Capture a PNG thumbnail of a window's current pixels for hover previews (bevel-cust).
    /// Null when unavailable (no Screen Recording permission, window gone, or unsupported PAL).
    /// Default: unsupported — non-macOS backends and test stubs need not implement it.</summary>
    Task<byte[]?> CaptureWindowAsync(ForeignWindowId id, int maxWidth, int maxHeight, CancellationToken ct = default)
        => Task.FromResult<byte[]?>(null);

    /// <summary>Quit (<paramref name="force"/>=false → graceful terminate, may prompt to save) or
    /// force-quit (true) a whole app by bundle id — the app-level action every taskbar button offers,
    /// including windowless-running ones (bevel-ww71). Default: unsupported (non-macOS backends / stubs).</summary>
    Task TerminateAppAsync(string bundleId, bool force, CancellationToken ct = default)
        => Task.CompletedTask;

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

    /// <summary>Forwards a click on a mirrored item to the real status item so its menu/popover opens
    /// (spec §5.5). Returns false if the item is gone / couldn't be located. Default no-op for hosts
    /// that don't own a real tray source.</summary>
    Task<bool> ForwardClickAsync(TrayItemId id, TrayButton button, TrayModifiers modifiers, CancellationToken ct = default)
        => Task.FromResult(false);

    event EventHandler<TrayItem>? ItemAdded;
    event EventHandler<TrayItem>? ItemRemoved;
    event EventHandler<TrayItem>? ItemUpdated;
}

/// <summary>Which mouse button a tray click forwards (status items differentiate by button).</summary>
public enum TrayButton { Left, Right }

/// <summary>Keyboard modifiers to mirror into a forwarded tray click (status items differentiate by
/// modifier — spec §5.9). Bit values match the helper's wire encoding.</summary>
[Flags]
public enum TrayModifiers { None = 0, Shift = 1, Control = 2, Option = 4, Command = 8 }

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

    /// <summary>Whether the OS currently has the app registered to launch at login. Reflects real
    /// login-item state (not the persisted preference), so a UI toggle can show what will happen.</summary>
    ValueTask<bool> IsRunAtLoginEnabledAsync(CancellationToken ct = default);
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

    /// <summary>Raised when the set of INSTALLED apps changes (an app added to or removed from the
    /// application folders), carrying the fresh full list. UI processes reconcile the Start menu's
    /// Programs from it, so the list stays live instead of a one-shot startup snapshot.</summary>
    event EventHandler<IReadOnlyList<InstalledApp>>? InstalledAppsChanged;
}

/// <summary>Opens a filesystem path with the OS default handler — what double-clicking a document (or
/// the "Open" verb) does in a file manager. A purely local operation (no shell-core round-trip), so
/// every process implements it directly rather than proxying through the owner.</summary>
public interface IFileOpener
{
    Task OpenPathAsync(string path, CancellationToken ct = default);

    /// <summary>Show a lightweight preview of the path (macOS Quick Look) without opening it in its
    /// default app — what Space does in Finder. Local, no shell-core round-trip.</summary>
    Task PreviewAsync(string path, CancellationToken ct = default);

    // ── "Open With" + reveal (bevel-wxt) ─────────────────────────────────────
    // Default-implemented so the Fake PAL and any non-macOS backend stay valid without change.

    /// <summary>Opens <paramref name="path"/> with a SPECIFIC application (its bundle/app path),
    /// e.g. <c>open -a &lt;app&gt; &lt;path&gt;</c> — the chosen row of the "Open With" submenu.</summary>
    Task OpenWithAsync(string path, string appPath, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Reveals <paramref name="path"/> in the system file browser (Finder), selecting it —
    /// <c>open -R</c>. Falls back to opening the path if the backend can't reveal.</summary>
    Task RevealAsync(string path, CancellationToken ct = default) => OpenPathAsync(path, ct);

    /// <summary>Enumerates the applications that can open <paramref name="path"/> (LaunchServices),
    /// default handler first. Empty when the backend can't enumerate (Fake PAL, headless).</summary>
    ValueTask<IReadOnlyList<OpenWithHandler>> GetHandlersAsync(string path, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<OpenWithHandler>>(System.Array.Empty<OpenWithHandler>());
}

/// <summary>Per-app tab enumeration and activation for tab-capable apps — browsers and terminals
/// (bevel-a40b). Purely process-local (like <see cref="IFileOpener"/>): the macOS backend talks
/// Apple Events to the target app, so no shell-core round-trip and no helper involvement. The
/// first query per target app triggers the OS Automation consent prompt ("Bevel wants to control
/// iTerm2"); a denial surfaces as an empty tab list, never an error.</summary>
public interface ITabProvider
{
    Capabilities Capabilities { get; }

    /// <summary>Whether this provider speaks <paramref name="bundleId"/>'s dialect. Callers gate on
    /// this BEFORE <see cref="GetTabsAsync"/> so unsupported apps never pay a query (or a consent
    /// prompt) for a guaranteed-empty answer.</summary>
    bool SupportsApp(string? bundleId);

    /// <summary>All tabs across all of the app's windows, in window-then-tab order. Empty when the
    /// app is not running, has no tabs, or Automation consent is denied. Never throws for those —
    /// the taskbar menu treats "no tabs" and "can't ask" identically.</summary>
    ValueTask<IReadOnlyList<AppTab>> GetTabsAsync(string bundleId, CancellationToken ct = default);

    /// <summary>Focuses <paramref name="tab"/>: selects it inside its window, raises the window,
    /// activates the app. Best-effort — a tab closed since enumeration is a silent no-op.</summary>
    Task ActivateAsync(AppTab tab, CancellationToken ct = default);
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

/// <summary>An <see cref="ISystemTrayHost"/> that never surfaces items — a stand-in for roles that
/// don't own the tray source yet (e.g. the split taskbar process before the shell-core tray bridge,
/// bevel-m3.1). Reports an unavailable, mirrored tray and no-ops the reclaim call.</summary>
public sealed class EmptySystemTrayHost : ISystemTrayHost
{
    public Capabilities Capabilities { get; } =
        Capabilities.None with { Notes = new[] { "tray: no source in this role" } };

    public ValueTask<IReadOnlyList<TrayItem>> GetItemsAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<TrayItem>>(Array.Empty<TrayItem>());

    public Task SetNativeTrayHiddenAsync(bool hidden, CancellationToken ct = default) => Task.CompletedTask;

    public event EventHandler<TrayItem>? ItemAdded { add { } remove { } }
    public event EventHandler<TrayItem>? ItemRemoved { add { } remove { } }
    public event EventHandler<TrayItem>? ItemUpdated { add { } remove { } }
}
