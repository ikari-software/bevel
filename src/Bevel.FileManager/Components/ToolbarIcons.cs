using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Path = Avalonia.Controls.Shapes.Path;

namespace Bevel.FileManager.Components;

/// <summary>
/// Self-drawn vector toolbar glyphs (clean-room: authored from the observed appearance of the
/// Win2000 Explorer toolbar, never traced from Microsoft assets). Each glyph is composed in a
/// 16-unit space and rasterized once at 2x into a 16-DIP <see cref="Bitmap"/> for the classic
/// <c>ToolBarButton.SmallIcon</c> slot — crisp on HiDPI, gradients instead of dithering.
/// </summary>
internal static class ToolbarIcons
{
    // ── Palette ────────────────────────────────────────────────────────
    private static LinearGradientBrush V(string top, string bottom) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse(top), 0), new GradientStop(Color.Parse(bottom), 1) },
    };
    private static SolidColorBrush S(string hex) => new(Color.Parse(hex));

    private static readonly IBrush White = Brushes.White;
    private static readonly IBrush Green = V("#86D24E", "#4C9E20");
    private static readonly IBrush GreenEdge = S("#2E6B12");
    private static readonly IBrush Blue = V("#5B93E0", "#2F6FC9");
    private static readonly IBrush BlueEdge = S("#22508F");
    private static readonly IBrush Gray = S("#828890");
    private static readonly IBrush FolderBack = V("#FFE49A", "#F0B03C");
    private static readonly IBrush FolderFront = V("#FFF3CE", "#FFD064");
    private static readonly IBrush FolderEdge = S("#9C6B15");
    private static readonly IBrush Paper = V("#FFFFFF", "#ECECEC");
    private static readonly IBrush PaperEdge = S("#7F9DB9");
    private static readonly IBrush PaperLine = S("#B4C6D8");
    private static readonly IBrush Red = S("#E13126");
    private static readonly IBrush Board = S("#CBAE79");
    private static readonly IBrush BoardEdge = S("#7A6030");

    private const string FolderBackData = "M1.5,4.3 H6 l1.4,1.4 H14 a0.7,0.7 0 0 1 0.7,0.7 V12.4 H1.5 Z";
    private const string FolderFrontData = "M1.5,6.9 H15.1 l-1.25,5.7 a0.7,0.7 0 0 1 -0.68,0.55 H2.35 a0.7,0.7 0 0 1 -0.68,-0.55 Z";

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

    private static Path Folder() => P(FolderBackData, FolderBack, FolderEdge, 0.5);
    private static Path FolderFlap() => P(FolderFrontData, FolderFront, FolderEdge, 0.5);

    // ── Glyphs ─────────────────────────────────────────────────────────
    public static Bitmap? Back() => Raster(
        E(1.4, 1.4, 13.2, 13.2, Green, GreenEdge, 0.6),
        P("M9.6,4.6 L5.0,8 L9.6,11.4 L9.6,9.1 L11.6,9.1 L11.6,6.9 L9.6,6.9 Z", White));

    public static Bitmap? Forward() => Raster(
        E(1.4, 1.4, 13.2, 13.2, Green, GreenEdge, 0.6),
        P("M6.4,4.6 L11.0,8 L6.4,11.4 L6.4,9.1 L4.4,9.1 L4.4,6.9 L6.4,6.9 Z", White));

    public static Bitmap? Up() => Raster(
        Folder(), FolderFlap(),
        P("M8,7.0 L10.6,10.0 L8.9,10.0 L8.9,12.4 L7.1,12.4 L7.1,10.0 L5.4,10.0 Z", Green, GreenEdge, 0.4));

    public static Bitmap? Search() => Raster(
        E(2.3, 2.3, 8.2, 8.2, White, Blue, 1.5),
        P("M9.4,9.4 L13.4,13.4", null, Blue, 2.0));

    public static Bitmap? Folders() => Raster(
        Folder(), FolderFlap(),
        P("M4.2,9.2 H8.2 M4.2,10.9 H10.6", null, S("#B67B1E"), 0.7));

    public static Bitmap? History() => Raster(
        E(2.2, 2.2, 11.6, 11.6, White, Blue, 1.1),
        P("M8,4.6 V8.1 L10.6,9.4", null, BlueEdge, 1.0));

    public static Bitmap? MoveTo() => Raster(
        Folder(), FolderFlap(),
        P("M4.2,9.4 H8.6 M8.6,7.7 L10.6,9.4 L8.6,11.1 Z", Green, GreenEdge, 0.5));

    public static Bitmap? CopyTo() => Raster(
        P("M6.5,3.2 H9.2 l0.9,0.9 H13 a0.5,0.5 0 0 1 0.5,0.5 V10.5 H6.5 Z", FolderBack, FolderEdge, 0.4),
        Folder(), FolderFlap());

    public static Bitmap? Cut() => Raster(
        P("M4.6,11.4 L12.2,3.4", null, Gray, 1.1),
        P("M11.4,11.4 L3.8,3.4", null, Gray, 1.1),
        E(2.6, 10.0, 3.2, 3.2, White, Gray, 0.9),
        E(9.2, 10.0, 3.2, 3.2, White, Gray, 0.9));

    public static Bitmap? Copy() => Raster(
        P("M6.2,2.6 H10.8 L12.8,4.6 V11.0 H6.2 Z", Paper, PaperEdge, 0.5),
        P("M3.4,5.2 H8.0 L10.0,7.2 V13.2 H3.4 Z", Paper, PaperEdge, 0.5),
        P("M4.6,8.4 H8.6 M4.6,10.0 H8.6", null, PaperLine, 0.6));

    public static Bitmap? Paste() => Raster(
        P("M3.4,3.2 H12.6 V14.2 H3.4 Z", Board, BoardEdge, 0.6),
        P("M6.4,2.0 H9.6 V3.6 H6.4 Z", Gray, BoardEdge, 0.5),
        P("M5.2,5.2 H10.8 V12.4 H5.2 Z", Paper, PaperEdge, 0.5),
        P("M6.2,7.4 H9.8 M6.2,9.0 H9.8 M6.2,10.6 H8.6", null, PaperLine, 0.6));

    public static Bitmap? Undo() => Raster(
        P("M3.4,8.6 A4.6,4.6 0 1 1 8,13.2", null, Blue, 1.4),
        P("M3.4,8.6 L2.4,5.6 L5.8,6.4 Z", Blue));

    public static Bitmap? Delete() => Raster(
        P("M4.2,4.2 L11.8,11.8", null, Red, 2.2),
        P("M11.8,4.2 L4.2,11.8", null, Red, 2.2));

    public static Bitmap? Properties() => Raster(
        P("M3.6,1.8 H9.6 L12.2,4.4 V14.0 H3.6 Z", Paper, PaperEdge, 0.5),
        P("M9.6,1.8 V4.4 H12.2 Z", S("#DCE7F2"), PaperEdge, 0.4),
        P("M5.2,8.4 L7.0,10.4 L11.0,5.6", null, S("#2E8B2E"), 1.4));

    public static Bitmap? Views() => Raster(
        P("M3.4,3.4 H7.0 V7.0 H3.4 Z", S("#5B93E0"), BlueEdge, 0.4),
        P("M9.0,3.4 H12.6 V7.0 H9.0 Z", S("#86C24E"), GreenEdge, 0.4),
        P("M3.4,9.0 H7.0 V12.6 H3.4 Z", S("#E6C24A"), FolderEdge, 0.4),
        P("M9.0,9.0 H12.6 V12.6 H9.0 Z", S("#E58A6A"), S("#A8482E"), 0.4));
}
