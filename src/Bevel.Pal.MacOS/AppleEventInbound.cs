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
    public enum Verb { Reveal, Delete, Duplicate, Make, Move, SetSelection }

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

    [DllImport(Frameworks.ObjC)] static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nint extraBytes);
    [DllImport(Frameworks.ObjC)] static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(Frameworks.ObjC)] static extern void objc_disposeClassPair(IntPtr cls);
    // ObjC BOOL is a single signed byte; without I1 the default 4-byte marshalling reads three bytes
    // of stack garbage above AL and can misread success as failure (bevel-376 review).
    [DllImport(Frameworks.ObjC)] [return: MarshalAs(UnmanagedType.I1)] static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr r, IntPtr s);
    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")] static extern uint SendU32(IntPtr r, IntPtr s);
    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send_u32(IntPtr r, IntPtr s, uint a);
    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send_nint(IntPtr r, IntPtr s, nint a);
    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")]
    static extern void SendSetHandler(IntPtr r, IntPtr s, IntPtr handler, IntPtr sel, uint cls, uint id);

    static IntPtr Cls(string n) => AppKitInterop.GetClass(n);
    static IntPtr Sel(string n) => AppKitInterop.Sel(n);
    static uint FourCC(string s) => ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3];

    static readonly uint keyDirectObject = FourCC("----");
    static readonly uint keyErrorNumber = FourCC("errn");   // keyErrorNumber — reply's error code slot
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
        // Dispose the not-yet-registered pair on failure so a retry can re-allocate the same name;
        // leaving it allocated makes objc_allocateClassPair return null forever after, permanently and
        // silently disabling AE handling (bevel-376 review).
        if (!class_addMethod(handlerCls, handleSel, imp, "v@:@@")) { objc_disposeClassPair(handlerCls); return; }
        objc_registerClassPair(handlerCls);
        var handler = Send(Send(handlerCls, Sel("alloc")), Sel("init"));

        var mgr = Send(Cls("NSAppleEventManager"), Sel("sharedAppleEventManager"));
        var setSel = Sel("setEventHandler:andSelector:forEventClass:andEventID:");
        void Register(string cls, string id) => SendSetHandler(mgr, setSel, handler, handleSel, FourCC(cls), FourCC(id));
        Register("misc", "mvis");   // reveal
        Register("misc", "slct");   // select {items}  (Finder-style; sdef `select` command)
        Register("core", "delo");   // delete
        Register("core", "clon");   // duplicate
        Register("core", "crel");   // make
        Register("core", "move");   // move
        Register("core", "getd");   // get    (object-model plumbing, bevel-3i4)
        Register("core", "cnte");   // count
        Register("core", "doex");   // exists
        Register("core", "setd");   // set    (set selection to …)

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
            if (cls == FourCC("core") && id == FourCC("getd")) { HandleQuery(QueryOp.Get, evt, reply); return; }
            if (cls == FourCC("core") && id == FourCC("cnte")) { HandleQuery(QueryOp.Count, evt, reply); return; }
            if (cls == FourCC("core") && id == FourCC("doex")) { HandleQuery(QueryOp.Exists, evt, reply); return; }
            if (cls == FourCC("core") && id == FourCC("setd")) { HandleSet(evt, reply); return; }
            // `select {items}` carries its targets in the direct object (not keyAEData like `set selection to`).
            if (cls == FourCC("misc") && id == FourCC("slct")) { Handler?.Invoke(BuildTargets(Verb.SetSelection, evt)); return; }

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

    static (List<string> Paths, List<AeSpecifier> Specifiers) ParseTargets(IntPtr evt) =>
        ParseTargetsFrom(Send_u32(evt, Sel("paramDescriptorForKeyword:"), keyDirectObject));

    static (List<string> Paths, List<AeSpecifier> Specifiers) ParseTargetsFrom(IntPtr direct)
    {
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
                var count = AppKitInterop.SendNInt(direct, Sel("numberOfItems"));
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

    // `set <property> of <specifier> to <value>`. The direct object is the property SPECIFIER (what is
    // being set); route on its property key. Only `set selection to …` (prop 'sele') is applied in v1 —
    // previously EVERY setd was assumed to be set-selection, so `set bounds/current view/target of window`
    // silently clobbered the selection with the mis-parsed value (bevel-v6o). The sdef advertises
    // bounds/current view/target/index as writable, so an unsupported set answers errAEEventNotHandled
    // rather than a wrong no-op. Computed inline (no surface round-trip) so it can't reintroduce the
    // bevel-odf UI-thread freeze.
    static void HandleSet(IntPtr evt, IntPtr reply)
    {
        var direct = Send_u32(evt, Sel("paramDescriptorForKeyword:"), keyDirectObject);
        var form = direct != IntPtr.Zero ? SendU32(DescFor(direct, "form"), Sel("enumCodeValue")) : 0;
        var prop = form == FourCC("prop") ? SendU32(DescFor(direct, "seld"), Sel("typeCodeValue")) : 0;

        if (prop == FourCC("sele"))
        {
            Handler?.Invoke(BuildSetSelection(evt));
            return;
        }

        Trace($"AE setd: unsupported set target (prop=0x{prop:X8}) — replying errAEEventNotHandled");
        WriteErrorReply(reply, -1708);   // errAEEventNotHandled
    }

    /// <summary>Writes an error code into the reply's <c>keyErrorNumber</c> slot, so a script gets a real
    /// "can't set that" instead of a silent (wrong) success.</summary>
    static void WriteErrorReply(IntPtr reply, int errorNumber)
    {
        if (reply == IntPtr.Zero) return;
        var desc = Send_i32(AeDescClass, Sel("descriptorWithInt32:"), errorNumber);
        if (desc != IntPtr.Zero)
            SendVoid_ptru32(reply, Sel("setParamDescriptor:forKeyword:"), desc, keyErrorNumber);
    }

    // `set selection to <value>`: the new value is in keyAEData ('data').
    static AeRequest BuildSetSelection(IntPtr evt)
    {
        var (paths, specs) = ParseTargetsFrom(Send_u32(evt, Sel("paramDescriptorForKeyword:"), FourCC("data")));
        return new AeRequest(Verb.SetSelection, paths, specs, null, null);
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
        var len = (int)AppKitInterop.SendNInt(data, Sel("length"));
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
