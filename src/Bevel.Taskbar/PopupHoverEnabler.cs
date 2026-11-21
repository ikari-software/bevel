using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Bevel.Taskbar;

/// <summary>
/// Gives every taskbar popup window (Flyout, MenuFlyout, context menu, the tray "show hidden icons"
/// popover) the same <c>acceptsMouseMovedEvents</c> opt-in the taskbar window itself gets.
///
/// On macOS each native <see cref="PopupRoot"/> is its own borderless NSWindow. AppKit delivers
/// <c>mouseMoved:</c> only to a window that opts in or is key, and the taskbar's popups are
/// neither until clicked. Without the moves, Avalonia never raises pointer-enter on their content,
/// so <c>ToolTip.Tip</c> and <c>:pointerover</c> stay dead until a click makes the popup key
/// (the reported "had to click the tray popover before its tooltips showed").
///
/// Process-wide by design: one class handler covers every popup, present and future, so no
/// flyout can be forgotten. Overlay popups (no <see cref="PopupRoot"/>) and non-macOS hosts are
/// skipped.
/// </summary>
internal static class PopupHoverEnabler
{
    private static int _installed;

    /// <summary>Idempotent; call once a native taskbar window exists (never from headless tests).</summary>
    public static void Install()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return;
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;
        Popup.IsOpenProperty.Changed.AddClassHandler<Popup>(OnIsOpenChanged);
    }

    private static void OnIsOpenChanged(Popup popup, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;
        // Popup.Open() creates and shows the host BEFORE it sets IsOpen, so the NSWindow exists here.
        if (popup.Host is not PopupRoot root) return;
        var handle = ((TopLevel)root).TryGetPlatformHandle();
        if (handle is null) return;
        var nsWindow = TaskbarNative.ResolveWindow(handle.Handle);
        if (nsWindow != IntPtr.Zero)
            TaskbarNative.SetAcceptsMouseMovedEvents(nsWindow, true);
    }
}
