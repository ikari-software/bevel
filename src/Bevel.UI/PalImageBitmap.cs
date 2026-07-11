using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Bevel.Pal.Abstractions;

namespace Bevel.UI;

/// <summary>
/// Converts a PAL <see cref="PalImage"/> (BGRA, premultiplied — the output of
/// <c>IIconProvider.GetIconAsync</c>) into an Avalonia bitmap. The first consumer of the
/// icon pixel path; shared so the taskbar, Start menu, desktop, and file manager all turn
/// PAL icons into images the same way.
/// </summary>
public static class PalImageBitmap
{
    public static Bitmap? ToBitmap(PalImage image)
    {
        var w = image.Width;
        var h = image.Height;
        if (w <= 0 || h <= 0 || image.Bgra is null || image.Bgra.Length < w * h * 4)
            return null;

        var bitmap = new WriteableBitmap(
            new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

        using var fb = bitmap.Lock();
        var rowBytes = w * 4;
        // Copy row by row — the locked framebuffer stride (RowBytes) may be padded wider
        // than the source rows.
        for (var y = 0; y < h; y++)
            Marshal.Copy(image.Bgra, y * rowBytes, fb.Address + y * fb.RowBytes, rowBytes);

        return bitmap;
    }
}
