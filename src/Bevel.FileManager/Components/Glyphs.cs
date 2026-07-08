using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Bevel.Core.Vfs;

namespace Bevel.FileManager.Components;

/// <summary>Shared self-drawn vector icon glyphs (semantic icon key -> Control), used by
/// ItemView (file listing), ToolbarIcons (folder path data) and PropertiesDialog. Authored in a
/// 16-unit space and vector-scaled, so crisp at any DPI. Circles go through Ell() which emits a
/// Path (not an Ellipse Shape) so nested circles stay concentric.</summary>
public static class Glyphs
{
    // Vector icons — crisp at any DPI (the "vectors + gradients, not dithered bitmaps" rule).
    // Authored in a 16-unit space and scaled by a Viewbox to the requested size.
    private static LinearGradientBrush VGrad(string top, string bottom) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse(top), 0), new GradientStop(Color.Parse(bottom), 1) },
    };

    static readonly IBrush FolderBack = VGrad("#FFE49A", "#F0B03C");
    static readonly IBrush FolderFront = VGrad("#FFF3CE", "#FFD064");
    static readonly IBrush FolderEdge = new SolidColorBrush(Color.Parse("#9C6B15"));
    static readonly IBrush PaperFill = VGrad("#FFFFFF", "#ECECEC");
    static readonly IBrush PaperEdge = new SolidColorBrush(Color.Parse("#7F9DB9"));
    static readonly IBrush PaperFold = new SolidColorBrush(Color.Parse("#DCE7F2"));
    static readonly IBrush PaperLine = new SolidColorBrush(Color.Parse("#B4C6D8"));

    static Avalonia.Controls.Shapes.Path Vec(string data, IBrush? fill, IBrush? stroke = null, double sw = 0.5) =>
        new() { Data = Geometry.Parse(data), Fill = fill, Stroke = stroke, StrokeThickness = sw };

    public const string FolderBackData = "M1.5,4.3 H6 l1.4,1.4 H14 a0.7,0.7 0 0 1 0.7,0.7 V12.4 H1.5 Z";
    public const string FolderFrontData = "M1.5,6.9 H15.1 l-1.25,5.7 a0.7,0.7 0 0 1 -0.68,0.55 H2.35 a0.7,0.7 0 0 1 -0.68,-0.55 Z";
    private const string DocPageData = "M3.4,1.5 H10 L12.6,4.1 V13.9 a0.4,0.4 0 0 1 -0.4,0.4 H3.4 a0.4,0.4 0 0 1 -0.4,-0.4 V1.9 a0.4,0.4 0 0 1 0.4,-0.4 Z";

    private static SolidColorBrush SB(string hex) => new(Color.Parse(hex));
    static readonly IBrush ExeTitle = SB("#0A246A");
    static readonly IBrush ExeLine = SB("#9DB4C8");
    static readonly IBrush Sky = SB("#A9D3F5");
    static readonly IBrush Sun = SB("#FFD64A");
    static readonly IBrush Mountain = SB("#5E9E52");
    static readonly IBrush ZipLine = SB("#6B6B6B");
    static readonly IBrush ZipTeeth = SB("#9A9A9A");
    static readonly IBrush DriveBody = SB("#CBD0D6");
    static readonly IBrush DriveEdge = SB("#7A8088");
    static readonly IBrush DriveLed = SB("#62C462");
    static readonly IBrush MonScreen = SB("#4F79A8");
    static readonly IBrush MonInner = SB("#2E5B90");
    static readonly IBrush MonEdge = SB("#3A3A3A");
    static readonly IBrush MonStand = SB("#B8BCC2");
    static readonly IBrush Note = SB("#5A50C8");
    static readonly IBrush FilmDark = SB("#333941");
    static readonly IBrush FilmEdge = SB("#20242A");
    static readonly IBrush FilmFrame = SB("#6E9BD0");
    static readonly IBrush Globe = SB("#2E86D8");
    static readonly IBrush GlobeEdge = SB("#1E5FA0");
    static readonly IBrush GearBody = SB("#B6BAC0");
    static readonly IBrush GearEdge = SB("#70747A");
    static readonly IBrush ConsoleBg = SB("#1E1E1E");
    static readonly IBrush ConsoleTitle = SB("#3C3C3C");
    static readonly IBrush FontInk = SB("#33373D");
    static readonly IBrush PdfRed = SB("#D93A2B");
    static readonly IBrush SheetGreen = SB("#217346");
    static readonly IBrush SheetGrid = SB("#8FBFA3");
    static readonly IBrush WordBlue = SB("#2B579A");
    static readonly IBrush PptOrange = SB("#D24726");
    static readonly IBrush DbBody = SB("#8FA9C4");
    static readonly IBrush DbTop = SB("#C2D4E6");
    static readonly IBrush DbEdge = SB("#5B7590");
    static readonly IBrush CodeInk = SB("#3B4A57");
    static readonly IBrush DiscBody = SB("#C7D2DE");
    static readonly IBrush DiscSheen = SB("#EAF1F8");

    // The iridescent rainbow sweep of a real CD's data side — a conic gradient cycling hues once
    // around the platter (first stop == last so the wrap is seamless).
    static readonly IBrush DiscRainbow = new ConicGradientBrush
    {
        Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        Angle = 210,
        GradientStops =
        {
            new GradientStop(Color.Parse("#8FE9D0"), 0.00),
            new GradientStop(Color.Parse("#9FD2F2"), 0.14),
            new GradientStop(Color.Parse("#B9B4F2"), 0.28),
            new GradientStop(Color.Parse("#EBA6DE"), 0.42),
            new GradientStop(Color.Parse("#F7B79E"), 0.56),
            new GradientStop(Color.Parse("#F1E39A"), 0.70),
            new GradientStop(Color.Parse("#AEE79C"), 0.85),
            new GradientStop(Color.Parse("#8FE9D0"), 1.00),
        },
    };

    static readonly HashSet<string> ExeExt = new(StringComparer.OrdinalIgnoreCase) { "exe", "com", "scr", "msi", "app" };
    static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { "png", "jpg", "jpeg", "gif", "bmp", "ico", "webp", "tif", "tiff", "svg" };
    static readonly HashSet<string> ArchiveExt = new(StringComparer.OrdinalIgnoreCase) { "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "cab" };
    static readonly HashSet<string> AudioExt = new(StringComparer.OrdinalIgnoreCase) { "mp3", "wav", "wma", "mid", "midi", "ogg", "flac", "m4a", "aac", "aiff", "au" };
    static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase) { "avi", "mpg", "mpeg", "wmv", "mov", "mp4", "mkv", "flv", "webm", "m4v", "3gp" };
    static readonly HashSet<string> WebExt = new(StringComparer.OrdinalIgnoreCase) { "htm", "html", "xhtml", "mht", "mhtml", "url", "asp", "aspx", "php", "jsp" };
    static readonly HashSet<string> SystemExt = new(StringComparer.OrdinalIgnoreCase) { "dll", "sys", "drv", "ocx", "vxd", "cpl" };
    static readonly HashSet<string> ScriptExt = new(StringComparer.OrdinalIgnoreCase) { "bat", "cmd", "vbs", "js", "ps1", "sh", "py", "pl", "rb", "wsf" };
    static readonly HashSet<string> FontExt = new(StringComparer.OrdinalIgnoreCase) { "ttf", "otf", "fon", "fnt", "ttc", "woff", "woff2" };
    static readonly HashSet<string> SheetExt = new(StringComparer.OrdinalIgnoreCase) { "xls", "xlsx", "xlsm", "csv", "tsv", "ods", "numbers" };
    static readonly HashSet<string> WordExt = new(StringComparer.OrdinalIgnoreCase) { "doc", "docx", "rtf", "odt", "pages", "wpd" };
    static readonly HashSet<string> SlideExt = new(StringComparer.OrdinalIgnoreCase) { "ppt", "pptx", "pps", "ppsx", "odp", "key" };
    static readonly HashSet<string> DbExt = new(StringComparer.OrdinalIgnoreCase) { "db", "sqlite", "sqlite3", "mdb", "accdb", "sql", "dbf" };
    static readonly HashSet<string> CodeExt = new(StringComparer.OrdinalIgnoreCase) { "xml", "xaml", "json", "yaml", "yml", "toml", "css", "cs", "c", "cpp", "cc", "h", "hpp", "java", "go", "rs", "ts", "tsx", "swift", "kt" };
    static readonly HashSet<string> DiscExt = new(StringComparer.OrdinalIgnoreCase) { "iso", "img", "dmg", "vhd", "vhdx", "bin", "cue", "nrg", "toast" };

    // A circle at top-left (x,y), diameter d — drawn as an absolute-coordinate Path, NOT an
    // Ellipse Shape. An Ellipse on a Canvas positions size-dependently (its layout bounds depend
    // on size/stroke), so nested different-sized ellipses — the CD platter/hub/spindle — drift
    // off-centre from each other. Path geometry is absolute, so circles stay concentric with each
    // other and with every Vec() path. Signature unchanged, so all call sites keep working.
    static Avalonia.Controls.Shapes.Path Ell(double x, double y, double d, IBrush? fill, IBrush? stroke = null, double sw = 0.4)
    {
        double r = d / 2, cx = x + r, cy = y + r;
        var data = FormattableString.Invariant($"M{cx - r},{cy} A{r},{r} 0 1 0 {cx + r},{cy} A{r},{r} 0 1 0 {cx - r},{cy} Z");
        return Vec(data, fill, stroke, sw);
    }

    /// <summary>Self-drawn vector glyph chosen from the node's semantic icon key. Crisp at any DPI.</summary>
    public static Control Icon(int size, IconKey key)
    {
        var c = new Canvas { Width = 16, Height = 16 };
        BuildGlyph(c, key.SemanticId ?? "doc.generic");
        return new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, Child = c };
    }

    /// <summary>Render a single semantic glyph at an arbitrary size — for previews and tooling.
    /// The glyph is authored in a 16-unit space and vector-scaled, so any size stays crisp.</summary>
    public static Control GlyphPreview(IconKey key, int size) => Icon(size, key);

    static void BuildGlyph(Canvas c, string id)
    {
        if (id.StartsWith("folder")) { FolderGlyph(c); return; }
        if (id == "computer") { ComputerGlyph(c); return; }
        if (id.StartsWith("drive")) { DriveGlyph(c, id); return; }
        var ext = id.StartsWith("doc.") ? id[4..] : "";
        if (ExeExt.Contains(ext)) { ExeGlyph(c); return; }
        if (ImageExt.Contains(ext)) { ImageGlyph(c); return; }
        if (ArchiveExt.Contains(ext)) { ArchiveGlyph(c); return; }
        if (AudioExt.Contains(ext)) { AudioGlyph(c); return; }
        if (VideoExt.Contains(ext)) { VideoGlyph(c); return; }
        if (WebExt.Contains(ext)) { WebGlyph(c); return; }
        if (SystemExt.Contains(ext)) { SystemGlyph(c); return; }
        if (ScriptExt.Contains(ext)) { ScriptGlyph(c); return; }
        if (FontExt.Contains(ext)) { FontGlyph(c); return; }
        if (ext == "pdf") { PdfGlyph(c); return; }
        if (SheetExt.Contains(ext)) { SheetGlyph(c); return; }
        if (WordExt.Contains(ext)) { WordGlyph(c); return; }
        if (SlideExt.Contains(ext)) { SlideGlyph(c); return; }
        if (DbExt.Contains(ext)) { DatabaseGlyph(c); return; }
        if (CodeExt.Contains(ext)) { CodeGlyph(c); return; }
        if (DiscExt.Contains(ext)) { DiscGlyph(c); return; }
        DocGlyph(c);
    }

    static void FolderGlyph(Canvas c)
    {
        c.Children.Add(Vec(FolderBackData, FolderBack, FolderEdge));
        c.Children.Add(Vec(FolderFrontData, FolderFront, FolderEdge));
    }

    static void DocGlyph(Canvas c)
    {
        c.Children.Add(Vec(DocPageData, PaperFill, PaperEdge));
        c.Children.Add(Vec("M10,1.5 V4.1 H12.6 Z", PaperFold, PaperEdge, 0.4));
        for (var i = 0; i < 3; i++)
            c.Children.Add(Vec($"M5,{7.0 + i * 2.0} H10.6", null, PaperLine, 0.7));
    }

    static void ExeGlyph(Canvas c)
    {
        // A little program window: navy title bar over a light body.
        c.Children.Add(Vec("M2.5,3.2 H13.5 V13 H2.5 Z", PaperFill, PaperEdge, 0.5));
        c.Children.Add(Vec("M2.5,3.2 H13.5 V5.4 H2.5 Z", ExeTitle));
        c.Children.Add(Vec("M4,7.6 H12 M4,9.4 H11 M4,11.2 H9", null, ExeLine, 0.7));
    }

    static void ImageGlyph(Canvas c)
    {
        c.Children.Add(Vec(DocPageData, PaperFill, PaperEdge));
        c.Children.Add(Vec("M4.6,6.4 H11.6 V12 H4.6 Z", Sky, PaperEdge, 0.4));   // photo inset
        c.Children.Add(Ell(9.4, 7.0, 1.8, Sun));                                  // sun
        c.Children.Add(Vec("M4.6,12 L6.7,9.2 L8.3,10.8 L10,8.6 L11.6,10.6 V12 Z", Mountain));
    }

    static void ArchiveGlyph(Canvas c)
    {
        FolderGlyph(c);
        c.Children.Add(Vec("M8,6.9 V13.0", null, ZipLine, 0.9));                  // zipper
        c.Children.Add(Vec("M7.1,8.2 H8.9 M7.1,9.7 H8.9 M7.1,11.2 H8.9", null, ZipTeeth, 0.6));
        c.Children.Add(Vec("M7.3,5.8 H8.7 V7.1 H7.3 Z", ZipTeeth, ZipLine, 0.4)); // pull tab
    }

    static void DriveGlyph(Canvas c, string id)
    {
        if (id.Contains("cd")) { CdDriveGlyph(c); return; }   // CD-ROM drive → bay + disc in front
        c.Children.Add(Vec("M2,5.5 H14 V11 H2 Z", DriveBody, DriveEdge, 0.5));
        c.Children.Add(Vec("M3.5,7 H10.5", null, DriveEdge, 0.5));
        c.Children.Add(Ell(11.5, 8.1, 1.4, DriveLed));
    }

    static void ComputerGlyph(Canvas c)
    {
        c.Children.Add(Vec("M2.5,3 H13.5 V10 H2.5 Z", MonEdge, MonEdge, 0.4));
        c.Children.Add(Vec("M3.3,3.8 H12.7 V9.2 H3.3 Z", MonScreen));
        c.Children.Add(Vec("M3.3,3.8 H12.7 V6.6 H3.3 Z", MonInner));
        c.Children.Add(Vec("M7,10 H9 V11.4 H7 Z", MonStand));
        c.Children.Add(Vec("M5,12.6 H11 V13.6 H5 Z", MonStand, MonEdge, 0.3));
    }

    static void AudioGlyph(Canvas c)
    {
        // A page with a musical note.
        c.Children.Add(Vec(DocPageData, PaperFill, PaperEdge));
        c.Children.Add(Vec("M10,1.5 V4.1 H12.6 Z", PaperFold, PaperEdge, 0.4));
        c.Children.Add(Vec("M10.6,5.6 V10.4", null, Note, 0.9));                // stem
        c.Children.Add(Vec("M10.6,5.6 Q12.6,6.0 12.0,8.0", null, Note, 0.9));   // flag
        c.Children.Add(Ell(8.2, 9.4, 2.4, Note));                              // note head
    }

    static void VideoGlyph(Canvas c)
    {
        // A filmstrip: dark band with sprocket holes and a play triangle.
        c.Children.Add(Vec("M2,4 H14 V12 H2 Z", FilmDark, FilmEdge, 0.4));
        c.Children.Add(Vec("M4.4,5.6 H11.6 V10.4 H4.4 Z", FilmFrame));
        for (var i = 0; i < 6; i++)
        {
            var x = 2.5 + i * 2.0;   // 6 holes, pitch 2, symmetric in the 2..14 strip
            c.Children.Add(Vec($"M{x:0.##},4.35 h1 v1 h-1 Z", Brushes.White));   // top holes
            c.Children.Add(Vec($"M{x:0.##},10.65 h1 v1 h-1 Z", Brushes.White));  // bottom holes
        }
        c.Children.Add(Vec("M7.1,6.7 L9.8,8 L7.1,9.3 Z", Brushes.White));        // play (centred on 8,8)
    }

    static void WebGlyph(Canvas c)
    {
        // A page with a wireframe globe (HTML/web document).
        c.Children.Add(Vec(DocPageData, PaperFill, PaperEdge));
        c.Children.Add(Vec("M10,1.5 V4.1 H12.6 Z", PaperFold, PaperEdge, 0.4));
        c.Children.Add(Ell(4.6, 5.8, 6.6, Globe, GlobeEdge, 0.4));               // globe (centre 7.9,9.1 r3.3)
        c.Children.Add(Vec("M4.8,9.1 H11.0", null, Brushes.White, 0.4));          // equator (within the rim)
        c.Children.Add(Vec("M7.9,5.9 Q5.2,9.1 7.9,12.3", null, Brushes.White, 0.4)); // meridian
        c.Children.Add(Vec("M7.9,5.9 Q10.6,9.1 7.9,12.3", null, Brushes.White, 0.4));
    }

    static void SystemGlyph(Canvas c)
    {
        // A page with a small gear (dll / sys / driver).
        c.Children.Add(Vec(DocPageData, PaperFill, PaperEdge));
        c.Children.Add(Vec("M10,1.5 V4.1 H12.6 Z", PaperFold, PaperEdge, 0.4));
        c.Children.Add(Vec("M7.3,5.2 H8.7 V6.6 H7.3 Z", GearBody, GearEdge, 0.3));   // top tooth
        c.Children.Add(Vec("M7.3,10.4 H8.7 V11.8 H7.3 Z", GearBody, GearEdge, 0.3)); // bottom
        c.Children.Add(Vec("M5.0,7.8 H6.4 V9.2 H5.0 Z", GearBody, GearEdge, 0.3));   // left
        c.Children.Add(Vec("M9.6,7.8 H11.0 V9.2 H9.6 Z", GearBody, GearEdge, 0.3));  // right
        c.Children.Add(Ell(5.9, 6.4, 4.2, GearBody, GearEdge, 0.4));                 // hub
        c.Children.Add(Ell(7.2, 7.7, 1.6, PaperFill, GearEdge, 0.3));                // bore
    }

    static void ScriptGlyph(Canvas c)
    {
        // A console window (batch / shell script): dark body, prompt caret.
        c.Children.Add(Vec("M2.5,3.5 H13.5 V12.5 H2.5 Z", ConsoleBg, MonEdge, 0.4));
        c.Children.Add(Vec("M2.5,3.5 H13.5 V5.0 H2.5 Z", ConsoleTitle));
        c.Children.Add(Vec("M4.2,7.4 L5.8,8.4 L4.2,9.4", null, Brushes.White, 0.7)); // ">"
        c.Children.Add(Vec("M6.6,9.4 H10.2", null, Brushes.White, 0.6));             // cursor
    }

    static void FontGlyph(Canvas c)
    {
        // A page with a serif "A" (font file).
        c.Children.Add(Vec(DocPageData, PaperFill, PaperEdge));
        c.Children.Add(Vec("M10,1.5 V4.1 H12.6 Z", PaperFold, PaperEdge, 0.4));
        c.Children.Add(Vec("M8,4.6 L5.5,11.4", null, FontInk, 1.2));    // left leg
        c.Children.Add(Vec("M8,4.6 L10.5,11.4", null, FontInk, 1.2));   // right leg
        c.Children.Add(Vec("M6.5,9.2 H9.5", null, FontInk, 1.0));       // crossbar
    }

    static void PdfGlyph(Canvas c)
    {
        // A page with a red label band.
        c.Children.Add(Vec(DocPageData, PaperFill, PaperEdge));
        c.Children.Add(Vec("M10,1.5 V4.1 H12.6 Z", PaperFold, PaperEdge, 0.4));
        c.Children.Add(Vec("M3.6,10.4 H12.4 V13.2 H3.6 Z", PdfRed));            // band centre x=8
        c.Children.Add(Vec("M5.2,11.1 V12.5 M8.0,11.1 V12.5 M10.8,11.1 V12.5", null, Brushes.White, 0.5));
    }

    static void SheetGlyph(Canvas c)
    {
        // A page with a green spreadsheet grid.
        c.Children.Add(Vec(DocPageData, PaperFill, PaperEdge));
        c.Children.Add(Vec("M10,1.5 V4.1 H12.6 Z", PaperFold, PaperEdge, 0.4));
        c.Children.Add(Vec("M4.4,6.2 H11.6 V12.6 H4.4 Z", Brushes.White, SheetGreen, 0.5)); // table
        c.Children.Add(Vec("M4.4,6.2 H11.6 V7.8 H4.4 Z", SheetGreen));                       // header row
        c.Children.Add(Vec("M4.4,9.4 H11.6 M4.4,11.0 H11.6", null, SheetGrid, 0.4));         // row lines
        c.Children.Add(Vec("M6.8,7.8 V12.6 M9.2,7.8 V12.6", null, SheetGrid, 0.4));          // column lines
    }

    static void WordGlyph(Canvas c)
    {
        // A page with a blue "W" (word processor document).
        c.Children.Add(Vec(DocPageData, PaperFill, PaperEdge));
        c.Children.Add(Vec("M10,1.5 V4.1 H12.6 Z", PaperFold, PaperEdge, 0.4));
        c.Children.Add(Vec("M4.8,6.6 L6.1,11.4 L8,7.9 L9.9,11.4 L11.2,6.6", null, WordBlue, 1.1));
    }

    static void SlideGlyph(Canvas c)
    {
        // A page with an orange bar chart (presentation / slide deck).
        c.Children.Add(Vec(DocPageData, PaperFill, PaperEdge));
        c.Children.Add(Vec("M10,1.5 V4.1 H12.6 Z", PaperFold, PaperEdge, 0.4));
        c.Children.Add(Vec("M4.6,12.2 H11.6", null, PptOrange, 0.6));       // baseline
        c.Children.Add(Vec("M5.2,9.6 H6.6 V12.0 H5.2 Z", PptOrange));       // short bar
        c.Children.Add(Vec("M7.3,7.8 H8.7 V12.0 H7.3 Z", PptOrange));       // tall bar
        c.Children.Add(Vec("M9.4,8.9 H10.8 V12.0 H9.4 Z", PptOrange));      // mid bar
    }

    static void DatabaseGlyph(Canvas c)
    {
        // A database cylinder.
        c.Children.Add(Vec("M3.5,4.5 V11.5 a4.5,1.7 0 0 0 9,0 V4.5 Z", DbBody, DbEdge, 0.5));   // body
        c.Children.Add(Vec("M3.5,4.5 a4.5,1.7 0 0 1 9,0 a4.5,1.7 0 0 1 -9,0 Z", DbTop, DbEdge, 0.5)); // top
        c.Children.Add(Vec("M3.5,7.0 a4.5,1.7 0 0 0 9,0", null, DbEdge, 0.4));                   // band 1
        c.Children.Add(Vec("M3.5,9.4 a4.5,1.7 0 0 0 9,0", null, DbEdge, 0.4));                   // band 2
    }

    static void CodeGlyph(Canvas c)
    {
        // A page with angle brackets (markup / source code).
        c.Children.Add(Vec(DocPageData, PaperFill, PaperEdge));
        c.Children.Add(Vec("M10,1.5 V4.1 H12.6 Z", PaperFold, PaperEdge, 0.4));
        c.Children.Add(Vec("M6.4,7.4 L4.6,9.6 L6.4,11.8", null, CodeInk, 0.8));    // <
        c.Children.Add(Vec("M9.6,7.4 L11.4,9.6 L9.6,11.8", null, CodeInk, 0.8));   // >
        c.Children.Add(Vec("M8.6,6.8 L7.4,12.4", null, CodeInk, 0.7));             // /
    }

    static void DiscGlyph(Canvas c) => DrawDisc(c, 8, 8, 5.6);   // bare optical disc (iso/dmg/…)

    /// <summary>The iridescent CD, drawn at an arbitrary centre/radius so it can stand alone
    /// (disc image) or sit in front of a drive bay. Everything is derived from (cx,cy,r), so the
    /// platter, glints, hub and spindle stay concentric by construction at any size.</summary>
    static void DrawDisc(Canvas c, double cx, double cy, double r)
    {
        var k = r / 5.6;   // scale relative to the full 16-unit disc
        // Floor the edge strokes so they don't shrink to sub-pixel (uneven anti-aliasing that reads
        // as off-centre) when the disc is drawn small, e.g. in front of the CD-ROM drive.
        double edge = Math.Max(0.35, 0.5 * k);
        c.Children.Add(Ell(cx - r, cy - r, r * 2, DiscRainbow, DriveEdge, edge));      // iridescent platter
        // Both glints ride the SAME circle (0.85r) so the shine reads as one concentric highlight.
        // Two EQUAL glints exactly 180° apart: the shine is point-symmetric about the centre, so
        // it doesn't pull the eye off-axis (an asymmetric highlight makes the centred hole *look*
        // off-centre). Both ride one circle (0.85r) so the shine is a single coherent ring.
        double gr = 0.85 * r, gw = Math.Max(0.5, 0.9 * k);
        c.Children.Add(ConcentricArc(cx, cy, gr, 200, 250, gw));   // upper-left
        c.Children.Add(ConcentricArc(cx, cy, gr, 20, 70, gw));     // lower-right (antipode)
        var hub = 0.357 * r;  c.Children.Add(Ell(cx - hub, cy - hub, hub * 2, DiscSheen, DriveEdge, Math.Max(0.3, 0.4 * k)));
        var bore = 0.125 * r; c.Children.Add(Ell(cx - bore, cy - bore, bore * 2, PaperFill, DriveEdge, Math.Max(0.28, 0.3 * k)));
    }

    /// <summary>A stroked minor arc lying on the circle of radius <paramref name="r"/> about
    /// (<paramref name="cx"/>,<paramref name="cy"/>) — endpoints computed from the angles so the
    /// arc is concentric by construction (angles in degrees, clockwise, 0° = +x).</summary>
    static Avalonia.Controls.Shapes.Path ConcentricArc(double cx, double cy, double r, double a1, double a2, double sw)
    {
        double t1 = a1 * Math.PI / 180, t2 = a2 * Math.PI / 180;
        var large = Math.Abs(a2 - a1) > 180 ? 1 : 0;
        double x1 = cx + r * Math.Cos(t1), y1 = cy + r * Math.Sin(t1);
        double x2 = cx + r * Math.Cos(t2), y2 = cy + r * Math.Sin(t2);
        var data = FormattableString.Invariant($"M{x1:0.###},{y1:0.###} A{r:0.###},{r:0.###} 0 {large} 1 {x2:0.###},{y2:0.###}");
        return Vec(data, null, DiscSheen, sw);
    }

    static void CdDriveGlyph(Canvas c)
    {
        // A CD-ROM drive: the bay unit, with the iridescent disc standing in front of it.
        c.Children.Add(Vec("M5,3.4 H14 V9.0 H5 Z", DriveBody, DriveEdge, 0.5));    // drive bay
        c.Children.Add(Vec("M9.5,7.4 H12.9", null, DriveEdge, 0.45));              // tray slot
        c.Children.Add(Ell(12.7, 4.3, 1.0, DriveLed));                            // activity LED
        DrawDisc(c, 5.6, 10.0, 3.7);                                              // disc, front-left
    }
}
