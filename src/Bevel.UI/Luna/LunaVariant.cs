using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using static Bevel.UI.Luna.LunaColorMath;

namespace Bevel.UI.Luna;

/// <summary>The Luna colour axis. Blue is the reference (identity transform); the others re-hue the
/// chrome family via HSL so the tuned light→dark relationships are preserved.</summary>
public enum LunaColorVariant { Blue, Silver, Black, Purple }

/// <summary>The Luna gloss axis. Hybrid = the ratified look (glossy dialog/task/caption controls, matte
/// bar + Start). Gloss = wet everywhere; Matte = flat/authentic everywhere.</summary>
public enum LunaGloss { Matte, Gloss, Hybrid }

/// <summary>How the colour axis re-hues the chrome family. Applied per gradient stop, so a variant is
/// the reference (Blue) chrome with its hue rotated / desaturated / darkened.</summary>
internal readonly record struct ColorXform(double HueShift, double SatMul, double LightMul, double LightShift)
{
    public static readonly ColorXform Identity = new(0, 1, 1, 0);
    public Color Apply(Color c) => this == Identity ? c : Transform(c, HueShift, SatMul, LightMul, LightShift);
}

/// <summary>A gloss role: how a surface reacts to the gloss axis. Control = glossy under Gloss/Hybrid,
/// matte under Matte. Chrome = matte under Matte/Hybrid, glossy under Gloss. Inert = gloss-independent.</summary>
internal enum GlossRole { Control, Chrome, Inert }

/// <summary>One factory-generated surface: its reference (Blue + current-gloss) stops, whether the colour
/// axis re-hues it, and its gloss role.</summary>
internal sealed record Surface(string Key, bool Chromatic, GlossRole Role, (double Off, string Hex)[] Stops, bool Horizontal = false)
{
    public static Surface Solid(string key, bool chromatic, string hex) =>
        new(key, chromatic, GlossRole.Inert, new[] { (0.0, hex) });
    public bool IsSolid => Stops.Length == 1;
}

/// <summary>
/// Runtime Luna variant engine (bevel-luna-variants). Every Luna chrome surface is a code-computable
/// vector gradient, so a look = colour transform × gloss profile, combined here and injected into
/// <see cref="Application"/>.Resources — which outranks the theme's Styles-set resources, so the XAML
/// brushes remain a safe fallback while these drive the selected variant. Only the surfaces that vary
/// by colour and/or gloss are generated; the constant neutrals/greens/semantics stay in the theme.
/// </summary>
public static class LunaVariantService
{
    public static readonly IReadOnlyList<(string Id, string Display)> Colors = new[]
    {
        ("Blue", "Luna Blue"), ("Silver", "Silver"), ("Black", "Black"), ("Purple", "Purple"),
    };
    public static readonly IReadOnlyList<(string Id, string Display)> Glosses = new[]
    {
        ("Hybrid", "Hybrid"), ("Gloss", "Glossy"), ("Matte", "Matte"),
    };
    public const string DefaultColor = "Blue";
    public const string DefaultGloss = "Hybrid";

    private static ColorXform Xform(LunaColorVariant v) => v switch
    {
        // Silver: drain the blue to a warm slate-grey, lift slightly. Black: near-grey + darken.
        // Purple: rotate blue(~220°) toward violet(~275°). Tuned against the reference chrome.
        LunaColorVariant.Silver => new ColorXform(HueShift: -6, SatMul: 0.22, LightMul: 1.4, LightShift: 0.05),
        LunaColorVariant.Black => new ColorXform(HueShift: 0, SatMul: 0.22, LightMul: 0.45, LightShift: 0.0),
        LunaColorVariant.Purple => new ColorXform(HueShift: 58, SatMul: 0.92, LightMul: 1.0, LightShift: 0.0),
        _ => ColorXform.Identity,
    };

