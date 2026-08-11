using System;

namespace Bevel.Pal.MacOS;

/// <summary>
/// Bevel's control <c>NSStatusItem</c> for Strategy C (bevel-7hf4): a menu-bar item Bevel owns, whose
/// expansion pushes the real status items off the visible bar (macOS lays them out right-to-left), and
/// whose collapse reveals them. Created and toggled here, in the MAIN Bevel app process, via ObjC
/// interop — NOT in the gRPC helper, which can't host a status item (its hand-rolled run loop deadlocks
/// the status-bar IPC). Avalonia gives this process a real <c>NSApplication</c> run loop, so it works.
///
/// Anchored via <c>autosaveName</c> + the private "NSStatusItem Preferred Position" default (seeded
/// before creation) so macOS keeps a stable, on-screen slot across length changes — without it the item
/// is parked off-screen (proven in <c>native/helper-macos/menubar-hide-poc.swift</c>).
///
/// THREADING: every method must run on the AppKit main thread — i.e. the Avalonia UI thread. Callers are
/// on the UI thread (the settings-apply path); this class does no marshalling of its own.
/// </summary>
public static class MacMenuBarControl
{
    private const string AutosaveName = "BevelTrayControl";
    private const double ExpandedLength = 10_000;   // clamped to the bar width → pushes neighbours off
    private const double VariableLength = -1;        // NSVariableStatusItemLength

    private static IntPtr _item;   // retained NSStatusItem, or Zero until created
    private static bool _hiding;

    /// <summary>Hide (expand) or reveal (collapse) the real status items. Creates the control item on
    /// first use. Main thread only.</summary>
    public static void SetHidden(bool hidden)
    {
        EnsureCreated();
        if (_item == IntPtr.Zero) return;
        _hiding = hidden;
        AppKitInterop.SendVoid_Double(_item, AppKitInterop.Sel("setLength:"), hidden ? ExpandedLength : VariableLength);
    }

    /// <summary>True while hiding the neighbours.</summary>
    public static bool IsHiding => _hiding;

    private static void EnsureCreated()
    {
        if (_item != IntPtr.Zero) return;

        // Seed the preferred-position anchor BEFORE creating the item (only if absent).
        var defaults = AppKitInterop.SendIntPtr(AppKitInterop.GetClass("NSUserDefaults"), AppKitInterop.Sel("standardUserDefaults"));
        var posKey = AppKitInterop.NSStringCreate($"NSStatusItem Preferred Position {AutosaveName}");
        if (defaults != IntPtr.Zero && posKey != IntPtr.Zero)
        {
            var existing = AppKitInterop.SendIntPtr_IntPtr(defaults, AppKitInterop.Sel("objectForKey:"), posKey);
            if (existing == IntPtr.Zero)
                AppKitInterop.SendVoid_Double_IntPtr(defaults, AppKitInterop.Sel("setDouble:forKey:"), 0.0, posKey);
        }

        var statusBar = AppKitInterop.SendIntPtr(AppKitInterop.GetClass("NSStatusBar"), AppKitInterop.Sel("systemStatusBar"));
        if (statusBar == IntPtr.Zero) { Release(posKey); return; }

        // [NSStatusBar statusItemWithLength:] returns an item owned by the status bar; retain our ref.
        var item = AppKitInterop.SendIntPtr_Double(statusBar, AppKitInterop.Sel("statusItemWithLength:"), VariableLength);
        if (item == IntPtr.Zero) { Release(posKey); return; }
        AppKitInterop.SendVoid(item, AppKitInterop.Sel("retain"));

        var autosave = AppKitInterop.NSStringCreate(AutosaveName);
        if (autosave != IntPtr.Zero) { AppKitInterop.SendVoid_IntPtr(item, AppKitInterop.Sel("setAutosaveName:"), autosave); Release(autosave); }

        // Give the button a small marker so the control is visible + clickable (like Ice's icon).
        var button = AppKitInterop.SendIntPtr(item, AppKitInterop.Sel("button"));
        if (button != IntPtr.Zero)
        {
            var title = AppKitInterop.NSStringCreate("◂◂"); // ◂◂
            if (title != IntPtr.Zero) { AppKitInterop.SendVoid_IntPtr(button, AppKitInterop.Sel("setTitle:"), title); Release(title); }
        }

        Release(posKey);
        _item = item;
    }

    private static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero) AppKitInterop.SendVoid(obj, AppKitInterop.Sel("release"));
    }
}
