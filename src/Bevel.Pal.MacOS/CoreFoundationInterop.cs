using System;
using System.Runtime.InteropServices;

namespace Bevel.Pal.MacOS;

/// <summary>
/// CoreFoundation entry points, declared once (bevel-uat). Consolidated from four private silos in
/// <see cref="AppKitInterop"/>, <c>GeckoTabEngine</c>, <c>MacOSAppBadgeSource</c> and
/// <c>MacOSThumbnailProvider</c>.
///
/// Type mapping, from the CF headers — six of these symbols disagreed across the old copies, and the
/// disagreements were benign only because Bevel is 64-bit-only:
/// <list type="bullet">
///   <item><c>CFIndex</c> = signed long → <c>nint</c> (NOT <c>long</c> or <c>int</c>: it is pointer-sized)</item>
///   <item><c>CFTypeID</c> = unsigned long → <c>nuint</c> (NOT <c>IntPtr</c>: it is unsigned; only
///   equality use saved us)</item>
///   <item><c>Boolean</c> = unsigned char → <c>[MarshalAs(UnmanagedType.U1)] bool</c> (NOT <c>I1</c>)</item>
/// </list>
/// Keep new declarations consistent with these. <c>InteropConventionTests</c> enforces single declaration
/// but cannot check a type against Apple's header for you.
/// </summary>
internal static class CoreFoundationInterop
{
    // ── Lifetime ──────────────────────────────────────────────────────────
    [DllImport(Frameworks.CoreFoundation)]
    public static extern void CFRelease(IntPtr cf);

    [DllImport(Frameworks.CoreFoundation)]
    public static extern IntPtr CFRetain(IntPtr cf);

    // ── Type identity. All four must share one type: the call sites compare them to each other, so a
    // mixture of nuint and IntPtr would not even compile — which is how the old split stayed invisible.
    [DllImport(Frameworks.CoreFoundation)]
    public static extern nuint CFGetTypeID(IntPtr cf);

    [DllImport(Frameworks.CoreFoundation)]
    public static extern nuint CFArrayGetTypeID();

    [DllImport(Frameworks.CoreFoundation)]
    public static extern nuint CFStringGetTypeID();

    [DllImport(Frameworks.CoreFoundation)]
    public static extern nuint CFURLGetTypeID();

    // ── Arrays ────────────────────────────────────────────────────────────
    [DllImport(Frameworks.CoreFoundation)]
    public static extern nint CFArrayGetCount(IntPtr theArray);

    [DllImport(Frameworks.CoreFoundation)]
    public static extern IntPtr CFArrayGetValueAtIndex(IntPtr theArray, nint idx);

    // ── Strings ───────────────────────────────────────────────────────────
    [DllImport(Frameworks.CoreFoundation)]
    public static extern IntPtr CFStringCreateWithCString(IntPtr alloc, IntPtr cStr, uint encoding);

    [DllImport(Frameworks.CoreFoundation)]
    public static extern IntPtr CFStringGetCStringPtr(IntPtr theString, uint encoding);

    [DllImport(Frameworks.CoreFoundation)]
    public static extern nint CFStringGetLength(IntPtr theString);

    // Return marshalling is left exactly as the original declared it (I1). It is not one of the six
    // divergences, and for a 0/1 Boolean I1 and U1 read the same byte — so changing it would be an
    // unforced behaviour change on a string-copy success flag.
    [DllImport(Frameworks.CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CFStringGetCString(IntPtr theString, IntPtr buffer, nint bufferSize, uint encoding);

    // ── URLs ──────────────────────────────────────────────────────────────
    [DllImport(Frameworks.CoreFoundation)]
    public static extern IntPtr CFURLCreateFromFileSystemRepresentation(
        IntPtr alloc, byte[] buffer, nint bufLen,
        [MarshalAs(UnmanagedType.U1)] bool isDirectory);

    [DllImport(Frameworks.CoreFoundation)]
    public static extern IntPtr CFURLCopyFileSystemPath(IntPtr url, nint pathStyle);

    /// <summary>A CFURLRef for a file-system path, owned by the caller (CFRelease it). Three files had
    /// their own three-line version of this, differing only in the <paramref name="isDirectory"/> they
    /// passed — and one of them shadowed the imported extern with a same-named wrapper, forcing a
    /// fully-qualified call at its only call site.</summary>
    public static IntPtr CFUrlForPath(string path, bool isDirectory)
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes(path);
        return CFURLCreateFromFileSystemRepresentation(IntPtr.Zero, utf8, utf8.Length, isDirectory);
    }

    // ── Bundles ───────────────────────────────────────────────────────────
    [DllImport(Frameworks.CoreFoundation)]
    public static extern IntPtr CFBundleCreate(IntPtr alloc, IntPtr bundleURL);

    [DllImport(Frameworks.CoreFoundation)]
    public static extern IntPtr CFBundleGetIdentifier(IntPtr bundle);

    // ── Collections, used to build the ImageIO thumbnail options dictionary ─
    [DllImport(Frameworks.CoreFoundation)]
    public static extern IntPtr CFDictionaryCreate(
        IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint numValues,
        IntPtr keyCallBacks, IntPtr valueCallBacks);

    // `type` is left as int, against the CFIndex rule above, deliberately. CFNumberType is
    // `typedef CFIndex CFNumberType`, so pointer-sized would be correct — but unlike the nint/long and
    // nuint/IntPtr swaps elsewhere here (identical 8-byte widths, provably no-ops), widening 4 bytes to 8
    // is a real ABI change. It works today and the thumbnail pixel tests cover it, so changing it is a
    // behaviour change with no demonstrated defect behind it. Tracked separately rather than smuggled
    // into a consolidation commit.
    [DllImport(Frameworks.CoreFoundation)]
    public static extern IntPtr CFNumberCreate(IntPtr allocator, int type, ref int value);
}
