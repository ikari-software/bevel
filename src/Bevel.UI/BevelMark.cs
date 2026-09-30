using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace Bevel.UI;

/// <summary>
/// Bevel's own mark: a glass cube outlined in a continuous spectrum — the Start badge, the About box,
/// and the app icon (<c>packaging/macos/BevelMark.svg</c> / <c>Bevel.icns</c>). Code-drawn vector in a
/// 100-unit box, so it is crisp at any size and DPI.
///
/// Two optical sizes of ONE mark, sharing the same silhouette, spectrum edges and glass treatment:
/// <list type="bullet">
/// <item><b>Full</b> (≥ <see cref="FullDetailFromSize"/> px): a solid core seen THROUGH the glass. Layer order is
/// what sells the depth — the cube's far faces at the bottom, the core above them, then the near faces as
/// translucent glass OVER the core, and the crisp coloured edges on top. The core is never drawn on top of
/// the shell (it then reads as a sticker on the front).</item>
/// <item><b>Glass</b> (smaller): the same cube without the core. At Start-button size the core collapses
/// into a busy blob, so the small mark is the same object, simplified — not a different design.</item>
/// </list>
///
/// Faces are fixed TRANSLUCENT tints, deliberately not theme tokens: the glass shows whatever button it sits
/// on, so the mark adapts by transparency (teal over the Luna green pill, pale over Classic grey) with no
/// token for the two runtime recolour engines to override. The nine spectrum edges are brand-fixed — they are
/// the signature, like a logo. Nothing here reproduces a third-party mark: a four-colour 2x2 palette or a
/// window-pane layout would, which is why the spectrum runs continuously round the silhouette instead.
/// </summary>
public static class BevelMark
{
    [Flags]
    internal enum Layers
    {
        None = 0,
        FarFaces = 1,
        Rays = 2,
        Core = 4,
        NearFaces = 8,
        Edges = 16,
        Glass = FarFaces | NearFaces | Edges,
        Full = Glass | Rays | Core,
    }

    /// <summary>Bottom-to-top paint order. THIS is what makes the core read as inside the glass: the near faces
    /// are translucent and must be painted OVER the core. Swap the core above them and it looks stuck on the
    /// front. Kept as data so a test can pin it.</summary>
    internal static readonly Layers[] DrawOrder =
        { Layers.FarFaces, Layers.Rays, Layers.Core, Layers.NearFaces, Layers.Edges };

    /// <summary>Below this the core is a few pixels of clutter; at and above it reads as a cube inside a cube.</summary>
    internal const double FullDetailFromSize = 24;

    /// <param name="fullDetail">Force the full mark below <see cref="FullDetailFromSize"/> (the user's "detailed badge"
    /// option). It can only add detail, never remove it.</param>
    internal static Layers LayersFor(double size, bool fullDetail = false)
        => fullDetail || size >= FullDetailFromSize ? Layers.Full : Layers.Glass;

    /// <summary>One of the nine visible edges of the cube: six round the silhouette, three spokes meeting at the
    /// near corner. <see cref="GlassWidth"/> is heavier because the small mark needs the colour to survive.</summary>
    internal readonly record struct Edge(string Name, string Data, string Hex, double FullWidth, double GlassWidth);

    internal static readonly IReadOnlyList<Edge> Edges = new Edge[]
    {
        // Silhouette, clockwise from the top: the spectrum runs once round the cube.
        new("top-right",    "M50 8 86 29",  "#FF8A1F", 5.5, 6.0),
        new("right",        "M86 29V71",    "#C6E32B", 5.5, 6.0),
        new("bottom-right", "M86 71 50 92", "#34D68A", 5.5, 6.0),
        new("bottom-left",  "M50 92 14 71", "#22C3F6", 5.5, 6.0),
        new("left",         "M14 71V29",    "#6B7BFF", 5.5, 6.0),
        new("top-left",     "M14 29 50 8",  "#FF4D8D", 5.5, 6.0),
        // Spokes from the near corner.
        new("spoke-left",   "M14 29 50 50", "#FFC233", 4.5, 5.5),
        new("spoke-right",  "M50 50 86 29", "#8BE04A", 4.5, 5.5),
        new("spoke-down",   "M50 50V92",    "#2BD4D0", 4.5, 5.5),
    };

