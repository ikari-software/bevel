using System.Runtime.Versioning;

namespace Bevel.Pal.MacOS;

/// <summary>
/// Marks the current process as a macOS "accessory" app (the runtime equivalent of
/// <c>LSUIElement</c>): no Dock tile, absent from Cmd-Tab. That is the right identity for a
/// Windows-style shell's chrome — the taskbar and desktop are the environment, not apps, and the
/// taskbar itself is the window switcher.
///
/// It also fixes a concrete bug (bevel-nji): the Swift helper only enumerates windows owned by
/// <c>.regular</c> apps, so once the shell's chrome is <c>.accessory</c> its OWN transient popups —
/// tooltips, menus, the Start menu — stop leaking into the taskbar's foreign-window list (where they
/// were being registered as windows, shifting the bar and dismissing themselves). This is process-wide,
/// so each split role process (taskbar, desktop) sets it for itself; the all-in-one process sets it once.
/// </summary>
[SupportedOSPlatform("macos")]
public static class ShellActivation
{
    // NSApplicationActivationPolicyAccessory — a UI process with no Dock tile / menu bar.
    private const int NSApplicationActivationPolicyAccessory = 1;

    /// <summary>Transitions this process to accessory activation. Must run after AppKit has created the
    /// shared application (i.e. once Avalonia is initialized). No-op if the app object isn't up yet.</summary>
    public static void HideFromDock()
    {
        AppKitInterop.EnsureAppKitLoaded();
        var app = AppKitInterop.SendIntPtr(
            AppKitInterop.GetClass("NSApplication"), AppKitInterop.Sel("sharedApplication"));
        if (app == IntPtr.Zero) return;
        _ = AppKitInterop.SendBool_IntPtr(
            app, AppKitInterop.Sel("setActivationPolicy:"), (IntPtr)NSApplicationActivationPolicyAccessory);
    }
}