    // ── Reference surfaces: exact current (Blue + ratified-hybrid) values, moved out of LunaTheme.axaml
    //    for the ones that vary. Chromatic=true → re-hued by the colour axis. ────────────────────────
    private static readonly Surface[] Surfaces =
    {
        // Chrome bands / bars (matte in Hybrid, beadified under Gloss)
        new("Bevel.Brush.CaptionActive", true, GlossRole.Chrome, new[]{
            (0.0,"3F88EC"),(0.06,"1E6EE7"),(0.28,"0D5CE3"),(0.62,"0A51DF"),(1.0,"003EC9")}),
        new("Bevel.Brush.CaptionInactive", true, GlossRole.Chrome, new[]{
            (0.0,"9EBBEA"),(0.5,"7FA0D2"),(1.0,"5E82B8")}),
        new("Bevel.Brush.TaskbarBackground", true, GlossRole.Chrome, new[]{
            (0.0,"4E95F2"),(0.02,"3A84EA"),(0.10,"2358C6"),(0.5,"2054BE"),(0.90,"2358C6"),(0.98,"3A84EA"),(1.0,"4E95F2")}),
        new("Bevel.Brush.TrayWell", true, GlossRole.Chrome, new[]{
            (0.0,"1E9AE8"),(0.5,"1585DF"),(1.0,"1F97E6")}),
        new("Luna.Brush.StartMenuHeader", true, GlossRole.Chrome, new[]{
            (0.0,"4F97EE"),(0.5,"2F7EE6"),(1.0,"1E63D6")}),
        new("Luna.Brush.StartMenuFooter", true, GlossRole.Chrome, new[]{
            (0.0,"3F8CE8"),(0.5,"245EDC"),(1.0,"1A4FBE")}),
        new("Luna.Brush.StartMenuPlacesColumn", true, GlossRole.Inert, new[]{ (0.0,"DCE8FA"),(1.0,"C6D8F0") }, Horizontal: true),

        // Glossy chrome controls (bead in Gloss/Hybrid, flattened under Matte)
        new("Luna.Brush.TaskButton", true, GlossRole.Control, new[]{
            (0.0,"5A97F0"),(0.5,"2F6FE0"),(0.5,"215FD8"),(1.0,"4483EC")}),
        new("Luna.Brush.TaskButtonHover", true, GlossRole.Control, new[]{
            (0.0,"79ABF2"),(0.5,"4384E4"),(1.0,"5695EC")}),
        new("Luna.Brush.TaskButtonChecked", true, GlossRole.Control, new[]{
            (0.0,"1B4EA8"),(0.5,"2559BC"),(1.0,"2E63C8")}),
        new("Luna.Brush.CaptionButton", true, GlossRole.Control, new[]{
            (0.0,"5AA6FF"),(0.5,"1E70EF"),(0.5,"0D57E6"),(1.0,"3F8BF3")}),

        // Beige control face (neutral colour, but glossy in Gloss/Hybrid, concave under Matte)
        new("Luna.Brush.Button", false, GlossRole.Control, new[]{
            (0.0,"FFFFFF"),(0.48,"F4F2E9"),(0.52,"ECE9D8"),(1.0,"E2DDC9")}),
        new("Luna.Brush.ButtonPressed", false, GlossRole.Inert, new[]{
            (0.0,"DED9C6"),(0.5,"E6E1D0"),(1.0,"EFEBDC")}),

        // Green Start pill (accent colour is constant across variants; matte in Hybrid, bead under Gloss)
        new("Luna.Brush.Start", false, GlossRole.Chrome, new[]{ (0.0,"5CB44A"),(0.5,"2C831F"),(1.0,"46A035") }),
        new("Luna.Brush.StartHover", false, GlossRole.Chrome, new[]{ (0.0,"7ED16C"),(0.5,"48AE38"),(1.0,"379B29") }),

        // Green progress (accent constant; glossy control)
        new("Luna.Brush.Progress", false, GlossRole.Control, new[]{
            (0.0,"B6F0A8"),(0.5,"5CC24A"),(0.5,"3BA32F"),(1.0,"63C94E")}),

        // Chrome solids (re-hued, gloss-inert)
        Surface.Solid("Bevel.Brush.WindowFrame", true, "0831D9"),
        Surface.Solid("Luna.Brush.TaskButtonBorder", true, "1C4D9C"),
        Surface.Solid("Luna.Brush.TaskButtonCheckedBorder", true, "12358A"),
        Surface.Solid("Luna.Brush.CaptionButtonBorder", true, "0A3EA8"),
        Surface.Solid("Luna.Brush.ButtonBorder", true, "7B9EBD"),
        Surface.Solid("Luna.Brush.ButtonBorderDefault", true, "2C628B"),
        Surface.Solid("Luna.Brush.Arrow", true, "1B3A6B"),
        Surface.Solid("Luna.Brush.StartMenuBorder", true, "1857C9"),
        Surface.Solid("Luna.Brush.StartMenuFooterHover", true, "4E93EC"),
        Surface.Solid("Luna.Brush.StartMenuDivider", true, "C9D2E4"),
        Surface.Solid("Luna.Brush.StartMenuPlacesDivider", true, "AEBFDB"),
        Surface.Solid("Luna.Brush.StartMenuComputerIcon", true, "3F6FC5"),
        Surface.Solid("Luna.Brush.StartMenuHelpIcon", true, "3F8EE6"),
        Surface.Solid("Bevel.Brush.Highlight", true, "316AC5"),
    };

    // Colour tokens (Bevel.Color.* + SystemColors caption keys) that must follow the colour axis too.
    private static readonly (string Key, string Hex)[] ColorTokens =
    {
        ("Bevel.Color.WindowFrame", "0831D9"),
        ("Bevel.Color.Highlight", "316AC5"),
        ("Bevel.Color.HotTracking", "0A51DF"),
        ("Bevel.Color.ActiveTitle", "0A51DF"),
        ("Bevel.Color.GradientActiveTitle", "3F88EC"),
    };

