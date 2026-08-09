using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Pixel-measurement guard for the Win2000 window-border geometry (bevel-ua0w). Commit a967ec8 restructured
/// the frame ("verified unchanged by render" — but there was no render test), which silently dropped the 2px
/// sizing-frame face above the caption and turned the 1px caption→client separator into a 4px band. This
/// renders a framed Win2000 window with a MAGENTA client and asserts the authentic top-border bands:
///   2px 3D bevel · 2px face · 18px caption · 1px separator · client   (= SM_CYSIZEFRAME 4 + caption + 1).
/// </summary>
[Collection("TaskbarTheme")]
public class RenderWin2000BorderTest
{
    [AvaloniaFact]
    public void Win2000_window_border_geometry_is_authentic()
    {
        try
        {
            // Deterministic default Win2000: drop any colour-scheme / Luna-variant token injections a prior
            // test in this collection left behind (they override the static tokens — caption colour AND height).
            Bevel.UI.ThemeService.Apply("win2000");
            Bevel.UI.Luna.LunaVariantService.Clear();
            Bevel.UI.ColorSchemeService.Clear();
            var window = new Bevel.UI.BevelWindow
            {
                Title = "Bevel",
                Width = 240,
                Height = 140,
                Background = new SolidColorBrush(Color.FromRgb(255, 0, 255)),
                Content = new Border { Background = new SolidColorBrush(Color.FromRgb(255, 0, 255)) },
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (Environment.GetEnvironmentVariable("BEVEL_WIN2K_BORDER_OUT") is { } outPath)
                frame!.Save(outPath);

            using var fb = frame!.Lock();

            (int R, int G, int B) At(int x, int y)
            {
                var p = fb.Address + y * fb.RowBytes + x * 4;
                int b0 = Marshal.ReadByte(p, 0), b1 = Marshal.ReadByte(p, 1), b2 = Marshal.ReadByte(p, 2);
                return fb.Format == PixelFormat.Rgba8888 ? (b0, b1, b2) : (b2, b1, b0); // else BGRA
            }
            bool Near((int R, int G, int B) p, int r, int g, int b)
                => Math.Abs(p.R - r) <= 14 && Math.Abs(p.G - g) <= 14 && Math.Abs(p.B - b) <= 14;
            bool Face(int x, int y) => Near(At(x, y), 0xD4, 0xD0, 0xC8);        // ButtonFace / sizing-frame grey
            bool Client(int x, int y) { var p = At(x, y); return p.R > 200 && p.G < 70 && p.B > 200; } // magenta
            // The caption band is whatever the title bar renders (active blue OR inactive grey) — measured by
            // GEOMETRY, so it's just "the solid band that is neither the face frame nor the magenta client".
            bool Caption(int x, int y) => !Face(x, y) && !Client(x, y);

            const int cx = 120;
            // TOP border, top-down: [0-1] 2px 3D bevel · [2-3] 2px face · [4-21] 18px caption · [22] 1px sep · [23+] client
            Assert.True(Face(cx, 2) && Face(cx, 3),
                $"regressed: no 2px sizing-frame face above the caption (a967ec8). y2={At(cx, 2)} y3={At(cx, 3)}");
            Assert.True(Caption(cx, 4), $"caption must start at y=4 (after 2px bevel + 2px face); got {At(cx, 4)}");
            Assert.True(Caption(cx, 21), $"caption must be the full 18px tall (through y=21); got {At(cx, 21)}");
            Assert.True(Face(cx, 22), $"caption→client must be a 1px separator, not a 4px band. y22={At(cx, 22)}");
            Assert.True(Client(cx, 23), $"client must begin at y=23 (2+2+18+1); got {At(cx, 23)}");

            // SIDE frame must be unchanged: [0-1] 2px bevel · [2-3] 2px face · [4+] client
            Assert.True(Face(2, 100) && Face(3, 100), "2px face on the side sizing-frame");
            Assert.True(Client(4, 100), "client must begin at x=4 on the sides (2px bevel + 2px face)");
        }
        finally
        {
            Bevel.UI.ThemeService.Apply("win2000");
        }
    }
}
