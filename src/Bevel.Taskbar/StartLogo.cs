using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Platform;
using Path = Avalonia.Controls.Shapes.Path;

namespace Bevel.Taskbar;

/// <summary>
/// The classic Start-button OS badge, picked by host OS at runtime: the Windows flag and Tux load
/// from SVG assets (avares://Bevel.Taskbar/Assets/StartBadge/…) parsed into Avalonia vector paths,
/// while the Apple mark stays self-drawn geometry — all three resolution-independent, one binary,
/// right badge per platform. Marks are brand-fixed by design (trademarks, not theme colors).
///
/// The SVG reader supports only the flat-fill subset Illustrator exports for these files —
/// &lt;style&gt; class fills plus &lt;path&gt;/&lt;polygon&gt; elements — deliberately, to avoid a
/// heavyweight SVG-rendering dependency for two static logos. No gradients, transforms or strokes.
/// </summary>
public static class StartLogo
{
    /// <summary>The three badge variants, so a render harness can exercise all of them regardless
    /// of the host OS (production always uses <see cref="For(double)"/>, which picks by OS).</summary>
    internal enum Kind { Windows, Apple, Tux }

    /// <summary>Cached parsed SVGs: viewBox size + the (geometry, fill) shapes, shareable across
    /// the fresh Path/Canvas controls each <see cref="Build"/> call creates.</summary>
    private static readonly ConcurrentDictionary<string, ParsedSvg> SvgCache = new();

    /// <summary>Builds the host OS's badge as a <paramref name="size"/>×<paramref name="size"/> control.</summary>
    public static Control For(double size) => Build(
        OperatingSystem.IsWindows() ? Kind.Windows
        : OperatingSystem.IsMacOS() ? Kind.Apple
        : Kind.Tux, size);

    internal static Control Build(Kind kind, double size) => kind switch
    {
        Kind.Apple => Apple(size),
        Kind.Windows => Svg("windows.svg", size),
        _ => Svg("tux.svg", size),
    };

    // ── Windows / Tux: SVG assets → vector paths ────────────────────────

    private static Control Svg(string file, double size)
    {
        try
        {
            var svg = SvgCache.GetOrAdd(file, LoadSvg);
            var canvas = new Canvas { Width = svg.Width, Height = svg.Height };
            foreach (var (geometry, fill) in svg.Shapes)
                canvas.Children.Add(new Path { Data = geometry, Fill = fill });
            return new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, Child = canvas };
        }
        catch (Exception ex)
        {
            // A missing/renamed/malformed asset must not take down taskbar construction — this runs
            // from TaskbarView.OnLoaded with no handler above it. Degrade to an empty badge slot.
            TaskbarLog.Swallowed($"StartLogo.Svg({file})", ex);
            return new Canvas { Width = size, Height = size };
        }
    }

    internal sealed record ParsedSvg(double Width, double Height, IReadOnlyList<(Geometry Geometry, IBrush Fill)> Shapes);

    private static ParsedSvg LoadSvg(string file)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://Bevel.Taskbar/Assets/StartBadge/{file}"));
        return Parse(stream);
    }

    /// <summary>Parses the flat-fill SVG subset (a &lt;style&gt; block of class fills plus
    /// &lt;path&gt;/&lt;polygon&gt; elements) into geometry+fill shapes. Internal so the parser can be
    /// unit-tested against arbitrary SVG streams, independent of the avares asset pipeline.</summary>
    internal static ParsedSvg Parse(System.IO.Stream stream)
    {
        var root = XDocument.Load(stream).Root
            ?? throw new InvalidOperationException("empty SVG");

        var vb = (root.Attribute("viewBox")?.Value ?? "0 0 16 16")
            .Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        var width = double.Parse(vb[2], CultureInfo.InvariantCulture);
        var height = double.Parse(vb[3], CultureInfo.InvariantCulture);

        // Map the <style> block's `.class { fill: #hex }` rules to brushes.
        var classFill = new Dictionary<string, IBrush>();
        var styleText = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "style")?.Value ?? "";
        foreach (Match m in Regex.Matches(styleText, @"\.([A-Za-z0-9_-]+)\s*\{[^}]*?fill:\s*(#[0-9A-Fa-f]+)"))
            classFill[m.Groups[1].Value] = new SolidColorBrush(Color.Parse(m.Groups[2].Value));

        IBrush FillOf(XElement el)
        {
            var inline = el.Attribute("fill")?.Value;
            if (!string.IsNullOrEmpty(inline) && inline != "none")
                return new SolidColorBrush(Color.Parse(inline));
            var cls = el.Attribute("class")?.Value;
            return cls is not null && classFill.TryGetValue(cls, out var b) ? b : Brushes.Black;
        }

        var shapes = new List<(Geometry, IBrush)>();
        foreach (var el in root.Descendants())
        {
            switch (el.Name.LocalName)
            {
                case "path" when el.Attribute("d")?.Value is { Length: > 0 } d:
                    shapes.Add((Geometry.Parse(d), FillOf(el)));
                    break;
                case "polygon" when el.Attribute("points")?.Value is { Length: > 0 } pts:
                    shapes.Add((Geometry.Parse(PolygonToPath(pts)), FillOf(el)));
                    break;
            }
        }
        return new ParsedSvg(width, height, shapes);
    }

    /// <summary>Turns an SVG polygon's `x0,y0 x1,y1 …` into a closed path-data string.</summary>
    private static string PolygonToPath(string points)
    {
        var n = points.Split(new[] { ' ', ',', '\n', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder("M");
        for (var i = 0; i + 1 < n.Length; i += 2)
            sb.Append(i == 0 ? " " : " L ").Append(n[i]).Append(',').Append(n[i + 1]);
        return sb.Append(" Z").ToString();
    }

    // ── macOS: the Apple mark, self-drawn (single path, 24-unit box) ─────

    private static Control Apple(double size)
    {
        // Body + leaf as one path (two subpaths). A top-lit radial silver gradient gives the mark a
        // brushed-metal sheen that reads on both the classic gray and the Luna green Start buttons.
        var path = new Path
        {
            Fill = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.4, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.42, 0.18, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.85, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.85, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#FCFCFE"), 0),
                    new GradientStop(Color.Parse("#DBDDE1"), 0.45),
                    new GradientStop(Color.Parse("#A9ACB2"), 0.78),
                    new GradientStop(Color.Parse("#7C7F86"), 1),
                },
            },
            Data = Geometry.Parse(
                "M12.152 6.896c-.948 0-2.415-1.078-3.96-1.04-2.04.027-3.91 1.183-4.961 3.014-2.117 " +
                "3.675-.546 9.103 1.519 12.09 1.013 1.454 2.208 3.09 3.792 3.039 1.52-.065 2.09-.987 " +
                "3.935-.987 1.831 0 2.35.987 3.96.948 1.637-.026 2.676-1.48 3.676-2.948 1.156-1.688 " +
                "1.636-3.325 1.662-3.415-.039-.013-3.182-1.221-3.22-4.857-.026-3.04 2.48-4.494 " +
                "2.597-4.559-1.429-2.09-3.623-2.324-4.39-2.376-2-.156-3.675 1.09-4.61 1.09zM15.53 " +
                "3.83c.843-1.012 1.4-2.427 1.245-3.83-1.207.052-2.662.805-3.532 1.818-.78.896-1.454 " +
                "2.338-1.273 3.714 1.338.104 2.715-.688 3.559-1.701"),
        };
        var canvas = new Canvas { Width = 24, Height = 24, Children = { path } };
        return new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, Child = canvas };
    }
}
