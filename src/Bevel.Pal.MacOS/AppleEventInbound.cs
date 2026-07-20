using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Bevel.Pal.MacOS;

/// <summary>
/// Inbound Apple Events tier 1 (08-os-interop.md §2.1.2 / bevel-376): receive the custom scripting
/// verbs (<c>tell application "Bevel" to reveal/delete/duplicate …</c>) into this process. A handler
/// class is created at runtime and its method IMP is a managed <see cref="UnmanagedCallersOnly"/>
/// callback, registered with <c>NSAppleEventManager</c>. Delivery is proven in <c>native/ae-probe</c>;
/// here we do NOT run our own loop — Avalonia's <c>[NSApp run]</c> services the AE Mach port (its
/// OpenUri activation already proves it), so registration alone suffices.
///
/// <para>Only the CUSTOM verbs are claimed. <c>open</c>/<c>odoc</c> stays with Avalonia's File
/// activation; the standard app events (<c>oapp</c>, <c>quit</c>) stay with NSApp.</para>
/// </summary>
public static unsafe class AppleEventInbound
{
    public enum Verb { Reveal, Delete, Duplicate }

    /// <summary>Wired by the app: given a verb and the direct object's POSIX file paths, act. Invoked
    /// on the AE dispatch thread (the UI thread under Avalonia's loop).</summary>
    public static Action<Verb, IReadOnlyList<string>>? Handler;

    private const string Obj = "/usr/lib/libobjc.dylib";
    [DllImport(Obj)] static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nint extraBytes);
    [DllImport(Obj)] static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(Obj)] static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
    [DllImport(Obj, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr r, IntPtr s);
    [DllImport(Obj, EntryPoint = "objc_msgSend")] static extern uint SendU32(IntPtr r, IntPtr s);
    [DllImport(Obj, EntryPoint = "objc_msgSend")] static extern nint SendNInt(IntPtr r, IntPtr s);
    [DllImport(Obj, EntryPoint = "objc_msgSend")] static extern IntPtr Send_u32(IntPtr r, IntPtr s, uint a);
    [DllImport(Obj, EntryPoint = "objc_msgSend")] static extern IntPtr Send_nint(IntPtr r, IntPtr s, nint a);
    [DllImport(Obj, EntryPoint = "objc_msgSend")]
    static extern void SendSetHandler(IntPtr r, IntPtr s, IntPtr handler, IntPtr sel, uint cls, uint id);

    static IntPtr Cls(string n) => AppKitInterop.GetClass(n);
    static IntPtr Sel(string n) => AppKitInterop.Sel(n);
    static uint FourCC(string s) => ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3];

    static readonly uint keyDirectObject = FourCC("----");
    static readonly uint typeFileURL = FourCC("furl");
    private static bool _installed;

    /// <summary>Registers the inbound handlers. Idempotent; no-op off macOS.</summary>
    public static void Install()
    {
        if (_installed || !OperatingSystem.IsMacOS()) return;
        AppKitInterop.EnsureAppKitLoaded();

        var handlerCls = objc_allocateClassPair(Cls("NSObject"), "BevelAppleEventHandler", 0);
        if (handlerCls == IntPtr.Zero) return;   // already registered (e.g. a prior Install)
        var handleSel = Sel("handleAppleEvent:withReplyEvent:");
        var imp = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&HandleEvent;
        if (!class_addMethod(handlerCls, handleSel, imp, "v@:@@")) return;
        objc_registerClassPair(handlerCls);
        var handler = Send(Send(handlerCls, Sel("alloc")), Sel("init"));

        var mgr = Send(Cls("NSAppleEventManager"), Sel("sharedAppleEventManager"));
        var setSel = Sel("setEventHandler:andSelector:forEventClass:andEventID:");
        void Register(string cls, string id) => SendSetHandler(mgr, setSel, handler, handleSel, FourCC(cls), FourCC(id));
        Register("misc", "mvis");   // reveal
        Register("core", "delo");   // delete
        Register("core", "clon");   // duplicate

        _installed = true;
    }

    // Opt-in diagnostic trace of received Apple Events (BEVEL_AE_TRACE=1 → ~/bevel-ae.log). Low
    // frequency (user-initiated scripts); off by default.
    private static readonly string? TracePath =
        Environment.GetEnvironmentVariable("BEVEL_AE_TRACE") == "1"
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "bevel-ae.log")
            : null;

    static void Trace(string s)
    {
        if (TracePath is not null)
            try { System.IO.File.AppendAllText(TracePath, $"{DateTime.Now:HH:mm:ss.fff} {s}\n"); } catch { }
    }

    [UnmanagedCallersOnly]
    static void HandleEvent(IntPtr self, IntPtr cmd, IntPtr evt, IntPtr reply)
    {
        try
        {
            var verb = VerbFor(SendU32(evt, Sel("eventClass")), SendU32(evt, Sel("eventID")));
            Trace($"AE received: verb={verb?.ToString() ?? "(unhandled)"}");
            if (verb is null) return;
            var paths = ExtractPaths(evt);
            Trace($"  paths=[{string.Join(", ", paths)}]");
            if (paths.Count > 0) Handler?.Invoke(verb.Value, paths);
        }
        catch (Exception ex) { Trace($"  EXCEPTION: {ex.Message}"); }
    }

    static Verb? VerbFor(uint cls, uint id) => (cls, id) switch
    {
        _ when cls == FourCC("misc") && id == FourCC("mvis") => Verb.Reveal,
        _ when cls == FourCC("core") && id == FourCC("delo") => Verb.Delete,
        _ when cls == FourCC("core") && id == FourCC("clon") => Verb.Duplicate,
        _ => null,
    };

    static IReadOnlyList<string> ExtractPaths(IntPtr evt)
    {
        var direct = Send_u32(evt, Sel("paramDescriptorForKeyword:"), keyDirectObject);
        if (direct == IntPtr.Zero) return Array.Empty<string>();

        var paths = new List<string>();
        var count = SendNInt(direct, Sel("numberOfItems"));
        if (count > 0)
            for (nint i = 1; i <= count; i++)
                Add(paths, Send_nint(direct, Sel("descriptorAtIndex:"), i));
        else
            Add(paths, direct);
        return paths;
    }

    static void Add(List<string> into, IntPtr desc)
    {
        if (desc == IntPtr.Zero) return;
        var url = Send_u32(desc, Sel("coerceToDescriptorType:"), typeFileURL);
        if (url == IntPtr.Zero) return;
        var data = Send(url, Sel("data"));
        if (data == IntPtr.Zero) return;
        var len = (int)SendNInt(data, Sel("length"));
        var bytes = Send(data, Sel("bytes"));
        if (bytes == IntPtr.Zero || len <= 0) return;
        var s = Marshal.PtrToStringUTF8(bytes, len);
        if (Uri.TryCreate(s, UriKind.Absolute, out var u) && u.IsFile) into.Add(u.LocalPath);
    }
}
