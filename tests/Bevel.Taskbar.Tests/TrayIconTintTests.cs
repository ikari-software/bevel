using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Bevel.Taskbar;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Tray-icon tinting is currently OFF (bevel-7hf4): every mirrored menu-bar icon keeps its REAL captured
/// colours, so Process never recolours — even a sparse monochrome template glyph passes through. The
/// template analysis stays in TrayIconTint behind its flag for a future opt-in "theme tray icons" setting;
/// when that flag returns, the first test flips back to asserting recolour-to-ink.
/// </summary>
public class TrayIconTintTests
{
    private const int N = 16;

    // Build a PNG where px(x,y) => (r,g,b,a).
    private static byte[] Png(System.Func<int, int, (byte r, byte g, byte b, byte a)> px)
    {
        var wb = new WriteableBitmap(new PixelSize(N, N), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var buf = new byte[N * N * 4];
        for (var y = 0; y < N; y++)
            for (var x = 0; x < N; x++)
            {
                var (r, g, b, a) = px(x, y);
                var i = (y * N + x) * 4;
                buf[i] = (byte)(b * a / 255);
                buf[i + 1] = (byte)(g * a / 255);
                buf[i + 2] = (byte)(r * a / 255);
                buf[i + 3] = a;
            }
        using (var fb = wb.Lock())
            for (var y = 0; y < N; y++)
                Marshal.Copy(buf, y * N * 4, fb.Address + y * fb.RowBytes, N * 4);
        using var ms = new MemoryStream();
        wb.Save(ms);
        return ms.ToArray();
    }

    private static bool InBlock(int x, int y, int lo, int hi) => x >= lo && x < hi && y >= lo && y < hi;

    [AvaloniaFact]
    public void Sparse_monochrome_glyph_keeps_captured_colour_while_tinting_is_off()
    {
        // ~25% fill, pure white, transparent elsewhere → a template glyph, the strongest recolour
        // candidate. With tinting disabled it must still pass through untouched.
        var png = Png((x, y) => InBlock(x, y, 4, 12) ? ((byte)255, (byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0));

        var res = TrayIconTint.Process(png, Colors.Black);
        Assert.NotNull(res);
        Assert.False(res!.Value.Recoloured);

        // A centre pixel stays the captured white, not the black ink.
        var px = ReadPixel(res.Value.Image, 8, 8);
        Assert.True(px.R > 215 && px.G > 215 && px.B > 215, $"expected captured white pixel, got {px}");
        Assert.True(px.A > 200, "glyph coverage should be preserved");
    }

    [AvaloniaFact]
    public void Coloured_icon_is_left_untouched()
    {
        // A red/green checker over ~40% of the cell → colourful, not a template.
        var png = Png((x, y) => InBlock(x, y, 3, 13)
            ? ((x + y) % 2 == 0 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)0, (byte)255, (byte)0, (byte)255))
            : ((byte)0, (byte)0, (byte)0, (byte)0));

        var res = TrayIconTint.Process(png, Colors.Black);
        Assert.NotNull(res);
        Assert.False(res!.Value.Recoloured);
    }

    [AvaloniaFact]
    public void Cell_filling_icon_keeps_its_own_background()
    {
        // A solid monochrome square filling the whole cell = its own background → keep as-is.
        var png = Png((x, y) => ((byte)40, (byte)40, (byte)40, (byte)255));

        var res = TrayIconTint.Process(png, Colors.White);
        Assert.NotNull(res);
        Assert.False(res!.Value.Recoloured);
    }

    private static (byte R, byte G, byte B, byte A) ReadPixel(Bitmap bmp, int x, int y)
    {
        var w = bmp.PixelSize.Width;
        var stride = w * 4;
        var buf = new byte[stride * bmp.PixelSize.Height];
        var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try { bmp.CopyPixels(new PixelRect(0, 0, w, bmp.PixelSize.Height), h.AddrOfPinnedObject(), buf.Length, stride); }
        finally { if (h.IsAllocated) h.Free(); }
        var i = (y * w + x) * 4;
        var a = buf[i + 3];
        // un-premultiply for the assertion
        byte U(byte c) => a == 0 ? (byte)0 : (byte)(c * 255 / a);
        return (U(buf[i + 2]), U(buf[i + 1]), U(buf[i]), a);
    }
}
