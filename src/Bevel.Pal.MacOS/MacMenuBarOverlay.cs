using System;
using Bevel.Pal.Abstractions;

namespace Bevel.Pal.MacOS;

/// <summary>
/// Strategy C (bevel-7hf4) OVERLAY-HIDE. A borderless, click-through <c>NSPanel</c> at window level
/// <c>26</c> (<c>kCGStatusWindowLevel</c> 25 + 1) that VISUALLY COVERS the real menu-bar status-item
/// strip while the items stay ON-SCREEN and on a display.
///
/// <para>This replaces <see cref="MacMenuBarControl"/>'s off-screen push (which expands a control item to
/// 10 000pt, shoving the real items fully off every display). Once off-display macOS freezes their backing
/// store, so <c>CGWindowListCreateImageFromArray</c> returns byte-identical frames forever and Bevel's
/// mirrored tray copies (clock, iStat, battery %) go stale. Under the overlay the items are merely
/// OCCLUDED-but-on-display — the one state proven to keep redrawing — so fresh pixels keep flowing to the
/// tray. Level 26 sits above the status items (25) but below pop-up menus (~101), so a revealed menu still
/// draws on top; the panel is <c>ignoresMouseEvents = true</c> (click-through) so the tray's AX-press
/// reaches the live item underneath and stray strip clicks pass through.</para>
///
/// <para>Occlusion insurance: the panel is filled with a fully-opaque menu-bar-matched colour but kept
/// <c>isOpaque = false</c>, so the WindowServer never counts it as occluding — the covered items can never
/// be occlusion/App-Nap throttled (design-doc risk 1). It looks solid regardless.</para>
///
/// <para>v1: primary display only, a solid <c>windowBackgroundColor</c> fill (tracks light/dark). Multi-
/// display, wallpaper-clipped cover art, and fullscreen/auto-hidden-menu-bar handling are follow-ups.</para>
///
/// THREADING: every method must run on the AppKit main thread — i.e. the Avalonia UI thread. Callers are
/// on the UI thread (the settings-apply path); this class does no marshalling of its own. Creating an
/// NSPanel off the main thread throws NSInternalInconsistencyException.
/// </summary>
public static class MacMenuBarOverlay
{
    // Style: borderless (0) | nonactivatingPanel (1<<7=128). nonactivatingPanel is an NSPanel-only mask bit.
    private const nuint StyleMask = 0 | (1 << 7);
    private const nuint BackingBuffered = 2;                  // NSBackingStoreBuffered
    private const nint OverlayLevel = 25 + 1;                 // kCGStatusWindowLevel + 1 = 26
    // canJoinAllSpaces(1) | stationary(16) | ignoresCycle(64) | fullScreenAuxiliary(256) = 337
    private const nuint CollectionBehavior = 1 | 16 | 64 | 256;
    private const double EdgePad = 2.0;                       // absorbs the ~1px reflow jitter (no event)

    private static IntPtr _panel;                            // retained NSPanel, Zero until first cover
    private static bool _shown;                              // current logical state
    private static AppKitInterop.NSRect _lastFrame;          // last applied frame (AppKit bottom-left coords)

    /// <summary>True while the overlay is covering the strip.</summary>
    public static bool IsHiding => _shown;

    /// <summary>Cover (<paramref name="hidden"/> == true) or uncover the mirrored menu-bar strip.
    /// <paramref name="strip"/> is the union of the mirrored items' bounds in GLOBAL TOP-LEFT CG points
    /// (straight from <c>TrayItem.Bounds</c>); its x-range drives the cover width, the menu-bar height comes
    /// from screen geometry. A null/empty strip while hiding is a no-op (nothing to cover yet — the host
    /// re-pokes when items arrive). Idempotent: every call converges on <c>(_shown, _lastFrame)</c>. The
    /// panel is created once and kept for the process lifetime; uncover is just <c>orderOut:</c>. Main
    /// thread only.</summary>
    public static void SetHidden(bool hidden, PalRect? strip)
    {
        if (!hidden)
        {
            if (_shown && _panel != IntPtr.Zero)
                AppKitInterop.SendVoid_IntPtr(_panel, AppKitInterop.Sel("orderOut:"), IntPtr.Zero);
            _shown = false;
            return;
        }

        if (strip is not { } s || s.Width <= 0)
            return;   // nothing to cover yet; host re-pokes on the first mirrored item

        if (!TryComputeFrame(s, out var frame))
            return;

        EnsureCreated();
        if (_panel == IntPtr.Zero)
            return;

        if (_shown && FrameEquals(frame, _lastFrame))
            return;   // idempotent — frame unchanged, nothing to do

        AppKitInterop.SendVoid_NSRect_Bool(_panel, AppKitInterop.Sel("setFrame:display:"), frame, true);
        AppKitInterop.SendVoid(_panel, AppKitInterop.Sel("orderFrontRegardless"));
        _lastFrame = frame;
        _shown = true;
    }

