using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Bevel.Pal.Abstractions;
using Bevel.UI;

namespace Bevel.Taskbar;

/// <summary>
/// The taskbar window: a borderless, auto-hiding panel at the bottom of the primary
/// display. Per 02-macos-platform.md §3.1/3.2: macOS window level
/// kCGMainMenuWindowLevel-1 with canJoinAllSpaces | stationary | ignoresCycle |
/// fullScreenAuxiliary collection behavior.
/// </summary>
public sealed class TaskbarWindow : BevelWindow
{
    private readonly IDockController? _dockController;

    public TaskbarWindow(IDockController? dockController = null)
    {
        _dockController = dockController;
        Title = "Bevel Taskbar";
        WindowState = WindowState.Normal;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;

        // The ClassicWindow theme sets MinHeight=50; without overriding it the taskbar
        // window is clamped to 50px (leaving a gray gap above the 30px content). Lock the
        // window to exactly the taskbar height.
        MinHeight = TaskbarTheme.TaskbarHeight;
        MaxHeight = TaskbarTheme.TaskbarHeight;

        // Position at the bottom of the primary display.
        PositionAtPrimaryDisplayBottom();
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        StripWindowChrome();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        StripWindowChrome();
        ApplyTaskbarBehaviors();
        // Claim the bottom edge: auto-hide the Dock while the taskbar is up.
        _ = _dockController?.SetAutoHideAsync(true);

        // Publish the taskbar band for WorkAreaMitigator (bevel-m2.13). Screen geometry is
        // only known once opened; recompute on move/scale so display reconfigs are picked up.
        RecomputeWorkAreaBand();
        PositionChanged += (_, _) => RecomputeWorkAreaBand();
        ScalingChanged += (_, _) => RecomputeWorkAreaBand();
    }

    private readonly object _bandLock = new();
    private PalRect? _workAreaBand;

    /// <summary>
    /// The taskbar's occupied band in the window manager's coordinate space — top-left-origin
    /// global POINTS, matching the CGWindowList/AX bounds that <see cref="IWindowManager"/>
    /// reports. It is the bottom <see cref="TaskbarTheme.TaskbarHeight"/> strip of the primary
    /// display. Null until the window has opened and screen geometry is known.
    ///
    /// Thread-safe: the value is computed on the UI thread (screen access is UI-thread-only)
    /// and cached, so the WorkAreaMitigator's background loop can read it without touching
    /// Avalonia off-thread. Passed to the mitigator as a delegate (<c>GetWorkAreaBand</c>).
    /// </summary>
    public PalRect? GetWorkAreaBand()
    {
        lock (_bandLock) return _workAreaBand;
    }

    private void RecomputeWorkAreaBand()
    {
        PalRect? band = null;
        var primary = Screens?.Primary ?? Screens?.All?.FirstOrDefault();
        if (primary is not null)
        {
            // Screen.Bounds is device pixels; window-manager bounds are points. Divide by the
            // display scale so Overlaps() compares like with like — a no-op at scale 1.0, but
            // the difference that keeps the band correct on a Retina display (scale 2.0).
            var scale = primary.Scaling <= 0 ? 1.0 : primary.Scaling;
            var xPts = primary.Bounds.X / scale;
            var yPts = primary.Bounds.Y / scale;
            var widthPts = primary.Bounds.Width / scale;
            var heightPts = primary.Bounds.Height / scale;
            var barH = TaskbarTheme.TaskbarHeight;
            band = new PalRect(
                (int)Math.Round(xPts),
                (int)Math.Round(yPts + heightPts - barH),
                (int)Math.Round(widthPts),
                barH);
        }
        lock (_bandLock) _workAreaBand = band;
    }

    /// <summary>
    /// Flattens the ClassicWindow chrome for the taskbar. The template (Classic.Avalonia
    /// Styles/Window.axaml) wraps content in a caption (AutoAttachTitleBar) plus a
    /// DockPanel#RootLayout with a 4px all-round margin — for a control bar that must fill
    /// the strip edge-to-edge, both are removed. Without this the 4px top margin leaves a
    /// gray gap above the taskbar buttons and clips them. Parts are matched by runtime type
    /// name / template Name since the theme types are internal to that assembly.
    /// </summary>
    private void StripWindowChrome()
    {
        foreach (var child in this.GetVisualDescendants())
        {
            if (child is not Control c) continue;
            if (c.GetType().Name == "AutoAttachTitleBar")
            {
                c.IsVisible = false;
                c.Height = 0;
            }
            else if (c.Name == "RootLayout")
            {
                c.Margin = new Thickness(0);
            }
        }
    }

