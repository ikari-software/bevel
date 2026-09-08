using Bevel.Pal.Abstractions;

namespace Bevel.Pal.Windows;

// Windows PAL bootstrap stubs (bevel-ncfp.1 / U1). Same idiom as Bevel.Pal.MacOS's M0: the signatures
// name the shell's needs; every real Win32 mechanism lands per later unit (U2-U12). Feature-detecting
// callers see Capabilities.Available == false and never invoke the unimplemented surface; the genuine
// user actions (window control, file ops, launch, set-as-shell) throw loudly if called anyway, while
// ambient/idempotent apply calls (reserve work-area, run-at-login, tray-hide) no-op so a Core boot
// doesn't crash on a settings-apply pass before the real impls exist.

internal static class NotYet
{
    public const string Message =
        "Bevel.Pal.Windows is a bootstrap stub (bevel-ncfp.1). Native Win32 integration lands per unit " +
        "(U2-U12). Run with --pal=fake for the demo scaffold.";

    public static Capabilities Unavailable { get; } = Capabilities.None with
    {
        Notes = new[] { "windows-pal: not implemented (bootstrap stub)" },
    };
}

/// <summary>U3 lands EnumWindows + SetWinEventHook on a dedicated message-pump thread. Stub for now.</summary>
public sealed class WindowsWindowManager : IWindowManager
{
    public Capabilities Capabilities => NotYet.Unavailable;

    public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<ForeignWindow>>(Array.Empty<ForeignWindow>());

    public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task RepositionAsync(ForeignWindowId id, PalRect bounds, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);

    public event EventHandler<ForeignWindow>? WindowOpened;
    public event EventHandler<ForeignWindow>? WindowClosed;
    public event EventHandler<ForeignWindow>? WindowChanged;
    public event EventHandler<ForeignWindow>? ForegroundChanged;
}

/// <summary>U4 (spike) decides mirror-vs-own-app. Stub reports no source (like EmptySystemTrayHost).</summary>
public sealed class WindowsSystemTrayHost : ISystemTrayHost
{
    public Capabilities Capabilities => NotYet.Unavailable;

    public ValueTask<IReadOnlyList<TrayItem>> GetItemsAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<TrayItem>>(Array.Empty<TrayItem>());

    public Task SetNativeTrayHiddenAsync(bool hidden, CancellationToken ct = default) => Task.CompletedTask;

    public event EventHandler<TrayItem>? ItemAdded;
    public event EventHandler<TrayItem>? ItemRemoved;
    public event EventHandler<TrayItem>? ItemUpdated;
}

/// <summary>U7 lands GetMonitors + ReserveWorkArea (AppBar / SPI_SETWORKAREA). Stub for now.</summary>
public sealed class WindowsDesktopEnvironment : IDesktopEnvironment
{
    public Capabilities Capabilities => NotYet.Unavailable;

    public ValueTask<IReadOnlyList<MonitorInfo>> GetMonitorsAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<MonitorInfo>>(Array.Empty<MonitorInfo>());

    public Task ReserveWorkAreaAsync(MonitorId monitor, DockEdge edge, int thicknessPx, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetWallpaperVisibleToHostAsync(bool hostWallpaperHidden, CancellationToken ct = default) => Task.CompletedTask;

    public event EventHandler? MonitorsChanged;
}

/// <summary>U5 (spike) lands per-user HKCU Winlogon\Shell + run-at-login + ExitWindowsEx. Stub for now.</summary>
public sealed class WindowsShellSession : IShellSession
{
    public Capabilities Capabilities => NotYet.Unavailable;

