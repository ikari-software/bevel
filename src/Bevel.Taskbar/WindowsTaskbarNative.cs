using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace Bevel.Taskbar;

/// <summary>
/// Win32 helpers so the taskbar is a non-activating topmost tool window parked on the
/// physical bottom edge (bevel-h0sr). A normal overlapped window is clamped into
/// <c>rcWork</c>, which still insets the native taskbar even after we hide
/// <c>Shell_TrayWnd</c> — that is the "sits too high, as if the system taskbar was
/// still there" report.
/// </summary>
internal static class WindowsTaskbarNative
{
    public static void ApplyToolWindowStyles(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        var hwnd = HwndOf(window);
        if (hwnd == IntPtr.Zero) return;

        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        ex = new IntPtr(ex.ToInt64() | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, ex);
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    public static void SetAlwaysOnTop(Window window, bool onTop)
    {
        if (!OperatingSystem.IsWindows()) return;
        var hwnd = HwndOf(window);
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, onTop ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Forces the HWND to a physical-pixel rect, bypassing work-area clamping.</summary>
    public static void PlaceAt(Window window, PixelPoint topLeft, int widthPx, int heightPx)
    {
        if (!OperatingSystem.IsWindows()) return;
        var hwnd = HwndOf(window);
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_TOPMOST, topLeft.X, topLeft.Y, widthPx, heightPx,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private static IntPtr HwndOf(Window window)
    {
        var handle = ((TopLevel)window).TryGetPlatformHandle();
        return handle?.Handle ?? IntPtr.Zero;
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));
}
