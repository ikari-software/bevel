using System.Runtime.InteropServices;
using static Bevel.Pal.MacOS.CoreFoundationInterop;

namespace Bevel.Pal.MacOS;

/// <summary>
/// Centralized P/Invoke helpers for safe in-process AppKit / Foundation calls on macOS.
/// Follows the pattern established in <c>Bevel.Desktop.NativeMac</c>: selectors are cached,
/// objc_msgSend overloads are typed, and all interop lives in one place.
/// Every call is non-fragile — no swizzling, no private API, no helper dependency.
/// </summary>
internal static class AppKitInterop
{
    // ------------------------------------------------------------------
    //  AppKit loading
    // ------------------------------------------------------------------

    private static bool _appKitLoaded;

    /// <summary>
    /// Ensures AppKit.framework is loaded into the process. Required because .NET
    /// processes don't link AppKit by default — only Foundation is available.
    /// Idempotent; safe to call multiple times.
    /// </summary>
    public static void EnsureAppKitLoaded()
    {
        if (_appKitLoaded)
            return;

        // Already-loaded is checked first so the common case costs no load attempt. A failure leaves the
        // flag false: this is a headless or sandboxed environment, every AppKit-dependent call degrades to
        // null/empty, and a later call may retry.
        if (Frameworks.IsLoaded(Frameworks.AppKit) || Frameworks.Load(Frameworks.AppKit))
            _appKitLoaded = true;
    }

