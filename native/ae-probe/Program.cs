using System;
using System.IO;
using System.Runtime.InteropServices;

// Faceless Apple-Events probe (M4-C native / bevel-376). Registers an NSAppleEventManager handler
// through a runtime-created ObjC class and logs every event it receives + the extracted direct
// object, so the interop mechanism can be proven with osascript before porting into Bevel.App.

AeProbe.Run();

static unsafe class AeProbe
{
    private const string Obj = "/usr/lib/libobjc.dylib";
    private static readonly string LogPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ae-probe.log");

    // ── objc runtime ────────────────────────────────────────────────────────
    [DllImport(Obj)] static extern IntPtr objc_getClass(string name);
    [DllImport(Obj)] static extern IntPtr sel_registerName(string name);
    [DllImport(Obj)] static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nint extraBytes);
    [DllImport(Obj)] static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(Obj)] static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

    [DllImport(Obj, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr r, IntPtr s);
    [DllImport(Obj, EntryPoint = "objc_msgSend")] static extern uint SendU32(IntPtr r, IntPtr s);
    [DllImport(Obj, EntryPoint = "objc_msgSend")] static extern nint SendNInt(IntPtr r, IntPtr s);
    [DllImport(Obj, EntryPoint = "objc_msgSend")] static extern IntPtr Send_u32(IntPtr r, IntPtr s, uint a);
    [DllImport(Obj, EntryPoint = "objc_msgSend")] static extern IntPtr Send_nint(IntPtr r, IntPtr s, nint a);
    [DllImport(Obj, EntryPoint = "objc_msgSend")] static extern void Send_nint_v(IntPtr r, IntPtr s, nint a);
    [DllImport(Obj, EntryPoint = "objc_msgSend")]
    static extern void SendSetHandler(IntPtr r, IntPtr s, IntPtr handler, IntPtr sel, uint cls, uint id);

    [DllImport(Obj, EntryPoint = "objc_msgSend")]
    static extern IntPtr SendNextEvent(IntPtr r, IntPtr s, ulong mask, IntPtr date, IntPtr mode, bool deq);
    [DllImport(Obj, EntryPoint = "objc_msgSend")]
    static extern IntPtr Send_ptr(IntPtr r, IntPtr s, IntPtr a);
    [DllImport(Obj, EntryPoint = "objc_msgSend")]
    static extern void Send_pp(IntPtr r, IntPtr s, IntPtr a, IntPtr b);

    [DllImport("/System/Library/Frameworks/Carbon.framework/Carbon")]
    static extern void RunApplicationEventLoop();

    static IntPtr NSString(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s + "\0");
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        try { return Send_ptr(Send(Cls("NSString"), Sel("alloc")), Sel("initWithUTF8String:"), ptr); }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    static IntPtr Cls(string n) => objc_getClass(n);
    static IntPtr Sel(string n) => sel_registerName(n);

    // FourCharCode packing (big-endian, as AE codes are written).
    static uint FourCC(string s) => ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3];
    static string FourCCStr(uint v) => new(new[] { (char)(v >> 24), (char)((v >> 16) & 0xFF), (char)((v >> 8) & 0xFF), (char)(v & 0xFF) });

    // AE constants.
    static readonly uint keyDirectObject = FourCC("----");
    static readonly uint typeFileURL = FourCC("furl");
    static readonly uint typeUTF8Text = FourCC("utf8");

    static void Log(string s) => File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff ") + s + "\n");

    public static void Run()
    {
        Log($"=== aeprobe starting (pid {Environment.ProcessId}) ===");

        // 1. Create an ObjC handler class with -handleEvent:withReplyEvent:.
        var handlerCls = objc_allocateClassPair(Cls("NSObject"), "BevelAEProbeHandler", 0);
        var handleSel = Sel("handleEvent:withReplyEvent:");
        var imp = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&HandleEvent;
        if (!class_addMethod(handlerCls, handleSel, imp, "v@:@@")) { Log("class_addMethod FAILED"); return; }
        objc_registerClassPair(handlerCls);
        var handler = Send(Send(handlerCls, Sel("alloc")), Sel("init"));
        Log($"handler class + instance ready (handler={handler:X})");

        // 2. Bring up NSApp and its Apple Event Mach port. finishLaunching (not full `run`) installs
        //    the port + NSApp's default handlers; [NSApp run] returns immediately under LaunchServices,
        //    so we drive CFRunLoop ourselves below.
        var app = Send(Cls("NSApplication"), Sel("sharedApplication"));
        Send_nint_v(app, Sel("setActivationPolicy:"), 1 /* NSApplicationActivationPolicyAccessory */);
        Send(app, Sel("finishLaunching"));

        // 3. Register our custom-verb handlers AFTER finishLaunching so NSApp can't clobber them.
        var mgr = Send(Cls("NSAppleEventManager"), Sel("sharedAppleEventManager"));
        var setSel = Sel("setEventHandler:andSelector:forEventClass:andEventID:");
        void Register(string cls, string id, string label)
        {
            SendSetHandler(mgr, setSel, handler, handleSel, FourCC(cls), FourCC(id));
            Log($"registered {label} ({cls}/{id})");
        }
        Register("misc", "mvis", "reveal");           // Finder-style reveal
        Register("core", "crel", "make");             // make new folder

        // Self-test: call the handler directly (nil args are safe to message in ObjC) to prove the
        // dynamically-created class + IMP dispatch works, independent of AE delivery.
        Log("self-test: direct handler invocation");
        Send_pp(handler, handleSel, IntPtr.Zero, IntPtr.Zero);
        Log("self-test: returned");

        // 4. Carbon's RunApplicationEventLoop — the canonical loop that dispatches inbound Apple
        //    Events (it services the AE Mach port), where [NSApp run] returned immediately and a bare
        //    CFRunLoop didn't process the AE queue.
        Log("entering RunApplicationEventLoop");
        RunApplicationEventLoop();
        Log($"!! RunApplicationEventLoop RETURNED (pid {Environment.ProcessId})");
    }

    [UnmanagedCallersOnly]
    static void HandleEvent(IntPtr self, IntPtr cmd, IntPtr evt, IntPtr reply)
    {
        try
        {
            var cls = SendU32(evt, Sel("eventClass"));
            var id = SendU32(evt, Sel("eventID"));
            Log($"EVENT {FourCCStr(cls)}/{FourCCStr(id)}");

            var direct = Send_u32(evt, Sel("paramDescriptorForKeyword:"), keyDirectObject);
            if (direct == IntPtr.Zero) { Log("  (no direct object)"); return; }

            var count = SendNInt(direct, Sel("numberOfItems"));
            if (count > 0)
                for (nint i = 1; i <= count; i++)
                    Log($"  item[{i}] = {PathOf(Send_nint(direct, Sel("descriptorAtIndex:"), i))}");
            else
                Log($"  direct = {PathOf(direct)}");
        }
        catch (Exception ex) { Log($"  HANDLER EXCEPTION: {ex.Message}"); }
    }

    // Coerce a descriptor to a file URL and return its POSIX path. A typeFileURL descriptor's data
    // IS the file:// URL bytes; parse them to a POSIX path (stringValue would give an HFS path).
    static string PathOf(IntPtr desc)
    {
        if (desc == IntPtr.Zero) return "(null)";
        var url = Send_u32(desc, Sel("coerceToDescriptorType:"), typeFileURL);
        if (url != IntPtr.Zero)
        {
            var data = Send(url, Sel("data"));   // NSData with the file:// URL bytes
            if (data != IntPtr.Zero)
            {
                var len = (int)SendNInt(data, Sel("length"));
                var bytes = Send(data, Sel("bytes"));
                if (bytes != IntPtr.Zero && len > 0)
                {
                    var s = Marshal.PtrToStringUTF8(bytes, len);
                    if (Uri.TryCreate(s, UriKind.Absolute, out var u) && u.IsFile) return u.LocalPath;
                    return s;
                }
            }
        }
        var txt = Send_u32(desc, Sel("coerceToDescriptorType:"), typeUTF8Text);
        return NSStr(Send(txt == IntPtr.Zero ? desc : txt, Sel("stringValue"))) ?? "(uncoercible)";
    }

    static string? NSStr(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero) return null;
        var utf8 = Send(nsString, Sel("UTF8String"));
        return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
    }
}
