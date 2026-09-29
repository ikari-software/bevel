using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Bevel.Core;
using Bevel.Taskbar;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Keeps the Bend proofs in <c>proofs/</c> honest.
///
/// Bend cannot see C#, so a <c>.bend</c> file proves things about a MODEL of our logic. A model that has
/// drifted from the code is worse than no model, because bend still reports success. These tests
/// are the anchor, and they deliberately READ THE PROOF FILES rather than restating their numbers: an
/// inline copy of the constants would be a third source of truth, and would only catch a change made on
/// the C# side. Parsing means a change to either side fails the build.
///
/// Division of labour: the proofs quantify over the whole domain (every tier, every handback world) which
/// sampled tests cannot; these tests check that the domain modelled is the one we ship.
/// </summary>
[Collection("TaskbarTheme")]   // reads shared TaskbarTheme metrics per tier
public class ProofModelTests
{
    private static readonly IReadOnlyDictionary<string, TaskbarButtonSize> Tiers =
        new Dictionary<string, TaskbarButtonSize>
        {
            ["Small"] = TaskbarButtonSize.Small,
            ["Normal"] = TaskbarButtonSize.Normal,
            ["Large"] = TaskbarButtonSize.Large,
            ["Big"] = TaskbarButtonSize.Big,
        };

    // ── proofs/taskbar_geometry.bend ──────────────────────────────────────

    [AvaloniaFact]
    public void Geometry_proof_models_the_real_tier_metrics()
    {
        var proof = ReadProof("taskbar_geometry.bend");
        var buttonHeights = ParseNumericArms(proof, "ButtonHeight");
        var iconSizes = ParseNumericArms(proof, "TaskIconSize");

        // The proof must cover every tier that exists, or it proves the invariant for a subset and the
        // uncovered arm is exactly where a regression hides.
        Assert.Equal(Tiers.Keys.OrderBy(k => k), buttonHeights.Keys.OrderBy(k => k));
        Assert.Equal(Tiers.Keys.OrderBy(k => k), iconSizes.Keys.OrderBy(k => k));

        try
        {
            foreach (var (name, tier) in Tiers)
            {
                TaskbarTheme.Configure(tier);
                Assert.Equal(buttonHeights[name], TaskbarTheme.ButtonHeight);
                Assert.Equal(iconSizes[name], TaskbarTheme.TaskIconSize);

                // The proof derives these rather than listing them; if the derivation stops matching, its
                // arithmetic describes a bar we no longer ship.
                Assert.Equal(TaskbarTheme.ButtonHeight + 4, TaskbarTheme.RowHeight);
                Assert.Equal(TaskbarTheme.RowHeight + 2, TaskbarTheme.TaskbarHeight);
            }
        }
        finally
        {
            TaskbarTheme.Configure(TaskbarButtonSize.Normal);
        }
    }

    [AvaloniaFact]
    public void Tray_budget_proof_models_the_real_budget()
    {
        // Every `TrayFits(<Tier>{}, <rows>, <budget>) == LT{}` law asserts that budget is SAFE. Here we
        // check the tray actually computes it — the division lives in the C#, its safety in the proof.
        var laws = Regex.Matches(ReadProof("taskbar_geometry.bend"),
            @"TrayFits\(\s*(?<tier>\w+)\{\}\s*,\s*(?<rows>\d+)\s*,\s*(?<budget>\d+)\s*\)\s*==\s*LT\{\}");
        Assert.NotEmpty(laws);

        try
        {
            foreach (Match law in laws)
            {
                var name = law.Groups["tier"].Value;
                var rows = int.Parse(law.Groups["rows"].Value);
                var budget = int.Parse(law.Groups["budget"].Value);
                Assert.True(Tiers.ContainsKey(name), $"proof names an unknown tier: {name}");

                TaskbarTheme.Configure(Tiers[name]);
                Assert.Equal(budget, TrayViewModel.MaxIconHeight(rows));

                // The property the proof establishes, re-checked against the live numbers: that many rows
                // of that height plus the 6pt sunken-well chrome still fit in the bar. This is the
                // bevel-xpfl overflow expressed as an invariant rather than a sampled case.
                Assert.True(rows * budget + 6 <= TaskbarTheme.HeightForRows(rows),
                    $"{name} x {rows}: {rows}*{budget}+6 must fit in {TaskbarTheme.HeightForRows(rows)}");
            }
        }
        finally
        {
            TaskbarTheme.Configure(TaskbarButtonSize.Normal);
        }
    }

    // ── proofs/handback.bend ──────────────────────────────────────────────

