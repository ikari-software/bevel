using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Bevel.UI;
using Path = Avalonia.Controls.Shapes.Path;

namespace Bevel.FileManager.Components;

/// <summary>
/// Self-drawn vector toolbar glyphs (clean-room: authored from the observed appearance of the
/// Win2000 Explorer toolbar, never traced from Microsoft assets). Each glyph is composed in a
/// 16-unit space and rasterized once at 2x into a 16-DIP <see cref="Bitmap"/> for the classic
/// <c>ToolBarButton.SmallIcon</c> slot — crisp on HiDPI, gradients instead of dithering.
/// Colors resolve from the active theme via <see cref="ThemeTokens"/> so glyphs adapt to all 14 schemes.
/// </summary>
public static class ToolbarIcons
{
    // ── Theme-aware color resolution ──────────────────────────────────────

    /// <summary>Resolve a color from the current theme resources by <see cref="ThemeTokens"/> key.
    /// Falls back to parsing the hex if no Application context exists (e.g. headless tests).</summary>
    private static Avalonia.Media.Color ResolveColor(string tokenKey, string fallbackHex)
    {
        try
        {
            if (Application.Current is { } app &&
                app.TryFindResource(tokenKey, null, out var val))
            {
                if (val is SolidColorBrush scb) return scb.Color;
                if (val is Color c) return c;
            }
        }
        catch { /* ignore - no app context or resource missing */ }
        return Color.Parse(fallbackHex);
    }

