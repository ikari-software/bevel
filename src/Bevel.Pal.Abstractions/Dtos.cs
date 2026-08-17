namespace Bevel.Pal.Abstractions;

// Immutable record DTOs and enums shared across every PAL surface (PAL-04).
// These deliberately model only what the M0 interface stubs reference; the full
// DTO set (thumbnails, progress, conflict responses, ...) lands with each track.

/// <summary>Opaque identifier for a foreign (non-shell) top-level window.</summary>
public readonly record struct ForeignWindowId(string Value);

/// <summary>Rectangle with integer coordinates, independent of any UI framework.</summary>
public readonly record struct PalRect(int X, int Y, int Width, int Height);

/// <summary>Opaque identifier for a host-OS tray / status item.</summary>
public readonly record struct TrayItemId(string Value);

/// <summary>Opaque identifier for a monitor.</summary>
public readonly record struct MonitorId(string Value);

/// <summary>
/// How authoritative the shell's tray is on this platform: it either owns the real
/// tray protocol (<see cref="Authoritative"/>, e.g. Windows Shell_TrayWnd, Linux SNI)
/// or it mirrors a host-owned tray it cannot replace (<see cref="Mirrored"/>, macOS).
/// </summary>
public enum TrayCapability
{
    Authoritative,
    Mirrored,
}

public enum ShellPermission
{
    Accessibility,
    ScreenRecording,
    FullDiskAccess,
    AppleEvents,
    InputMonitoring,
}

public enum PermissionState
{
    Unknown,
    Granted,
    Denied,
    NotApplicable,
}

public enum LogoutKind
{
    LogOut,
    Restart,
    Shutdown,
    Lock,
}

public enum DockEdge
{
    Left,
    Top,
    Right,
    Bottom,
}

public enum DeleteMode
{
    Trash,
    Permanent,
}

/// <summary>
/// Capability descriptor every PAL interface exposes so UI code feature-detects
/// instead of branching on <c>OperatingSystem.Is*</c> (PAL-02).
/// </summary>
public sealed record Capabilities(
    bool Available,
    TrayCapability TrayMode,
    IReadOnlyList<string> Notes,
    bool SupportsReposition = false)
{
    public static Capabilities None { get; } =
        new(Available: false, TrayMode: TrayCapability.Mirrored, Notes: Array.Empty<string>());
}

/// <summary>A foreign top-level window as seen by the taskbar window list.</summary>
public sealed record ForeignWindow(
    ForeignWindowId Id,
    string Title,
    string? AppId,
    bool IsMinimized,
    bool IsFocused,
    PalRect Bounds,
    /// <summary>App icon PNG bytes (from the owning app), or empty when unavailable.</summary>
    byte[]? IconPng = null,
    /// <summary>True for a synthetic app-presence entry (bevel-ww71): a regular app running with no
    /// window. Its <see cref="Id"/> is <c>app:&lt;bundle&gt;</c>; the taskbar renders it dim + icon-only
    /// and a click reopens the app. Suppressed by the projector when a real window for the same app
    /// exists.</summary>
    bool IsAppPresence = false,
    /// <summary>Owning app's bundle id (a stable machine key), used for app-level actions like Quit
    /// (bevel-ww71). Distinct from <see cref="AppId"/>, which is the friendly display name.</summary>
    string? BundleId = null);

/// <summary>A host-OS tray / status item (mirrored on macOS, owned elsewhere). On macOS the id is
/// <c>ownerPID:windowNumber</c>; <see cref="IconPng"/> is a live ScreenCaptureKit grab when
/// <see cref="IsLive"/>, else the owning app's icon (limited mode, spec §5.5); <see cref="Bounds"/>
/// is the real item's screen rect used for click-forwarding.</summary>
public sealed record TrayItem(
    TrayItemId Id,
    string Tooltip,
    string? OwnerBundleId = null,
    string? OwnerName = null,
    byte[]? IconPng = null,
    PalRect? Bounds = null,
    bool IsLive = false);

/// <summary>Physical monitor geometry.</summary>
public sealed record MonitorInfo(
    MonitorId Id,
    int X,
    int Y,
    int WidthPx,
    int HeightPx,
    bool IsPrimary);

/// <summary>A currently running application (taskbar registry).</summary>
public sealed record RunningApp(
    string AppId,
    string DisplayName,
    int ProcessId);

/// <summary>An installed application (start-menu enumeration). <paramref name="Subtitle"/> is an optional
/// short category label (e.g. "Developer Tools") shown as a second line under featured Start-menu entries.</summary>
public sealed record InstalledApp(
    string AppId,
    string DisplayName,
    string? IconPath,
    string? Subtitle = null);

/// <summary>An application that can open a given file — one entry per row of the "Open With" submenu
/// (bevel-wxt). Deliberately icon-free: the icon comes from <c>IIconProvider</c> keyed on
/// <see cref="AppPath"/>, the same path that renders app-bundle icons elsewhere.</summary>
public sealed record OpenWithHandler(
    string AppName,
    string AppPath,
    string? BundleId = null,
    bool IsDefault = false);

/// <summary>A decoded BGRA image handed across the PAL (icons, tray pixels).</summary>
public sealed record PalImage(
    int Width,
    int Height,
    byte[] Bgra);

/// <summary>One tab inside a tab-capable app (browser window, terminal window) as enumerated by
/// <see cref="ITabProvider"/> (bevel-a40b). <see cref="WindowRef"/> is the provider's own opaque
/// window token (an AppleScript window id or index on macOS) — it is NOT a
/// <see cref="ForeignWindowId"/>; scripting window identity and the window server's never align,
/// so tabs attach to the app, not to a specific taskbar button's window. Refs are only guaranteed
/// valid short-term (menu-open to click), not across enumerations.</summary>
/// <param name="IconPng">The tab's favicon as an encoded PNG, when the platform can source one
/// (browser on-disk favicon caches; bevel-l17f) — raster because favicons ARE raster sources,
/// like app icons. Null when the app has no favicon store or the page has no cached icon.
/// Note: as a byte[] this field gives the record REFERENCE semantics for equality/hashing —
/// don't compare AppTabs by value.</param>
public sealed record AppTab(
    string BundleId,
    string WindowRef,
    int TabIndex,
    string Title,
    string? Url = null,
    byte[]? IconPng = null);
