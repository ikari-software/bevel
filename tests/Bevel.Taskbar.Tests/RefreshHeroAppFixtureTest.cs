using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Bevel.UI;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Regenerates tests/fixtures/hero-apps/ from the machine's REAL installed applications, via the same
/// MacOSAppEnvironment and MacOSIconProvider the shell uses at runtime. Neither needs the Swift helper
/// or a TCC grant — enumeration walks the app roots and icons come from the bundle — so this runs
/// from a plain test host.
///
/// Opt-in and macOS-only: it reads the host's applications, so it must never run as part of an
/// ordinary suite or in CI. The fixture it writes is what everything else consumes, which is what
/// keeps the hero byte-reproducible everywhere.
///
///   BEVEL_REFRESH_HERO_APPS=1 dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj \
///     --filter FullyQualifiedName~RefreshHeroAppFixtureTest
/// </summary>
public class RefreshHeroAppFixtureTest
{
    /// <summary>The programs the hero shows, in order. Named rather than "the first four installed":
    /// the shot has to be stable and legible, and an alphabetical scrape opens with whatever 0 A.D.
    /// happens to sort above.</summary>
    private static readonly string[] Wanted = ["Safari", "Terminal", "Preview", "Calculator"];

    [AvaloniaFact]
    public async Task Refresh_hero_app_fixture()
    {
        if (Environment.GetEnvironmentVariable("BEVEL_REFRESH_HERO_APPS") != "1") return;
        Assert.True(OperatingSystem.IsMacOS(), "the fixture is captured from a real macOS install");

        using var env = new Bevel.Pal.MacOS.MacOSAppEnvironment();
        var installed = await env.EnumerateInstalledAppsAsync();
        var icons = new Bevel.Pal.MacOS.MacOSIconProvider();

        Directory.CreateDirectory(HeroApps.Dir);
        var entries = new System.Collections.Generic.List<HeroApp>();

        foreach (var name in Wanted)
        {
            var app = installed.FirstOrDefault(a => a.DisplayName == name);
            Assert.True(app is not null, $"'{name}' is not installed on this machine");

            // 64px: the pinned rows draw at 28 logical, so 2x the HiDPI shot needs 56. 64 leaves the
            // downscale a little room rather than resampling at exactly 1:1.
            var image = await icons.GetIconAsync(app!.AppId, 64);
            var bitmap = PalImageBitmap.ToBitmap(image);
            Assert.True(bitmap is not null, $"no icon came back for {name}");

            var file = name + ".png";
            bitmap!.Save(Path.Combine(HeroApps.Dir, file));
            entries.Add(new HeroApp(app.AppId, app.DisplayName, app.Subtitle, file));
        }

        File.WriteAllText(Path.Combine(HeroApps.Dir, "apps.json"),
            JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
    }
}
