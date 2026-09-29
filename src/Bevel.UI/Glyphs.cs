using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Bevel.Core.Vfs;

namespace Bevel.UI;

/// <summary>Shared self-drawn vector icon glyphs (semantic icon key -> Control), used by
/// ItemView (file listing), ToolbarIcons (folder path data) and PropertiesDialog.
/// Authored in a 16-unit space and vector-scaled, so crisp at any DPI.
/// Circles go through Ell() which emits a Path (not an Ellipse Shape) so nested circles stay concentric.
/// Colors are resolved from the active theme via <see cref="ThemeTokens"/> so glyphs adapt to all 14 schemes.</summary>
public static class Glyphs
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

    // Brushes are resolved from theme resources on EVERY icon build, once per glyph part — a hot
    // path when a large folder listing realizes hundreds of ItemView containers. Cache them per
    // token; the immutable brushes are safe to share across icons. InvalidateThemeCache() drops the
    // cache when a theme/scheme/variant swap changes the underlying tokens (ce-review bevel-lha4).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IBrush> _brushCache = new();

    /// <summary>Drop the resolved-brush cache. Called by the theme/scheme/variant engines after they
    /// change the token values so the next icon build re-resolves against the new palette. Raises
    /// <see cref="ThemeCacheInvalidated"/> so a glyph HOST that keeps a built icon alive (<see cref="GlyphIcon"/>)
    /// can rebuild it — a built icon holds the brushes it was built with, and nothing else tells it they
    /// went stale.</summary>
    public static void InvalidateThemeCache()
    {
        _brushCache.Clear();
        ThemeCacheInvalidated?.Invoke();
    }

    /// <summary>Raised after <see cref="InvalidateThemeCache"/> drops the brushes (UI thread — the theme
    /// engines apply there). Subscribers rebuild any icon they are holding; they must unsubscribe on detach.</summary>
    public static event Action? ThemeCacheInvalidated;

    /// <summary>Vertical gradient from two theme token keys (cached per token pair).</summary>
    private static IBrush VGrad(string topKey, string bottomKey, string topFallback, string bottomFallback) =>
        _brushCache.GetOrAdd(topKey + "|" + bottomKey, _ => new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(ResolveColor(topKey, topFallback), 0),
                new GradientStop(ResolveColor(bottomKey, bottomFallback), 1),
            },
        });

    /// <summary>Solid brush from theme token key (cached per token).</summary>
    private static IBrush S(string tokenKey, string fallbackHex) =>
        _brushCache.GetOrAdd(tokenKey, _ => new SolidColorBrush(ResolveColor(tokenKey, fallbackHex)));

    // ── Semantic color mappings (icon-specific tokens) ─────────────────────
    // Each icon part has its own Bevel.Color.Icon* token so schemes can adapt
    // them independently (e.g. dark schemes can lighten folder fills without
    // changing the window background). Fallbacks match the original Win2000
    // Windows Standard palette — used only when no Application.Current exists.
    // At runtime the active scheme provides the real values.

    // Folder
    private static readonly string TokIFB  = ThemeTokens.ColorIconFolderBackTop;
    private static readonly string TokIFBB = ThemeTokens.ColorIconFolderBackBottom;
    private static readonly string TokIFF  = ThemeTokens.ColorIconFolderFrontTop;
    private static readonly string TokIFFB = ThemeTokens.ColorIconFolderFrontBottom;
    private static readonly string TokIFE  = ThemeTokens.ColorIconFolderEdge;
    // Paper / document base
    private static readonly string TokIPF  = ThemeTokens.ColorIconPaperFillTop;
    private static readonly string TokIPFB = ThemeTokens.ColorIconPaperFillBottom;
    private static readonly string TokIPE  = ThemeTokens.ColorIconPaperEdge;
    private static readonly string TokIPO  = ThemeTokens.ColorIconPaperFold;
    private static readonly string TokIPL  = ThemeTokens.ColorIconPaperLine;
    // EXE
    private static readonly string TokIET  = ThemeTokens.ColorIconExeTitle;
    private static readonly string TokIEL  = ThemeTokens.ColorIconExeLine;
    // Landscape
    private static readonly string TokISK  = ThemeTokens.ColorIconSky;
    private static readonly string TokISN  = ThemeTokens.ColorIconSun;
    private static readonly string TokIMT  = ThemeTokens.ColorIconMountain;
    // ZIP
    private static readonly string TokIZL  = ThemeTokens.ColorIconZipLine;
    private static readonly string TokIZT  = ThemeTokens.ColorIconZipTeeth;
    // Drive
    private static readonly string TokIDB  = ThemeTokens.ColorIconDriveBody;
    private static readonly string TokIDBB = ThemeTokens.ColorIconDriveBody; // top of gradient (same token, gradient via opacity handled by scheme)
    private static readonly string TokIDE  = ThemeTokens.ColorIconDriveEdge;
    private static readonly string TokIDL  = ThemeTokens.ColorIconDriveLed;
    // Monitor
    private static readonly string TokIMS  = ThemeTokens.ColorIconMonitorScreen;
    private static readonly string TokIMI  = ThemeTokens.ColorIconMonitorInner;
    private static readonly string TokIME  = ThemeTokens.ColorIconMonitorEdge;
    private static readonly string TokIMSt = ThemeTokens.ColorIconMonitorStand;
    // Note / audio
    private static readonly string TokINO  = ThemeTokens.ColorIconNote;
    // Film / video
    private static readonly string TokIFD  = ThemeTokens.ColorIconFilmDark;
    private static readonly string TokIFE2 = ThemeTokens.ColorIconFilmEdge;
    private static readonly string TokIFR  = ThemeTokens.ColorIconFilmFrame;
    // Globe
    private static readonly string TokIGL  = ThemeTokens.ColorIconGlobe;
    private static readonly string TokIGE  = ThemeTokens.ColorIconGlobeEdge;
    // Gear / settings
    private static readonly string TokIGB  = ThemeTokens.ColorIconGearBody;
    private static readonly string TokIGE2 = ThemeTokens.ColorIconGearEdge;
    // Console
    private static readonly string TokICB  = ThemeTokens.ColorIconConsoleBg;
    private static readonly string TokICT  = ThemeTokens.ColorIconConsoleTitle;
    // Font / ink
    private static readonly string TokIFN  = ThemeTokens.ColorIconFontInk;
    // PDF
    private static readonly string TokIPR  = ThemeTokens.ColorIconPdfRed;
    // Spreadsheet
    private static readonly string TokISG  = ThemeTokens.ColorIconSheetGreen;
    private static readonly string TokISGr = ThemeTokens.ColorIconSheetGrid;
    // Word
    private static readonly string TokIWB  = ThemeTokens.ColorIconWordBlue;
    // PowerPoint
    private static readonly string TokIPO2 = ThemeTokens.ColorIconPptOrange;
    // Database
    private static readonly string TokIDBd = ThemeTokens.ColorIconDbBody;
    private static readonly string TOKIDT  = ThemeTokens.ColorIconDbTop;
    private static readonly string TokIDDe = ThemeTokens.ColorIconDbEdge;
    // Code
    private static readonly string TokICD  = ThemeTokens.ColorIconCodeInk;
    // Disc
    private static readonly string TokIDC  = ThemeTokens.ColorIconDiscBody;
    private static readonly string TokIDCS = ThemeTokens.ColorIconDiscSheen;

    // ── Resolved brushes (lazy, theme-aware) ────────────────────────────────

    private static IBrush FolderBack       => VGrad(TokIFB, TokIFBB, "#FFE49A", "#F0B03C");
    private static IBrush FolderFront      => VGrad(TokIFF, TokIFFB, "#FFF3CE", "#FFD064");
    private static IBrush FolderEdge       => S(TokIFE, "#9C6B15");
    private static IBrush PaperFill        => VGrad(TokIPF, TokIPFB, "#FFFFFF", "#ECECEC");
    private static IBrush PaperEdge        => S(TokIPE, "#7F9DB9");
    private static IBrush PaperFold        => S(TokIPO, "#DCE7F2");
    private static IBrush PaperLine        => S(TokIPL, "#B4C6D8");

    private static IBrush ExeTitle         => S(TokIET, "#0A246A");
    private static IBrush ExeLine          => S(TokIEL, "#9DB4C8");
    private static IBrush Sky              => S(TokISK, "#A9D3F5");
    private static IBrush Sun              => S(TokISN, "#FFD64A");
    private static IBrush Mountain         => S(TokIMT, "#5E9E52");
    private static IBrush ZipLine          => S(TokIZL, "#6B6B6B");
    private static IBrush ZipTeeth         => S(TokIZT, "#9A9A9A");
    private static IBrush DriveBody        => VGrad(TokIDB, TokIDBB, "#CBD0D6", "#B0B8C0");
    private static IBrush DriveEdge        => S(TokIDE, "#7A8088");
    private static IBrush DriveLed         => S(TokIDL, "#62C462");
    private static IBrush MonScreen        => S(TokIMS, "#4F79A8");
    private static IBrush MonInner         => S(TokIMI, "#2E5B90");
    private static IBrush MonEdge          => S(TokIME, "#3A3A3A");
    private static IBrush MonStand         => S(TokIMSt, "#B8BCC2");
    private static IBrush Note             => S(TokINO, "#5A50C8");
    private static IBrush FilmDark         => S(TokIFD, "#333941");
    private static IBrush FilmEdge         => S(TokIFE2, "#20242A");
    private static IBrush FilmFrame        => S(TokIFR, "#6E9BD0");
    private static IBrush Globe            => S(TokIGL, "#2E86D8");
    private static IBrush GlobeEdge        => S(TokIGE, "#1E5FA0");
    private static IBrush GearBody         => S(TokIGB, "#B6BAC0");
    private static IBrush GearEdge         => S(TokIGE2, "#70747A");
    private static IBrush ConsoleBg        => S(TokICB, "#1E1E1E");
    private static IBrush ConsoleTitle     => S(TokICT, "#3C3C3C");
    private static IBrush FontInk          => S(TokIFN, "#33373D");
    private static IBrush PdfRed           => S(TokIPR, "#D93A2B");
    private static IBrush SheetGreen       => S(TokISG, "#217346");
    private static IBrush SheetGrid        => S(TokISGr, "#8FBFA3");
    private static IBrush WordBlue         => S(TokIWB, "#2B579A");
    private static IBrush PptOrange        => S(TokIPO2, "#D24726");
    private static IBrush DbBody           => S(TokIDBd, "#8FA9C4");
    private static IBrush DbTop            => S(TOKIDT, "#C2D4E6");
    private static IBrush DbEdge           => S(TokIDDe, "#5B7590");
    private static IBrush CodeInk          => S(TokICD, "#3B4A57");
    private static IBrush DiscBody         => VGrad(TokIDC, TokIDCS, "#C7D2DE", "#A8B4C2");
    private static IBrush DiscSheen        => S(TokIDCS, "#EAF1F8");

    // The iridescent rainbow sweep of a real CD's data side — a conic gradient cycling hues once
    // around the platter (first stop == last so the wrap is seamless).
    // This is SCHEME-INVARIANT by design (physical phenomenon, not UI color).
    private static readonly IBrush DiscRainbow = new ConicGradientBrush
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

    // ── Extension sets (unchanged) ────────────────────────────────────────

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

    // ── Geometry primitives ──────────────────────────────────────────────

    // Parsed path geometry is immutable and identical across every call for a given data string —
    // all glyphs are authored in the fixed 16-unit space, so the same folder/document/… paths recur
    // for thousands of items. Cache and share one Geometry across every Path that draws it, instead
    // of re-running Geometry.Parse per icon per container realization on the UI thread.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Geometry> _geometryCache = new();
    static Geometry ParseGeometry(string data) => _geometryCache.GetOrAdd(data, static d => Geometry.Parse(d));

    static Avalonia.Controls.Shapes.Path Vec(string data, IBrush? fill, IBrush? stroke = null, double sw = 0.5) =>
        new() { Data = ParseGeometry(data), Fill = fill, Stroke = stroke, StrokeThickness = sw };

    // The back panel TAPERS to meet the front flap's bottom corners; it is not a rectangle behind a
    // trapezoid. Drawn square (V12.4 H1.5) its vertical sides crossed outside the flap's slant from
    // y~8.7 downwards — 0.81 units of bare back protruding on the right by the bottom edge, 0.16 on the
    // left, plus two hard corners below it. Shared by every folder-bearing glyph (Folder, Programs,
    // Documents, Search, the stack), so the shape is fixed once here rather than per icon.
    //   back top-right (14.7,6.4) -> flap's bottom-right shoulder (13.85,12.6) -> left shoulder (1.67,12.6)
    // Both new edges sit inside the flap's own edges over their whole length, so nothing pokes out at
    // any height, and the two silhouettes meet exactly at the bottom.
    public const string FolderBackData = "M1.5,4.3 H6 l1.4,1.4 H14 a0.7,0.7 0 0 1 0.7,0.7 L13.85,12.6 H1.67 Z";
    public const string FolderFrontData = "M1.5,6.9 H15.1 l-1.25,5.7 a0.7,0.7 0 0 1 -0.68,0.55 H2.35 a0.7,0.7 0 0 1 -0.68,-0.55 Z";
    /// <summary>
    /// The folder paths as parsed geometry, so XAML can bind the ONE definition instead of copying the
    /// path data. AddressBar.axaml kept its own transcription of both paths under the comment "matches
    /// the list glyph" — which stopped being true the moment the back panel's shape was corrected here.
    /// </summary>
    public static Geometry FolderBackGeometry => ParseGeometry(FolderBackData);

    /// <inheritdoc cref="FolderBackGeometry"/>
    public static Geometry FolderFrontGeometry => ParseGeometry(FolderFrontData);

    private const string DocPageData = "M3.4,1.5 H10 L12.6,4.1 V13.9 a0.4,0.4 0 0 1 -0.4,0.4 H3.4 a0.4,0.4 0 0 1 -0.4,-0.4 V1.9 a0.4,0.4 0 0 1 0.4,-0.4 Z";

    /// <summary>A circle at top-left (x,y), diameter d — drawn as an absolute-coordinate Path, NOT an
    /// Ellipse Shape. An Ellipse on a Canvas positions size-dependently (its layout bounds depend on
    /// size/stroke), so nested different-sized ellipses — the CD platter/hub/spindle — drift
    /// off-centre from each other. Path geometry is absolute, so circles stay concentric with each
    /// other and with every Vec() path. Signature unchanged, so all call sites keep working.</summary>
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
        if (id.StartsWith("start.")) { StartGlyph(c, id); return; }
        if (id.StartsWith("folder")) { FolderGlyph(c); return; }
        if (id == "computer") { ComputerGlyph(c); return; }
        if (id.StartsWith("drive")) { DriveGlyph(c, id); return; }
        if (id == "network") { NetworkGlyph(c); return; }
        if (id == "trash.empty") { TrashGlyph(c, false); return; }
        if (id == "trash.full") { TrashGlyph(c, true); return; }

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

    // ── Glyph implementations (geometry unchanged, colors now theme-aware) ──

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
            c.Children.Add(Vec(FormattableString.Invariant($"M5,{7.0 + i * 2.0} H10.6"), null, PaperLine, 0.7));
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
        if (id.Contains("cd")) { CdDriveGlyph(c); return; }
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

    static void NetworkGlyph(Canvas c)
    {
        // Two connected monitors (Network Neighborhood metaphor)
        // Left monitor
        c.Children.Add(Vec("M1.5,3 H7.5 V8.5 H1.5 Z", MonEdge, MonEdge, 0.4));
        c.Children.Add(Vec("M2.3,3.8 H6.7 V7.7 H2.3 Z", MonScreen));
        c.Children.Add(Vec("M3.5,9 H5.5 V10 H3.5 Z", MonStand));
        // Right monitor
        c.Children.Add(Vec("M8.5,3 H14.5 V8.5 H8.5 Z", MonEdge, MonEdge, 0.4));
        c.Children.Add(Vec("M9.3,3.8 H13.7 V7.7 H9.3 Z", MonScreen));
        c.Children.Add(Vec("M10.5,9 H12.5 V10 H10.5 Z", MonStand));
        // Connection line between them
        c.Children.Add(Vec("M7.5,5.7 H8.5", null, MonEdge, 1.0));
        c.Children.Add(Vec("M7.5,6.7 H8.5", null, MonEdge, 1.0));
    }

    static void TrashGlyph(Canvas c, bool full)
    {
        // Waste basket: trapezoid body + handle + (optional) crumpled paper
        c.Children.Add(Vec("M3.5,5.5 H12.5 V12.5 L11.5,14 H4.5 Z", PaperFill, PaperEdge, 0.5));
        c.Children.Add(Vec("M5.5,3.5 H10.5 V5 H5.5 Z", PaperFill, PaperEdge, 0.5)); // handle
        c.Children.Add(Vec("M6.5,4 H9.5", null, PaperLine, 0.5)); // handle detail lines
        c.Children.Add(Vec("M6.5,4.5 H9.5", null, PaperLine, 0.5));
        if (full)
        {
            // Crumpled paper sticking out
            c.Children.Add(Vec("M5.5,3.5 L7.5,1.5 L9.5,3.5 Z", PaperFill, PaperEdge, 0.4));
            c.Children.Add(Vec("M7,2.8 L8,2.2 L8.5,3.2", null, PaperLine, 0.4));
        }
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
            // Invariant, like every other computed path in this file: under a comma-decimal locale
            // (pl_PL!) culture-sensitive interpolation emits "2,5" and PathMarkupParser throws on
            // the UI thread — one video file in a Filer folder crashed the whole shell.
            c.Children.Add(Vec(FormattableString.Invariant($"M{x:0.##},4.35 h1 v1 h-1 Z"), Brushes.White));   // top holes
            c.Children.Add(Vec(FormattableString.Invariant($"M{x:0.##},10.65 h1 v1 h-1 Z"), Brushes.White));  // bottom holes
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

    // ── Start-menu fixed-item glyphs (bevel-m2.14) ───────────────────────
    // The eight standard Win2000 Start-menu entries. Authored in the same 16-unit
    // vector space and theme-aware brushes as the file-type glyphs, exported to PNG
    // by Bevel.IconPreview, and loaded by the Start menu as its fixed MenuItem icons.

    static void StartGlyph(Canvas c, string id)
    {
        switch (id)
        {
            case "start.programs":  ProgramsGlyph(c); break;
            case "start.documents": DocumentsGlyph(c); break;
            case "start.settings":  SettingsGlyph(c); break;
            case "start.search":    SearchGlyph(c); break;
            case "start.help":      HelpGlyph(c); break;
            case "start.run":       RunGlyph(c); break;
            case "start.logoff":    LogOffGlyph(c); break;
            case "start.shutdown":  ShutDownGlyph(c); break;
            default:                DocGlyph(c); break;
        }
    }

    // Programs — a manila folder with a small program window resting on it.
    static void ProgramsGlyph(Canvas c)
    {
        c.Children.Add(Vec(FolderBackData, FolderBack, FolderEdge));
        c.Children.Add(Vec(FolderFrontData, FolderFront, FolderEdge));
        c.Children.Add(Vec("M7.8,2.0 H14.4 V7.0 H7.8 Z", PaperFill, PaperEdge, 0.4));   // window body
        c.Children.Add(Vec("M7.8,2.0 H14.4 V3.5 H7.8 Z", ExeTitle));                    // title bar
        c.Children.Add(Vec("M8.7,4.6 H13.5 M8.7,5.7 H12.3", null, ExeLine, 0.55));      // content lines
    }

    // Documents — a folder pocket holding a lined sheet that rises above the flap.
    static void DocumentsGlyph(Canvas c)
    {
        c.Children.Add(Vec(FolderBackData, FolderBack, FolderEdge));
        c.Children.Add(Vec("M5.5,2.2 H10.4 L12.4,4.2 V9.6 H5.5 Z", PaperFill, PaperEdge, 0.4));  // sheet
        c.Children.Add(Vec("M10.4,2.2 V4.2 H12.4 Z", PaperFold, PaperEdge, 0.35));                // corner fold
        c.Children.Add(Vec("M6.6,5.4 H11.2 M6.6,6.7 H11.2 M6.6,8.0 H9.6", null, PaperLine, 0.55)); // text lines
        c.Children.Add(Vec(FolderFrontData, FolderFront, FolderEdge));                            // front flap over sheet
    }

    // Settings — the Control Panel window with a cog in front.
    static void SettingsGlyph(Canvas c)
    {
        c.Children.Add(Vec("M1.8,3.0 H11.3 V10.8 H1.8 Z", PaperFill, PaperEdge, 0.45));  // window body
        c.Children.Add(Vec("M1.8,3.0 H11.3 V4.6 H1.8 Z", ExeTitle));                     // title bar
        c.Children.Add(Vec("M3.0,6.4 H9.2 M3.0,7.8 H7.6", null, ExeLine, 0.55));         // content lines
        Gear(c, 11.1, 10.3, 4.0);                                                        // cog, front-right
    }

    // Search — a folder behind a magnifying glass.
    static void SearchGlyph(Canvas c)
    {
        var frame = S(ThemeTokens.ColorWindowFrame, "#000000");
        c.Children.Add(Vec(FolderBackData, FolderBack, FolderEdge));
        c.Children.Add(Vec(FolderFrontData, FolderFront, FolderEdge));
        double ld = 5.4, r = ld / 2, cx = 8.6 + r, cy = 3.8 + r;
        c.Children.Add(Ell(8.6, 3.8, ld, Sky, frame, 0.7));                              // glass lens
        double hx = cx + r * 0.72, hy = cy + r * 0.72;
        c.Children.Add(Vec(FormattableString.Invariant($"M{hx:0.##},{hy:0.##} L{hx + 2.7:0.##},{hy + 2.7:0.##}"),
            null, frame, 1.2));                                                          // handle
    }

    // Help — a blue book with a question mark on its page.
    static void HelpGlyph(Canvas c)
    {
        var mark = S(ThemeTokens.ColorHighlight, "#0A246A");
        c.Children.Add(Vec("M3.0,2.6 H12.2 a1,1 0 0 1 1,1 V13.4 H4.0 a1,1 0 0 1 -1,-1 Z", WordBlue, PaperEdge, 0.4)); // cover
        c.Children.Add(Vec("M4.0,3.4 H12.4 V12.0 H4.0 Z", PaperFill, PaperEdge, 0.35));                                // page
        c.Children.Add(Vec("M6.4,5.8 a1.7,1.7 0 0 1 3.3,0.7 c0,1.1 -1.35,1.35 -1.35,2.4", null, mark, 0.95));         // ? hook
        c.Children.Add(Ell(7.85, 10.0, 1.05, mark));                                                                   // ? dot
    }

    // Run — a program window with a green "go" triangle.
    static void RunGlyph(Canvas c)
    {
        c.Children.Add(Vec("M2.0,3.2 H11.0 V11.4 H2.0 Z", PaperFill, PaperEdge, 0.45));  // window body
        c.Children.Add(Vec("M2.0,3.2 H11.0 V4.8 H2.0 Z", ExeTitle));                     // title bar
        c.Children.Add(Vec("M3.2,6.6 H9.2 M3.2,8.0 H8.0 M3.2,9.4 H6.8", null, ExeLine, 0.55)); // lines
        c.Children.Add(Vec("M10.4,7.6 L14.6,10.2 L10.4,12.8 Z", SheetGreen, PaperEdge, 0.3));   // launch triangle
    }

    // Log Off — an open door with an arrow leaving through it.
    static void LogOffGlyph(Canvas c)
    {
        var frame = S(ThemeTokens.ColorButtonShadow, "#808080");
        var door = S(ThemeTokens.ColorButtonFace, "#D4D0C8");
        var arrow = S(ThemeTokens.ColorHighlight, "#0A246A");
        c.Children.Add(Vec("M7.6,2.2 H13.4 V13.8 H7.6 Z", door, frame, 0.5));            // door panel
        c.Children.Add(Ell(8.4, 7.3, 1.1, S(ThemeTokens.ColorWindowFrame, "#000000")));  // knob
        c.Children.Add(Vec("M1.8,8.0 H6.6 M4.2,5.6 L6.8,8.0 L4.2,10.4", null, arrow, 1.2)); // out arrow
    }

    // Shut Down — the red power symbol (broken ring + stem).
    static void ShutDownGlyph(Canvas c)
    {
        var red = PdfRed;
        c.Children.Add(Vec("M5.7,5.2 A4.6,4.6 0 1 0 10.3,5.2", null, red, 1.5));  // ring, open at top
        c.Children.Add(Vec("M8,2.6 V7.8", null, red, 1.5));                        // stem
    }

    // A small cog: eight radial teeth, a body disc and a bore — for Settings.
    static void Gear(Canvas c, double cx, double cy, double d)
    {
        double r = d / 2;
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4;
            double x1 = cx + Math.Cos(a) * r * 0.9, y1 = cy + Math.Sin(a) * r * 0.9;
            double x2 = cx + Math.Cos(a) * (r + r * 0.45), y2 = cy + Math.Sin(a) * (r + r * 0.45);
            c.Children.Add(Vec(FormattableString.Invariant($"M{x1:0.##},{y1:0.##} L{x2:0.##},{y2:0.##}"),
                null, GearBody, r * 0.5));
        }
        c.Children.Add(Ell(cx - r, cy - r, d, GearBody, GearEdge, 0.4));   // body
        double hr = r * 0.42;
        c.Children.Add(Ell(cx - hr, cy - hr, hr * 2, PaperFill, GearEdge, 0.3)); // bore
    }
}