using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Bevel.Pal.MacOS;

// Inbound AE object-model plumbing (bevel-3i4): get / count / exists resolve a query and write a
// RESULT into the reply descriptor. These reply synchronously inside the handler, so the app-side
// QueryHandler must compute from synchronously-readable state (known folders, the window registry,
// the active controller) — no async surface round-trip.

/// <summary>What a get/count/exists is asking for. Application-property queries are the common case;
/// filesystem-specifier get/count/exists is carried as <see cref="Specifier"/>.</summary>
public enum AeQueryKind { Version, Home, Desktop, Trash, StartupDisk, Selection, WindowCount, ResolvePaths }

/// <summary>Which query verb.</summary>
public enum QueryOp { Get, Count, Exists }

public sealed record AeQuery(AeQueryKind Kind, AeSpecifier? Specifier, QueryOp Op);

/// <summary>The value to write back into the AE reply.</summary>
public abstract record AeResult;
public sealed record AeText(string Value) : AeResult;
public sealed record AeCount(int Value) : AeResult;
public sealed record AeBool(bool Value) : AeResult;
public sealed record AePath(string Value) : AeResult;                    // a single file/folder reference
public sealed record AePaths(IReadOnlyList<string> Values) : AeResult;   // a list of file references

public static partial class AppleEventInbound
{
    /// <summary>Wired by the app: synchronously answer a get/count/exists. Runs on the AE dispatch
    /// (UI) thread; must not await a surface round-trip.</summary>
    public static Func<AeQuery, AeResult?>? QueryHandler;

    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send_ptr(IntPtr r, IntPtr s, IntPtr a);
    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send_i32(IntPtr r, IntPtr s, int a);
    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send_bool(IntPtr r, IntPtr s, [MarshalAs(UnmanagedType.I1)] bool a);
    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send_u32ptr(IntPtr r, IntPtr s, uint a, IntPtr b);
    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send_ptrnint(IntPtr r, IntPtr s, IntPtr a, nint b);
    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")] static extern void SendVoid_ptru32(IntPtr r, IntPtr s, IntPtr a, uint b);
    [DllImport(Frameworks.ObjC, EntryPoint = "objc_msgSend")] static extern void SendVoid_ptrnint(IntPtr r, IntPtr s, IntPtr a, nint b);

    static IntPtr AeDescClass => Cls("NSAppleEventDescriptor");

    /// <summary>Handles a get/count/exists event: parse the query, ask the app, write the reply.</summary>
    static void HandleQuery(QueryOp op, IntPtr evt, IntPtr reply)
    {
        var query = ParseQuery(evt, op);
        if (query is null) return;
        var result = QueryHandler?.Invoke(query);
        if (result is not null) WriteReply(reply, result);
    }

    static AeQuery? ParseQuery(IntPtr evt, QueryOp op)
    {
        // `count <class>` carries the class in keyAEObjectClass ('kocl'), not the direct object.
        if (op == QueryOp.Count)
        {
            var kocl = Send_u32(evt, Sel("paramDescriptorForKeyword:"), FourCC("kocl"));
            if (kocl != IntPtr.Zero && SendU32(kocl, Sel("typeCodeValue")) == FourCC("cwin"))
                return new AeQuery(AeQueryKind.WindowCount, null, op);
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
            if (kind is { } k) return new AeQuery(k, null, op);
        }

        if (want == FourCC("cwin")) return new AeQuery(AeQueryKind.WindowCount, null, op);

        // A filesystem specifier: get returns the items, count how many, exists whether any.
        return ParseSpecifier(direct) is { } spec ? new AeQuery(AeQueryKind.ResolvePaths, spec, op) : null;
    }

    // ── Reply descriptor construction ──────────────────────────────────────────
    static void WriteReply(IntPtr reply, AeResult result)
    {
        if (reply == IntPtr.Zero) return;
        var desc = result switch
        {
            AeText t => TextDesc(t.Value),
            AeCount c => Send_i32(AeDescClass, Sel("descriptorWithInt32:"), c.Value),
            AeBool b => Send_bool(AeDescClass, Sel("descriptorWithBoolean:"), b.Value),
            AePath p => FileUrlDesc(p.Value),
            AePaths p => PathListDesc(p.Values),
            _ => IntPtr.Zero,
        };
        if (desc != IntPtr.Zero)
            SendVoid_ptru32(reply, Sel("setParamDescriptor:forKeyword:"), desc, keyDirectObject);
    }

    static IntPtr TextDesc(string s)
    {
        // NSStringCreate returns a +1 OWNED string; descriptorWithString: doesn't take ownership,
        // so it must be released or it leaks per reply (bevel-fo2 class). Mirror AppKitInterop.FileUrl.
        var ns = AppKitInterop.NSStringCreate(s);
        try { return Send_ptr(AeDescClass, Sel("descriptorWithString:"), ns); }
        finally { if (ns != IntPtr.Zero) AppKitInterop.SendVoid(ns, Sel("release")); }
    }

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
        if (string.IsNullOrEmpty(posixPath) || posixPath[0] != '/')
            return IntPtr.Zero;
        // Percent-encode each segment so '#', '?', '%', spaces and non-ASCII survive as path data
        // rather than being parsed as URL fragment/query/escape and truncating the name (bevel-3i4 review).
        var segments = posixPath.Split('/');
        for (var i = 0; i < segments.Length; i++) segments[i] = Uri.EscapeDataString(segments[i]);
        var bytes = System.Text.Encoding.UTF8.GetBytes("file://" + string.Join("/", segments));
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
