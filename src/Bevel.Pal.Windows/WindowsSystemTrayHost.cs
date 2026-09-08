using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Bevel.Pal.Abstractions;

namespace Bevel.Pal.Windows;

/// <summary>
/// Windows <see cref="ISystemTrayHost"/> — U4 (bevel-ncfp.4, HIGH-RISK spike, KTD-5).
///
/// <para><b>Spike decision — own-app tray only for v1.</b> Mirroring third-party notification-area
/// icons on Windows is genuinely hard and fragile, so v1 ships the SAFE FALLBACK: Bevel surfaces only
/// its <i>own</i> status items, and reports that honestly via <see cref="Capabilities"/>. No fragile
/// cross-process <c>ReadProcessMemory</c> hack ships. The tray is <see cref="TrayCapability.Authoritative"/>
/// because Windows shells own the tray protocol (unlike macOS, which can only mirror) — but the
/// authoritative source is not wired yet, so <see cref="GetItemsAsync"/> is empty and items would arrive
/// via <see cref="ItemAdded"/>/<see cref="ItemUpdated"/>/<see cref="ItemRemoved"/> once a source pushes them.</para>
///
/// <para><b>Two mirror approaches considered and deferred (KTD-5):</b>
/// <list type="number">
/// <item><description><b>Own <c>Shell_TrayWnd</c>.</b> Register a window with class name
/// <c>Shell_TrayWnd</c> first after broadcasting/receiving <c>TaskbarCreated</c>, so apps send their
/// <c>Shell_NotifyIcon</c> (<c>NIM_ADD/MODIFY/DELETE</c>) traffic to us directly. Precondition: explorer's
/// taskbar is not running (orthogonal to set-as-shell). Clean, event-driven, but only viable in
/// shell-replacement mode.</description></item>
/// <item><description><b>Cross-process <c>TB_*</c> enumeration.</b> Walk
/// <c>Shell_TrayWnd</c>→<c>ToolbarWindow32</c> (and, on <b>Win11</b>, the
/// <c>NotifyIconOverflowWindow</c> where most icons now live — <b>poll-only</b>, no add/remove events)
/// reading the undocumented <c>TBBUTTON</c>/<c>dwData</c> layout via <c>VirtualAllocEx</c> +
/// <c>ReadProcessMemory</c> in explorer's address space. Windows-version and bitness sensitive;
/// <see cref="ForwardClickAsync"/> would need the owner HWND + callback message from that same
/// cross-process memory. Fragile — explicitly NOT shipped in v1.</description></item>
/// </list></para>
///
/// <para>Surface shape mirrors <c>MacOSSystemTrayHost</c>; only discovery differs (deferred here). All
/// P/Invoke is guarded to no-op off Windows so tests construct and exercise this on macOS/Linux CI.</para>
/// </summary>
public sealed class WindowsSystemTrayHost : ISystemTrayHost
{
    // Own-app-tray capability report (bevel-ncfp.4). Authoritative because Windows shells own the tray
    // protocol; the honest Notes flag that third-party mirroring is a deferred spike.
    private static readonly Capabilities OwnAppTray = new(
        Available: true,
        TrayMode: TrayCapability.Authoritative,
        Notes: new[]
        {
            "windows-tray: own-app tray only (v1)",
            "third-party notification-area mirroring deferred (U4 spike, KTD-5)",
        },
        SupportsReposition: false);

    /// <summary>Own-app tray on Windows; <see cref="Capabilities.None"/> off Windows (feature-detect
    /// contract — callers branch on this, never on <c>OperatingSystem.Is*</c>).</summary>
    public Capabilities Capabilities => OperatingSystem.IsWindows() ? OwnAppTray : Capabilities.None;

    // Real backing events (NOT empty add/remove): a future mirror source — either Shell_TrayWnd ownership
    // or cross-process enumeration — pushes items in through here without changing this surface. Empty for
    // v1 because no source is wired.
    public event EventHandler<TrayItem>? ItemAdded;
    public event EventHandler<TrayItem>? ItemRemoved;
    public event EventHandler<TrayItem>? ItemUpdated;

    /// <summary>Empty for v1 (own-app fallback — no third-party mirroring, and no own-app source wired
    /// yet). Kept async-shaped so a real source can back it later; off Windows also empty.</summary>
    public ValueTask<IReadOnlyList<TrayItem>> GetItemsAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<TrayItem>>(Array.Empty<TrayItem>());

    /// <summary>Raise <see cref="ItemAdded"/>/<see cref="ItemUpdated"/>/<see cref="ItemRemoved"/> from a
    /// future mirror source. Kept internal so the surface stays push-ready without exposing the mechanism;
    /// unused in v1's own-app fallback.</summary>
    internal void RaiseAdded(TrayItem item) => ItemAdded?.Invoke(this, item);
    internal void RaiseUpdated(TrayItem item) => ItemUpdated?.Invoke(this, item);
    internal void RaiseRemoved(TrayItem item) => ItemRemoved?.Invoke(this, item);

    // ForwardClickAsync: keep the interface default (returns false) — own-app mode has no real host status
    // item to forward a click to.

    /// <summary>The one genuinely-safe native action: hide/show the real notification area. Finds the
    /// tray's <c>TrayNotifyWnd</c> child of <c>Shell_TrayWnd</c> and <c>ShowWindow</c>s it. Best-effort and
    /// guarded — a no-op when the tray window isn't found or when Bevel isn't the shell (there is nothing
    /// to hide, F13). Never throws.</summary>
    public Task SetNativeTrayHiddenAsync(bool hidden, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        TryToggleNativeTray(hidden);
        return Task.CompletedTask;
    }

    // ── Win32 (user32) — private to this class, no shared Interop file ───────────────────────────────

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    [SupportedOSPlatform("windows")]
    private static void TryToggleNativeTray(bool hidden)
    {
        try
        {
            var tray = FindWindowW("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return;   // no explorer taskbar / not found — nothing to toggle
            var notify = FindWindowExW(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notify == IntPtr.Zero) return;
            ShowWindow(notify, hidden ? SW_HIDE : SW_SHOW);
        }
        catch (DllNotFoundException) { /* not Windows / user32 absent — best-effort no-op */ }
        catch (EntryPointNotFoundException) { /* defensive — best-effort no-op */ }
    }

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowExW(IntPtr hWndParent, IntPtr hWndChildAfter,
        string? lpszClass, string? lpszWindow);

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
