using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Classic.Avalonia.Theme;

namespace Bevel.UI;

/// <summary>The classic Win2000 3-D edge styles.</summary>
public enum BevelStyle { Raised, Sunken, Etched, ThinRaised, ThinSunken }

/// <summary>
/// Standalone Win2000 3-D border primitive for Bevel-owned chrome and previews. Shares the
/// Smooth-mode painter (<see cref="BevelEdgeRenderer"/>) and <see cref="EdgeRendering"/> enum
/// with the themed <see cref="ClassicBorderDecorator"/> (bevel-38y.1); the palette is exposed
/// as properties so it also renders standalone (BevelShot previews, dialogs).
/// </summary>
public class BevelBorder : Decorator
{
    public static readonly StyledProperty<BevelStyle> BevelStyleProperty =
        AvaloniaProperty.Register<BevelBorder, BevelStyle>(nameof(BevelStyle), BevelStyle.Raised);

    // Default Smooth at all scalings (decision 2026-07-06): the ramp reads well even at 1×/2×
    // and it removes 150% fuzziness. Crisp remains available as a user override. See spec §8.
    public static readonly StyledProperty<EdgeRendering> EdgeRenderingProperty =
        AvaloniaProperty.Register<BevelBorder, EdgeRendering>(nameof(EdgeRendering), EdgeRendering.Smooth);

    /// <summary>Logical edge thickness (DIP). 2 for full 3-D edges, 1 for the Thin* styles.</summary>
    public static readonly StyledProperty<double> EdgeThicknessProperty =
        AvaloniaProperty.Register<BevelBorder, double>(nameof(EdgeThickness), 2d);

    public static readonly StyledProperty<IBrush?> FaceProperty =
        AvaloniaProperty.Register<BevelBorder, IBrush?>(nameof(Face), new SolidColorBrush(Color.FromRgb(0xD4, 0xD0, 0xC8)));

    // Win2000 "Windows Standard" 3-D colours (05-theming.md §Colors).
    public Color Hilight { get; set; } = Color.FromRgb(0xFF, 0xFF, 0xFF);
    public Color Light { get; set; } = Color.FromRgb(0xD4, 0xD0, 0xC8);
    public Color Shadow { get; set; } = Color.FromRgb(0x80, 0x80, 0x80);
    public Color DarkShadow { get; set; } = Color.FromRgb(0x40, 0x40, 0x40);

    public BevelStyle BevelStyle { get => GetValue(BevelStyleProperty); set => SetValue(BevelStyleProperty, value); }
    public EdgeRendering EdgeRendering { get => GetValue(EdgeRenderingProperty); set => SetValue(EdgeRenderingProperty, value); }
    public double EdgeThickness { get => GetValue(EdgeThicknessProperty); set => SetValue(EdgeThicknessProperty, value); }
    public IBrush? Face { get => GetValue(FaceProperty); set => SetValue(FaceProperty, value); }

    static BevelBorder()
    {
        AffectsRender<BevelBorder>(BevelStyleProperty, EdgeRenderingProperty, EdgeThicknessProperty, FaceProperty);
        AffectsMeasure<BevelBorder>(EdgeThicknessProperty);
    }

    private Color FaceColor => (Face as ISolidColorBrush)?.Color ?? Light;

    /// <summary>Outer→inner colour bands for the light (top/left) and dark (bottom/right) sides.</summary>
    private (Color[] light, Color[] dark) Bands() => BevelStyle switch
    {
        BevelStyle.Raised     => (new[] { Hilight, Light }, new[] { DarkShadow, Shadow }),
        BevelStyle.Sunken     => (new[] { Shadow, DarkShadow }, new[] { Hilight, Light }),
        BevelStyle.Etched     => (new[] { Shadow, Hilight }, new[] { Shadow, Hilight }),
        BevelStyle.ThinRaised => (new[] { Hilight }, new[] { Shadow }),
        BevelStyle.ThinSunken => (new[] { Shadow }, new[] { Hilight }),
        _ => (new[] { Hilight, Light }, new[] { DarkShadow, Shadow }),
    };

