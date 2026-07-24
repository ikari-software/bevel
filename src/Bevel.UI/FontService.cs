using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;

namespace Bevel.UI;

/// <summary>
/// Runtime UI font-family override (FNT-01 / bevel-9js). The shell's semantic font key
/// <c>Bevel.Font.UI</c> (Tokens.axaml) is consumed via <c>DynamicResource</c>, so shadowing it at
/// Application level — where Application.Resources' own keys win over the theme's — reskins every
/// bound control live, the same mechanism as <see cref="ThemeOptions"/> and <see cref="ColorSchemeService"/>.
///
/// <para>An empty family means "theme default": the override is <b>removed</b> so the theme's bundled
/// face (Noto Sans) shows through again, rather than pinned to a copy of it.</para>
/// </summary>
public static class FontService
{
    /// <summary>Installed font families the picker offers, name-sorted. Index 0 in the picker is the
    /// synthetic "(theme default)" entry — these back indices 1…N.</summary>
    public static IReadOnlyList<string> Families
    {
        get
        {
            if (_families is { Count: > 0 }) return _families;
            // Don't memoize an early-call empty result (FontManager not yet initialized) — that would
            // poison the picker permanently. Cache only once a real family list is available.
            var built = BuildFamilies();
            if (built.Count > 0) _families = built;
            return built;
        }
    }
    private static IReadOnlyList<string>? _families;

    private static readonly string[] _keys = { "Bevel.Font.UI", "Bevel.Font.Caption" };
    private static string _applied = "";

    private static IReadOnlyList<string> BuildFamilies()
    {
        try
        {
            return FontManager.Current.SystemFonts
                .Select(f => f.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct()
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch
        {
            // FontManager needs an initialized Avalonia app; if we're called too early just offer
            // the theme default (index 0) — a picker with one entry is still coherent.
            return Array.Empty<string>();
        }
    }

    /// <summary>Applies a UI font family live. Empty/whitespace restores the theme default by removing
    /// the shadow. No-op if already applied. Must run on the UI thread.</summary>
    public static void Apply(string? family)
    {
        var fam = string.IsNullOrWhiteSpace(family) ? "" : family!.Trim();
        if (fam == _applied) return;
        if (Application.Current?.Resources is not { } res) return;

        if (fam.Length == 0)
        {
            foreach (var key in _keys) res.Remove(key);
        }
        else
        {
            var ff = new FontFamily(fam);
            foreach (var key in _keys) res[key] = ff;
        }
        _applied = fam;
    }
}
