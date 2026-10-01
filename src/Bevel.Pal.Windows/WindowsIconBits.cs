using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Bevel.Pal.Windows;

/// <summary>
/// HICON → top-down 32bpp BGRA with STRAIGHT (non-premultiplied) alpha. The one place the
/// icon-bitmap interop lives: the taskbar's per-window icons (<see cref="WindowsWindowManager"/>)
/// and the file/app type icons (<see cref="WindowsIconProvider"/>) need exactly this conversion,
/// and a second hand-rolled copy of the GetIconInfo/GetObject/GetDIBits dance is how the two drift
/// apart (one of them gaining a fix the other doesn't).
///
/// Deliberately NOT a general "Interop" junk-drawer — the port's no-shared-interop rule is about
/// not growing one of those: this is a single conversion with a single entry point, and its
/// P/Invoke stays private to it.
///
/// Alpha is left straight because that is what a PNG encoder wants; the <c>PalImage</c> consumer
/// premultiplies on its way into the shared icon pool.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsIconBits
{
    /// <summary>
    /// Reads <paramref name="hIcon"/>'s colour bitmap as top-down BGRA (straight alpha). Returns
    /// false with an empty buffer for a monochrome icon or any GDI failure — a caller treats that as
    /// "this window/file has no icon", never as an error.
    ///
    /// Ownership of <paramref name="hIcon"/> stays with the caller and is NOT consumed here: a class
    /// icon or a WM_GETICON result belongs to the other process and must never be destroyed.
    /// </summary>
    internal static bool TryRead(IntPtr hIcon, out int width, out int height, out byte[] bgra)
    {
        width = height = 0;
        bgra = Array.Empty<byte>();
        if (hIcon == IntPtr.Zero) return false;
        if (!GetIconInfo(hIcon, out ICONINFO ii)) return false;

        IntPtr hbmColor = ii.hbmColor, hbmMask = ii.hbmMask;
        try
        {
            // Monochrome icon (colour bitmap absent, mask carries AND+XOR at double height): rare
            // enough in 2026 that the honest answer is "no icon" rather than a wrong rendering.
            if (hbmColor == IntPtr.Zero) return false;

            var bm = new BITMAP();
            if (GetObject(hbmColor, Marshal.SizeOf<BITMAP>(), ref bm) == 0) return false;
            int w = bm.bmWidth, h = bm.bmHeight;
            if (w <= 0 || h <= 0) return false;

            var colour = ReadDib(hbmColor, w, h);
            if (colour is null) return false;

            // A 32bpp icon carries its own alpha. A legacy (24bpp/palette) icon comes back with
            // alpha all-zero, i.e. fully transparent = invisible; its transparency lives in the
            // 1bpp AND mask instead, so read that and derive alpha from it. Forcing opaque (the
            // older behaviour here) turned such an icon into a solid square block.
            if (!HasAnyAlpha(colour))
                ApplyMaskAlpha(colour, hbmMask, w, h);

            width = w;
            height = h;
            bgra = colour;
            return true;
        }
        catch { return false; }
        finally
        {
            // GetIconInfo hands over COPIES of both bitmaps — leaking these leaks GDI objects, and
            // the taskbar walks every window every poll.
            if (hbmColor != IntPtr.Zero) DeleteObject(hbmColor);
            if (hbmMask != IntPtr.Zero) DeleteObject(hbmMask);
        }
    }

    /// <summary>Premultiplies BGR by A in place — the layout <c>PalImage</c> carries.</summary>
    internal static void Premultiply(byte[] bgra)
    {
        for (int i = 0; i < bgra.Length; i += 4)
        {
            byte a = bgra[i + 3];
            if (a == 255) continue;
            bgra[i] = (byte)(bgra[i] * a / 255);
            bgra[i + 1] = (byte)(bgra[i + 1] * a / 255);
            bgra[i + 2] = (byte)(bgra[i + 2] * a / 255);
        }
    }

    /// <summary>Pulls any bitmap out as 32bpp BGRA, top-down (negative biHeight). A 1bpp mask
    /// converts through its implicit palette, so mask bit 1 arrives as white and 0 as black.</summary>
    private static byte[]? ReadDib(IntPtr hbm, int w, int h)
    {
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,          // negative => top-down rows
            biPlanes = 1,
            biBitCount = 32,
            biCompression = BI_RGB,
        };

        var bytes = new byte[checked(w * h * 4)];
        IntPtr hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return null;
        try
        {
            return GetDIBits(hdc, hbm, 0, (uint)h, bytes, ref bmi, DIB_RGB_COLORS) == 0 ? null : bytes;
        }
        finally { ReleaseDC(IntPtr.Zero, hdc); }
    }

    private static bool HasAnyAlpha(byte[] bgra)
    {
        for (int i = 3; i < bgra.Length; i += 4)
            if (bgra[i] != 0) return true;
        return false;
    }

    /// <summary>Alpha from the AND mask: a set mask pixel (white) is transparent, a clear one
    /// (black) opaque. If the mask can't be read, fall back to fully opaque — a visible icon with
    /// a square background still beats an invisible one.</summary>
    private static void ApplyMaskAlpha(byte[] bgra, IntPtr hbmMask, int w, int h)
    {
        var mask = hbmMask == IntPtr.Zero ? null : ReadDib(hbmMask, w, h);
        for (int i = 3; i < bgra.Length; i += 4)
            bgra[i] = mask is null || mask[i - 3] < 128 ? (byte)255 : (byte)0;
    }

    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, ref BITMAP lpvObject);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines,
        byte[] lpvBits, ref BITMAPINFOHEADER lpbi, uint uUsage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
