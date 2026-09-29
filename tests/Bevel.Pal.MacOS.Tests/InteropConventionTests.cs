using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>
/// Structural guard for this assembly's P/Invoke surface (bevel-uat).
///
/// The duplication this prevents was never caused by a visibility barrier — <c>AppKitInterop</c> has
/// always exposed public externs. It was caused by each new file starting its own private silo, which is
/// a habit no code review reliably catches across 80 symbols. So it is asserted instead.
///
/// It also asserts every DllImport names a <see cref="Frameworks"/> constant rather than a path literal,
/// so there is exactly one place to be right about each framework's location. Note the guard sees only
/// DllImport attributes: a path passed as a dlopen ARGUMENT is invisible to it, so those are centralised
/// by convention (see AppKitInterop.EnsureAppKitLoaded, LoginItemRegistrar).
/// </summary>
public class InteropConventionTests
{
    // Deliberately does NOT require an accessibility modifier. C# defaults to private, and the first
    // version of this regex demanded (private|internal|public) -- which silently skipped 18 of the
    // assembly's 98 declarations (all of the AppleEvent* files write a bare `static extern`), hiding two
    // genuine cross-file duplicates behind a green test. Match anything between the attribute and
    // `extern` instead, so how the author spelled the visibility cannot change what is covered.
    private static readonly Regex Extern = new(
        @"\[DllImport\(\s*(?<lib>[^,)\]]+)[^\]]*\]\s*(?:\[[^\]]*\]\s*)*" +
        @"(?:\w+\s+)*?extern\s+[\w\.\<\>\[\]\*\?]+\s+(?<name>\w+)\s*\(",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Scanned once per process. xUnit builds a fresh instance per test, so an instance field
    /// would re-read every source file for each test — ~33 files and 258 KB, twice, for byte-identical
    /// input.</summary>
    private static readonly List<(string File, string Lib, string Symbol)> All = Scan().ToList();

    private static IEnumerable<(string File, string Lib, string Symbol)> Scan()
    {
        foreach (var path in Directory.EnumerateFiles(SourceDir(), "*.cs"))
        {
            var text = File.ReadAllText(path);
            if (!text.Contains("DllImport", StringComparison.Ordinal)) continue;   // most files have none
            foreach (Match m in Extern.Matches(text))
                yield return (Path.GetFileName(path), m.Groups["lib"].Value.Trim(), m.Groups["name"].Value);
        }
    }

    private static List<string> DuplicatedSymbols() =>
        All.GroupBy(d => d.Symbol)
            .Where(g => g.Select(d => d.File).Distinct().Count() > 1)
            .Select(g => g.Key)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void No_symbol_is_declared_in_two_files()
    {
        var offenders = DuplicatedSymbols();
        if (offenders.Count > 0)
            Assert.Fail(
                "These P/Invoke symbols are declared in more than one file. Put each in the interop " +
                "class for its framework instead of starting a private silo:\n  " +
                string.Join("\n  ", offenders));
    }

    [Fact]
    public void Every_dll_import_names_a_frameworks_constant()
    {
        var literals = All
            .Where(d => !d.Lib.StartsWith("Frameworks.", StringComparison.Ordinal))
            .Select(d => $"{d.File}: {d.Symbol} -> {d.Lib}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        if (literals.Count > 0)
            Assert.Fail(
                "These DllImports name a path directly instead of a Frameworks constant, so there is " +
                "no single place to be right about it:\n  " + string.Join("\n  ", literals));
    }

    private static string SourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Bevel.Pal.MacOS")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Bevel.Pal.MacOS");
    }
}
