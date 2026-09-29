using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>
/// Structural guard for the P/Invoke surface of a PAL assembly (bevel-uat, bevel-5z2k).
///
/// Asserted by REFLECTION over the built assembly, not by regexing source. The first version of this
/// guard used a regex that required an explicit accessibility modifier — C# defaults to private, so it
/// silently covered 80 of 98 declarations and hid two genuine cross-file duplicates behind a green test.
/// Reflection is complete by construction: no modifier, <c>#if</c>, formatting, nested type, partial
/// class or new subdirectory can hide a declaration from <see cref="MethodAttributes.PinvokeImpl"/>.
/// (Verified: <c>DllImportAttribute</c> and <c>MarshalAsAttribute</c> are reconstructed by
/// <c>GetCustomAttribute</c> even though both are metadata pseudo-attributes.)
///
/// The one thing reflection cannot see is how the author SPELLED the library — it only has the resolved
/// string — so a single narrow source assertion covers that.
///
/// Written as a Theory over targets so another PAL assembly is one row, not a copied class.
/// </summary>
public class InteropConventionTests
{
    /// <summary>One row per guarded assembly: a type inside it, and the type holding its library paths.
    /// Add Bevel.Pal.Windows here when its interop is consolidated (bevel-p5bx).</summary>
    public static TheoryData<string> Targets => new() { "Bevel.Pal.MacOS" };

    private static (Assembly Asm, Type Paths) Resolve(string target) => target switch
    {
        "Bevel.Pal.MacOS" => (typeof(MacOSPermissionBroker).Assembly, typeof(MacOSPermissionBroker)
            .Assembly.GetType("Bevel.Pal.MacOS.Frameworks", throwOnError: true)!),
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "unknown guard target"),
    };

    private sealed record Decl(Type Type, string Member, string EntryPoint, string Library, MethodInfo Method)
    {
        /// <summary>Return type and parameter types, which is what distinguishes a legitimate second
        /// declaration of a variadic-ish entry point from a copied silo. <c>objc_msgSend</c> is declared
        /// ~22 times on purpose — C# needs one typed shape per call signature, and the ARM64 register
        /// classification depends on it — so the entry point alone cannot be the key.</summary>
        public string Signature =>
            Method.ReturnType.Name + "(" +
            string.Join(",", Method.GetParameters().Select(p => p.ParameterType.Name)) + ")";
    }

    private static List<Decl> Declarations(string target)
    {
        var (asm, _) = Resolve(target);
        var decls = new List<Decl>();
        foreach (var type in asm.GetTypes())
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                          | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (!m.Attributes.HasFlag(MethodAttributes.PinvokeImpl)) continue;
            var import = m.GetCustomAttribute<DllImportAttribute>();
            Assert.NotNull(import);   // a P/Invoke without a readable DllImport would break every rule below
            decls.Add(new Decl(type, m.Name, import!.EntryPoint ?? m.Name, import.Value, m));
        }
        return decls;
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void No_native_entry_point_is_declared_in_two_types(string target)
    {
        // Keyed on (library, entry point, SIGNATURE), not the C# member name. Two differently-named
        // members binding the same native function with the same signature are the same duplication —
        // that is how GeckoTabEngine's objc_msgSend_bool escaped a name-based check. The signature has to
        // be part of the key because objc_msgSend is legitimately declared once per call shape.
        var offenders = Declarations(target)
            .GroupBy(d => (d.Library, d.EntryPoint, d.Signature))
            .Where(g => g.Select(d => d.Type).Distinct().Count() > 1)
            .Select(g => $"{g.Key.EntryPoint} {g.Key.Signature} in "
                         + string.Join(", ", g.Select(d => $"{d.Type.Name}.{d.Member}")))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        if (offenders.Count > 0)
            Assert.Fail($"[{target}] These native entry points are declared in more than one type. Put " +
                        "each in the interop class for its framework instead of starting a private " +
                        "silo:\n  " + string.Join("\n  ", offenders));
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void Every_library_is_one_of_the_shared_path_constants(string target)
    {
        var (_, paths) = Resolve(target);
        var known = paths.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(known);

        var offenders = Declarations(target)
            .Where(d => !known.Contains(d.Library))
            .Select(d => $"{d.Type.Name}.{d.Member} -> \"{d.Library}\"")
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        if (offenders.Count > 0)
            Assert.Fail($"[{target}] These DllImports load a library that is not one of {paths.Name}'s " +
                        "constants, so there is no single place to be right about its path:\n  " +
                        string.Join("\n  ", offenders));
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void Every_native_bool_declares_its_marshalling(string target)
    {
        // The defect this prevents, found by hand during bevel-uat: a bare C# `bool` marshals as a 4-byte
        // Win32 BOOL. AXIsProcessTrusted was declared that way in MacOSPermissionBroker, so it read four
        // bytes of a one-byte native Boolean and let three undefined register bytes decide whether the
        // Accessibility grant was held; the two CG screen-capture gates had it too.
        //
        // The rule is "explicit MarshalAs", NOT "must be U1": U1 is right for CF/CG `Boolean`
        // (unsigned char) and I1 is right for ObjC `BOOL` (signed char), and both are legitimately here.
        var offenders = new List<string>();
        foreach (var d in Declarations(target))
        {
            if (d.Method.ReturnType == typeof(bool)
                && d.Method.ReturnParameter.GetCustomAttribute<MarshalAsAttribute>() is null)
                offenders.Add($"{d.Type.Name}.{d.Member} -> bare bool RETURN");

            foreach (var p in d.Method.GetParameters())
                if (p.ParameterType == typeof(bool) && p.GetCustomAttribute<MarshalAsAttribute>() is null)
                    offenders.Add($"{d.Type.Name}.{d.Member} -> bare bool parameter '{p.Name}'");
        }

        if (offenders.Count > 0)
            Assert.Fail($"[{target}] A bare `bool` in a P/Invoke signature marshals as a 4-byte Win32 " +
                        "BOOL, which reads three undefined bytes over a one-byte native Boolean. Declare " +
                        "[MarshalAs(UnmanagedType.U1)] for CF/CG Boolean or I1 for ObjC BOOL:\n  " +
                        string.Join("\n  ", offenders));
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void No_dll_import_names_a_path_literal_in_source(string target)
    {
        // The one rule reflection cannot express: it sees only the RESOLVED library string, so a literal
        // that happens to equal a constant is indistinguishable from the constant. One unambiguous
        // pattern — a quote immediately after the opening paren — covers it without parsing declarations.
        var dir = Path.Combine(RepoPaths.Root, "src", target);
        Assert.True(Directory.Exists(dir), $"missing source directory: {dir}");

        var offenders = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadLines(f)
                .Select((line, i) => (f, i, line))
                .Where(x => x.line.Contains("[DllImport(\"", StringComparison.Ordinal)))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}: {x.line.Trim()}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        if (offenders.Count > 0)
            Assert.Fail($"[{target}] These DllImports write the library path inline instead of naming a " +
                        "shared constant:\n  " + string.Join("\n  ", offenders));
    }

}
