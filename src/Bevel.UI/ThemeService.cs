using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;

namespace Bevel.UI;

/// <summary>
/// Runtime <b>theme</b> switching (PKG-03 / bevel-9js) — distinct from <see cref="ColorSchemeService"/>,
/// which only swaps the palette. A theme swaps the whole non-colour token bundle (metrics, edge
/// rendering, fonts) by merging a token-override dictionary at Application level, on top of the
/// Win2000 base <c>Tokens.axaml</c> that ships in the theme styles.
///
/// <para>This is the packaging/switch <b>engine</b>: <see cref="Themes"/> is the registry and
/// <see cref="Apply"/> is the runtime swap. The default theme ("win2000", the frozen id) merges
/// nothing — the base tokens are the theme. Additional themes each contribute one override dict.</para>
///
/// <para><b>v1 scope.</b> A theme currently composes the token axes that already cascade live —
/// edge rendering and metrics — over the shared Classic control templates. Alternate control-template
/// <i>geometry</i> (true XP/Luna-style chrome) is a later milestone: it needs the ENG-01 template
/// migration (routing the vendored Classic templates onto <c>Bevel.Metric.*</c>) landed first, so a
/// theme's metric overrides reach every control and not just the Bevel-owned ones. The stub "Flat"
/// theme proves the switch path end-to-end today.</para>
///
/// <para><b>Layering.</b> User overrides (font via <see cref="FontService"/>, "Crisp bevels" via
/// <see cref="ThemeOptions"/>) write <i>top-level</i> Application resources, which outrank this merged
/// theme dict — so a theme sets defaults a user can still override, and the scheme dict's keys are
/// disjoint (system colours), so order between them is irrelevant.</para>
/// </summary>
public static class ThemeService
{
    /// <summary>Selectable themes as (persisted id, display name). "win2000" is the frozen default id.</summary>
    public static readonly IReadOnlyList<(string Id, string Display)> Themes = new[]
    {
        ("win2000", "Bevel Classic"),
        ("flat", "Bevel Flat (preview)"),
    };

    /// <summary>The default theme id (the base Win2000 tokens). Empty/unknown resolves here.</summary>
    public const string DefaultTheme = "win2000";

    private static readonly Uri BaseUri = new("avares://Bevel.App/App.axaml");
    private static ResourceInclude? _applied;
    private static string _appliedId = DefaultTheme;

    /// <summary>The token-override dictionary a theme merges, or null for the base theme (merges nothing).</summary>
    private static string? SourceFor(string id) => id switch
    {
        "flat" => "avares://Bevel.Themes.Win2000/ThemeFlat.axaml",
        _ => null,
    };

    /// <summary>True if <paramref name="id"/> is a known theme.</summary>
    public static bool IsKnown(string id)
    {
        foreach (var (themeId, _) in Themes)
            if (themeId == id) return true;
        return false;
    }

    /// <summary>Applies the named theme to the running app by swapping its merged token-override dict.
    /// Unknown/empty ids fall back to <see cref="DefaultTheme"/>. No-op if already applied. Must run on
    /// the UI thread.</summary>
    public static void Apply(string? id)
    {
        var theme = string.IsNullOrWhiteSpace(id) || !IsKnown(id!) ? DefaultTheme : id!;
        if (theme == _appliedId) return;
        if (Application.Current?.Resources is not { } res) return;

        // Drop the previously-applied theme dict (if any) so themes don't stack in the merge list.
        if (_applied is not null)
        {
            res.MergedDictionaries.Remove(_applied);
            _applied = null;
        }

        if (SourceFor(theme) is { } src)
        {
            var dict = new ResourceInclude(BaseUri) { Source = new Uri(src) };
            res.MergedDictionaries.Add(dict);
            _applied = dict;
        }
        _appliedId = theme;
    }
}