    private static string _appliedColor = DefaultColor;
    private static string _appliedGloss = DefaultGloss;
    private static readonly List<string> _injected = new();

    public static bool IsKnownColor(string id) { foreach (var (i, _) in Colors) if (i == id) return true; return false; }
    public static bool IsKnownGloss(string id) { foreach (var (i, _) in Glosses) if (i == id) return true; return false; }

    /// <summary>Applies a (colour, gloss) Luna look live by regenerating the varying chrome brushes and
    /// injecting them into Application.Resources. Unknown ids fall back to the defaults. UI thread only.</summary>
    public static void Apply(string? colorId, string? glossId)
    {
        var color = string.IsNullOrWhiteSpace(colorId) || !IsKnownColor(colorId!) ? DefaultColor : colorId!;
        var gloss = string.IsNullOrWhiteSpace(glossId) || !IsKnownGloss(glossId!) ? DefaultGloss : glossId!;
        if (color == _appliedColor && gloss == _appliedGloss && _injected.Count > 0) return;
        if (Application.Current?.Resources is not { } res) return;

        var variant = Enum.Parse<LunaColorVariant>(color);
        var g = Enum.Parse<LunaGloss>(gloss);
        var xform = Xform(variant);

        // Drop the previous injection so looks don't stack.
        foreach (var key in _injected) res.Remove(key);
        _injected.Clear();

        foreach (var s in Surfaces)
        {
            res[s.Key] = BuildBrush(s, xform, g);
            _injected.Add(s.Key);
        }
        foreach (var (key, hex) in ColorTokens)
        {
            res[key] = xform.Apply(Hex(hex));
            _injected.Add(key);
        }

        _appliedColor = color;
        _appliedGloss = gloss;
    }

    private static IBrush BuildBrush(Surface s, ColorXform xform, LunaGloss gloss)
    {
        // Re-hue first (chrome only), then let the gloss profile reshape the arrangement.
        Color C(string hex) => s.Chromatic ? xform.Apply(Hex(hex)) : Hex(hex);

        if (s.IsSolid) return new SolidColorBrush(C(s.Stops[0].Hex));

        // Does the requested look want THIS surface glossy? Hybrid = glossy controls, matte chrome.
        bool targetGlossy = gloss switch
        {
            LunaGloss.Gloss => true,
            LunaGloss.Matte => false,
            _ => s.Role == GlossRole.Control, // Hybrid
        };

        List<GradientStop> stops;
        if (s.Role == GlossRole.Inert || targetGlossy == IsReferenceGlossy(s.Role))
        {
            // Reference arrangement already matches the requested gloss — keep the tuned stored stops
            // (e.g. the 7-stop concave taskbar, the 5-stop caption band), just re-hued.
            stops = new List<GradientStop>();
            foreach (var (off, hex) in s.Stops) stops.Add(new GradientStop(C(hex), off));
        }
        else
        {
            stops = targetGlossy ? Bead(BaseColor(s, C)) : Matte(BaseColor(s, C));
        }

        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = s.Horizontal ? new RelativePoint(1, 0, RelativeUnit.Relative)
                                    : new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops().Also(stops),
        };
    }

    // The reference (stored) arrangement: Control surfaces are glossy, Chrome surfaces are matte.
    private static bool IsReferenceGlossy(GlossRole role) => role == GlossRole.Control;

    /// <summary>Representative mid colour of a surface's reference stops (drop pure-white sheen stops so
    /// the beige button's base is its body, not the highlight).</summary>
    private static Color BaseColor(Surface s, Func<string, Color> c)
    {
        // Prefer the deepest stop's neighbour: the stop nearest offset 0.55 that isn't near-white.
        (double Off, string Hex) best = s.Stops[^1];
        foreach (var st in s.Stops)
            if (Math.Abs(st.Off - 0.55) < Math.Abs(best.Off - 0.55)) best = st;
        return c(best.Hex);
    }

    /// <summary>Glossy "shiny bead": bright top sheen, hard 50% split, lighter bottom.</summary>
    private static List<GradientStop> Bead(Color b) => new()
    {
        new GradientStop(Lighten(b, 0.34), 0.0),
        new GradientStop(Lighten(b, 0.08), 0.5),
        new GradientStop(Darken(b, 0.10), 0.5),
        new GradientStop(Lighten(b, 0.10), 1.0),
    };

    /// <summary>Matte concave: soft light top edge, dim body, faint lighter bottom — no hard split.</summary>
    private static List<GradientStop> Matte(Color b) => new()
    {
        new GradientStop(Lighten(b, 0.20), 0.0),
        new GradientStop(Darken(b, 0.03), 0.55),
        new GradientStop(Lighten(b, 0.05), 1.0),
    };
}

internal static class GradientStopsExt
{
    /// <summary>Fills a fresh GradientStops with the given stops (small ergonomic helper).</summary>
    public static GradientStops Also(this GradientStops gs, List<GradientStop> stops)
    {
        foreach (var s in stops) gs.Add(s);
        return gs;
    }
}
