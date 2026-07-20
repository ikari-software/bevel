using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Bevel.Pal.MacOS;

/// <summary>
/// Inbound Apple Events tier 1 (08-os-interop.md §2.1.2 / bevel-376): receive the custom scripting
/// verbs (<c>tell application "Bevel" to reveal/delete/duplicate/make …</c>) into this process. A
/// handler class is created at runtime and its method IMP is a managed <see cref="UnmanagedCallersOnly"/>
/// callback registered with <c>NSAppleEventManager</c>. Delivery is proven in <c>native/ae-probe</c>
/// and validated under Avalonia's loop in <c>native/ae-probe2</c>; no custom run loop here.
///
/// <para>The direct object is parsed into literal file paths AND object specifiers
/// (<see cref="AeSpecifier"/>, e.g. <c>folder "x" of home</c>, <c>every file … whose …</c>); the app
/// converts the specifiers to the command model's ObjectSpecifier and resolves them. Only the CUSTOM
/// verbs are claimed — <c>open</c>/<c>odoc</c> stays with Avalonia's File activation.</para>
/// </summary>
public static unsafe partial class AppleEventInbound
{
    public enum Verb { Reveal, Delete, Duplicate, Make, Move }

    /// <summary>A parsed inbound request. For reveal/delete/duplicate: <see cref="Paths"/> (literal
    /// files) + <see cref="Specifiers"/> (descriptive references) are the targets. For make:
    /// <see cref="Container"/> + <see cref="Name"/>.</summary>
    public sealed record AeRequest(
        Verb Verb,
        IReadOnlyList<string> Paths,
        IReadOnlyList<AeSpecifier> Specifiers,
        AeSpecifier? Container,
        string? Name);

    /// <summary>Wired by the app. Invoked on the AE dispatch thread (the UI thread under Avalonia).</summary>
    public static Action<AeRequest>? Handler;

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
    static readonly uint typeAEList = FourCC("list");
    private static bool _installed;

    private static readonly string? TracePath =
        Environment.GetEnvironmentVariable("BEVEL_AE_TRACE") == "1"
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "bevel-ae.log")
            : null;

    static void Trace(string s)
    {
        if (TracePath is not null)
            try { System.IO.File.AppendAllText(TracePath, $"{DateTime.Now:HH:mm:ss.fff} {s}\n"); } catch { }
    }

    /// <summary>Registers the inbound handlers. Idempotent; no-op off macOS.</summary>
    public static void Install()
    {
        if (_installed || !OperatingSystem.IsMacOS()) return;
        AppKitInterop.EnsureAppKitLoaded();

        var handlerCls = objc_allocateClassPair(Cls("NSObject"), "BevelAppleEventHandler", 0);
        if (handlerCls == IntPtr.Zero) return;
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
        Register("core", "crel");   // make
        Register("core", "move");   // move
        Register("core", "getd");   // get   (object-model plumbing, bevel-3i4)
        Register("core", "cnte");   // count

        _installed = true;
    }

    [UnmanagedCallersOnly]
    static void HandleEvent(IntPtr self, IntPtr cmd, IntPtr evt, IntPtr reply)
    {
        try
        {
            var cls = SendU32(evt, Sel("eventClass"));
            var id = SendU32(evt, Sel("eventID"));

            // Query verbs reply synchronously in-place (bevel-3i4).
            if (cls == FourCC("core") && id == FourCC("getd")) { HandleQuery(isCount: false, evt, reply); return; }
            if (cls == FourCC("core") && id == FourCC("cnte")) { HandleQuery(isCount: true, evt, reply); return; }

            var verb = VerbFor(cls, id);
            if (verb is null) return;

            var request = verb switch
            {
                Verb.Make => BuildMake(evt),
                Verb.Move => BuildMove(evt),
                _ => BuildTargets(verb.Value, evt),
            };
            Trace($"AE {verb}: paths={request.Paths.Count} specs={request.Specifiers.Count} name={request.Name}");
            Handler?.Invoke(request);
        }
        catch (Exception ex) { Trace($"EXCEPTION: {ex.Message}"); }
    }

    static Verb? VerbFor(uint cls, uint id) => (cls, id) switch
    {
        _ when cls == FourCC("misc") && id == FourCC("mvis") => Verb.Reveal,
        _ when cls == FourCC("core") && id == FourCC("delo") => Verb.Delete,
        _ when cls == FourCC("core") && id == FourCC("clon") => Verb.Duplicate,
        _ when cls == FourCC("core") && id == FourCC("crel") => Verb.Make,
        _ when cls == FourCC("core") && id == FourCC("move") => Verb.Move,
        _ => null,
    };

    static (List<string> Paths, List<AeSpecifier> Specifiers) ParseTargets(IntPtr evt)
    {
        var direct = Send_u32(evt, Sel("paramDescriptorForKeyword:"), keyDirectObject);
        var paths = new List<string>();
        var specs = new List<AeSpecifier>();

        void Handle(IntPtr d)
        {
            if (ParseSpecifier(d) is { } spec) specs.Add(spec);
            else AddPath(paths, d);
        }

        if (direct != IntPtr.Zero)
        {
            // Only a real AE list is iterated; an object specifier is itself a record whose
            // numberOfItems counts its internal fields — parse it whole.
            if (SendU32(direct, Sel("descriptorType")) == typeAEList)
            {
                var count = SendNInt(direct, Sel("numberOfItems"));
                for (nint i = 1; i <= count; i++) Handle(Send_nint(direct, Sel("descriptorAtIndex:"), i));
            }
            else
            {
                Handle(direct);
            }
        }
        return (paths, specs);
    }

    static AeRequest BuildTargets(Verb verb, IntPtr evt)
    {
        var (paths, specs) = ParseTargets(evt);
        return new AeRequest(verb, paths, specs, null, null);
    }

    /// <summary>The <c>at</c>/<c>to</c> container: keyAEInsertHere ('insh'), possibly wrapped in an
    /// insertion location ('insl') whose 'kobj' is the container object.</summary>
    static AeSpecifier? ParseInsertionContainer(IntPtr evt)
    {
        var insh = Send_u32(evt, Sel("paramDescriptorForKeyword:"), FourCC("insh"));
        var containerDesc = insh;
        if (insh != IntPtr.Zero && SendU32(insh, Sel("descriptorType")) == FourCC("insl"))
            containerDesc = DescFor(insh, "kobj");
        return ParseSpecifier(containerDesc);
    }

    static AeRequest BuildMove(IntPtr evt)
    {
        var (paths, specs) = ParseTargets(evt);
        return new AeRequest(Verb.Move, paths, specs, ParseInsertionContainer(evt), null);
    }

    static AeRequest BuildMake(IntPtr evt)
    {
        var container = ParseInsertionContainer(evt);

        // `with properties {name:…}`: keyAEPropData ('prdt') record → pName ('pnam').
        var prdt = Send_u32(evt, Sel("paramDescriptorForKeyword:"), FourCC("prdt"));
        string? name = null;
        if (prdt != IntPtr.Zero)
        {
            var pnam = DescFor(prdt, "pnam");
            if (pnam != IntPtr.Zero) name = NSStr(Send(pnam, Sel("stringValue")));
        }
        return new AeRequest(Verb.Make, Array.Empty<string>(), Array.Empty<AeSpecifier>(), container, name);
    }

    static void AddPath(List<string> into, IntPtr desc)
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

    static string? NSStr(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero) return null;
        var utf8 = Send(nsString, Sel("UTF8String"));
        return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
    }
}