    /// <summary>Computes the AppKit (bottom-left origin) cover frame for the primary display from the CG
    /// top-left mirrored-items <paramref name="strip"/>. On the primary display both spaces share the x
    /// origin, so x needs no conversion; y flips against the screen height and the strip spans the full
    /// menu-bar height, top-aligned.</summary>
    private static bool TryComputeFrame(PalRect strip, out AppKitInterop.NSRect frame)
    {
        frame = default;
        AppKitInterop.EnsureAppKitLoaded();

        var screens = AppKitInterop.NSScreenScreens();
        if (screens == IntPtr.Zero || AppKitInterop.NSArrayCount(screens) == 0)
            return false;

        var primary = AppKitInterop.NSArrayObjectAtIndex(screens, 0);   // screens[0] == primary in AppKit
        if (primary == IntPtr.Zero)
            return false;

        var (sx, sy, sw, sh) = AppKitInterop.NSScreenFrame(primary);
        if (sw <= 0 || sh <= 0)
            return false;

        var menuH = AppKitInterop.MenuBarHeight(primary);
        if (menuH <= 0)
            return false;

        // x-range from the mirrored strip, padded and clamped to the screen. (CG top-left x ≡ AppKit x
        // on the primary display.)
        var x = Math.Max(sx, strip.X - EdgePad);
        var right = Math.Min((double)(sx + sw), strip.X + (double)strip.Width + EdgePad);
        var width = right - x;
        if (width <= 0)
            return false;

        // Top strip, bottom-left origin: sit the panel's top edge at the screen's top.
        var y = (sy + sh) - menuH;

        frame = new AppKitInterop.NSRect { X = x, Y = y, Width = width, Height = menuH };
        return true;
    }

    private static void EnsureCreated()
    {
        if (_panel != IntPtr.Zero)
            return;

        AppKitInterop.EnsureAppKitLoaded();

        // Must be NSPanel specifically: the nonactivatingPanel style bit is honored only by NSPanel.
        var cls = AppKitInterop.GetClass("NSPanel");
        if (cls == IntPtr.Zero)
            return;

        var alloced = AppKitInterop.SendIntPtr(cls, AppKitInterop.Sel("alloc"));
        if (alloced == IntPtr.Zero)
            return;

        // -init… returns a +1 OWNED object; SetHidden applies the real frame via setFrame:display:
        // immediately after, so a zero initial rect is fine.
        var panel = AppKitInterop.SendIntPtr_NSRect_NUInt_NUInt_Bool(
            alloced, AppKitInterop.Sel("initWithContentRect:styleMask:backing:defer:"),
            default, StyleMask, BackingBuffered, deferCreation: false);
        if (panel == IntPtr.Zero)
            return;

        // NSPanel defaults releasedWhenClosed = YES; we own the +1 from init and never call close (only
        // orderOut:), so disarm auto-release to keep the retained panel valid for re-cover.
        AppKitInterop.SendVoid_Bool(panel, AppKitInterop.Sel("setReleasedWhenClosed:"), false);

        AppKitInterop.SendVoid_NInt(panel, AppKitInterop.Sel("setLevel:"), OverlayLevel);
        AppKitInterop.SendVoid_NUInt(panel, AppKitInterop.Sel("setCollectionBehavior:"), CollectionBehavior);
        // Opaque FILL but isOpaque = false → the WindowServer never marks it occluding, so covered items
        // keep redrawing (design-doc risk 1 can never trigger). Visually still solid.
        AppKitInterop.SendVoid_Bool(panel, AppKitInterop.Sel("setOpaque:"), false);
        AppKitInterop.SendVoid_Bool(panel, AppKitInterop.Sel("setHasShadow:"), false);
        AppKitInterop.SendVoid_Bool(panel, AppKitInterop.Sel("setIgnoresMouseEvents:"), true);

        var color = MenuBarMatchedColor();
        if (color != IntPtr.Zero)
            AppKitInterop.SendVoid_IntPtr(panel, AppKitInterop.Sel("setBackgroundColor:"), color);

        _panel = panel;
    }

    /// <summary>The v1 cover fill: <c>[NSColor windowBackgroundColor]</c> — a dynamic system colour that
    /// tracks light/dark automatically. It is an autoreleased system singleton; do NOT release (cf. the
    /// bevel-fo2 hazard note). Upgrade path (design-doc risk 3): wallpaper clipped to the strip, à la Ice.</summary>
    private static IntPtr MenuBarMatchedColor()
    {
        var nsColor = AppKitInterop.GetClass("NSColor");
        if (nsColor == IntPtr.Zero)
            return IntPtr.Zero;
        return AppKitInterop.SendIntPtr(nsColor, AppKitInterop.Sel("windowBackgroundColor"));
    }

    private static bool FrameEquals(AppKitInterop.NSRect a, AppKitInterop.NSRect b)
    {
        const double eps = 0.5;
        return Math.Abs(a.X - b.X) < eps
            && Math.Abs(a.Y - b.Y) < eps
            && Math.Abs(a.Width - b.Width) < eps
            && Math.Abs(a.Height - b.Height) < eps;
    }
}
