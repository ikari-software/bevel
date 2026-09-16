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
        ("luna", "Bevel Luna (XP)"),
        ("flat", "Bevel Flat (preview)"),
    };

    /// <summary>The default theme id (the base Win2000 tokens). Empty/unknown resolves here.</summary>
    public const string DefaultTheme = "win2000";

    /// <summary>The currently-applied theme id. Surfaces that swap whole layouts by theme (the Start
    /// menu's classic single column vs Luna's two-column panel) read this to choose which to show.</summary>
    public static string Current => _appliedId;

    private static readonly Uri BaseUri = new("avares://Bevel.App/App.axaml");
    private static ResourceInclude? _applied;
    private static StyleInclude? _appliedStyles;
    private static string _appliedId = DefaultTheme;

    /// <summary>The token-override dictionary a theme merges, or null when it ships none (base theme, or
    /// a Styles-set theme that carries its tokens inside its own <see cref="StylesFor"/> bundle).</summary>
    private static string? SourceFor(string id) => id switch
    {
        "flat" => "avares://Bevel.Themes.Win2000/ThemeFlat.axaml",
        _ => null,
    };

    /// <summary>A theme's control-template <c>Styles</c> set (its own <c>ControlTheme</c>s + tokens),
    /// added to <see cref="Application.Styles"/> so it overrides the Classic templates by precedence,
    /// or null for a token-only theme that reuses the shared Classic templates (bevel-dob). This is
    /// what lets a real alternate visual style (Luna's gradient chrome) replace geometry, not just
    /// colours.</summary>
    private static string? StylesFor(string id) => id switch
    {
        "luna" => "avares://Bevel.Themes.Luna/LunaTheme.axaml",
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
    /// the UI thread. Returns TRUE when the template engine is now on the requested theme (including the
    /// already-applied no-op), FALSE when the live re-template FAILED — the caller must then NOT apply
    /// the colour variant / font for that theme, or the two engines end up disagreeing (ce-review).</summary>
    public static bool Apply(string? id)
    {
        var theme = string.IsNullOrWhiteSpace(id) || !IsKnown(id!) ? DefaultTheme : id!;
        if (theme == _appliedId) return true;   // already in place — safe to apply the variant/font
        if (Application.Current is not { } app || app.Resources is not { } res) return false;

        // Drop the previously-applied theme contributions (if any) so themes don't stack — both the
        // token dict at Application level and the control-template Styles set.
        if (_applied is not null)
        {
            res.MergedDictionaries.Remove(_applied);
            _applied = null;
        }
        if (_appliedStyles is not null)
        {
            app.Styles.Remove(_appliedStyles);
            _appliedStyles = null;
        }

        if (SourceFor(theme) is { } src)
        {
            var dict = new ResourceInclude(BaseUri) { Source = new Uri(src) };
            res.MergedDictionaries.Add(dict);
            _applied = dict;
        }
        // Append the theme's Styles LAST so its ControlThemes + tokens win over the base Win2000 set.
        // Adding them re-resolves every Bevel.Theme.* DynamicResource, re-templating live controls in
        // place. Guard it: a faulty re-template inside a (third-party) control template must degrade to a
        // partial swap, never crash the whole shell on a theme change — a control-theme handler throwing
        // during the resource-changed notification would otherwise propagate straight out of here.
        var applied = true;
        if (StylesFor(theme) is { } stylesSrc)
        {
            var styles = new StyleInclude(BaseUri) { Source = new Uri(stylesSrc) };
            _appliedStyles = styles;   // tracked for removal even if a re-template side-effect throws
            try { app.Styles.Add(styles); }
            catch (Exception ex)
            {
                applied = false;
                Console.Error.WriteLine($"[ThemeService] live re-template raised (theme={theme}): {ex.Message}");
                // ROLL THE FAILED ENTRY BACK OUT (bevel-bxol). Styles.Add inserts first and re-resolves
                // after, so a throw leaves a HALF-WIRED StyleInclude sitting in app.Styles — its Loaded
                // content never materialised. Every later resource lookup walks that entry, and
                // Avalonia.Styling.Styles.TryGetResource NREs on it: the next control attached to the
                // logical tree (TaskbarView.OnLoaded adding children) dies on the dispatcher and takes the
                // whole process with it. Leaving it for the NEXT Apply() to remove is too late — nothing
                // survives to call it. Degrade to the previous theme's templates instead: partial, but live.
                try { app.Styles.Remove(styles); } catch { /* best-effort; never throw out of a rollback */ }
                _appliedStyles = null;
            }
        }
        // Commit the applied id only if nothing threw. On a faulty swap we leave _appliedId at the previous
        // theme so a retry of the SAME theme isn't swallowed as a no-op — otherwise a half-applied theme
        // would latch and Apply(theme) could never re-run to recover.
        if (applied) _appliedId = theme;
        Glyphs.InvalidateThemeCache();   // token bundle swapped — drop cached icon brushes (bevel-lha4)
        return applied;

        // Live re-templating (bevel-dob): controls bind Theme="{DynamicResource Bevel.Theme.*}", so adding
        // the theme's Styles above re-resolves those keys and re-templates every bound control IN PLACE —
        // geometry + layout, no window rebuild. (This supersedes the old detach/reattach approach, which
        // visibly rebuilt windows and crashed.) Surface brushes (Bevel.Brush.*) update as plain
        // DynamicResource tokens. Controls NOT on the contract keep their creation-time template until the
        // surface is next built.
    }
}