    /// <summary>Applies macOS-specific window level and collection behaviors.</summary>
    private void ApplyTaskbarBehaviors()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return;

        var handle = TryGetNativeHandle();
        if (handle == IntPtr.Zero) return;

        // kCGMainMenuWindowLevel = 24; taskbar sits just below the menu bar.
        TaskbarNative.SetWindowLevel(handle, TaskbarNative.CGMainMenuWindowLevel - 1);
        TaskbarNative.SetCollectionBehavior(handle,
            TaskbarNative.NSWindowCollectionBehaviorCanJoinAllSpaces |
            TaskbarNative.NSWindowCollectionBehaviorStationary |
            TaskbarNative.NSWindowCollectionBehaviorIgnoresCycle |
            TaskbarNative.NSWindowCollectionBehaviorFullScreenAuxiliary);
    }

    /// <summary>
    /// Positions the window at the bottom of the primary display, full-width,
    /// with the themed taskbar height (30 logical px).
    /// </summary>
    private void PositionAtPrimaryDisplayBottom()
    {
        // Use the primary screen from the platform abstraction.
        // On first open, the screen list is available via Screens.
        var screens = Screens;
        var primary = screens?.Primary ?? screens?.All?.FirstOrDefault();
        if (primary is null) return;

        var bounds = primary.Bounds;
        var taskbarHeight = TaskbarTheme.TaskbarHeight;

        Position = new PixelPoint(bounds.X, bounds.Y + bounds.Height - taskbarHeight);
        Width = bounds.Width;
        Height = taskbarHeight;
    }

    private IntPtr TryGetNativeHandle()
    {
        // Avalonia returns the content NSView on macOS; resolve its owning NSWindow so
        // setLevel:/setCollectionBehavior: land on the right object. The previous
        // INativePlatformHandleSurface cast failed (the macOS window handle doesn't
        // implement it), returned Zero, and left the taskbar at window level 0 — behind
        // ordinary windows and their drop shadows.
        var handle = ((TopLevel)this).TryGetPlatformHandle();
        return handle is null ? IntPtr.Zero : TaskbarNative.ResolveWindow(handle.Handle);
    }
}

/// <summary>Taskbar-specific theme constants.</summary>
internal static class TaskbarTheme
{
    /// <summary>Taskbar height in logical pixels, matching Win2000 classic.</summary>
    public const int TaskbarHeight = 30;
}

/// <summary>
/// Minimal P/Invoke helpers for taskbar-specific AppKit calls on macOS.
/// Mirrors Bevel.Desktop.NativeMac; U4/U5 centralize these into a shared
/// AppKitInterop.cs once both are stable.
/// </summary>
internal static class TaskbarNative
{
    public const int CGMainMenuWindowLevel = 24; // kCGMainMenuWindowLevel

    // NSWindowCollectionBehavior flags
    public const int NSWindowCollectionBehaviorCanJoinAllSpaces = 1 << 0;
    public const int NSWindowCollectionBehaviorStationary = 1 << 4;
    public const int NSWindowCollectionBehaviorIgnoresCycle = 1 << 5;
    public const int NSWindowCollectionBehaviorFullScreenAuxiliary = 1 << 17;

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_intptr_intptr(IntPtr receiver, IntPtr selector, int arg);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_ret(IntPtr receiver, IntPtr selector);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern byte objc_msgSend_bool_sel(IntPtr receiver, IntPtr selector, IntPtr arg);

    private static readonly IntPtr sel_setLevel;
    private static readonly IntPtr sel_setCollectionBehavior;
    private static readonly IntPtr sel_window;
    private static readonly IntPtr sel_respondsToSelector;

    static TaskbarNative()
    {
        sel_setLevel = SelectorCache.Get("setLevel:");
        sel_setCollectionBehavior = SelectorCache.Get("setCollectionBehavior:");
        sel_window = SelectorCache.Get("window");
        sel_respondsToSelector = SelectorCache.Get("respondsToSelector:");
    }

    /// <summary>
    /// Resolves the NSWindow for a native handle. Avalonia hands back the content NSView on
    /// macOS, but setLevel:/setCollectionBehavior: are NSWindow methods. If the object already
    /// responds to setLevel: it is the window; otherwise fetch <c>[nsView window]</c>. Checking
    /// respondsToSelector: first avoids "unrecognized selector" crashes across Avalonia versions
    /// that might hand back the window directly.
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
/// Thin wrapper for ObjC selector lookup, shared across taskbar native helpers.
/// </summary>
internal static class SelectorCache
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