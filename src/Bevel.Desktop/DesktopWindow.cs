using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Bevel.UI;

namespace Bevel.Desktop;

/// <summary>
/// A borderless, full-screen window positioned at the desktop level
/// (kCGDesktopWindowLevel + 1 on macOS). Hosts the wallpaper and icon grid.
/// Per 02-macos-platform.md §3: uses safe in-process AppKit calls for
/// window level and collection behaviors — no helper dependency.
/// </summary>
public sealed class DesktopWindow : BevelWindow
{
    public DesktopWindow()
    {
        Title = "Bevel Desktop";
        WindowState = WindowState.Normal;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ApplyDesktopBehaviors();
    }

    /// <summary>
    /// Positions the window at desktop level on macOS. No-op on other platforms.
    /// </summary>
    private void ApplyDesktopBehaviors()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return;

        var handle = TryGetNativeHandle();
        if (handle == IntPtr.Zero) return;

        NativeMac.SetWindowLevel(handle, NativeMac.CGDesktopWindowLevel + 1);
        NativeMac.SetCollectionBehavior(handle,
            NativeMac.NSWindowCollectionBehaviorCanJoinAllSpaces |
            NativeMac.NSWindowCollectionBehaviorStationary |
            NativeMac.NSWindowCollectionBehaviorIgnoresCycle);
    }

    private IntPtr TryGetNativeHandle()
    {
        var handle = ((TopLevel)this).TryGetPlatformHandle();
        if (handle is INativePlatformHandleSurface surface)
            // Avalonia hands back the content NSView on macOS; setLevel:/setCollectionBehavior: are
            // NSWindow methods, so resolve the owning NSWindow first — else they were silent no-ops
            // and the desktop stayed at window level 0 instead of behind everything (bevel-rj8).
            return NativeMac.ResolveWindow(surface.Handle);
        return IntPtr.Zero;
    }
}

/// <summary>
/// Minimal P/Invoke helpers for safe AppKit calls on macOS.
/// Used only for non-fragile, in-process operations per 02 spec §1.7.
/// </summary>
internal static class NativeMac
{
    public const int CGDesktopWindowLevel = -1000; // kCGDesktopWindowLevel

    // NSWindowCollectionBehavior flags
    public const int NSWindowCollectionBehaviorCanJoinAllSpaces = 1 << 0;
    public const int NSWindowCollectionBehaviorStationary = 1 << 4;
    public const int NSWindowCollectionBehaviorIgnoresCycle = 1 << 5;

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_intptr_intptr(IntPtr receiver, IntPtr selector, int arg);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_ret(IntPtr receiver, IntPtr selector);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern byte objc_msgSend_bool_sel(IntPtr receiver, IntPtr selector, IntPtr arg);

    private static IntPtr sel_setLevel = IntPtr.Zero;
    private static IntPtr sel_setCollectionBehavior = IntPtr.Zero;
    private static IntPtr sel_window = IntPtr.Zero;
    private static IntPtr sel_respondsToSelector = IntPtr.Zero;

    static NativeMac()
    {
        sel_setLevel = Selector.Get("setLevel:");
        sel_setCollectionBehavior = Selector.Get("setCollectionBehavior:");
        sel_window = Selector.Get("window");
        sel_respondsToSelector = Selector.Get("respondsToSelector:");
    }

    /// <summary>
    /// Resolves the NSWindow for a native handle. Avalonia hands back the content NSView on macOS,
    /// but setLevel:/setCollectionBehavior: are NSWindow methods. If the object already responds to
    /// setLevel: it is the window; otherwise fetch <c>[nsView window]</c>. Checking respondsToSelector:
    /// first avoids "unrecognized selector" crashes across Avalonia versions that hand back the
    /// window directly. (Mirrors Bevel.Taskbar's TaskbarNative.ResolveWindow — bevel-rj8.)
    /// </summary>
    public static IntPtr ResolveWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return IntPtr.Zero;
        if (objc_msgSend_bool_sel(handle, sel_respondsToSelector, sel_setLevel) != 0)
            return handle; // already an NSWindow
        if (objc_msgSend_bool_sel(handle, sel_respondsToSelector, sel_window) != 0)
            return objc_msgSend_ret(handle, sel_window); // NSView → its NSWindow
        return IntPtr.Zero;
    }

    public static void SetWindowLevel(IntPtr nsWindow, int level)
        => objc_msgSend_void_intptr_intptr(nsWindow, sel_setLevel, level);

    public static void SetCollectionBehavior(IntPtr nsWindow, int behavior)
        => objc_msgSend_void_intptr_intptr(nsWindow, sel_setCollectionBehavior, behavior);
}

/// <summary>
/// Thin wrapper for ObjC selector lookup.
/// </summary>
internal static class Selector
{
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr sel_registerName(string name);

    private static readonly Dictionary<string, IntPtr> _cache = new();

    public static IntPtr Get(string name)
    {
        if (!_cache.TryGetValue(name, out var ptr))
        {
            ptr = sel_registerName(name);
            _cache[name] = ptr;
        }
        return ptr;
    }
}