    /// <summary>Representative pids for each modelled world. The proof reasons about the five ways the
    /// three pids can relate, because the C# only ever compares them for equality — that is what makes the
    /// abstraction faithful, and what lets five constructors stand in for the whole input space.</summary>
    private static readonly IReadOnlyDictionary<string, (int Prior, int Frontmost, int Own)> Worlds =
        new Dictionary<string, (int, int, int)>
        {
            ["NoCapture"] = (0, 777, 999),
            ["Unknown"] = (501, 0, 999),
            ["StillPrior"] = (501, 501, 999),
            ["Ours"] = (501, 999, 999),
            ["ThirdApp"] = (501, 777, 999),
        };

    [AvaloniaFact]
    public void Handback_proof_models_the_real_decision()
    {
        var arms = ParseDecisionArms(ReadProof("handback.bend"));
        Assert.Equal(Worlds.Keys.OrderBy(k => k), arms.Keys.OrderBy(k => k));

        foreach (var (world, decision) in arms)
        {
            var (prior, frontmost, own) = Worlds[world];
            var expected = decision switch
            {
                "HandBack" => true,
                "StandDown" => false,
                _ => throw new InvalidOperationException($"unknown decision in proof: {decision}"),
            };
            Assert.Equal(expected, TaskbarWindow.ShouldHandBackKeyFocus(prior, frontmost, own));
        }
    }

    // ── The proofs actually get checked ───────────────────────────────────

    [Theory]
    [InlineData("handback.bend")]
    [InlineData("taskbar_geometry.bend")]
    public void Bend_proofs_check(string proof)
    {
        var path = Path.Combine(RepoPaths.Root, "proofs", proof);
        Assert.True(File.Exists(path), $"missing proof: {path}");

        var bend = ResolveBend();
        if (bend is null) return;   // Bend is optional tooling; the model-pinning tests above still run

        using var p = Process.Start(new ProcessStartInfo(bend, $"\"{path}\" --check-only")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(60_000), $"bend did not finish checking {proof}");

        // The EXIT CODE is bend's contract: 0 when every proof checks, 1 when any fails. This used to
        // also assert the literal success banner "All terms check" — which broke on a routine bend
        // upgrade (2.0.27 -> 2.0.32) that reworded it to "ALL PROOFS CHECK", with the proofs still
        // checking. Never couple a test to a third-party tool's human-readable wording when it exposes
        // a real signal. The output is still inspected, but only for a failure marker, which survives
        // rewording better than matching a success phrase.
        var output = stdout + stderr;
        Assert.True(p.ExitCode == 0, $"bend rejected {proof} (exit {p.ExitCode}):\n{output}");
        Assert.DoesNotContain("FAIL", output, StringComparison.OrdinalIgnoreCase);
    }

    // ── Proof-file parsing ────────────────────────────────────────────────

    /// <summary>`case Foo{}:` arms returning a bare number, for one `def &lt;name&gt;` block.</summary>
    private static Dictionary<string, int> ParseNumericArms(string proof, string function)
    {
        var body = DefBody(proof, function);
        return Regex.Matches(body, @"case\s+(?<ctor>\w+)\{\}\s*:\s*(?<value>\d+)", RegexOptions.Singleline)
            .ToDictionary(m => m.Groups["ctor"].Value, m => int.Parse(m.Groups["value"].Value));
    }

    /// <summary>`case Foo{}:` arms returning a Decision constructor, from ShouldHandBack.</summary>
    private static Dictionary<string, string> ParseDecisionArms(string proof)
    {
        var body = DefBody(proof, "ShouldHandBack");
        return Regex.Matches(body, @"case\s+(?<ctor>\w+)\{\}\s*:\s*(?<decision>\w+)\{\}", RegexOptions.Singleline)
            .ToDictionary(m => m.Groups["ctor"].Value, m => m.Groups["decision"].Value);
    }

    /// <summary>The text of one `def &lt;name&gt;(...)` block, up to the next top-level `def`/`law`/`type`.</summary>
    private static string DefBody(string proof, string function)
    {
        var start = Regex.Match(proof, $@"^def\s+{Regex.Escape(function)}\s*\(", RegexOptions.Multiline);
        Assert.True(start.Success, $"proof has no `def {function}(`");
        var rest = proof[start.Index..];
        var end = Regex.Match(rest[1..], @"^(def|law|type)\s", RegexOptions.Multiline);
        return end.Success ? rest[..(end.Index + 1)] : rest;
    }

    private static string ReadProof(string name) => File.ReadAllText(Path.Combine(RepoPaths.Root, "proofs", name));

    /// <summary>Bend is optional dev tooling, so it is looked up rather than assumed. Absolute Homebrew
    /// paths stay out of build logic (bevel-ka6c): PATH only.</summary>
    private static string? ResolveBend()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir, "bend");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

}
