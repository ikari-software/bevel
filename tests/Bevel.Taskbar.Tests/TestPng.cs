using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Bevel.Taskbar.Tests;

/// <summary>Builds small synthetic PNGs for the tray tests — the byte shape a mirrored status item's
/// <c>IconPng</c> arrives in — from a per-pixel (r,g,b,a) function, so a test can say exactly what is
/// in the icon (a sparse white glyph, a solid block, …) without a fixture file.</summary>
internal static class TestPng
{
    public static byte[] Build(int n, System.Func<int, int, (byte r, byte g, byte b, byte a)> px)
    {
        var wb = new WriteableBitmap(new PixelSize(n, n), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var buf = new byte[n * n * 4];
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                var (r, g, b, a) = px(x, y);
                var i = (y * n + x) * 4;
                buf[i] = (byte)(b * a / 255);
                buf[i + 1] = (byte)(g * a / 255);
                buf[i + 2] = (byte)(r * a / 255);
                buf[i + 3] = a;
            }
        using (var fb = wb.Lock())
            for (var y = 0; y < n; y++)
                Marshal.Copy(buf, y * n * 4, fb.Address + y * fb.RowBytes, n * 4);
        using var ms = new MemoryStream();
        wb.Save(ms);
        return ms.ToArray();
    }

    /// <summary>A fully opaque single-colour square — a "real app icon" as far as the tint heuristic
    /// is concerned (cell-filling, so never recoloured).</summary>
    public static byte[] Solid(int n, byte r, byte g, byte b) => Build(n, (_, _) => (r, g, b, 255));
}
