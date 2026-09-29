using System;
using System.Runtime.InteropServices;

namespace Bevel.Pal.MacOS;

/// <summary>
/// Every native library path used by this assembly, written exactly once (bevel-uat).
///
/// Before this existed, five files each declared their own <c>private const string CoreFoundation = …</c>
/// and <see cref="AppKitInterop"/> inlined the literals. They all happened to agree, but there was no
/// single place to be right: two files even used different NAMES (<c>AppServices</c>,
/// <c>ApplicationServices</c>) for the same path, so a divergence would not have looked like one in review.
/// </summary>
internal static class Frameworks
{
    private const string Sys = "/System/Library/Frameworks/";

    public const string AppKit = Sys + "AppKit.framework/AppKit";
    public const string ApplicationServices = Sys + "ApplicationServices.framework/ApplicationServices";
    public const string CoreFoundation = Sys + "CoreFoundation.framework/CoreFoundation";
    public const string CoreGraphics = Sys + "CoreGraphics.framework/CoreGraphics";
    public const string CoreServices = Sys + "CoreServices.framework/CoreServices";
    public const string ImageIO = Sys + "ImageIO.framework/ImageIO";
    /// <summary>dlopen-only (LoginItemRegistrar); no DllImport binds it directly.</summary>
    public const string ServiceManagement = Sys + "ServiceManagement.framework/ServiceManagement";

    public const string LibSystem = "/usr/lib/libSystem.dylib";
    public const string ObjC = "/usr/lib/libobjc.dylib";

    // ── Loading ───────────────────────────────────────────────────────────
    // dlopen lives here, private, as the SOLE way to load a framework in this assembly. It used to be
    // public on AppKitInterop so LoginItemRegistrar could call it, which meant the one thing the
    // InteropConventionTests guard cannot see -- a path passed as an ARGUMENT rather than as a DllImport
    // library -- was centralised only by a comment asking authors to be careful. It had already drifted:
    // RTLD_LAZY was a private const in two files. Making this the only caller turns the convention into a
    // type boundary: you cannot reach dlopen to hand it a literal.
    //
    // Self-qualified as Frameworks.LibSystem rather than a bare LibSystem so this declaration satisfies
    // the same "names a Frameworks constant" rule as every other DllImport in the assembly.
    [DllImport(Frameworks.LibSystem)]
    private static extern IntPtr dlopen(string path, int mode);

    private const int RTLD_LAZY = 0x1;
    private const int RTLD_NOLOAD = 0x10;

    /// <summary>Loads the framework or dylib at <paramref name="path"/> (lazily bound) into this process.
    /// Returns false when it cannot be loaded — a headless or sandboxed environment, or an OS too old to
    /// ship it — which every caller treats as "the feature is unavailable", never as an error.</summary>
    public static bool Load(string path) => dlopen(path, RTLD_LAZY) != IntPtr.Zero;

    /// <summary>Whether <paramref name="path"/> is ALREADY loaded, without loading it. RTLD_NOLOAD
    /// returns a handle only for an image already in the process.</summary>
    public static bool IsLoaded(string path) => dlopen(path, RTLD_LAZY | RTLD_NOLOAD) != IntPtr.Zero;
}
