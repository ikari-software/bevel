using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Regenerates the 64×64 PNGs in tests/fixtures/hero-apps/ from the committed SVG sources
/// under src/. Opt-in — never runs in an ordinary suite or CI (needs rsvg-convert).
///
///   BEVEL_REFRESH_HERO_APPS=1 dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj \
///     --filter FullyQualifiedName~RefreshHeroAppFixtureTest
///
/// These are Bevel-drawn originals (bevel-fztj). Do NOT scrape host macOS app icons — that path
/// reintroduced vendor marks into the tree.
/// </summary>
public class RefreshHeroAppFixtureTest
{
    private static readonly (string Name, string? Subtitle)[] Wanted =
    [
        ("Browser", "Productivity"),
        ("Console", "Utilities"),
        ("Viewer", "Productivity"),
        ("Calculator", null),
    ];

    [AvaloniaFact]
    public void Refresh_hero_app_fixture_from_svg()
    {
        if (Environment.GetEnvironmentVariable("BEVEL_REFRESH_HERO_APPS") != "1") return;

        var srcDir = Path.Combine(HeroApps.Dir, "src");
        Assert.True(Directory.Exists(srcDir), $"missing SVG sources: {srcDir}");
        Directory.CreateDirectory(HeroApps.Dir);

        var entries = new System.Collections.Generic.List<HeroApp>();
        foreach (var (name, subtitle) in Wanted)
        {
            var svg = Path.Combine(srcDir, name + ".svg");
            var png = Path.Combine(HeroApps.Dir, name + ".png");
            Assert.True(File.Exists(svg), $"missing {svg}");

            var psi = new ProcessStartInfo
            {
                FileName = "rsvg-convert",
                ArgumentList = { "-w", "64", "-h", "64", svg, "-o", png },
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi)!;
            var err = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            Assert.True(proc.ExitCode == 0 && File.Exists(png),
                $"rsvg-convert failed for {name}: {err}");

            entries.Add(new HeroApp($"bevel.fixture.{name.ToLowerInvariant()}", name, subtitle, name + ".png"));
        }

        File.WriteAllText(Path.Combine(HeroApps.Dir, "apps.json"),
            JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
    }
}
