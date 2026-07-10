using Avalonia;
using Avalonia.Media;

namespace Classic.Avalonia.Theme;

/// <summary>
/// How a Win2000 3-D edge is rasterized (Bevel fork addition, bevel-38y.1 —
/// docs/spec/win2000-explorer-chrome.md §8). Both modes keep the same logical thickness.
/// </summary>
public enum EdgeRendering
{
    /// <summary>Authentic: each logical-pixel colour band is a hard, device-pixel-snapped line.</summary>
    Crisp,

    /// <summary>
    /// Default (decision 2026-07-06): band colours render as an eased sRGB gradient
    /// (outer → inner → face) rasterized at device resolution. Near-crisp at 1×, a sub-pixel
    /// ramp at 2×/3×, and it fixes 150 % fuzziness — a gradient is MEANT to occupy fractional
    /// pixels, so fractional scales look intentional instead of blurred.
    /// </summary>
    Smooth,
}

/// <summary>
/// Shared Smooth-mode edge painter (Bevel fork addition, bevel-38y.1 / spec §8):
/// draws a 3-D edge frame whose four sides are 45°-mitered trapezoids filled with an eased
/// gradient across the logical thickness. The trapezoid bounds stay logical-pixel-aligned
/// (proportions exact per DPI-01); the fill rasterizes at device resolution. The miter is
/// essential here: without it a vertical gradient would abut a horizontal one at each corner
/// and leave a visible seam — on the diagonal both edges share the same normalized depth, so
/// light meets dark cleanly, matching Win2000's real 3-D corners.
/// </summary>
public static class BevelEdgeRenderer
{
    /// <summary>
    /// Draws the full mitered frame. <paramref name="topLeftBands"/> /
    /// <paramref name="bottomRightBands"/> are the outer→inner band colours for the
    /// light-catching and shadow sides (the classic DrawEdge algebra — callers pass the same
    /// colours they would use for hard rings). Sides with zero thickness are skipped.
    /// </summary>
    public static void DrawSmoothFrame(
        DrawingContext dc, Rect bounds, Thickness t,
        Color[] topLeftBands, Color[] bottomRightBands, Color face)
    {
        if (bounds.Width < t.Left + t.Right || bounds.Height < t.Top + t.Bottom)
            return;
        if (topLeftBands.Length == 0 || bottomRightBands.Length == 0)
            return;

        var x0 = bounds.X;
        var y0 = bounds.Y;
        var x1 = bounds.Right;
        var y1 = bounds.Bottom;

        if (t.Top > 0)
            DrawSide(dc,
                new Rect(x0, y0, bounds.Width, t.Top),
                new[] { new Point(x0, y0), new Point(x1, y0), new Point(x1 - t.Right, y0 + t.Top), new Point(x0 + t.Left, y0 + t.Top) },
                topLeftBands, face,
                new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(0, 1, RelativeUnit.Relative));

        if (t.Bottom > 0)
            DrawSide(dc,
                new Rect(x0, y1 - t.Bottom, bounds.Width, t.Bottom),
                new[] { new Point(x0, y1), new Point(x1, y1), new Point(x1 - t.Right, y1 - t.Bottom), new Point(x0 + t.Left, y1 - t.Bottom) },
                bottomRightBands, face,
                new RelativePoint(0, 1, RelativeUnit.Relative), new RelativePoint(0, 0, RelativeUnit.Relative));

        if (t.Left > 0)
            DrawSide(dc,
                new Rect(x0, y0, t.Left, bounds.Height),
                new[] { new Point(x0, y0), new Point(x0 + t.Left, y0 + t.Top), new Point(x0 + t.Left, y1 - t.Bottom), new Point(x0, y1) },
                topLeftBands, face,
                new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(1, 0, RelativeUnit.Relative));

        if (t.Right > 0)
            DrawSide(dc,
                new Rect(x1 - t.Right, y0, t.Right, bounds.Height),
                new[] { new Point(x1, y0), new Point(x1, y1), new Point(x1 - t.Right, y1 - t.Bottom), new Point(x1 - t.Right, y0 + t.Top) },
                bottomRightBands, face,
                new RelativePoint(1, 0, RelativeUnit.Relative), new RelativePoint(0, 0, RelativeUnit.Relative));
    }

    private static void DrawSide(
        DrawingContext dc, Rect strip, Point[] trapezoid,
        Color[] bands, Color face, RelativePoint start, RelativePoint end)
    {
        var geometry = new StreamGeometry();
        using (var c = geometry.Open())
        {
            c.BeginFigure(trapezoid[0], isFilled: true);
            for (var i = 1; i < trapezoid.Length; i++)
                c.LineTo(trapezoid[i]);
            c.EndFigure(isClosed: true);
        }

        using (dc.PushGeometryClip(geometry))
            dc.FillRectangle(EdgeGradient(bands, face, start, end), strip);
    }

    /// <summary>
    /// The eased outer→inner→face ramp (spec §8): hold the outer (light-catching) colour to
    /// ~35 % of the thickness, then ramp — a plain linear ramp reads washed-out.
    /// </summary>
    private static LinearGradientBrush EdgeGradient(
        Color[] bands, Color face, RelativePoint start, RelativePoint end)
    {
        var stops = new GradientStops
        {
            new GradientStop(bands[0], 0.0),
            new GradientStop(bands[0], 0.35),
        };
        for (var i = 1; i < bands.Length; i++)
            stops.Add(new GradientStop(bands[i], 0.35 + 0.45 * i / bands.Length));
        stops.Add(new GradientStop(face, 1.0));

        return new LinearGradientBrush { StartPoint = start, EndPoint = end, GradientStops = stops };
    }
}
