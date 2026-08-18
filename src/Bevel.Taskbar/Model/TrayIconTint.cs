using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Bevel.Taskbar;

/// <summary>
/// Adaptive tray-icon colouring — the macOS "template image" model for mirrored menu-bar status items.
///
/// We mirror OTHER apps' status items via screen capture, so we only have the PIXELS (no <c>isTemplate</c>
/// flag). We infer it: a glyph that is SPARSE (mostly-transparent cell, doesn't fill its own background) and
/// near-MONOCHROME (its ink is all one hue) is treated as a template and RECOLOURED to the bar's contrast
/// ink (<c>Bevel.Brush.TrayText</c> — dark on bright bars, light on dark), so it reads directly on the
/// taskbar. Icons that are colourful, or that fill their cell with their own (contrasting) background, are
/// left untouched. This replaces the old always-dark backing chip.
/// </summary>
public static class TrayIconTint
{
    public readonly record struct Result(Bitmap Image, bool Recoloured);

    // Tuning: an "ink" pixel is >~10% alpha; a pixel is "coloured" if its channel spread exceeds this;
    // a glyph is a template if it fills less than this fraction of the cell and is almost all monochrome.
    private const byte InkAlpha = 24;
    private const int ColourSpread = 40;
    private const double MaxFillForTemplate = 0.55;
    private const double MaxColourFracForTemplate = 0.06;

    // Disabled per user preference (bevel-7hf4): show every tray icon in its REAL captured appearance
    // rather than flattening monochrome glyphs to a single theme ink. The analysis stays behind this
    // flag for a future opt-in "theme tray icons" setting; when it flips back on, move Process off the
    // UI thread (ce-review: the per-pixel scan + recolour must not run inline in a tray update).
    private const bool TintTemplateIcons = false;

    /// <summary>Decode <paramref name="png"/>, and if it looks like a template glyph, return a copy
    /// recoloured to <paramref name="ink"/> (alpha preserved). Otherwise return the icon untouched.
    /// Null only when the PNG can't be decoded.</summary>
    public static Result? Process(byte[]? png, Color ink)
    {
        if (png is null || png.Length == 0) return null;
        Bitmap src;
        try { using var ms = new MemoryStream(png); src = new Bitmap(ms); }
        catch { return null; }

        var size = src.PixelSize;
        int w = size.Width, h = size.Height;
        if (w <= 0 || h <= 0) return new Result(src, false);

        // Tinting is disabled (bevel-7hf4) — skip the whole per-pixel analysis, which otherwise runs
        // on the UI thread on every tray ItemUpdated / ink change and discards its result (ce-review:
        // performance + standards). The analysis below stays intact for the future opt-in setting.
        if (!TintTemplateIcons) return new Result(src, false);

        int stride = w * 4;
        var buf = new byte[stride * h];
        var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try { src.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), buf.Length, stride); }
        catch { handle.Free(); return new Result(src, false); }
        finally { if (handle.IsAllocated) handle.Free(); }

        // Analyse (BGRA, premultiplied). Channel ORDER doesn't matter here: the fill/monochrome metrics are
        // order-independent, and the recolour takes RGB from `ink` and only the ALPHA (always last) from src.
        long opaque = 0, coloured = 0, total = (long)w * h;
        for (int i = 0; i < buf.Length; i += 4)
        {
            byte a = buf[i + 3];
            if (a < InkAlpha) continue;
            opaque++;
            // un-premultiply so a faint antialiased edge isn't mistaken for a dark colour
            int c0 = buf[i] * 255 / a, c1 = buf[i + 1] * 255 / a, c2 = buf[i + 2] * 255 / a;
            int max = Math.Max(c0, Math.Max(c1, c2)), min = Math.Min(c0, Math.Min(c1, c2));
            if (max - min > ColourSpread) coloured++;
        }

        double fill = (double)opaque / total;
        double colourFrac = opaque == 0 ? 1 : (double)coloured / opaque;
        bool isTemplate = opaque > 0 && fill < MaxFillForTemplate && colourFrac < MaxColourFracForTemplate;
        if (!isTemplate) return new Result(src, false);

        // Recolour: RGB <- ink, A <- source alpha (premultiplied). The glyph SHAPE lives in the alpha.
        var outBuf = new byte[stride * h];
        for (int i = 0; i < buf.Length; i += 4)
        {
            byte a = buf[i + 3];
            outBuf[i] = (byte)(ink.B * a / 255);
            outBuf[i + 1] = (byte)(ink.G * a / 255);
            outBuf[i + 2] = (byte)(ink.R * a / 255);
            outBuf[i + 3] = a;
        }

        var dpi = src.Dpi.X > 0 ? src.Dpi : new Vector(96, 96);
        var wb = new WriteableBitmap(size, dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = wb.Lock())
            for (int y = 0; y < h; y++)
                Marshal.Copy(outBuf, y * stride, fb.Address + y * fb.RowBytes, stride);

        src.Dispose();
        return new Result(wb, true);
    }
}
