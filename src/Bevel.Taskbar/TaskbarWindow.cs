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
        var handle = ((TopLevel)this).TryGetPlatformHandle();
        if (handle is INativePlatformHandleSurface surface)
            return surface.Handle;
        return IntPtr.Zero;
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

    private static readonly IntPtr sel_setLevel;
    private static readonly IntPtr sel_setCollectionBehavior;

    static TaskbarNative()
    {
        sel_setLevel = SelectorCache.Get("setLevel:");
        sel_setCollectionBehavior = SelectorCache.Get("setCollectionBehavior:");
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