using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Classic.Avalonia.Theme;
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

        // The taskbar is a chromeless special window: the Luna framed-window frame
        // (Bevel.Metric.WindowContentBorder — a blue border on the sides+bottom below the caption) must NOT
        // wrap it. Zero it in the window's own resource scope so the ClassicWindow template resolves 0 here
        // while framed dialogs still get their frame.
        Resources["Bevel.Metric.WindowContentBorder"] = new Avalonia.Thickness(0);

        // Let the background-opacity setting actually show through (bevel-cust.appearance): a transparent
        // window means RootGrid's translucent background composites over the desktop instead of an opaque
        // window fill (the root cause of "opacity did nothing"). At 100% opacity RootGrid is fully opaque,
        // so the default look is unchanged.
        Background = Avalonia.Media.Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;

        // The inherited ClassicWindow template paints an OPAQUE ControlBrushKey (button-face grey) fill
        // behind the content (Classic's Window.axaml), independent of Window.Background — so a translucent
        // RootGrid composited over THAT, revealing grey, not the desktop (the "opacity fades back to the
        // button-face colour" report). The taskbar is chromeless and draws its own band, so give it a
        // minimal template: a transparent content host plus only the classic raised top bevel. Now the
        // RootGrid tint composites straight over the transparent window and the desktop shows through.
        // No ClassicBorderDecorator here: an AltRaised decorator draws a 4-sided win2k raised frame,
        // which now that the fill is translucent reads as a window border around the bar. The taskbar's
        // top edge is a theme concern (TaskbarView draws it via a token), not a window frame.
        Template = new FuncControlTemplate<TaskbarWindow>((parent, scope) =>
        {
            var presenter = new ContentPresenter
            {
                Name = "PART_ContentPresenter",
                [!ContentPresenter.ContentProperty] = parent[!ContentControl.ContentProperty],
                [!ContentPresenter.ContentTemplateProperty] = parent[!ContentControl.ContentTemplateProperty],
            };
            presenter.RegisterInNameScope(scope);
            // Wrap in a VisualLayerManager so the window still provides the overlay/adorner layers that
            // popups, tooltips and the window-preview flyout need (the stock ClassicWindow template had
            // one; a bare ContentPresenter does not → "no overlay layer" and dead tooltips).
            return new VisualLayerManager { Child = presenter };
        });

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
        var clamped = Math.Clamp(rows, 1, MaxRows);
        TaskbarLog.Debug($"SetRows req={rows} clamped={clamped} MaxRows={MaxRows} cur={_rows}");
        rows = clamped;
        if (rows == _rows) return;
        _rows = rows;
        var h = TaskbarTheme.HeightForRows(_rows);
        MinHeight = h;
        MaxHeight = h;
        PositionAtPrimaryDisplayBottom(); // re-anchors the bottom edge and sets Height
        RecomputeWorkAreaBand();
        RowsChanged?.Invoke(_rows);
    }

    /// <summary>Re-applies the window height/anchor + work-area band after a per-button METRIC change
    /// that isn't a row-count change — i.e. the button-size tier changed live (bevel-cust). Mirrors
    /// <see cref="SetRows"/>'s resize steps (height now derives from the new <see cref="TaskbarTheme.ButtonHeight"/>)
    /// and re-raises <see cref="RowsChanged"/> so the view re-runs its row layout at the new height.</summary>
    public void ReapplyMetrics()
    {
        var h = TaskbarTheme.HeightForRows(_rows);
        MinHeight = h;
        MaxHeight = h;
        PositionAtPrimaryDisplayBottom();
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
        PositionChanged += (_, _) => { TaskbarLog.Debug($"PositionChanged scaling={RenderScaling} bounds={(Screens?.Primary ?? Screens?.All?.FirstOrDefault())?.Bounds}"); RecomputeWorkAreaBand(); };
        ScalingChanged += (_, _) => { TaskbarLog.Debug($"ScalingChanged scaling={RenderScaling} MaxRows={MaxRows} rows={_rows}"); RecomputeWorkAreaBand(); };

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
            else if (c is ClassicBorderDecorator { Name: "Bd" } frame)
            {
                // The Classic Window template's outer frame border ("Bd") — an AltRaised 3D bevel drawn
                // from SystemColors (grey face + white highlight). For an edge-to-edge control bar it
                // shows as a Win2000 raised edge along the taskbar's top even under Luna, so flatten it:
                // the themed RootGrid then meets the desktop cleanly. The tray well is a separate,
                // unnamed Sunken decorator inside the content and is left alone.
                frame.BorderStyle = ClassicBorderStyle.None;
                frame.BorderThickness = new Thickness(0);
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

    /// <summary>Toggles the taskbar's always-on-top level live (bevel-cust.behavior). On → just below
    /// the menu bar (the default set in <see cref="ApplyTaskbarBehaviors"/>); off → the normal window
    /// level, so ordinary windows can cover it.</summary>
    public void SetAlwaysOnTop(bool onTop)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return;
        var handle = TryGetNativeHandle();
        if (handle == IntPtr.Zero) return;
        TaskbarNative.SetWindowLevel(handle, onTop ? TaskbarNative.CGMainMenuWindowLevel - 1 : 0);
    }

    // ── Menu-scoped key focus (bevel-vk4n) ───────────────────────────────
    // The taskbar is a non-activating utility bar: at idle it must NEVER steal key focus from the user's
    // app (canBecomeKeyWindow stays false, set in ApplyTaskbarBehaviors). But a menu/popover (Start menu,
    // a task-button or tray context menu) needs live keystrokes — arrow navigation, Enter to activate,
    // Escape to dismiss — so for exactly the duration one is open we FLIP canBecomeKeyWindow true and make
    // the bar key, then flip it back and hand key focus BACK to the app that had it (macOS menu-bar-extra
    // popover behaviour). This is what makes the wired Ctrl+Esc / Escape handler and the menus' own arrow
    // keys actually reach the taskbar instead of being dead code.

    private int _priorAppPid;

    /// <summary>Whether the taskbar is currently permitted to become the key window. False at idle; true
    /// only while a menu/popover it owns is open. Drives the native <c>canBecomeKeyWindow</c> flip. Exposed
    /// (managed state, independent of the native NSWindow) so headless tests can assert the scope without a
    /// real window.</summary>
    public bool IsKeyFocusAllowed { get; private set; }

    /// <summary>True when a real native NSWindow backs this toplevel (i.e. not the headless test surface),
    /// so callers can gate process-global native hooks — e.g. the Start-menu hotkey monitor — off in tests
    /// where <see cref="TryGetNativeHandle"/> yields Zero.</summary>
    public bool HasNativeWindow =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && TryGetNativeHandle() != IntPtr.Zero;

    /// <summary>Allows or forbids the taskbar becoming the key window, for the lifetime of an open menu.
    /// On allow: capture the frontmost app (to restore later), set <c>canBecomeKeyWindow</c> true, and make
    /// the bar key so its popups get keystrokes. On forbid: set it false again and re-activate the captured
    /// app, which returns key focus to where it was. The managed <see cref="IsKeyFocusAllowed"/> flag always
    /// tracks the request, even with no native window (headless), so the scope is observable in tests.</summary>
    public void SetKeyFocusAllowed(bool allowed)
    {
        if (IsKeyFocusAllowed == allowed) return;
        IsKeyFocusAllowed = allowed;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return;
        var handle = TryGetNativeHandle();
        if (handle == IntPtr.Zero) return;

        if (allowed)
        {
            // Remember who was frontmost so we can hand key focus back on close (resigning alone can
            // leave focus nowhere on a non-activating window).
            _priorAppPid = TaskbarNative.FrontmostAppPid();
            TaskbarNative.SetCanBecomeKeyWindow(handle, true);
            TaskbarNative.MakeKey(handle);
        }
        else
        {
            TaskbarNative.SetCanBecomeKeyWindow(handle, false);
            // Re-activate the app that owned key focus when the menu opened — the reliable way to return
            // focus from a utility bar (macOS won't auto-pick a new key window for us).
            if (_priorAppPid > 0)
            {
                TaskbarNative.ActivateAppByPid(_priorAppPid);
                _priorAppPid = 0;
            }
        }
    }

    /// <summary>Cancels the pending key-focus handback (bevel-nxic): forgets the captured prior app so the
    /// next <see cref="SetKeyFocusAllowed"/>(false) resigns key WITHOUT re-activating anyone. The handback
    /// exists to restore focus after a menu that took NO action (open, arrow, Escape); when the menu's own
    /// action was to activate a foreign window/app, re-raising the prior app would stomp that
    /// just-activated window below it (the regression this fixes). Callers invoke this the moment they
    /// drive a foreign activation/quit from an open taskbar menu. No-op when nothing was captured.</summary>
    public void CancelKeyFocusHandback() => _priorAppPid = 0;

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

    // ── Menu-scoped key focus helpers (bevel-vk4n) ───────────────────────
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern int objc_msgSend_int(IntPtr receiver, IntPtr selector);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_ret_int(IntPtr receiver, IntPtr selector, int arg);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_nuint(IntPtr receiver, IntPtr selector, nuint arg);

    private static readonly IntPtr cls_NSWorkspace = objc_getClass("NSWorkspace");
    private static readonly IntPtr cls_NSRunningApplication = objc_getClass("NSRunningApplication");
    private static readonly IntPtr sel_sharedWorkspace = SelectorCache.Get("sharedWorkspace");
    private static readonly IntPtr sel_frontmostApplication = SelectorCache.Get("frontmostApplication");
    private static readonly IntPtr sel_processIdentifier = SelectorCache.Get("processIdentifier");
    private static readonly IntPtr sel_runningAppWithPid = SelectorCache.Get("runningApplicationWithProcessIdentifier:");
    private static readonly IntPtr sel_activateWithOptions = SelectorCache.Get("activateWithOptions:");
    private static readonly IntPtr sel_makeKeyAndOrderFront = SelectorCache.Get("makeKeyAndOrderFront:");
    private static readonly IntPtr sel_keyCode = SelectorCache.Get("keyCode");

    // NSApplicationActivateIgnoringOtherApps — bring the target app fully forward on restore.
    private const nuint NSApplicationActivateIgnoringOtherApps = 1 << 1;

    /// <summary>PID of the frontmost application right now, or 0 if unavailable — captured when a menu opens
    /// so focus can be returned to it on close.</summary>
    public static int FrontmostAppPid()
    {
        if (cls_NSWorkspace == IntPtr.Zero) return 0;
        var ws = objc_msgSend_ret(cls_NSWorkspace, sel_sharedWorkspace);
        if (ws == IntPtr.Zero) return 0;
        var app = objc_msgSend_ret(ws, sel_frontmostApplication);
        return app == IntPtr.Zero ? 0 : objc_msgSend_int(app, sel_processIdentifier);
    }

    /// <summary>Re-activates the running application with <paramref name="pid"/> — used to hand key focus
    /// back to the app that had it when a taskbar menu opened.</summary>
    public static void ActivateAppByPid(int pid)
    {
        if (cls_NSRunningApplication == IntPtr.Zero || pid <= 0) return;
        var app = objc_msgSend_ret_int(cls_NSRunningApplication, sel_runningAppWithPid, pid);
        if (app != IntPtr.Zero)
            objc_msgSend_void_nuint(app, sel_activateWithOptions, NSApplicationActivateIgnoringOtherApps);
    }

    /// <summary>Makes the window key + front, so its open popups receive keystrokes (only called after
    /// <see cref="SetCanBecomeKeyWindow"/> has been flipped true for the menu's lifetime).</summary>
    public static void MakeKey(IntPtr nsWindow)
        => objc_msgSend_void_ptr(nsWindow, sel_makeKeyAndOrderFront, IntPtr.Zero);

    // ── Live modifier state (bevel-ww71) ─────────────────────────────────────
    // The taskbar is non-activating, so keyboard events don't route to its popups and (verified) macOS
    // pointer events over the popup carry NO modifier flags. [NSEvent modifierFlags] returns the CURRENT
    // global modifier state on demand, independent of focus — poll it to drive the Quit ⇄ Force Quit swap.

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_getClass")]
    private static extern IntPtr objc_getClass(string name);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nuint objc_msgSend_nuint(IntPtr receiver, IntPtr selector);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_ret_nuint_ptr(IntPtr receiver, IntPtr selector, nuint mask, IntPtr block);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_ptr(IntPtr receiver, IntPtr selector, IntPtr arg);

    private static readonly IntPtr cls_NSEvent = objc_getClass("NSEvent");
    private static readonly IntPtr sel_modifierFlags = SelectorCache.Get("modifierFlags");
    private static readonly IntPtr sel_addGlobalMonitor = SelectorCache.Get("addGlobalMonitorForEventsMatchingMask:handler:");
    private static readonly IntPtr sel_removeMonitor = SelectorCache.Get("removeMonitor:");

    private static readonly nuint NSEventModifierFlagOption = (nuint)(1UL << 19);
    private static readonly nuint NSEventMaskLeftMouseDown = (nuint)(1UL << 1);
    private static readonly nuint NSEventMaskRightMouseDown = (nuint)(1UL << 3);
    private static readonly nuint NSEventMaskKeyDown = (nuint)(1UL << 10);

    /// <summary>True iff Option/Alt is held right now (queried from macOS, focus-independent).</summary>
    public static bool OptionKeyDown()
        => cls_NSEvent != IntPtr.Zero && (objc_msgSend_nuint(cls_NSEvent, sel_modifierFlags) & NSEventModifierFlagOption) != 0;

    // ── Global mouse-down monitor for click-outside dismiss (bevel-ww71) ──────
    // addGlobalMonitorForEventsMatchingMask: fires ONLY for events delivered to OTHER apps — exactly
    // "the user clicked away from us". No focus/coordinate assumptions. The handler is a GLOBAL ObjC
    // block whose Context field carries a GCHandle to THIS monitor's callback, so multiple monitors
    // can coexist — the callback is recovered from the block pointer, not a shared static.

    // Per-token pinned state. Keyed by the native monitor id so RemoveMonitor frees EXACTLY the
    // handles for the monitor being removed. The prior design kept a single set of statics, so
    // opening a second task menu overwrote the first's handles and a later RemoveMonitor freed the
    // WRONG (still-registered) monitor's block under AppKit — a native use-after-free (P0, ce-review).
    private sealed class MonitorState { public GCHandle Action; public GCHandle Block; public GCHandle Desc; }
    private static readonly Dictionary<IntPtr, MonitorState> _monitors = new();
    private static readonly object _monitorLock = new();

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void MonitorInvoke(IntPtr block, IntPtr nsEvent)
    {
        // The block pointer IS our pinned BlockLiteral; recover this monitor's own callback from its
        // Context field rather than a shared static, so the right flyout dismisses.
        try
        {
            var ctx = Marshal.PtrToStructure<BlockLiteral>(block).Context;
            if (ctx != IntPtr.Zero && GCHandle.FromIntPtr(ctx).Target is Action a) a();
        }
        catch { /* stale/freed handle — monitor is being torn down */ }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void KeyMonitorInvoke(IntPtr block, IntPtr nsEvent)
    {
        // Key variant: hand the NSEvent to the stored reader, which extracts keyCode + modifierFlags.
        try
        {
            var ctx = Marshal.PtrToStructure<BlockLiteral>(block).Context;
            if (ctx != IntPtr.Zero && GCHandle.FromIntPtr(ctx).Target is Action<IntPtr> a) a(nsEvent);
        }
        catch { /* stale/freed handle — monitor is being torn down */ }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral { public IntPtr Isa; public int Flags; public int Reserved; public IntPtr Invoke; public IntPtr Descriptor; public IntPtr Context; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor { public nuint Reserved; public nuint Size; }

    [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "dlsym")]
    private static extern IntPtr dlsym(IntPtr handle, string symbol);
    private static readonly IntPtr RTLD_DEFAULT = new(-2);

    /// <summary>Install a global mouse-down monitor; <paramref name="onOutsideClick"/> runs (on the calling
    /// dispatcher's thread is the caller's job) when a click lands in another app. Returns a token to pass to
    /// <see cref="RemoveMonitor"/>, or Zero on failure (dismiss then falls back to Avalonia light-dismiss).</summary>
    public static unsafe IntPtr AddGlobalMouseDownMonitor(Action onOutsideClick)
    {
        try
        {
            var isa = dlsym(RTLD_DEFAULT, "_NSConcreteGlobalBlock");
            if (isa == IntPtr.Zero || cls_NSEvent == IntPtr.Zero) return IntPtr.Zero;

            var actionHandle = GCHandle.Alloc(onOutsideClick);   // keeps the callback alive; not pinned
            var descHandle = GCHandle.Alloc(
                new BlockDescriptor { Reserved = 0, Size = (nuint)Marshal.SizeOf<BlockLiteral>() },
                GCHandleType.Pinned);
            var block = new BlockLiteral
            {
                Isa = isa,
                Flags = 1 << 28,                 // BLOCK_IS_GLOBAL
                Reserved = 0,
                Invoke = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&MonitorInvoke,
                Descriptor = descHandle.AddrOfPinnedObject(),
                Context = GCHandle.ToIntPtr(actionHandle),
            };
            var blockHandle = GCHandle.Alloc(block, GCHandleType.Pinned);
            var mask = NSEventMaskLeftMouseDown | NSEventMaskRightMouseDown;
            var token = objc_msgSend_ret_nuint_ptr(cls_NSEvent, sel_addGlobalMonitor, mask, blockHandle.AddrOfPinnedObject());
            if (token == IntPtr.Zero)
            {
                actionHandle.Free(); blockHandle.Free(); descHandle.Free();
                return IntPtr.Zero;
            }
            lock (_monitorLock)
                _monitors[token] = new MonitorState { Action = actionHandle, Block = blockHandle, Desc = descHandle };
            return token;
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>Install a global key-down monitor so Ctrl+Esc / Option+Esc can summon the Start menu even
    /// though the taskbar is a non-activating window that gets no keystrokes at idle (bevel-vk4n). Global
    /// monitors observe events delivered to OTHER apps (observe-only — they never consume), so this is the
    /// honest way to make the wired open-gesture live from anywhere. <paramref name="onKey"/> receives the
    /// event's keyCode and modifierFlags. Returns a token for <see cref="RemoveMonitor"/>, or Zero on
    /// failure. Mirrors the mouse monitor's block ABI; the callback reads the NSEvent on invoke.</summary>
    public static unsafe IntPtr AddGlobalKeyDownMonitor(Action<ulong, ulong> onKey)
    {
        try
        {
            var isa = dlsym(RTLD_DEFAULT, "_NSConcreteGlobalBlock");
            if (isa == IntPtr.Zero || cls_NSEvent == IntPtr.Zero) return IntPtr.Zero;

            // The reader runs on invoke: pull keyCode + modifierFlags off the NSEvent and forward them.
            Action<IntPtr> reader = nsEvent =>
            {
                var keyCode = (ulong)objc_msgSend_nuint(nsEvent, sel_keyCode);
                var flags = (ulong)objc_msgSend_nuint(nsEvent, sel_modifierFlags);
                onKey(keyCode, flags);
            };

            var actionHandle = GCHandle.Alloc(reader);
            var descHandle = GCHandle.Alloc(
                new BlockDescriptor { Reserved = 0, Size = (nuint)Marshal.SizeOf<BlockLiteral>() },
                GCHandleType.Pinned);
            var block = new BlockLiteral
            {
                Isa = isa,
                Flags = 1 << 28,                 // BLOCK_IS_GLOBAL
                Reserved = 0,
                Invoke = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&KeyMonitorInvoke,
                Descriptor = descHandle.AddrOfPinnedObject(),
                Context = GCHandle.ToIntPtr(actionHandle),
            };
            var blockHandle = GCHandle.Alloc(block, GCHandleType.Pinned);
            var token = objc_msgSend_ret_nuint_ptr(cls_NSEvent, sel_addGlobalMonitor, NSEventMaskKeyDown, blockHandle.AddrOfPinnedObject());
            if (token == IntPtr.Zero)
            {
                actionHandle.Free(); blockHandle.Free(); descHandle.Free();
                return IntPtr.Zero;
            }
            lock (_monitorLock)
                _monitors[token] = new MonitorState { Action = actionHandle, Block = blockHandle, Desc = descHandle };
            return token;
        }
        catch { return IntPtr.Zero; }
    }

    public static void RemoveMonitor(IntPtr token)
    {
        if (token == IntPtr.Zero) return;
        try { objc_msgSend_void_ptr(cls_NSEvent, sel_removeMonitor, token); } catch { }
        MonitorState? st;
        lock (_monitorLock) { _monitors.Remove(token, out st); }
        if (st is null) return;
        if (st.Action.IsAllocated) st.Action.Free();
        if (st.Block.IsAllocated) st.Block.Free();
        if (st.Desc.IsAllocated) st.Desc.Free();
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