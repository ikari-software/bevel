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
/// <see cref="KnownDuplicates"/> is a SHRINKING baseline: it lists the symbols still duplicated, so the
/// suite stays green mid-migration while the list can only get smaller. The final task empties it and
/// deletes it.
/// </summary>
public class InteropConventionTests
{
    /// <summary>Symbols still declared in more than one file. Delete entries as they are consolidated;
    /// never add one. An empty list means the migration is done.</summary>
    private static readonly HashSet<string> KnownDuplicates = new(StringComparer.Ordinal)
    {
        "dlopen",
    };

    private static readonly Regex Extern = new(
        @"\[DllImport\(\s*(?<lib>[^,)\]]+)[^\]]*\]\s*(?:\[[^\]]*\]\s*)*" +
        @"(?:private|internal|public)\s+static\s+extern\s+[\w\.\<\>\[\]\*\?]+\s+(?<name>\w+)\s*\(",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static IEnumerable<(string File, string Lib, string Symbol)> Declarations()
    {
        foreach (var path in Directory.EnumerateFiles(SourceDir(), "*.cs"))
        foreach (Match m in Extern.Matches(File.ReadAllText(path)))
            yield return (Path.GetFileName(path), m.Groups["lib"].Value.Trim(), m.Groups["name"].Value);
    }

    private static List<string> DuplicatedSymbols() =>
        Declarations()
            .GroupBy(d => d.Symbol)
            .Where(g => g.Select(d => d.File).Distinct().Count() > 1)
            .Select(g => g.Key)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void No_symbol_is_declared_in_two_files()
    {
        var offenders = DuplicatedSymbols().Where(s => !KnownDuplicates.Contains(s)).ToList();

        Assert.True(offenders.Count == 0,
            "These P/Invoke symbols are declared in more than one file. Put each in the interop class " +
            "for its framework instead of a private silo:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Baseline_lists_only_real_duplicates()
    {
        // Stops the baseline from rotting into a list of names that no longer exist, which would let a
        // genuine duplicate hide behind a stale entry.
        var actual = DuplicatedSymbols().ToHashSet(StringComparer.Ordinal);
        var stale = KnownDuplicates.Except(actual).OrderBy(s => s, StringComparer.Ordinal).ToList();

        Assert.True(stale.Count == 0,
            "KnownDuplicates names symbols that are no longer duplicated — delete them:\n  " +
            string.Join("\n  ", stale));
    }

    [Fact(Skip = "Turns on in the final consolidation task (bevel-uat.5) — until then it lists every " +
                 "not-yet-migrated declaration, which is noise rather than signal.")]
    public void Every_dll_import_names_a_frameworks_constant()
    {
        var literals = Declarations()
            .Where(d => !d.Lib.StartsWith("Frameworks.", StringComparison.Ordinal))
            .Select(d => $"{d.File}: {d.Symbol} -> {d.Lib}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(literals.Count == 0,
            "These DllImports name a path directly instead of a Frameworks constant, so there is no " +
            "single place to be right about it:\n  " + string.Join("\n  ", literals));
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
