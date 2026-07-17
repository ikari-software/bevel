using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Bevel.Core;
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
    private int _rows;

    /// <summary>Current number of button rows (Win2000 drag-to-resize, bevel-0ml).</summary>
    public int Rows => _rows;

    /// <summary>Raised after the row count changes (drag-resize) so the view can re-flow
    /// buttons and the composition root can persist the new count.</summary>
    public event Action<int>? RowsChanged;

    /// <summary>Raised after a display reconfiguration (resolution/arrangement change) has
    /// re-anchored the bar and refreshed the work-area band, so the host can kick an immediate
    /// window re-nudge — a display change moves no window, so the mitigator's event stream would
    /// otherwise stay silent until its slow safety poll.</summary>
    public event Action? WorkAreaChanged;

    public TaskbarWindow(IDockController? dockController = null, int rows = 1)
    {
        _dockController = dockController;
        _rows = Math.Max(1, rows);
        Title = "Bevel Taskbar";
        WindowState = WindowState.Normal;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;

        // The ClassicWindow theme sets MinHeight=50; without overriding it the taskbar
        // window is clamped to 50px (leaving a gray gap above the content). Lock the window
        // to exactly the taskbar height for the current row count.
        var h = TaskbarTheme.HeightForRows(_rows);
        MinHeight = h;
        MaxHeight = h;

        // Position at the bottom of the primary display.
        PositionAtPrimaryDisplayBottom();
    }

    /// <summary>Largest row count that keeps the bar within ~40% of the display height.</summary>
    public int MaxRows
    {
        get
        {
            var primary = Screens?.Primary ?? Screens?.All?.FirstOrDefault();
            if (primary is null) return 4;
            var scale = primary.Scaling <= 0 ? 1.0 : primary.Scaling;
            var heightPts = primary.Bounds.Height / scale;
            var cap = (int)((heightPts * 0.4 - TaskbarTheme.TaskbarHeight) / TaskbarTheme.RowHeight) + 1;
            return Math.Clamp(cap, 1, 8);
        }
    }

    /// <summary>
    /// Sets the taskbar to <paramref name="rows"/> button rows (clamped to [1, <see cref="MaxRows"/>]),
    /// growing the bottom-anchored window upward, refreshing the work-area band, and raising
    /// <see cref="RowsChanged"/>. No-op if the count is unchanged.
    /// </summary>
    public void SetRows(int rows)
    {
        rows = Math.Clamp(rows, 1, MaxRows);
        if (rows == _rows) return;
        _rows = rows;
        var h = TaskbarTheme.HeightForRows(_rows);
        MinHeight = h;
        MaxHeight = h;
        PositionAtPrimaryDisplayBottom(); // re-anchors the bottom edge and sets Height
        RecomputeWorkAreaBand();
        RowsChanged?.Invoke(_rows);
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

        // A resolution/arrangement change (applicationDidChangeScreenParameters on macOS) moves
        // the bottom edge but neither Avalonia nor the OS re-anchors our borderless bar, and no
        // window emits a move event — so re-anchor the bar, refresh the band, and re-nudge here.
        if (Screens is not null)
            Screens.Changed += OnScreensChanged;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (Screens is not null)
            Screens.Changed -= OnScreensChanged;
        base.OnClosed(e);
    }

    /// <summary>Re-anchors the bar to the (possibly resized) primary display, refreshes the
    /// work-area band, and signals the host to re-nudge windows onto the new work area.</summary>
    private void OnScreensChanged(object? sender, EventArgs e)
    {
        // A shorter display may no longer fit the current row count; keep the bar within bounds.
        var previousRows = _rows;
        _rows = Math.Clamp(_rows, 1, MaxRows);
        var h = TaskbarTheme.HeightForRows(_rows);
        MinHeight = h;
        MaxHeight = h;

        PositionAtPrimaryDisplayBottom(); // re-anchor bottom edge + span the new full width
        RecomputeWorkAreaBand();          // refresh the band the mitigator reads
        // If the display forced fewer rows, the view's content height (RootGrid.Height, set only via
        // ApplyRowLayout on RowsChanged) and the persisted count would otherwise stay stale.
        if (_rows != previousRows)
            RowsChanged?.Invoke(_rows);
        WorkAreaChanged?.Invoke();         // kick an immediate re-nudge (no window moved on its own)
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
            var barH = TaskbarTheme.HeightForRows(_rows);
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
        TaskbarNative.SetCanBecomeKeyWindow(handle, false);
        TaskbarNative.SetAcceptsMouseMovedEvents(handle, true);
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
        var taskbarHeight = TaskbarTheme.HeightForRows(_rows);

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

/// <summary>Taskbar-specific theme metrics. The button height — and the row/bar heights derived from
/// it — vary by the user's Small/Normal/Large tier (bevel-m2.10.1); <see cref="Configure"/> is called
/// once at startup. Normal reproduces the Win2000 classic 24/28/30 exactly.</summary>
public static class TaskbarTheme
{
    /// <summary>Window-button height in logical px for the active tier (default Normal = 24).</summary>
    public static int ButtonHeight { get; private set; } = 24;

    /// <summary>Height per button row: the button plus its 4px (2+2) vertical margin.</summary>
    public static int RowHeight => ButtonHeight + 4;

    /// <summary>Single-row taskbar height in logical px: one row plus the 2px chrome inset.</summary>
    public static int TaskbarHeight => RowHeight + 2;

    /// <summary>Total taskbar height (logical px) for <paramref name="rows"/> button rows.</summary>
    public static int HeightForRows(int rows) => TaskbarHeight + (Math.Max(1, rows) - 1) * RowHeight;

    /// <summary>Selects the button-height tier. Call once at startup, before the taskbar window is
    /// built. Normal (default) keeps the classic 24/28/30 metrics.</summary>
    public static void Configure(TaskbarButtonSize size) => ButtonHeight = size switch
    {
        TaskbarButtonSize.Small => 18,   // → row 22, bar 24
        TaskbarButtonSize.Large => 30,   // → row 34, bar 36
        _ => 24,                          // Normal → row 28, bar 30 (Win2000 classic)
    };
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
    private static readonly IntPtr sel_setCanBecomeKeyWindow;
    private static readonly IntPtr sel_setCollectionBehavior;
    private static readonly IntPtr sel_setAcceptsMouseMovedEvents;
    private static readonly IntPtr sel_window;
    private static readonly IntPtr sel_respondsToSelector;

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_intptr_bool(IntPtr receiver, IntPtr selector, byte arg);

    static TaskbarNative()
    {
        sel_setLevel = SelectorCache.Get("setLevel:");
        sel_setCanBecomeKeyWindow = SelectorCache.Get("setCanBecomeKeyWindow:");
        sel_setCollectionBehavior = SelectorCache.Get("setCollectionBehavior:");
        sel_setAcceptsMouseMovedEvents = SelectorCache.Get("setAcceptsMouseMovedEvents:");
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

    public static void SetCanBecomeKeyWindow(IntPtr nsWindow, bool canBecomeKey)
    {
        if (objc_msgSend_bool_sel(nsWindow, sel_respondsToSelector, sel_setCanBecomeKeyWindow) != 0)
            objc_msgSend_void_intptr_bool(nsWindow, sel_setCanBecomeKeyWindow, canBecomeKey ? (byte)1 : (byte)0);
    }

    /// <summary>macOS requires this for hover/pointer-enter on borderless utility windows.</summary>
    public static void SetAcceptsMouseMovedEvents(IntPtr nsWindow, bool accepts)
    {
        if (objc_msgSend_bool_sel(nsWindow, sel_respondsToSelector, sel_setAcceptsMouseMovedEvents) != 0)
            objc_msgSend_void_intptr_bool(nsWindow, sel_setAcceptsMouseMovedEvents, accepts ? (byte)1 : (byte)0);
    }

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