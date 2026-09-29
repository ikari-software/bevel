using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Bevel.Pal.Abstractions;
using static Bevel.Pal.MacOS.CoreFoundationInterop;
using static Bevel.Pal.MacOS.CoreGraphicsInterop;
using static Bevel.Pal.MacOS.ImageIOInterop;

namespace Bevel.Pal.MacOS;

/// <summary>
/// Real content previews for user files (bevel-9elh) — the thumbnail source behind the taskbar's
/// macOS-style stack grid. Two decoders, both pure C frameworks (no ObjC, no blocks, so the whole
/// path is P/Invoke-only and safe off the main thread):
///
/// • <b>Images</b> — ImageIO's <c>CGImageSourceCreateThumbnailAtIndex</c>, which decodes an
///   embedded thumbnail when the file has one and downsamples the full image otherwise. This is the
///   same engine Finder's icon previews use, so a photo previews as the photo (HEIC/JPEG/PNG/TIFF/
///   GIF/BMP/WebP/ICO), EXIF-rotated upright.
/// • <b>PDF</b> — CoreGraphics' <c>CGPDFDocument</c>: page 1, crop box, honouring /Rotate, drawn on
///   white (a PDF page is transparent, and transparent-on-transparent reads as an empty cell).
///
/// Anything else returns null: this provider does NOT pretend to preview formats it cannot decode —
/// the caller falls back to a larger file-TYPE icon from <see cref="IIconProvider"/>. Text, Office
/// documents, video and audio would need a Quick Look generator round-trip (QuickLookThumbnailing is
/// an ObjC-block API); that is a separate, deliberate step, not something to fake here.
///
/// Results are cached per (path, size, last-write time) so replacing a file re-previews it. Decode
/// happens on the thread pool, never on the caller's thread (core rule: never block the UI thread).
/// </summary>
public sealed class MacOSThumbnailProvider : IThumbnailProvider
{
    /// <summary>Formats the two decoders above actually handle. Checked before any I/O.</summary>
    private static readonly HashSet<string> Previewable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".heic", ".heif", ".gif", ".tif", ".tiff",
        ".bmp", ".webp", ".ico", ".icns", ".jp2", ".avif", ".dng", ".cr2", ".nef", ".arw",
        ".pdf",
    };

    /// <summary>Bound on the cache so a long-lived process that walks a huge folder can't grow it
    /// without limit; previews are far bigger than icons, so the ceiling is modest.</summary>
    private const int MaxCacheEntries = 256;

    private readonly ConcurrentDictionary<string, PalImage?> _cache = new();

    public bool CanPreview(string path)
        => !string.IsNullOrEmpty(path) && Previewable.Contains(Path.GetExtension(path));

    public ValueTask<PalImage?> GetThumbnailAsync(string path, int maxPixelSize, CancellationToken ct = default)
    {
        if (maxPixelSize <= 0) maxPixelSize = 64;
        if (!OperatingSystem.IsMacOS() || !CanPreview(path))
            return ValueTask.FromResult<PalImage?>(null);

        // Key on the write stamp too: a file replaced at the same path must not keep a stale preview.
        long stamp;
        try { stamp = File.GetLastWriteTimeUtc(path).Ticks; }
        catch { return ValueTask.FromResult<PalImage?>(null); }

        var key = path + "|" + maxPixelSize + "|" + stamp;
        if (_cache.TryGetValue(key, out var cached))
            return ValueTask.FromResult(cached);   // warm: instant, no thread hop

        return new ValueTask<PalImage?>(Task.Run(() =>
        {
            var image = Render(path, maxPixelSize);
            if (_cache.Count >= MaxCacheEntries) _cache.Clear();
            _cache[key] = image;
            return image;
        }, ct));
    }

    // ── Decode ───────────────────────────────────────────────────────────────

    private static PalImage? Render(string path, int max)
    {
        try
        {
            return string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase)
                ? RenderPdf(path, max)
                : RenderImage(path, max);
        }
        catch
        {
            return null; // a preview is a nicety: an unreadable/corrupt file falls back to its type icon
        }
    }

    private static PalImage? RenderImage(string path, int max)
    {
        var url = CFUrlForPath(path, isDirectory: false);
        if (url == IntPtr.Zero) return null;
        try
        {
            var source = CGImageSourceCreateWithURL(url, IntPtr.Zero);
            if (source == IntPtr.Zero) return null;
            try
            {
                var options = ThumbnailOptions(max);
                if (options == IntPtr.Zero) return null;
                IntPtr cgImage;
                try { cgImage = CGImageSourceCreateThumbnailAtIndex(source, 0, options); }
                finally { CFRelease(options); }
                if (cgImage == IntPtr.Zero) return null;
                try { return Rasterize(cgImage, max); }
                finally { CFRelease(cgImage); }
            }
            finally { CFRelease(source); }
        }
        finally { CFRelease(url); }
    }

    /// <summary>Draws a decoded CGImage into a premultiplied-BGRA buffer fitted inside
    /// <paramref name="max"/> on its longest side — same pixel contract as the icon path, so
    /// <c>PalImageBitmap</c> turns both into Avalonia bitmaps unchanged.</summary>
    private static PalImage? Rasterize(IntPtr cgImage, int max)
    {
        var srcW = (int)CGImageGetWidth(cgImage);
        var srcH = (int)CGImageGetHeight(cgImage);
        if (srcW <= 0 || srcH <= 0) return null;
        var (w, h) = Fit(srcW, srcH, max);

        var ctx = CreateBitmapContext(w, h);
        if (ctx == IntPtr.Zero) return null;
        try
        {
            CGContextDrawImage(ctx, new CGRect(0, 0, w, h), cgImage);
            return Copy(ctx, w, h);
        }
        finally { CGContextRelease(ctx); }
    }

    private static PalImage? RenderPdf(string path, int max)
    {
        var url = CFUrlForPath(path, isDirectory: false);
        if (url == IntPtr.Zero) return null;
        try
        {
            var doc = CGPDFDocumentCreateWithURL(url);
            if (doc == IntPtr.Zero) return null;
            try
            {
                if (CGPDFDocumentGetNumberOfPages(doc) < 1) return null;
                var page = CGPDFDocumentGetPage(doc, 1);   // page 1 is the cover — what Finder shows
                if (page == IntPtr.Zero) return null;

                var box = CGPDFPageGetBoxRect(page, kCGPDFCropBox);
                var rotated = Math.Abs(CGPDFPageGetRotationAngle(page) / 90) % 2 == 1;
                var srcW = rotated ? box.Height : box.Width;
                var srcH = rotated ? box.Width : box.Height;
                if (srcW <= 0 || srcH <= 0) return null;
                var (w, h) = Fit((int)Math.Round(srcW), (int)Math.Round(srcH), max);

                var ctx = CreateBitmapContext(w, h);
                if (ctx == IntPtr.Zero) return null;
                try
                {
                    // A PDF page paints no background; on a transparent buffer that reads as an empty
                    // cell, so lay the page on white the way every PDF viewer does.
                    CGContextSetRGBFillColor(ctx, 1, 1, 1, 1);
                    CGContextFillRect(ctx, new CGRect(0, 0, w, h));
                    // GetDrawingTransform handles both the crop-box offset and /Rotate for us.
                    var transform = CGPDFPageGetDrawingTransform(
                        page, kCGPDFCropBox, new CGRect(0, 0, w, h), 0, preserveAspectRatio: true);
                    CGContextConcatCTM(ctx, transform);
                    CGContextDrawPDFPage(ctx, page);
                    return Copy(ctx, w, h);
                }
                finally { CGContextRelease(ctx); }
            }
            finally { CGPDFDocumentRelease(doc); }
        }
        finally { CFRelease(url); }
    }

    /// <summary>Longest side scaled to <paramref name="max"/>, aspect preserved, never upscaled
    /// past the source (a 24×24 favicon must not ship as a blurry 128×128).</summary>
    internal static (int Width, int Height) Fit(int srcW, int srcH, int max)
    {
        var scale = Math.Min(1.0, (double)max / Math.Max(srcW, srcH));
        return (Math.Max(1, (int)Math.Round(srcW * scale)), Math.Max(1, (int)Math.Round(srcH * scale)));
    }

    private static PalImage? Copy(IntPtr ctx, int w, int h)
    {
        var data = CGBitmapContextGetData(ctx);
        if (data == IntPtr.Zero) return null;
        var stride = (int)CGBitmapContextGetBytesPerRow(ctx);
        var bytes = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            Marshal.Copy(data + y * stride, bytes, y * w * 4, w * 4);
        return new PalImage(w, h, bytes);
    }

    private static IntPtr CreateBitmapContext(int w, int h)
    {
        var colorSpace = CGColorSpaceCreateDeviceRGB();
        if (colorSpace == IntPtr.Zero) return IntPtr.Zero;
        // AlphaPremultipliedFirst (2) | ByteOrder32Little (2<<12): B,G,R,A premultiplied in memory —
        // Avalonia's Bgra8888 + AlphaFormat.Premul, matching the icon path.
        const uint bitmapInfo = 2u | (2u << 12);
        var ctx = CGBitmapContextCreate(IntPtr.Zero, (nuint)w, (nuint)h, 8, (nuint)(w * 4), colorSpace, bitmapInfo);
        CGColorSpaceRelease(colorSpace);
        // Previews are photographic content scaled down — interpolate at the highest quality the
        // rasterizer offers (never trade smoothness for "authenticity").
        if (ctx != IntPtr.Zero) CGContextSetInterpolationQuality(ctx, kCGInterpolationHigh);
        return ctx;
    }


    /// <summary>The ImageIO options dictionary: always produce a thumbnail (even when the file has no
    /// embedded one), cap its longest side, and apply the EXIF orientation so a phone photo is
    /// upright. Owned by the caller (CFRelease).</summary>
    private static IntPtr ThumbnailOptions(int max)
    {
        var keyAlways = Constant(Frameworks.ImageIO, "kCGImageSourceCreateThumbnailFromImageAlways");
        var keyTransform = Constant(Frameworks.ImageIO, "kCGImageSourceCreateThumbnailWithTransform");
        var keyMaxPixel = Constant(Frameworks.ImageIO, "kCGImageSourceThumbnailMaxPixelSize");
        var trueValue = Constant(Frameworks.CoreFoundation, "kCFBooleanTrue");
        var keyCallbacks = SymbolAddress(Frameworks.CoreFoundation, "kCFTypeDictionaryKeyCallBacks");
        var valueCallbacks = SymbolAddress(Frameworks.CoreFoundation, "kCFTypeDictionaryValueCallBacks");
        if (keyAlways == IntPtr.Zero || keyTransform == IntPtr.Zero || keyMaxPixel == IntPtr.Zero
            || trueValue == IntPtr.Zero || keyCallbacks == IntPtr.Zero || valueCallbacks == IntPtr.Zero)
            return IntPtr.Zero;

        var maxNumber = CFNumberCreate(IntPtr.Zero, kCFNumberIntType, ref max);
        if (maxNumber == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            var keys = new[] { keyAlways, keyTransform, keyMaxPixel };
            var values = new[] { trueValue, trueValue, maxNumber };
            return CFDictionaryCreate(IntPtr.Zero, keys, values, keys.Length, keyCallbacks, valueCallbacks);
        }
        finally { CFRelease(maxNumber); }
    }

    // ── Exported data symbols (CFStringRef / callback structs) ────────────────
    // These are DATA, not functions, so they can't be [DllImport]ed: resolve the symbol and either
    // dereference it (a CFTypeRef constant) or pass its address (a callbacks struct).

    private static readonly ConcurrentDictionary<string, IntPtr> _symbols = new();

    private static IntPtr SymbolAddress(string library, string name)
        => _symbols.GetOrAdd(library + "#" + name, _ =>
        {
            try
            {
                return NativeLibrary.TryLoad(library, out var handle)
                    && NativeLibrary.TryGetExport(handle, name, out var address)
                        ? address : IntPtr.Zero;
            }
            catch { return IntPtr.Zero; }
        });

    private static IntPtr Constant(string library, string name)
    {
        var address = SymbolAddress(library, name);
        return address == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(address);
    }

    // ── Native ───────────────────────────────────────────────────────────────


    private const int kCFNumberIntType = 9;
    private const int kCGPDFCropBox = 1;
    private const int kCGInterpolationHigh = 3;

}
