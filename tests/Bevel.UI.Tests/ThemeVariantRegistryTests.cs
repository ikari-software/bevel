using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Headless.XUnit;
using Bevel.Core;
using Bevel.UI.Blue2001;
using Xunit;

namespace Bevel.UI.Tests;

/// <summary>
/// Guards the two halves of the vendor-name rename that nothing else can see (bevel-e14f).
///
/// <para>The rename moved the persisted ids to <see cref="ThemeIds"/> — "industrial1999" and
/// "blue2001" — and <see cref="ThemeIds"/> states outright that there is deliberately NO
/// back-compatibility mapping, so a legacy "luna"/"win2000" id is simply UNKNOWN. Two places kept
/// using the old vocabulary anyway, and both failed silently: a dictionary keyed on ids that can no
/// longer arrive just misses, and an AXAML DynamicResource naming a key nobody defines resolves to
/// nothing at RENDER time, long after the compiler and the CI dead-code gate have stopped looking.</para>
///
/// <para>These are plain unit tests rather than render tests on purpose. The existing coverage of
/// this area, RenderBlue2001VariantsTest, opens with <c>if (string.IsNullOrEmpty(dir)) return;</c>
/// against an opt-in environment variable and then asserts NOTHING — it harvests PNGs. It therefore
/// exercised the broken lookup on every CI run and reported green, which is why a user-facing
/// regression this size survived in main.</para>
/// </summary>
public class ThemeVariantRegistryTests
{
    // ── The registry must be reachable by the ids that actually arrive ──────────────────

    /// <summary>Every theme the picker offers must be findable in the variant registry by its REAL
    /// id. Pastel is the one legitimate empty: it ships no appearance axis of its own.
    ///
    /// <para>This is the regression that matters. ThemeVariants keyed its first two entries
    /// ["win2000"] and ["luna"] while ThemeService persists "industrial1999" and "blue2001", so
    /// OptionsFor returned EMPTY for both flagship themes: the Appearance tab built from it
    /// (OnboardingWindow.BuildAppearanceOptions) simply had no colour-scheme picker and no
    /// colour/gloss axes, and ThemeVariants.Apply's TryGetValue missed, so neither engine ever ran
    /// its ApplyLive — a saved colourway was silently never applied.</para></summary>
    [Theory]
    [InlineData(ThemeIds.Industrial1999)]
    [InlineData(ThemeIds.Blue2001)]
    [InlineData(ThemeIds.Flat)]
    public void Every_theme_with_an_appearance_axis_is_reachable_by_its_persisted_id(string themeId)
    {
        var options = ThemeVariants.OptionsFor(themeId);

        Assert.True(options.Count > 0,
            $"ThemeVariants has no options for '{themeId}'. If the registry is keyed on a legacy " +
            "vendor id, the Appearance tab renders empty and ApplyLive never runs for this theme.");
    }

    /// <summary>The two engines' axes are the user-visible contract, so name them rather than only
    /// counting: a registry that returned the WRONG theme's options would still pass a count check.</summary>
    [Fact]
    public void The_flagship_themes_offer_their_documented_axes()
    {
        Assert.Contains("Colour scheme",
            ThemeVariants.OptionsFor(ThemeIds.Industrial1999).Select(o => o.Label));

        var blue = ThemeVariants.OptionsFor(ThemeIds.Blue2001).Select(o => o.Label).ToList();
        Assert.Contains("Colour", blue);
        Assert.Contains("Gloss", blue);
    }

    /// <summary>A legacy id must stay UNKNOWN. ThemeIds is explicit that no compatibility mapping
    /// exists, so if one of these ever starts returning options it means the old vocabulary has been
    /// reintroduced — which is the thing the rename existed to remove.</summary>
    [Theory]
    [InlineData("luna")]
    [InlineData("win2000")]
    public void Legacy_vendor_ids_resolve_to_nothing(string legacyId)
    {
        Assert.Empty(ThemeVariants.OptionsFor(legacyId));
    }

