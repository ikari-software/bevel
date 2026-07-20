using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Bevel.Pal.MacOS;

// Inbound AE object-model plumbing (bevel-3i4): get / count / exists resolve a query and write a
// RESULT into the reply descriptor. These reply synchronously inside the handler, so the app-side
// QueryHandler must compute from synchronously-readable state (known folders, the window registry,
// the active controller) — no async surface round-trip.

/// <summary>What a get/count/exists is asking for. Application-property queries are the common case;
/// filesystem-specifier get/count is carried as <see cref="Specifier"/>.</summary>
public enum AeQueryKind { Version, Home, Desktop, Trash, StartupDisk, Selection, WindowCount, ResolvePaths }

public sealed record AeQuery(AeQueryKind Kind, AeSpecifier? Specifier, bool IsCount);

/// <summary>The value to write back into the AE reply.</summary>
public abstract record AeResult;
public sealed record AeText(string Value) : AeResult;
public sealed record AeCount(int Value) : AeResult;
public sealed record AePath(string Value) : AeResult;                    // a single file/folder reference
public sealed record AePaths(IReadOnlyList<string> Values) : AeResult;   // a list of file references

public static partial class AppleEventInbound
{
    /// <summary>Wired by the app: synchronously answer a get/count/exists. Runs on the AE dispatch
    /// (UI) thread; must not await a surface round-trip.</summary>
    public static Func<AeQuery, AeResult?>? QueryHandler;

    [DllImport(Obj, EntryPoint = "objc_msgSend")] static extern IntPtr Send_ptr(IntPtr r, IntPtr s, IntPtr a);
    [DllImport(Obj, EntryPoint = "objc_msgSend")] static extern IntPtr Send_i32(IntPtr r, IntPtr s, int a);
    [DllImport(Obj, EntryPoint = "objc_msgSend")] static extern IntPtr Send_u32ptr(IntPtr r, IntPtr s, uint a, IntPtr b);
    [DllImport(Obj, EntryPoint = "objc_msgSend")] static extern IntPtr Send_ptrnint(IntPtr r, IntPtr s, IntPtr a, nint b);
    [DllImport(Obj, EntryPoint = "objc_msgSend")] static extern void SendVoid_ptru32(IntPtr r, IntPtr s, IntPtr a, uint b);
    [DllImport(Obj, EntryPoint = "objc_msgSend")] static extern void SendVoid_ptrnint(IntPtr r, IntPtr s, IntPtr a, nint b);

    static IntPtr AeDescClass => Cls("NSAppleEventDescriptor");

    /// <summary>Handles a get/count/exists event: parse the query, ask the app, write the reply.</summary>
    static void HandleQuery(bool isCount, IntPtr evt, IntPtr reply)
    {
        var query = ParseQuery(evt, isCount);
        if (query is null) return;
        var result = QueryHandler?.Invoke(query);
        if (result is not null) WriteReply(reply, result);
    }

    static AeQuery? ParseQuery(IntPtr evt, bool isCount)
    {
        // `count <class>` carries the class in keyAEObjectClass ('kocl'), not the direct object.
        if (isCount)
        {
            var kocl = Send_u32(evt, Sel("paramDescriptorForKeyword:"), FourCC("kocl"));
            if (kocl != IntPtr.Zero && SendU32(kocl, Sel("typeCodeValue")) == FourCC("cwin"))
                return new AeQuery(AeQueryKind.WindowCount, null, true);
        }

        var direct = Send_u32(evt, Sel("paramDescriptorForKeyword:"), keyDirectObject);
        if (direct == IntPtr.Zero || SendU32(direct, Sel("descriptorType")) != typeObjectSpecifier)
            return null;

        var want = SendU32(DescFor(direct, "want"), Sel("typeCodeValue"));
        var form = SendU32(DescFor(direct, "form"), Sel("enumCodeValue"));

        if (form == FourCC("prop"))
        {
            var prop = SendU32(DescFor(direct, "seld"), Sel("typeCodeValue"));
            var kind = prop switch
            {
                _ when prop == FourCC("vers") => (AeQueryKind?)AeQueryKind.Version,
                _ when prop == FourCC("home") => AeQueryKind.Home,
                _ when prop == FourCC("desk") => AeQueryKind.Desktop,
                _ when prop == FourCC("trsh") => AeQueryKind.Trash,
                _ when prop == FourCC("sdsk") => AeQueryKind.StartupDisk,
                _ when prop == FourCC("sele") => AeQueryKind.Selection,
                _ => null,
            };
            if (kind is { } k) return new AeQuery(k, null, isCount);
        }

        if (want == FourCC("cwin")) return new AeQuery(AeQueryKind.WindowCount, null, isCount);

        // A filesystem specifier: get returns the items, count returns how many.
        return ParseSpecifier(direct) is { } spec ? new AeQuery(AeQueryKind.ResolvePaths, spec, isCount) : null;
    }

    // ── Reply descriptor construction ──────────────────────────────────────────
    static void WriteReply(IntPtr reply, AeResult result)
    {
        if (reply == IntPtr.Zero) return;
        var desc = result switch
        {
            AeText t => TextDesc(t.Value),
            AeCount c => Send_i32(AeDescClass, Sel("descriptorWithInt32:"), c.Value),
            AePath p => FileUrlDesc(p.Value),
            AePaths p => PathListDesc(p.Values),
            _ => IntPtr.Zero,
        };
        if (desc != IntPtr.Zero)
            SendVoid_ptru32(reply, Sel("setParamDescriptor:forKeyword:"), desc, keyDirectObject);
    }

    static IntPtr TextDesc(string s) =>
        Send_ptr(AeDescClass, Sel("descriptorWithString:"), AppKitInterop.NSStringCreate(s));

    static IntPtr PathListDesc(IReadOnlyList<string> paths)
    {
        var list = Send(AeDescClass, Sel("listDescriptor"));
        nint i = 1;
        foreach (var p in paths)
        {
            var item = FileUrlDesc(p);
            if (item != IntPtr.Zero) SendVoid_ptrnint(list, Sel("insertDescriptor:atIndex:"), item, i++);
        }
        return list;
    }

    static IntPtr FileUrlDesc(string posixPath)
    {
        if (!Uri.TryCreate(posixPath, UriKind.Absolute, out var uri) &&
            !Uri.TryCreate("file://" + posixPath, UriKind.Absolute, out uri))
            return IntPtr.Zero;
        var bytes = System.Text.Encoding.UTF8.GetBytes(uri.AbsoluteUri);
        var data = NSData(bytes);
        return data == IntPtr.Zero
            ? IntPtr.Zero
            : Send_u32ptr(AeDescClass, Sel("descriptorWithDescriptorType:data:"), typeFileURL, data);
    }

    static IntPtr NSData(byte[] bytes)
    {
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            return Send_ptrnint(Cls("NSData"), Sel("dataWithBytes:length:"), ptr, bytes.Length);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }
}
