using System;
using System.Runtime.InteropServices;

namespace Bevel.Pal.MacOS;

/// <summary>
/// Accessibility (AX) entry points, declared once (bevel-uat). Consolidated from <c>GeckoTabEngine</c>,
/// <c>MacOSAppBadgeSource</c> and <c>MacOSPermissionBroker</c>, which between them named the same
/// framework path under two different constant names (<c>AppServices</c>, <c>ApplicationServices</c>) —
/// verified identical before merging, because a genuine path change here alters TCC prompting.
///
/// Every function here needs the Accessibility TCC grant. <see cref="AXIsProcessTrusted"/> is the gate
/// the rest depend on.
/// </summary>
internal static class AccessibilityInterop
{
    /// <summary>Whether this process holds the Accessibility grant.
    ///
    /// The <c>U1</c> return marshalling is load-bearing and was NOT uniform before this consolidation:
    /// MacOSAppBadgeSource declared I1, MacOSPermissionBroker declared a bare <c>bool</c>. A bare
    /// <c>bool</c> marshals as a 4-byte Win32 BOOL, so the broker was reading four bytes of a one-byte
    /// <c>Boolean</c> return — three bytes of undefined register content deciding a permission check.
    /// AX returns <c>Boolean</c> (unsigned char), so U1 is the correct width and signedness.</summary>
    [DllImport(Frameworks.ApplicationServices)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool AXIsProcessTrusted();

    [DllImport(Frameworks.ApplicationServices)]
    public static extern IntPtr AXUIElementCreateApplication(int pid);

    [DllImport(Frameworks.ApplicationServices)]
    public static extern IntPtr AXUIElementCreateSystemWide();

    [DllImport(Frameworks.ApplicationServices)]
    public static extern int AXUIElementCopyAttributeValue(IntPtr element, IntPtr attribute, out IntPtr value);

    [DllImport(Frameworks.ApplicationServices)]
    public static extern int AXUIElementPerformAction(IntPtr element, IntPtr action);

    /// <summary>Bounds a single AX request so a hung target cannot wedge the caller. Transcribed verbatim
    /// from GeckoTabEngine, including the un-prefixed symbol name — the Swift helper uses the underscored
    /// private spelling, but this managed copy has always used this one and renaming a P/Invoke entry
    /// point on an unverified symbol is not a refactor.</summary>
    [DllImport(Frameworks.ApplicationServices)]
    public static extern int AXUIElementSetMessagingTimeout(IntPtr element, float seconds);
}
