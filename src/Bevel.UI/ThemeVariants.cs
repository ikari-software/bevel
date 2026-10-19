using System;
using System.Collections.Generic;
using Bevel.Core;
using Bevel.UI.Blue2001;

namespace Bevel.UI;

/// <summary>
/// One appearance option a theme contributes to the Properties dialog: a labelled selector over a set
/// of choices, backed by a settings field. The dialog renders these generically and rebuilds when the
/// theme changes — it never hard-codes a specific theme's options.
/// </summary>
public sealed record ThemeOption(
    string Label,
    IReadOnlyList<(string Id, string Display)> Choices,
    Func<BevelSettings, string> Get,
    Action<BevelSettings, string> Set)
{
    /// <summary>Index of the settings' current value in <see cref="Choices"/> (0 if unset/unknown, so
    /// an empty setting resolves to the first/default choice).</summary>
    public int CurrentIndex(BevelSettings s)
    {
        var cur = Get(s);
        for (var i = 0; i < Choices.Count; i++)
            if (Choices[i].Id == cur) return i;
        return 0;
    }
}

/// <summary>A theme's appearance contribution: the options it offers and how to apply them live.</summary>
public sealed record ThemeVariantSpec(
    string ThemeId,
    IReadOnlyList<ThemeOption> Options,
    Action<BevelSettings> ApplyLive);

/// <summary>
/// The theme → appearance-options registry (bevel-luna-variants). Each theme OWNS its variant options
/// here rather than the dialog knowing about them: Win2000 contributes its colour schemes, Luna its
/// colour + gloss axes, and a future theme whatever it needs. <see cref="Apply"/> routes to the active
/// theme's engine and deactivates the others, so shared chrome keys never bleed across themes.
/// </summary>
public static class ThemeVariants
{
    private static readonly Dictionary<string, ThemeVariantSpec> ByTheme = new(StringComparer.OrdinalIgnoreCase)
    {
        ["win2000"] = new ThemeVariantSpec("win2000",
            new[]
            {
                new ThemeOption("Colour scheme", ColorSchemeService.Schemes,
                    s => s.ColorScheme, (s, v) => s.ColorScheme = v),
            },
            s => ColorSchemeService.Apply(s.ColorScheme)),

        ["luna"] = new ThemeVariantSpec("luna",
            new[]
            {
                new ThemeOption("Colour", Blue2001VariantService.Colors,
                    s => s.Blue2001Color, (s, v) => s.Blue2001Color = v),
                new ThemeOption("Gloss", Blue2001VariantService.Glosses,
                    s => s.Blue2001Gloss, (s, v) => s.Blue2001Gloss = v),
            },
            s => Blue2001VariantService.Apply(s.Blue2001Color, s.Blue2001Gloss)),

        [ThemeIds.Flat] = new ThemeVariantSpec(ThemeIds.Flat,
            new[]
            {
                new ThemeOption("Watercolor variation", FlatVariantService.Variants,
                    s => s.FlatVariant, (s, v) => s.FlatVariant = v),
            },
            s => FlatVariantService.Apply(s)),
        [ThemeIds.Pastel] = new ThemeVariantSpec(ThemeIds.Pastel, Array.Empty<ThemeOption>(), _ => { }),
    };

    /// <summary>The active theme's option specs, or empty if the theme contributes none.</summary>
    public static IReadOnlyList<ThemeOption> OptionsFor(string themeId) =>
        ByTheme.TryGetValue(themeId, out var spec) ? spec.Options : Array.Empty<ThemeOption>();

    /// <summary>Applies the active theme's appearance options live from <paramref name="s"/>, and
    /// deactivates every other theme's engine so its chrome doesn't linger in Application.Resources.
    /// Call after <see cref="ThemeService"/>.Apply and whenever a variant option changes. UI thread only.</summary>
    public static void Apply(BevelSettings s)
    {
        // Deactivate engines that own shared resource keys before applying the active theme's — each
        // engine merges Application-level resources that would otherwise linger and outrank the incoming
        // theme (e.g. a Win2000 scheme's WindowCaptionHeightKey=18 shrinking Luna's 25 caption — bevel-p3va).
        if (!string.Equals(s.ThemeId, Bevel.Core.ThemeIds.Blue2001, StringComparison.OrdinalIgnoreCase))
            Blue2001VariantService.Clear();
        if (!string.Equals(s.ThemeId, Bevel.Core.ThemeIds.Flat, StringComparison.OrdinalIgnoreCase))
            FlatVariantService.Clear();
        if (!string.Equals(s.ThemeId, Bevel.Core.ThemeIds.Industrial1999, StringComparison.OrdinalIgnoreCase))
            ColorSchemeService.Clear();

        if (ByTheme.TryGetValue(s.ThemeId, out var spec))
            spec.ApplyLive(s);
    }
}
