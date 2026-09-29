using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Bevel.UI.Native;

/// <summary>
/// The raw ObjC primitives shared by Bevel's window chrome (bevel-hgg6).
///
/// <b>Why here.</b> <c>TaskbarWindow</c> and <c>DesktopWindow</c> both derive from
/// <see cref="BevelWindow"/> in this project, and both need the same handful of <c>objc_msgSend</c>
/// shapes to set a window's level and collection behaviour. They each declared their own private copies —
/// four symbols were name-for-name identical across the two files, and the libobjc path was written as a
/// literal 17 times between them.
///
/// <c>Bevel.Pal.MacOS.AppKitInterop</c> already owns a fuller version of this family, but neither
/// <c>Bevel.Taskbar</c> nor <c>Bevel.Desktop</c> references that project and they must not: a UI project
/// depending on a concrete platform PAL inverts the PAL layering, and the macOS PAL would then be pulled
/// into every platform's UI build. <see cref="BevelWindow"/>'s own project is the one place both
/// consumers already share, and it already contains macOS-specific window work.
///
/// The consequence, stated rather than hidden: these few primitives exist in two layers — here for
/// window chrome, and in <c>AppKitInterop</c> for the PAL. That is the price of the layering, and it is
/// bounded to this file. If the overlap grows past window-level calls, the next step is a dependency-free
/// leaf project both layers can reference, not a reference from either of these to the other.
/// </summary>
public static class MacObjC
{
    /// <summary>libobjc's path, written once. Previously a literal in 17 DllImports across two projects.</summary>
    public const string ObjC = "/usr/lib/libobjc.dylib";

    /// <summary>libSystem's path, for <c>dlsym</c>-style lookups.</summary>
    public const string LibSystem = "/usr/lib/libSystem.dylib";

    // ── The shapes both windows need ──────────────────────────────────────

    /// <summary>objc_msgSend with one 32-bit integer arg (e.g. <c>setLevel:</c>,
    /// <c>setCollectionBehavior:</c>).</summary>
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern void SendVoid_Int(IntPtr receiver, IntPtr selector, int arg);

    /// <summary>objc_msgSend returning an object (e.g. <c>window</c>).</summary>
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendIntPtr(IntPtr receiver, IntPtr selector);

    /// <summary>objc_msgSend returning ObjC BOOL as a raw byte, with one selector arg (e.g.
    /// <c>respondsToSelector:</c>). Declared as <c>byte</c> rather than <c>bool</c> deliberately: ObjC
    /// BOOL is one byte, and a bare C# <c>bool</c> would marshal as a 4-byte Win32 BOOL and read three
    /// undefined bytes. Callers compare against 0.</summary>
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern byte SendByte_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

    // ── Selectors ─────────────────────────────────────────────────────────

    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern IntPtr sel_registerName(string name);

    /// <summary>Registers (and caches) an ObjC selector. Concurrent because window chrome resolves
    /// selectors from more than one thread; the two previous copies used a plain Dictionary and a
    /// static-ctor-populated field set respectively.</summary>
    public static IntPtr Sel(string name) => _selectors.GetOrAdd(name, static n => sel_registerName(n));

    private static readonly ConcurrentDictionary<string, IntPtr> _selectors = new();
}