    protected override Size MeasureOverride(Size availableSize)
    {
        var t = EdgeThickness;
        var child = Child;
        if (child is null) return new Size(t * 2, t * 2);
        child.Measure(availableSize.Deflate(new Thickness(t)));
        return child.DesiredSize.Inflate(new Thickness(t));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var t = EdgeThickness;
        Child?.Arrange(new Rect(finalSize).Deflate(new Thickness(t)));
        return finalSize;
    }

    public override void Render(DrawingContext ctx)
    {
        var rect = new Rect(Bounds.Size);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        if (Face is { } face) ctx.FillRectangle(face, rect);

        var t = EdgeThickness;
        var (light, dark) = Bands();
        var mode = EdgeRendering;
        var w = rect.Width;
        var h = rect.Height;

        if (w < 2 * t || h < 2 * t) return;

        if (mode == EdgeRendering.Smooth)
        {
            // Shared painter: mitered trapezoids with the eased outer→inner→face gradient.
            BevelEdgeRenderer.DrawSmoothFrame(ctx, rect, new Thickness(t), light, dark, FaceColor);
            return;
        }

        // Crisp: mitered frame of hard 1-DIP bands — each edge clipped to a 45°-cornered
        // trapezoid so adjacent edges meet on the diagonal (highlight-L meets shadow-L),
        // the authentic Win2000 3-D corner.
        DrawMiteredEdge(ctx, Side.Top,    new Rect(0, 0, w, t),     light, mode, w, h, t);
        DrawMiteredEdge(ctx, Side.Bottom, new Rect(0, h - t, w, t), dark,  mode, w, h, t);
        DrawMiteredEdge(ctx, Side.Left,   new Rect(0, 0, t, h),     light, mode, w, h, t);
        DrawMiteredEdge(ctx, Side.Right,  new Rect(w - t, 0, t, h), dark,  mode, w, h, t);
    }

    private enum Side { Top, Bottom, Left, Right }

    private void DrawMiteredEdge(DrawingContext ctx, Side side, Rect bbox, Color[] bands,
                                 EdgeRendering mode, double w, double h, double t)
    {
        using (ctx.PushGeometryClip(Trapezoid(side, w, h, t)))
            DrawEdgeFill(ctx, side, bbox, bands, mode);
    }

    /// <summary>The 45°-mitered trapezoid an edge occupies (outer full-length side → inner short side).</summary>
    private static Geometry Trapezoid(Side side, double w, double h, double t)
    {
        var p = side switch
        {
            Side.Top    => new[] { new Point(0, 0), new Point(w, 0), new Point(w - t, t), new Point(t, t) },
            Side.Bottom => new[] { new Point(0, h), new Point(w, h), new Point(w - t, h - t), new Point(t, h - t) },
            Side.Left   => new[] { new Point(0, 0), new Point(t, t), new Point(t, h - t), new Point(0, h) },
            _           => new[] { new Point(w, 0), new Point(w, h), new Point(w - t, h - t), new Point(w - t, t) },
        };
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(p[0], true);
            for (var i = 1; i < p.Length; i++) c.LineTo(p[i]);
            c.EndFigure(true);
        }
        return g;
    }

    private void DrawEdgeFill(DrawingContext ctx, Side side, Rect edge, Color[] bands, EdgeRendering mode)
    {
        if (edge.Width <= 0 || edge.Height <= 0 || bands.Length == 0) return;

        // Hard bands: one 1-DIP line per band, outer→inner. UseLayoutRounding keeps them
        // on the device-pixel grid. (Smooth mode never reaches here — Render short-circuits
        // to BevelEdgeRenderer.DrawSmoothFrame.)
        var n = bands.Length;
        for (var i = 0; i < n; i++)
        {
            var b = new SolidColorBrush(bands[i]);
            var r = side switch
            {
                Side.Top    => new Rect(edge.X, edge.Y + i, edge.Width, 1),
                Side.Bottom => new Rect(edge.X, edge.Bottom - 1 - i, edge.Width, 1),
                Side.Left   => new Rect(edge.X + i, edge.Y, 1, edge.Height),
                _           => new Rect(edge.Right - 1 - i, edge.Y, 1, edge.Height),
            };
            ctx.FillRectangle(b, r);
        }
    }
}