    // Far faces: the three faces seen through the glass (floor and the two back walls).
    private static readonly (string Data, string Hex, double Alpha)[] FarFaces =
    {
        ("M50 50 86 71 50 92 14 71Z", "#9DB8EA", .30),
        ("M50 8 86 29V71L50 50Z",     "#4A74C4", .22),
        ("M50 8 14 29V71L50 50Z",     "#8FB0EC", .22),
    };

    // Near faces: the glass OVER the core (top, left, right).
    private static readonly (string Data, string Hex, double Alpha)[] NearFaces =
    {
        ("M50 8 86 29 50 50 14 29Z", "#EAF2FF", .38),
        ("M14 29 50 50V92L14 71Z",   "#7BA3E6", .34),
        ("M50 50 86 29V71L50 92Z",   "#3A66BC", .40),
    };

    // The solid core: half the shell, sharing its centre.
    private static readonly (string Data, string Hex)[] CoreFaces =
    {
        ("M50 29 68 39.5 50 50 32 39.5Z", "#F2F7FF"),
        ("M32 39.5 50 50V71L32 60.5Z",    "#6E9BE6"),
        ("M50 50 68 39.5V60.5L50 71Z",    "#2B5DB4"),
    };

    private static readonly (string Data, string Hex)[] CoreEdges =
    {
        ("M32 39.5 50 29 68 39.5", "#FFB14D"),
        ("M68 39.5V60.5",          "#B7E64A"),
        ("M32 39.5V60.5",          "#7C88FF"),
        ("M50 50V71",              "#2BD4D0"),
    };

    // Depth connectors from each shell corner to the matching core corner.
    private const string RaysData = "M50 8V29M86 29 68 39.5M86 71 68 60.5M50 92V71M14 71 32 60.5M14 29 32 39.5";

    /// <summary>Builds the mark as a <paramref name="size"/> × <paramref name="size"/> control.</summary>
    public static Control Create(double size, bool fullDetail = false)
    {
        var layers = LayersFor(size, fullDetail);
        var full = layers.HasFlag(Layers.Core);
        var canvas = new Canvas { Width = 100, Height = 100 };

        foreach (var layer in DrawOrder)
        {
            if (!layers.HasFlag(layer)) continue;
            switch (layer)
            {
                case Layers.FarFaces:
                    foreach (var (data, hex, alpha) in FarFaces) canvas.Children.Add(Face(data, Tint(hex, alpha)));
                    break;
                case Layers.Rays:
                    canvas.Children.Add(Stroke(RaysData, Tint("#FFFFFF", .6), 2));
                    break;
                case Layers.Core:
                    foreach (var (data, hex) in CoreFaces) canvas.Children.Add(Face(data, Tint(hex, 1)));
                    foreach (var (data, hex) in CoreEdges) canvas.Children.Add(Stroke(data, Tint(hex, .95), 3.6));
                    break;
                case Layers.NearFaces:
                    foreach (var (data, hex, alpha) in NearFaces) canvas.Children.Add(Face(data, Tint(hex, alpha)));
                    break;
                case Layers.Edges:
                    foreach (var e in Edges)
                        canvas.Children.Add(Stroke(e.Data, Tint(e.Hex, 1), full ? e.FullWidth : e.GlassWidth));
                    break;
            }
        }

        return new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, Child = canvas };
    }

    private static Path Face(string data, IBrush fill) => new() { Data = Geometry.Parse(data), Fill = fill };

    private static Path Stroke(string data, IBrush stroke, double width) => new()
    {
        Data = Geometry.Parse(data),
        Stroke = stroke,
        StrokeThickness = width,
        StrokeLineCap = PenLineCap.Round,
        StrokeJoin = PenLineJoin.Round,
    };

    private static IBrush Tint(string hex, double alpha)
    {
        var c = Color.Parse(hex);
        return new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), c.R, c.G, c.B));
    }
}