    private static LinearGradientBrush VGrad(string topKey, string bottomKey, string topFallback, string bottomFallback) =>
        new()
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(ResolveColor(topKey, topFallback), 0),
                new GradientStop(ResolveColor(bottomKey, bottomFallback), 1),
            },
        };

    private static SolidColorBrush S(string tokenKey, string fallbackHex) =>
        new(ResolveColor(tokenKey, fallbackHex));

    // ── Semantic color token aliases (Win2000 Standard fallbacks in comments) ──

    private static readonly string TokBF = ThemeTokens.ColorButtonFace;           // #D4D0C8
    private static readonly string TokBH = ThemeTokens.ColorButtonHighlight;      // #FFFFFF
    private static readonly string TokBL = ThemeTokens.ColorButtonLight;          // #D4D0C8
    private static readonly string TokBS = ThemeTokens.ColorButtonShadow;         // #808080
    private static readonly string TokBD = ThemeTokens.ColorButtonDkShadow;       // #404040
    private static readonly string TokW  = ThemeTokens.ColorWindow;               // #FFFFFF
    private static readonly string TokAT = ThemeTokens.ColorActiveTitle;          // #0A246A
    private static readonly string TokGA = ThemeTokens.ColorGradientActiveTitle;  // #A6CAF0
    private static readonly string TokHL = ThemeTokens.ColorHighlight;            // #0A246A
    private static readonly string TokHT = ThemeTokens.ColorHighlightText;        // #FFFFFF
    private static readonly string TokHK = ThemeTokens.ColorHotTracking;          // #000080

    // ── Resolved brushes (lazy, theme-aware) ────────���───────────────────────

    private static IBrush White         => Brushes.White;
    private static IBrush Green         => new SolidColorBrush(Color.Parse("#469E4A"));
    private static IBrush GreenEdge     => new SolidColorBrush(Color.Parse("#235826"));
    private static IBrush Blue          => new SolidColorBrush(Color.Parse("#2B6DD8"));
    private static IBrush BlueEdge      => new SolidColorBrush(Color.Parse("#16428C"));
    private static IBrush Gray          => new SolidColorBrush(Color.Parse("#76736A"));
    private static IBrush FolderBack    => new SolidColorBrush(Color.Parse("#E0A838"));
    private static IBrush FolderFront   => new SolidColorBrush(Color.Parse("#FFD15C"));
    private static IBrush FolderEdge    => new SolidColorBrush(Color.Parse("#8C5E14"));
    private static IBrush Paper         => Brushes.White;
    private static IBrush PaperEdge     => new SolidColorBrush(Color.Parse("#6E6B62"));
    private static IBrush PaperLine     => new SolidColorBrush(Color.Parse("#4A85DF"));
    private static IBrush Red           => new SolidColorBrush(Color.Parse("#D9383A"));
    private static IBrush Board         => new SolidColorBrush(Color.Parse("#C2884A"));
    private static IBrush BoardEdge     => new SolidColorBrush(Color.Parse("#6E4518"));

    // Folder path data is shared with the list-view glyphs — single source of truth in Glyphs.

    // ── Primitives ─────────────────────────────────────────────────────

    private static Path P(string data, IBrush? fill, IBrush? stroke = null, double sw = 0.7) => new()
    {
        Data = Geometry.Parse(data),
        Fill = fill,
        Stroke = stroke,
        StrokeThickness = sw,
        StrokeLineCap = PenLineCap.Round,
        StrokeJoin = PenLineJoin.Round,
    };

    private static Ellipse E(double x, double y, double w, double h, IBrush? fill, IBrush? stroke = null, double sw = 0.7)
    {
        var e = new Ellipse { Width = w, Height = h, Fill = fill, Stroke = stroke, StrokeThickness = sw };
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        return e;
    }

    /// <summary>Compose the parts into a 16-DIP bitmap at 2x. Returns null if no rasterizer is
    /// available (e.g. headless unit tests) so callers just render an icon-less button.</summary>
    private static Bitmap? Raster(params Control[] parts)
    {
        try
        {
            var canvas = new Canvas { Width = 16, Height = 16 };
            foreach (var p in parts)
                canvas.Children.Add(p);
            var host = new Viewbox { Width = 16, Height = 16, Stretch = Stretch.Uniform, Child = canvas };
            host.Measure(new Size(16, 16));
            host.Arrange(new Rect(0, 0, 16, 16));
            var rtb = new RenderTargetBitmap(new PixelSize(32, 32), new Vector(192, 192));
            rtb.Render(host);
            return rtb;
        }
        catch
        {
            return null;
        }
    }

    private static Path Folder() => P(Glyphs.FolderBackData, FolderBack, FolderEdge, 0.5);
    private static Path FolderFlap() => P(Glyphs.FolderFrontData, FolderFront, FolderEdge, 0.5);

    // ── Glyphs ─────────────────────────────────────────────────────────

    public static Bitmap? Back() => Raster(
        E(1.4, 1.4, 13.2, 13.2, Green, GreenEdge, 0.6),
        P("M9.6,4.6 L5.0,8 L9.6,11.4 L9.6,9.1 L11.6,9.1 L11.6,6.9 L9.6,6.9 Z", White));

    public static Bitmap? Forward() => Raster(
        E(1.4, 1.4, 13.2, 13.2, Green, GreenEdge, 0.6),
        P("M6.4,4.6 L11.0,8 L6.4,11.4 L6.4,9.1 L4.4,9.1 L4.4,6.9 L6.4,6.9 Z", White));

    public static Bitmap? Up() => Raster(
        Folder(), FolderFlap(),
        P("M8,5.4 L10.9,8.7 L9.0,8.7 L9.0,11.6 L7.0,11.6 L7.0,8.7 L5.1,8.7 Z", Green, GreenEdge, 0.4));

    public static Bitmap? Search() => Raster(
        E(2.3, 2.3, 8.2, 8.2, White, Blue, 1.5),
        P("M9.4,9.4 L13.4,13.4", null, Blue, 2.0));

    public static Bitmap? Folders() => Raster(
        Folder(), FolderFlap(),
        P("M4.2,9.2 H8.2 M4.2,10.9 H10.6", null, S(TokBD, "#404040"), 0.7));

    public static Bitmap? History() => Raster(
        E(2.2, 2.2, 11.6, 11.6, White, Blue, 1.1),
        P("M8,4.6 V8.1 L10.6,9.4", null, BlueEdge, 1.0));

    public static Bitmap? MoveTo() => Raster(
        Folder(), FolderFlap(),
        P("M4.2,9.4 H8.6 M8.6,7.7 L10.6,9.4 L8.6,11.1 Z", Green, GreenEdge, 0.5));

    public static Bitmap? CopyTo() => Raster(
        // A second folder peeking up-and-right behind the front one.
        P("M4.6,1.9 H8.1 l1.2,1.2 H14.4 a0.6,0.6 0 0 1 0.6,0.6 V8.6 H4.6 Z", FolderBack, FolderEdge, 0.4),
        Folder(), FolderFlap());

    public static Bitmap? Cut() => Raster(
        P("M4.6,11.4 L12.2,3.4", null, Blue, 1.4),
        P("M11.4,11.4 L3.8,3.4", null, Blue, 1.4),
        E(2.2, 9.6, 3.8, 3.8, White, Red, 1.2),
        E(8.8, 9.6, 3.8, 3.8, White, Red, 1.2));

    public static Bitmap? Copy() => Raster(
        P("M6.2,2.6 H10.8 L12.8,4.6 V11.0 H6.2 Z", Paper, Blue, 0.8),
        P("M3.4,5.2 H8.0 L10.0,7.2 V13.2 H3.4 Z", Paper, Blue, 0.8),
        P("M4.6,8.4 H8.6 M4.6,10.0 H8.6", null, PaperLine, 0.8));

    public static Bitmap? Paste() => Raster(
        P("M3.4,3.2 H12.6 V14.2 H3.4 Z", Board, BoardEdge, 0.8),
        P("M6.4,2.0 H9.6 V3.6 H6.4 Z", Gray, BoardEdge, 0.6),
        P("M5.2,5.2 H10.8 V12.4 H5.2 Z", Paper, Blue, 0.7),
        P("M6.2,7.4 H9.8 M6.2,9.0 H9.8 M6.2,10.6 H8.6", null, PaperLine, 0.8));

    public static Bitmap? Undo() => Raster(
        P("M6.4,7.2 C7.4,5.0 9.6,4.0 12.0,4.6 C14.2,5.2 15.2,7.4 14.6,9.8 C13.8,11.8 12.0,13.4 9.5,13.5", null, Blue, 1.5),
        P("M1.6,7.2 L6.6,3.8 L6.6,10.2 Z", Blue));

    public static Bitmap? Delete() => Raster(
        P("M4.2,4.2 L11.8,11.8", null, Red, 2.2),
        P("M11.8,4.2 L4.2,11.8", null, Red, 2.2));

    public static Bitmap? Properties() => Raster(
        P("M3.6,1.8 H9.6 L12.2,4.4 V14.0 H3.6 Z", Paper, PaperEdge, 0.5),
        P("M9.6,1.8 V4.4 H12.2 Z", VGrad(TokGA, TokAT, "#A6CAF0", "#0A246A"), PaperEdge, 0.4),
        P("M5.2,8.4 L7.0,10.4 L11.0,5.6", null, S(TokHT, "#FFFFFF"), 1.4));

    public static Bitmap? Views() => Raster(
        P("M3.4,3.4 H7.0 V7.0 H3.4 Z", S(TokGA, "#A6CAF0"), BlueEdge, 0.4),
        P("M9.0,3.4 H12.6 V7.0 H9.0 Z", S(TokHL, "#0A246A"), GreenEdge, 0.4),
        P("M3.4,9.0 H7.0 V12.6 H3.4 Z", S(TokGA, "#A6CAF0"), FolderEdge, 0.4),
        P("M9.0,9.0 H12.6 V12.6 H9.0 Z", S(TokHL, "#0A246A"), S(TokBD, "#404040"), 0.4));
}