    // ------------------------------------------------------------------
    //  objc_msgSend overloads
    // ------------------------------------------------------------------

    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")]
    public static extern void SendVoid(IntPtr receiver, IntPtr selector);

    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendIntPtr(IntPtr receiver, IntPtr selector);

    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendIntPtr_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendIntPtr_IntPtr_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);

    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendIntPtr_IntPtr_IntPtr_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2, IntPtr arg3);

    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SendBool_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I4)]
    public static extern int SendInt(IntPtr receiver, IntPtr selector);

    /// <summary>objc_msgSend returning ObjC BOOL with no args (e.g. <c>isActive</c>). I1 because ObjC
    /// BOOL is a signed char on ARM64 — a bare bool would marshal as a 4-byte Win32 BOOL.</summary>
    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SendBool(IntPtr receiver, IntPtr selector);

    /// <summary>objc_msgSend returning ObjC BOOL with one NSUInteger arg (e.g.
    /// <c>activateWithOptions:</c>).</summary>
    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SendBool_NUInt(IntPtr receiver, IntPtr selector, nuint arg1);

    /// <summary>objc_msgSend returning a pointer-width signed integer (NSInteger) — e.g. an enum status.</summary>
    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")]
    public static extern nint SendNInt(IntPtr receiver, IntPtr selector);

    /// <summary>objc_msgSend with a void return and one object arg (e.g. <c>setAutosaveName:</c>).</summary>
    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")]
    public static extern void SendVoid_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

    /// <summary>objc_msgSend with one CGFloat (double) arg returning an object (e.g. <c>statusItemWithLength:</c>).
    /// On arm64 the double is passed in a float register (v0) per AAPCS.</summary>
    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendIntPtr_Double(IntPtr receiver, IntPtr selector, double arg1);

    /// <summary>objc_msgSend with a void return and one CGFloat (double) arg (e.g. <c>setLength:</c>).</summary>
    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")]
    public static extern void SendVoid_Double(IntPtr receiver, IntPtr selector, double arg1);

    /// <summary>objc_msgSend with a void return, one CGFloat (double) arg and one object arg
    /// (e.g. <c>setDouble:forKey:</c>).</summary>
    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")]
    public static extern void SendVoid_Double_IntPtr(IntPtr receiver, IntPtr selector, double arg1, IntPtr arg2);

    // ── Window/panel creation + geometry (MacMenuBarOverlay, bevel-7hf4) ──
    // NSRect is a 4-double HFA: on ARM64 it is passed IN v0–v3 (and returned in v0–v3), so a fixed
    // objc_msgSend signature carries it correctly — no _stret. Integer/BOOL args follow in x2… as usual.

    /// <summary>objc_msgSend for <c>initWithContentRect:styleMask:backing:defer:</c> — returns the
    /// initialized NSPanel/NSWindow. NSRect in v0–v3, the two NSUIntegers in x2/x3, the BOOL in w4.</summary>
    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendIntPtr_NSRect_NUInt_NUInt_Bool(
        IntPtr receiver, IntPtr selector, NSRect rect, nuint styleMask, nuint backing,
        [MarshalAs(UnmanagedType.I1)] bool deferCreation);

    /// <summary>objc_msgSend for <c>setFrame:display:</c> (void; NSRect + BOOL).</summary>
    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")]
    public static extern void SendVoid_NSRect_Bool(
        IntPtr receiver, IntPtr selector, NSRect rect, [MarshalAs(UnmanagedType.I1)] bool display);

    /// <summary>objc_msgSend with one NSInteger arg (e.g. <c>setLevel:</c>).</summary>
    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")]
    public static extern void SendVoid_NInt(IntPtr receiver, IntPtr selector, nint arg1);

    /// <summary>objc_msgSend with one NSUInteger arg (e.g. <c>setCollectionBehavior:</c>).</summary>
    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")]
    public static extern void SendVoid_NUInt(IntPtr receiver, IntPtr selector, nuint arg1);

    /// <summary>objc_msgSend with one ObjC BOOL arg (1 byte on ARM64 — hence I1; e.g. <c>setOpaque:</c>,
    /// <c>setHasShadow:</c>, <c>setIgnoresMouseEvents:</c>, <c>setReleasedWhenClosed:</c>).</summary>
    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")]
    public static extern void SendVoid_Bool(IntPtr receiver, IntPtr selector,
        [MarshalAs(UnmanagedType.I1)] bool arg1);

    /// <summary>objc_msgSend returning a CGFloat (double) — e.g. <c>[NSStatusBar thickness]</c>.
    /// On ARM64 the double comes back in d0; plain objc_msgSend (no _fpret).</summary>
    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")]
    public static extern double SendDouble(IntPtr receiver, IntPtr selector);

    // ------------------------------------------------------------------
    //  Selector cache
    // ------------------------------------------------------------------

    [DllImport(Frameworks.ObjC, EntryPoint = "sel_registerName")]
    private static extern IntPtr sel_registerName(string name);

    // Concurrent: resolved from threadpool threads too (icon renders, the Gecko AX tab walk) — a
    // plain Dictionary raced its buckets under concurrent insert.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IntPtr> _selectors = new();

    public static IntPtr Sel(string name)
        => _selectors.GetOrAdd(name, static n => sel_registerName(n));

    // ------------------------------------------------------------------
    //  objc_getClass
    // ------------------------------------------------------------------

    [DllImport(Frameworks.ObjC, EntryPoint = "objc_getClass")]
    private static extern IntPtr objc_getClass(string name);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IntPtr> _classes = new();

    public static IntPtr GetClass(string name)
        => _classes.GetOrAdd(name, static n => objc_getClass(n));

    // ------------------------------------------------------------------
    //  NSWorkspace
    // ------------------------------------------------------------------

    private static IntPtr? _sharedWorkspace;

    /// <summary>Returns the singleton [NSWorkspace sharedWorkspace], or IntPtr.Zero if AppKit is unavailable.</summary>
    public static IntPtr SharedWorkspace()
    {
        if (_sharedWorkspace.HasValue)
            return _sharedWorkspace.Value;

        EnsureAppKitLoaded();

        var cls = GetClass("NSWorkspace");
        if (cls == IntPtr.Zero)
        {
            _sharedWorkspace = IntPtr.Zero;
            return IntPtr.Zero;
        }

        var sel = Sel("sharedWorkspace");
        _sharedWorkspace = SendIntPtr(cls, sel);
        return _sharedWorkspace.Value;
    }

    /// <summary>Returns [workspace runningApplications] as an NSArray of NSRunningApplication.</summary>
    public static IntPtr RunningApplications(IntPtr workspace)
    {
        if (workspace == IntPtr.Zero)
            return IntPtr.Zero;
        return SendIntPtr(workspace, Sel("runningApplications"));
    }

    /// <summary>
    /// Calls [workspace URLForApplicationWithBundleIdentifier:]. Returns the NSURL or IntPtr.Zero.
    /// </summary>
    public static IntPtr URLForApplicationWithBundleIdentifier(IntPtr workspace, string bundleId)
    {
        if (workspace == IntPtr.Zero)
            return IntPtr.Zero;

        var nsBundleId = NSStringCreate(bundleId);
        try
        {
            return SendIntPtr_IntPtr(workspace, Sel("URLForApplicationWithBundleIdentifier:"), nsBundleId);
        }
        finally
        {
            if (nsBundleId != IntPtr.Zero)
                SendVoid(nsBundleId, Sel("release"));
        }
    }

    /// <summary>[workspace URLsForApplicationsToOpenURL:url] — an NSArray&lt;NSURL*&gt; of every app that
    /// can open the URL, most-suitable first (macOS 12+). The array is AUTORELEASED; do NOT release it.
    /// Returns IntPtr.Zero when AppKit/workspace/url is unavailable.</summary>
    public static IntPtr URLsForApplicationsToOpenURL(IntPtr workspace, IntPtr url)
    {
        if (workspace == IntPtr.Zero || url == IntPtr.Zero)
            return IntPtr.Zero;
        return SendIntPtr_IntPtr(workspace, Sel("URLsForApplicationsToOpenURL:"), url);
    }

    /// <summary>[workspace URLForApplicationToOpenURL:url] — the NSURL of the DEFAULT handler for the
    /// URL, or IntPtr.Zero. Autoreleased; do NOT release.</summary>
    public static IntPtr URLForApplicationToOpenURL(IntPtr workspace, IntPtr url)
    {
        if (workspace == IntPtr.Zero || url == IntPtr.Zero)
            return IntPtr.Zero;
        return SendIntPtr_IntPtr(workspace, Sel("URLForApplicationToOpenURL:"), url);
    }

    // ------------------------------------------------------------------
    //  NSArray
    // ------------------------------------------------------------------

    public static int NSArrayCount(IntPtr array)
    {
        if (array == IntPtr.Zero)
            return 0;
        return SendInt(array, Sel("count"));
    }

    public static IntPtr NSArrayObjectAtIndex(IntPtr array, int index)
    {
        if (array == IntPtr.Zero)
            return IntPtr.Zero;
        return SendIntPtr_IntPtr(array, Sel("objectAtIndex:"), (IntPtr)index);
    }

    // ------------------------------------------------------------------
    //  NSRunningApplication
    // ------------------------------------------------------------------

    /// <summary>Returns [app bundleIdentifier] as an NSString, or IntPtr.Zero.</summary>
    public static IntPtr RunningAppBundleIdentifier(IntPtr app)
        => SendIntPtr(app, Sel("bundleIdentifier"));

    /// <summary>Returns [app localizedName] as an NSString, or IntPtr.Zero.</summary>
    public static IntPtr RunningAppLocalizedName(IntPtr app)
        => SendIntPtr(app, Sel("localizedName"));

    /// <summary>Returns [app processIdentifier] (pid_t).</summary>
    public static int RunningAppProcessIdentifier(IntPtr app)
        => SendInt(app, Sel("processIdentifier"));

    // ------------------------------------------------------------------
    //  NSString → C# string
    // ------------------------------------------------------------------

    /// <summary>Converts an NSString instance to a managed string. Returns null for IntPtr.Zero.</summary>
    public static string? NSStringToString(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero)
            return null;

        var utf8 = SendIntPtr(nsString, Sel("UTF8String"));
        if (utf8 == IntPtr.Zero)
            return null;

        return Marshal.PtrToStringUTF8(utf8);
    }

    // ------------------------------------------------------------------
    //  NSURL
    // ------------------------------------------------------------------

    /// <summary>Returns [nsurl path] as an NSString, or IntPtr.Zero.</summary>
    public static IntPtr NSURLPath(IntPtr url)
        => SendIntPtr(url, Sel("path"));

    /// <summary>Creates a file NSURL via <c>[NSURL fileURLWithPath:]</c>. Unlike <see cref="NSStringCreate"/>
    /// this returns an AUTORELEASED object — the caller must NOT release it (releasing it would double-free;
    /// cf. the bevel-fo2 hazard note on NSStringCreate). Returns IntPtr.Zero if AppKit is unavailable.</summary>
    public static IntPtr FileUrl(string path)
    {
        var cls = GetClass("NSURL");
        if (cls == IntPtr.Zero)
            return IntPtr.Zero;

        var nsPath = NSStringCreate(path);   // +1 owned — released below
        if (nsPath == IntPtr.Zero)
            return IntPtr.Zero;
        try { return SendIntPtr_IntPtr(cls, Sel("fileURLWithPath:"), nsPath); }
        finally { SendVoid(nsPath, Sel("release")); }
    }

    // ------------------------------------------------------------------
    //  NSString factory
    // ------------------------------------------------------------------

    /// <summary>
    /// Creates an NSString via <c>[[NSString alloc] initWithUTF8String:]</c> — a +1 OWNED instance
    /// with no pending autorelease, so it is safe to use on any thread with no ambient autorelease
    /// pool (a background icon render, say). The caller MUST <c>release</c> it exactly once when done.
    /// (Was <c>+stringWithUTF8String:</c>, which returns an AUTORELEASED string the caller does not
    /// own — callers that released it double-freed real heap strings; bevel-fo2.)
    /// </summary>
    public static IntPtr NSStringCreate(string str)
    {
        var cls = GetClass("NSString");
        if (cls == IntPtr.Zero)
            return IntPtr.Zero;

        var utf8Ptr = Marshal.StringToHGlobalAnsi(str);
        try
        {
            var alloced = SendIntPtr(cls, Sel("alloc"));
            if (alloced == IntPtr.Zero)
                return IntPtr.Zero;
            return SendIntPtr_IntPtr(alloced, Sel("initWithUTF8String:"), utf8Ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(utf8Ptr);
        }
    }

    // ------------------------------------------------------------------
    //  CFURL / LaunchServices (C API — no AppKit required)
    // ------------------------------------------------------------------

    /// <summary>
    /// Opens an application (or file) at the given URL using LaunchServices.
    /// Returns 0 on success (noErr). Handles single-instance semantics correctly.
    /// </summary>
    [DllImport(Frameworks.CoreServices)]
    public static extern int LSOpenCFURLRef(IntPtr url, out IntPtr launchedURL);

    /// <summary>
    /// Creates a CFURLRef from a file path and opens it via LaunchServices.
    /// This is the primary launch mechanism — works without AppKit.
    /// </summary>
    public static bool LaunchApplication(string appPath)
    {
        var pathBytes = System.Text.Encoding.UTF8.GetBytes(appPath);
        var isDirectory = Directory.Exists(appPath) || appPath.EndsWith(".app", StringComparison.OrdinalIgnoreCase);

        var url = CFURLCreateFromFileSystemRepresentation(IntPtr.Zero, pathBytes, (IntPtr)pathBytes.Length, isDirectory);
        if (url == IntPtr.Zero)
            return false;

        try
        {
            var result = LSOpenCFURLRef(url, out var _);
            return result == 0; // noErr
        }
        finally
        {
            CFRelease(url);
        }
    }

    // ── NSScreen ────────────────────────────────────────────────────────

    /// <summary>NSRect: 4 doubles (origin.x, origin.y, size.width, size.height).
    /// On ARM64 this is an HFA returned in v0–v3 by plain objc_msgSend — no _stret.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NSRect
    {
        public double X, Y, Width, Height;
    }

    /// <summary>NSEdgeInsets: 4 doubles (top, left, bottom, right). Like <see cref="NSRect"/> a 4-double
    /// HFA — returned in v0–v3 by plain objc_msgSend on ARM64.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NSEdgeInsets
    {
        public double Top, Left, Bottom, Right;
    }

    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")]
    private static extern NSRect SendNSRect(IntPtr receiver, IntPtr selector);

    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")]
    private static extern NSEdgeInsets SendNSEdgeInsets(IntPtr receiver, IntPtr selector);

    /// <summary>Returns [NSScreen screens] as an NSArray.</summary>
    public static IntPtr NSScreenScreens()
    {
        EnsureAppKitLoaded();
        var cls = GetClass("NSScreen");
        return cls == IntPtr.Zero ? IntPtr.Zero : SendIntPtr(cls, Sel("screens"));
    }

    /// <summary>Returns [screen frame] in AppKit coordinates (origin bottom-left).</summary>
    public static (int X, int Y, int Width, int Height) NSScreenFrame(IntPtr screen)
    {
        if (screen == IntPtr.Zero) return (0, 0, 0, 0);
        var r = SendNSRect(screen, Sel("frame"));
        return ((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
    }

    /// <summary>Returns [screen visibleFrame] (frame minus menu bar and Dock).</summary>
    public static (int X, int Y, int Width, int Height) NSScreenVisibleFrame(IntPtr screen)
    {
        if (screen == IntPtr.Zero) return (0, 0, 0, 0);
        var r = SendNSRect(screen, Sel("visibleFrame"));
        return ((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
    }

    /// <summary>The menu-bar strip height in points for the given screen: the notch/safe-area top inset
    /// when notched (<c>[screen safeAreaInsets].top</c> > 0), else <c>[[NSStatusBar systemStatusBar] thickness]</c>.
    /// Matches the PoC's height derivation. Returns 0 if AppKit/screen is unavailable.</summary>
    public static double MenuBarHeight(IntPtr screen)
    {
        if (screen == IntPtr.Zero) return 0;
        var insets = SendNSEdgeInsets(screen, Sel("safeAreaInsets"));
        if (insets.Top > 0) return insets.Top;
        var statusBar = SendIntPtr(GetClass("NSStatusBar"), Sel("systemStatusBar"));
        if (statusBar == IntPtr.Zero) return 0;
        return SendDouble(statusBar, Sel("thickness"));
    }

    // ------------------------------------------------------------------
    //  Autorelease pool — for ObjC work on threadpool threads, which have
    //  no ambient pool, so autoreleased objects would otherwise accumulate
    //  until the thread dies (bevel-fo2 class). Push before, Pop in finally.
    // ------------------------------------------------------------------

    [DllImport(Frameworks.ObjC)] public static extern IntPtr objc_autoreleasePoolPush();
    [DllImport(Frameworks.ObjC)] public static extern void objc_autoreleasePoolPop(IntPtr pool);
}