    /// <summary>The other half of the same defect, and the half a user would actually notice.
    ///
    /// <para>OptionsFor decides whether the PICKERS appear; <see cref="ThemeVariants.Apply"/>'s own
    /// TryGetValue decides whether the chosen value is ever HANDED to the engine. Both missed on the
    /// same bad key, so a colourway saved in settings was silently discarded on every launch — the
    /// theme came up in its default blue no matter what had been chosen.</para>
    ///
    /// <para>Asserts against the default rather than a hard-coded colour so it tests the ROUTING, not
    /// Purple's exact transform, which is Blue2001Variant's business and may legitimately change.</para></summary>
    [AvaloniaFact]
    public void Apply_routes_a_saved_colourway_to_the_theme_engine()
    {
        try
        {
            Blue2001VariantService.Apply(Blue2001VariantService.DefaultColor, Blue2001VariantService.DefaultGloss);
            var atDefault = Application.Current!.Resources["Bevel.Color.WindowFrame"];

            ThemeVariants.Apply(new BevelSettings
            {
                ThemeId = ThemeIds.Blue2001,
                Blue2001Color = "Purple",
                Blue2001Gloss = "Gloss",
            });

            Assert.NotEqual(atDefault, Application.Current!.Resources["Bevel.Color.WindowFrame"]);
        }
        finally
        {
            // Symmetrical Clear is mandatory for these engines (see ThemeVariants.Apply): a lingering
            // injection outranks the next theme's tokens in Application.Resources.
            Blue2001VariantService.Clear();
        }
    }

    // ── No AXAML may reference a resource key that nobody defines ───────────────────────

    /// <summary>Every <c>{DynamicResource}</c> a shipped theme references under a theme-owned prefix
    /// must be defined somewhere in the repo's AXAML.
    ///
    /// <para>An unresolved DynamicResource is invisible to every gate we have: it compiles, the
    /// WarningsAsErrors dead-code set never sees it, and at runtime the property just keeps its
    /// default while the app carries on. Blue2001Theme.axaml asked for <c>Luna.Brush.MenuBorder</c>,
    /// which no file defines and which the rename had turned into Blue2001.Brush.MenuBorder — so the
    /// Blue2001 tooltip's BorderBrush silently resolved to nothing.</para>
    ///
    /// <para>Scoped to theme-owned prefixes deliberately: Avalonia's own and Classic.Avalonia's keys
    /// are defined outside this repo, so asserting on them would fail for the wrong reason.</para></summary>
    [Fact]
    public void Every_theme_scoped_DynamicResource_key_is_defined_somewhere()
    {
        var axaml = AxamlFiles().ToList();
        Assert.NotEmpty(axaml);   // a bad repo-root walk must fail loudly, not pass vacuously

        var text = axaml.ToDictionary(f => f, StripComments);

        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var body in text.Values)
            foreach (Match m in Regex.Matches(body, @"x:Key\s*=\s*""([^""]+)"""))
                defined.Add(m.Groups[1].Value);

        // Only keys a THEME in this repo owns; anything else is defined upstream.
        var owned = new Regex(@"^(Bevel|Blue2001|Flat|Pastel|Industrial1999|Luna|Win2000)\.");
        var missing = new List<string>();

        foreach (var (file, body) in text)
            foreach (Match m in Regex.Matches(body, @"\{DynamicResource\s+([A-Za-z0-9_.]+)\s*\}"))
            {
                var key = m.Groups[1].Value;
                if (!owned.IsMatch(key) || defined.Contains(key)) continue;
                missing.Add($"{Path.GetFileName(file)}: {key}");
            }

        Assert.True(missing.Count == 0,
            "DynamicResource keys referenced but never defined (these fail silently at render):\n  "
            + string.Join("\n  ", missing.Distinct().OrderBy(x => x)));
    }

    /// <summary>The rename is finished only when the vendor names are gone from live lookups. Catches
    /// a reintroduction the way ab58f0b reintroduced the dead ToolbarIcons aliases during a merge.</summary>
    [Fact]
    public void No_axaml_references_a_vendor_named_resource_key()
    {
        var offenders =
            from file in AxamlFiles()
            from Match m in Regex.Matches(StripComments(file), @"\{DynamicResource\s+((?:Luna|Win2000)\.[A-Za-z0-9_.]+)\s*\}")
            select $"{Path.GetFileName(file)}: {m.Groups[1].Value}";

        Assert.True(!offenders.Any(),
            "Vendor-named resource keys still referenced (ThemeIds says these no longer exist):\n  "
            + string.Join("\n  ", offenders.Distinct()));
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────

    /// <summary>Reads an AXAML file with its XML comments removed.
    ///
    /// <para>Required, not cosmetic: these files explain their own conventions in prose, and
    /// Industrial1999Theme.axaml says controls bind <c>Theme="{DynamicResource Bevel.Theme.X}"</c>
    /// with X standing in for a control name. Scanning raw text reports that illustration as an
    /// undefined key — a false positive that would train the next reader to ignore this test.</para></summary>
    private static string StripComments(string file)
        => Regex.Replace(File.ReadAllText(file), "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    private static IEnumerable<string> AxamlFiles()
    {
        var src = Path.Combine(RepoRoot(), "src");
        return Directory.EnumerateFiles(src, "*.axaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    /// <summary>Walks up to the directory holding Bevel.sln, so the test is independent of the
    /// configuration/TFM path the assembly happens to run from.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bevel.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
