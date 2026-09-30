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
/// The Start-button badge: Bevel's own <see cref="Bevel.UI.BevelMark"/> everywhere, except Linux, which keeps
/// Tux (Larry Ewing's permissively licensed mascot; the acknowledgment lives in the About box). Tux loads from an
/// SVG asset (avares://Bevel.Taskbar/Assets/StartBadge/tux.svg) parsed into Avalonia vector paths.
///
/// This used to show the host vendor's logo (Windows flag / Apple mark). That was removed on purpose: embedding
/// a third-party graphic trademark in the primary control is the single highest IP risk in the product, and
/// Apple/Microsoft guidelines both prohibit it. Do not reintroduce a vendor badge.
///
/// The SVG reader supports only the flat-fill subset Illustrator exports for the Tux file —
/// &lt;style&gt; class fills plus &lt;path&gt;/&lt;polygon&gt; elements — deliberately, to avoid a
/// heavyweight SVG-rendering dependency for one static logo. No gradients, transforms or strokes.
/// </summary>
public static class StartLogo
{
    /// <summary>The badge variants, so a render harness can exercise both regardless of the host OS
    /// (production always uses <see cref="For(double)"/>, which picks by OS).</summary>
    internal enum Kind { Bevel, Tux }

    /// <summary>Badge edge when the active theme publishes no <c>Bevel.Metric.StartBadgeSize</c>.</summary>
    public const double DefaultSize = 20;

    /// <summary>Cached parsed SVGs: viewBox size + the (geometry, fill) shapes, shareable across
    /// the fresh Path/Canvas controls each <see cref="Build"/> call creates.</summary>
    private static readonly ConcurrentDictionary<string, ParsedSvg> SvgCache = new();

    /// <summary>Builds the host OS's badge as a <paramref name="size"/>×<paramref name="size"/> control.</summary>
    public static Control For(double size, bool fullDetail = false)
        => Build(OperatingSystem.IsLinux() ? Kind.Tux : Kind.Bevel, size, fullDetail);

    internal static Control Build(Kind kind, double size, bool fullDetail = false) => kind switch
    {
        Kind.Tux => Svg("tux.svg", size),
        _ => Bevel.UI.BevelMark.Create(size, fullDetail),
    };

    // ── Tux: SVG asset → vector paths ───────────────────────────────────

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
}
