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

    // Enabled (bevel-dotj): mirrored macOS status items are TEMPLATE glyphs — white ink designed for the
    // dark system menu bar, so captured as-is they render white-on-gray on Bevel's light Win2000 tray (no
    // contrast). Recolour only those inferred templates to the bar's contrast ink (Bevel.Brush.TrayText —
    // dark on the Win2000 grey well, light on the Luna blue well); REAL multi-colour app icons are left
    // untouched by the colour/fill heuristic below. The per-pixel scan + recolour is heavy, so callers run
    // Process OFF the UI thread (TrayItemViewModel.Retint marshals only the finished bitmap back).
    // static readonly, not const: it gates a runtime branch below (the opt-out early-return), and a const
    // would let the compiler fold that branch to unreachable → CS0162, which CI treats as an error.
    private static readonly bool TintTemplateIcons = true;

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

        // Opt-out hook (kept for a future "show real captured colours" setting): when off, skip the
        // per-pixel analysis and pass the icon through unchanged.
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