    public ValueTask<bool> IsRegisteredAsShellAsync(CancellationToken ct = default) => ValueTask.FromResult(false);
    public Task RegisterAsShellAsync(CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task UnregisterAsync(CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task SetRunAtLoginAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask<bool> IsRunAtLoginEnabledAsync(CancellationToken ct = default) => ValueTask.FromResult(false);
    public Task LogOutAsync(LogoutKind kind, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);

    public event EventHandler? SessionChanged;
}

/// <summary>U8 lands IFileOperation (STA COM). Stub throws until then.</summary>
public sealed class WindowsFileOperations : IFileOperations
{
    public Capabilities Capabilities => NotYet.Unavailable;

    public Task CopyAsync(IReadOnlyList<string> sources, string destDir, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task MoveAsync(IReadOnlyList<string> sources, string destDir, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task DeleteAsync(IReadOnlyList<string> paths, DeleteMode mode, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task RenameAsync(string path, string newName, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
}

/// <summary>U8 lands SHGetFileInfo/IExtractIcon on the STA thread. Stub returns a blank 1x1 so the
/// pooled-icon pipeline and any bound Image stay non-null instead of crashing.</summary>
public sealed class WindowsIconProvider : IIconProvider
{
    private static readonly PalImage Blank = new(1, 1, new byte[4]);

    public ValueTask<PalImage> GetIconAsync(string pathOrExtension, int size, CancellationToken ct = default)
        => ValueTask.FromResult(Blank);

    public event EventHandler? IconInvalidated;
}

/// <summary>U6 lands process/window correlation + Start-menu enumeration. Stub for now.</summary>
public sealed class WindowsAppEnvironment : IAppEnvironment
{
    public ValueTask<IReadOnlyList<RunningApp>> GetRunningAppsAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<RunningApp>>(Array.Empty<RunningApp>());

    public ValueTask<IReadOnlyList<InstalledApp>> EnumerateInstalledAppsAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<InstalledApp>>(Array.Empty<InstalledApp>());

    public Task LaunchAsync(string appIdOrPath, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);

    public event EventHandler<RunningApp>? AppLaunched;
    public event EventHandler<RunningApp>? AppTerminated;
    public event EventHandler<IReadOnlyList<InstalledApp>>? InstalledAppsChanged;
}

/// <summary>U8 lands ShellExecuteEx + SHAssocEnumHandlers. Stub throws on open, no-ops preview.</summary>
public sealed class WindowsFileOpener : IFileOpener
{
    public Task OpenPathAsync(string path, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task PreviewAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Windows has no TCC — U9 reports every capability NotApplicable. Stub already does.</summary>
public sealed class WindowsPermissionBroker : IPermissionBroker
{
    public ValueTask<PermissionState> GetStateAsync(ShellPermission permission, CancellationToken ct = default)
        => ValueTask.FromResult(PermissionState.NotApplicable);

    public Task<PermissionState> RequestAsync(ShellPermission permission, CancellationToken ct = default)
        => Task.FromResult(PermissionState.NotApplicable);

    public event EventHandler<ShellPermission>? PermissionChanged;
}

/// <summary>U9 lands winmm PlaySound. Stub no-ops (theme sounds must never crash the shell).</summary>
public sealed class WindowsAudioPlayback : IAudioPlayback
{
    public Capabilities Capabilities => NotYet.Unavailable;
    public bool Muted { get; set; }

    public Task PlayAsync(string wavPath, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>The Windows "dock" analogue is hide/restore of the native Shell_TrayWnd (U7). Stub no-ops.</summary>
public sealed class WindowsDockController : IDockController
{
    public Capabilities Capabilities => NotYet.Unavailable;

    public Task SetAutoHideAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>U10 decides UIAutomation-over-browser vs defer. Stub supports nothing.</summary>
public sealed class WindowsTabProvider : ITabProvider
{
    public Capabilities Capabilities => NotYet.Unavailable;

    public bool SupportsApp(string? bundleId) => false;

    public ValueTask<IReadOnlyList<AppTab>> GetTabsAsync(string bundleId, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<AppTab>>(Array.Empty<AppTab>());

    public Task ActivateAsync(AppTab tab, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>.NET reports a Windows DriveInfo.VolumeLabel natively, so this can stay a null-returning
/// pass-through (callers fall back to the mount path) until/unless a richer label source is wanted.</summary>
public sealed class WindowsVolumeLabelSource : IVolumeLabelSource
{
    public string? LabelFor(string mountPath) => null;
}
