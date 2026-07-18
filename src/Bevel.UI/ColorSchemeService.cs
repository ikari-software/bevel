using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;

namespace Bevel.UI;

/// <summary>
/// Runtime Win2000 colour-scheme switching (W2K-01 / bevel-9js). Each scheme is a generated
/// <c>Colors/&lt;Name&gt;.axaml</c> ResourceDictionary in the Classic theme assembly that redefines the
/// <c>SystemColors.*ColorKey</c> colour resources; the theme's brushes derive from those keys via
/// <c>{DynamicResource}</c> (Classic <c>Accents/Base.axaml</c>), so merging a scheme dictionary at the
/// Application level recolours every control live (ENG-05) — no per-element engine work needed.
///
/// <para>v1 ships scheme <b>presets</b> only (per-element editing is deferred, spec §8.1); this is the
/// runtime + selection surface over the existing generated palette dictionaries.</para>
/// </summary>
public static class ColorSchemeService
{
    /// <summary>Selectable schemes as (persisted id, display name). The id is the generated axaml name
    /// under Classic's <c>Colors/</c> dir; the display names are the Win2000 scheme labels.</summary>
    public static readonly IReadOnlyList<(string Id, string Display)> Schemes = new[]
    {
        ("StandardWindows", "Windows Standard"),
        ("ClassicWindows", "Windows Classic"),
        ("Brick", "Brick"),
        ("Desert", "Desert"),
        ("Eggplant", "Eggplant"),
        ("Maple", "Maple"),
        ("Marine", "Marine"),
        ("Plum", "Plum"),
        ("Pumpkin", "Pumpkin"),
        ("Rose", "Rose"),
        ("Sprouce", "Spruce"),
        ("StarsAndStripes", "Red, White and Blue"),
        ("Storm", "Storm"),
        ("Wheat", "Wheat"),
    };

    /// <summary>The default scheme id (Win2000 "Windows Standard"). An empty/unknown persisted value
    /// resolves to this.</summary>
    public const string DefaultScheme = "StandardWindows";

    // The scheme dictionary we merged last, so a switch replaces rather than stacks.
    private static ResourceInclude? _applied;
    private static string _appliedId = DefaultScheme;

    private static readonly Uri BaseUri = new("avares://Bevel.App/App.axaml");
    private static bool _aliasesMerged;

    /// <summary>Merges the Bevel semantic-brush alias layer once (bevel-9js). Tokens.axaml's
    /// <c>Bevel.Brush.*</c> are <c>StaticResource</c>-frozen and can't follow a scheme change; this
    /// aliases the palette ones to the Classic <c>SystemColors</c> keys via DynamicResource, at
    /// Application level (outranks Tokens), so Bevel-owned chrome (Start menu, etc.) recolours too.</summary>
    private static void EnsureAliases(IResourceDictionary appResources)
    {
        if (_aliasesMerged) return;
        appResources.MergedDictionaries.Add(new ResourceInclude(BaseUri)
        {
            Source = new Uri("avares://Bevel.Themes.Win2000/SchemeAliases.axaml"),
        });
        _aliasesMerged = true;
    }

    /// <summary>True if <paramref name="id"/> is a known scheme.</summary>
    public static bool IsKnown(string id)
    {
        foreach (var (schemeId, _) in Schemes)
            if (schemeId == id) return true;
        return false;
    }

    /// <summary>Applies the named scheme to the running app by swapping the merged colour dictionary.
    /// Unknown/empty ids fall back to <see cref="DefaultScheme"/>. Safe to call before or after the UI
    /// is up; no-op if the scheme is already applied. Must be called on the UI thread.</summary>
    public static void Apply(string? id)
    {
        var scheme = string.IsNullOrWhiteSpace(id) || !IsKnown(id!) ? DefaultScheme : id!;
        if (scheme == _appliedId && _applied is not null) return;

        if (Application.Current?.Resources is not { } appResources) return;

        EnsureAliases(appResources);

        var dict = new ResourceInclude(BaseUri)
        {
            Source = new Uri($"avares://Classic.Avalonia.Theme/Colors/{scheme}.axaml"),
        };

        // Replace the previously-applied scheme (if any) so schemes don't stack in the merge list.
        if (_applied is not null)
            appResources.MergedDictionaries.Remove(_applied);
        appResources.MergedDictionaries.Add(dict);
        _applied = dict;
        _appliedId = scheme;
    }
}
