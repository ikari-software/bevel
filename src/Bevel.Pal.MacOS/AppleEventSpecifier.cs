using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Bevel.Pal.MacOS;

// Platform-neutral parse of an Apple Event object specifier (08-os-interop.md §2.1.2). The native
// AEDesc → this tree is the only ObjC part; Bevel.App converts it to Bevel.Interop's ObjectSpecifier
// and resolves it. Kept here (not referencing the command model) so the PAL stays free of it.

public abstract record AeSpecifier;
public sealed record AeProperty(string Name) : AeSpecifier;                                        // home / desktop / trash / startup disk
public sealed record AeByName(string Class, string Name, AeSpecifier? Container) : AeSpecifier;    // folder "x" of …
public sealed record AeByIndex(string Class, int Index, AeSpecifier? Container) : AeSpecifier;     // item N of … (-1 = last)
public sealed record AeEvery(string Class, AeSpecifier? Container, AeWhose? Filter) : AeSpecifier;  // every file of … [whose …]
public sealed record AeWhose(string Key, string Op, string Value);                                 // name/name-extension/kind  is/begins/ends/contains  value

public static partial class AppleEventInbound
{
    private static readonly uint typeObjectSpecifier = FourCC("obj ");
    private static readonly uint typeAbsoluteOrdinal = FourCC("abso");


    static IntPtr DescFor(IntPtr desc, string keyword) => Send_u32(desc, Sel("descriptorForKeyword:"), FourCC(keyword));

    // A hostile script can nest a specifier ('folder of folder of …') tens of thousands deep; the
    // recursive parse would overflow the stack with an UNCATCHABLE StackOverflowException that kills
    // the whole shell. Cap the container chain well above any real script (bevel-376 review).
    private const int MaxSpecifierDepth = 64;

    /// <summary>Parses an AEDesc into an <see cref="AeSpecifier"/>, or null when it isn't an object
    /// specifier (e.g. a literal file — handled by the file-URL path instead).</summary>
    static AeSpecifier? ParseSpecifier(IntPtr desc) => ParseSpecifier(desc, 0);

    static AeSpecifier? ParseSpecifier(IntPtr desc, int depth)
    {
        if (desc == IntPtr.Zero || depth > MaxSpecifierDepth || SendU32(desc, Sel("descriptorType")) != typeObjectSpecifier)
            return null;

        var want = SendU32(DescFor(desc, "want"), Sel("typeCodeValue"));
        var form = SendU32(DescFor(desc, "form"), Sel("enumCodeValue"));
        var seld = DescFor(desc, "seld");
        var container = ParseSpecifier(DescFor(desc, "from"), depth + 1);   // null 'from' → null container (default home)
        var cls = ClassName(want);

        if (form == FourCC("prop"))
            return new AeProperty(PropertyName(SendU32(seld, Sel("typeCodeValue"))));

        if (form == FourCC("name"))
            return new AeByName(cls, NSStr(Send(seld, Sel("stringValue"))) ?? "", container);

        if (form == FourCC("indx"))
        {
            if (SendU32(seld, Sel("descriptorType")) == typeAbsoluteOrdinal)
            {
                var ord = SendU32(seld, Sel("typeCodeValue"));
                if (ord == FourCC("firs")) return new AeByIndex(cls, 1, container);
                if (ord == FourCC("last")) return new AeByIndex(cls, -1, container);
                return new AeEvery(cls, container, null);        // 'all ' (and any other ordinal)
            }
            return new AeByIndex(cls, AppKitInterop.SendInt(seld, Sel("int32Value")), container);
        }

        if (form == FourCC("test"))
            return new AeEvery(cls, container, ParseWhose(seld));

        return null;   // unsupported key form
    }

    static AeWhose? ParseWhose(IntPtr comparison)
    {
        if (comparison == IntPtr.Zero) return null;
        var op = SendU32(DescFor(comparison, "relo"), Sel("enumCodeValue"));   // comparison operator
        var lhs = DescFor(comparison, "obj1");                                  // a property specifier
        var value = NSStr(Send(DescFor(comparison, "obj2"), Sel("stringValue"))) ?? "";
        var prop = SendU32(DescFor(lhs, "seld"), Sel("typeCodeValue"));
        return new AeWhose(WhoseKey(prop), CompareOp(op), value);
    }

    static string ClassName(uint code) => code switch
    {
        _ when code == FourCC("file") || code == FourCC("docf") || code == FourCC("alia") => "file",
        _ when code == FourCC("cfol") => "folder",
        _ when code == FourCC("cdis") => "disk",
        _ => "item",
    };

    static string PropertyName(uint code) => code switch
    {
        _ when code == FourCC("home") => "home",
        _ when code == FourCC("desk") => "desktop",
        _ when code == FourCC("trsh") => "trash",
        _ when code == FourCC("sdsk") => "startup disk",
        _ => "home",
    };

    static string WhoseKey(uint code) => code switch
    {
        _ when code == FourCC("extn") => "name extension",
        _ when code == FourCC("kind") => "kind",
        _ => "name",   // 'pnam' and default
    };

    static string CompareOp(uint code) => code switch
    {
        _ when code == FourCC("bgwt") => "begins",
        _ when code == FourCC("ends") => "ends",
        _ when code == FourCC("cont") => "contains",
        _ => "equals",   // '=   ' and default
    };
}
