using System.Diagnostics;
using System.Runtime.InteropServices;
using Bevel.Pal.Windows;
using Xunit;

namespace Bevel.Pal.Windows.Tests;

/// <summary>
/// The HICON → BGRA conversion shared by the taskbar's window icons and the file/app type icons
/// (bevel-epsz). Portable facts run everywhere; the Windows-only ones use real icons off
/// <c>%WINDIR%</c> binaries and are gated with an early return so the file still passes on the
/// macOS/Linux CI runners.
/// </summary>
public class WindowsIconBitsTests
{
    [Fact]
    public void A_null_handle_is_no_icon_not_an_error()
    {
        Assert.False(WindowsIconBits.TryRead(IntPtr.Zero, out int w, out int h, out var bgra));
        Assert.Equal(0, w);
        Assert.Equal(0, h);
        Assert.Empty(bgra);
    }

    [Fact]
    public void Premultiply_scales_colour_by_alpha_and_leaves_opaque_pixels_alone()
    {
        // b,g,r,a per pixel: half-transparent mid grey, then an opaque pixel that must not change.
        var bgra = new byte[] { 200, 100, 50, 128, 200, 100, 50, 255 };
        WindowsIconBits.Premultiply(bgra);
        Assert.Equal(new byte[] { 100, 50, 25, 128, 200, 100, 50, 255 }, bgra);
    }

    [Fact]
    public void Windows_reads_a_real_icon_with_its_alpha_channel_intact()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!TryExtract(@"C:\Windows\explorer.exe", 64, out var hIcon)) return;

        try
        {
            Assert.True(WindowsIconBits.TryRead(hIcon, out int w, out int h, out var bgra));
            Assert.Equal(64, w);
            Assert.Equal(64, h);
            Assert.Equal(64 * 64 * 4, bgra.Length);

            // Explorer's icon has transparent corners and opaque middle. Both halves matter: all-zero
            // alpha is the invisible-icon bug, all-255 is the solid-square bug.
            int transparent = 0, opaque = 0;
            for (int i = 3; i < bgra.Length; i += 4)
            {
                if (bgra[i] == 0) transparent++;
                else if (bgra[i] == 255) opaque++;
            }
            Assert.True(transparent > 0, "no transparent pixel: alpha was forced opaque");
            Assert.True(opaque > 0, "no opaque pixel: the icon decoded fully transparent");
        }
        finally { DestroyIcon(hIcon); }
    }

    /// <summary>
    /// The shell re-enumerates every 2 s for the life of the session and renders a cold icon for
    /// every newly-seen window, so the conversion must return every GDI object it takes:
    /// <c>GetIconInfo</c> hands over bitmap COPIES, not borrows. A leak here is unbounded.
    /// </summary>
    [Fact]
    public void Windows_repeated_reads_leak_no_gdi_objects()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!TryExtract(@"C:\Windows\explorer.exe", 32, out var hIcon)) return;

        try
        {
            var self = Process.GetCurrentProcess().Handle;
            for (var i = 0; i < 50; i++) WindowsIconBits.TryRead(hIcon, out _, out _, out _);
            uint before = GetGuiResources(self, GR_GDIOBJECTS);

            for (var i = 0; i < 500; i++)
                Assert.True(WindowsIconBits.TryRead(hIcon, out _, out _, out _));

            uint after = GetGuiResources(self, GR_GDIOBJECTS);
            // Exact equality would be brittle (the runtime owns GDI objects of its own), but 500
            // conversions leaking two bitmaps each would be +1000, so any real leak is unmissable.
            Assert.True(after <= before + 10, $"GDI objects grew {before} -> {after} over 500 reads");
        }
        finally { DestroyIcon(hIcon); }
    }

    private static bool TryExtract(string path, int edge, out IntPtr hIcon)
    {
        hIcon = IntPtr.Zero;
        if (!File.Exists(path)) return false;
        var icons = new IntPtr[1];
        uint got = PrivateExtractIcons(path, 0, edge, edge, icons, null, 1, 0);
        if (got == 0 || got == uint.MaxValue || icons[0] == IntPtr.Zero) return false;
        hIcon = icons[0];
        return true;
    }

    private const uint GR_GDIOBJECTS = 0;

    [DllImport("user32.dll", EntryPoint = "PrivateExtractIconsW", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy,
        IntPtr[] phicon, uint[]? piconid, uint nIcons, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
}
