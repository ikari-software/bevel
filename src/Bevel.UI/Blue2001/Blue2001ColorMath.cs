using System;
using System.Globalization;
using Avalonia.Media;

namespace Bevel.UI.Blue2001;

/// <summary>
/// Colour helpers for the runtime Luna variant factory (bevel-luna-variants). All Luna chrome is a
/// code-computable vector gradient, so a colour variant is an HSL transform of the reference (Blue)
/// palette and a gloss variant is a stop-pattern choice — no bitmaps, combined at runtime.
/// </summary>
internal static class Blue2001ColorMath
{
    /// <summary>Parses "#RRGGBB" or "#AARRGGBB" (leading # optional) into a Color.</summary>
    public static Color Hex(string hex)
    {
        var s = hex.StartsWith('#') ? hex[1..] : hex;
        if (s.Length == 6) s = "FF" + s;
        var v = uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    /// <summary>Mixes toward white by <paramref name="t"/> (0..1), preserving alpha.</summary>
    public static Color Lighten(Color c, double t) => Mix(c, Colors.White, t);

    /// <summary>Mixes toward black by <paramref name="t"/> (0..1), preserving alpha.</summary>
    public static Color Darken(Color c, double t) => Mix(c, Color.FromRgb(0, 0, 0), t);

    /// <summary>Linear RGB mix a→b by <paramref name="t"/> (0..1); keeps a's alpha.</summary>
    public static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(a.A,
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    /// <summary>Applies an HSL transform: absolute hue shift (degrees), and multipliers on saturation
    /// and lightness. Used to re-hue the reference (Blue) chrome palette into Silver/Black/Purple while
    /// preserving each stop's relative light→dark relationship. Alpha is preserved.</summary>
    public static Color Transform(Color c, double hueShiftDeg, double satMul, double lightMul, double lightShift = 0)
    {
        var (h, s, l) = ToHsl(c);
        h = (h + hueShiftDeg) % 360.0; if (h < 0) h += 360.0;
        s = Math.Clamp(s * satMul, 0, 1);
        l = Math.Clamp(l * lightMul + lightShift, 0, 1);
        return FromHsl(h, s, l, c.A);
    }

    public static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double h = 0, s, l = (max + min) / 2.0;
        double d = max - min;
        if (d == 0) { s = 0; }
        else
        {
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60.0;
        }
        return (h, s, l);
    }

    public static Color FromHsl(double h, double s, double l, byte a = 255)
    {
        double r, g, b;
        if (s == 0) { r = g = b = l; }
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            r = HueToRgb(p, q, h / 360.0 + 1.0 / 3.0);
            g = HueToRgb(p, q, h / 360.0);
            b = HueToRgb(p, q, h / 360.0 - 1.0 / 3.0);
        }
        return Color.FromArgb(a, (byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1; if (t > 1) t -= 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
        return p;
    }
}
