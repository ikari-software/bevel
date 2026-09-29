using System;
using System.IO;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// site/shots/hero-luna.png — the landing page's opening image.
///
/// It was the last hand-composited shot, which cost exactly what an unproduced shot always costs: when
/// the folder glyph's back panel was corrected, every other shot picked the fix up on the next harvest
/// and the hero kept showing the old silhouette, because nothing could regenerate it.
///
/// The three pieces are NOT rebuilt here. Each is dumped by the test that already knows how to render
/// it — Render_luna_start_menu_to_png with its settling loop, Render_luna_framed_window_to_png,
/// Render_luna_taskbar_to_png — so the hero inherits their assertions instead of re-deriving the setup.
/// This step only arranges them, which is why it runs as a separate pass over BEVEL_HERO_PARTS.
/// </summary>
public class SiteHeroTest
{
    [AvaloniaFact]
    public void Render_site_hero()
    {
        var parts = Environment.GetEnvironmentVariable("BEVEL_HERO_PARTS");
        var outPath = Environment.GetEnvironmentVariable("BEVEL_HERO_OUT");
        if (string.IsNullOrEmpty(parts) || string.IsNullOrEmpty(outPath)) return;   // opt-in

        // Loaded as plain bitmaps so one unit is one device pixel throughout — the same reason
        // Render_site_four_up round-trips its tiles through PNG. See SiteShot for what goes wrong
        // when a 192-dpi RenderTargetBitmap is composed directly.
        using var start = Load(parts, "start.png");
        using var window = Load(parts, "window.png");
        using var taskbar = Load(parts, "taskbar.png");

        // The page's hero box, in device pixels. The canvas size is OURS, not the taskbar's: a
        // TaskbarWindow sizes itself to the whole screen (1920 logical here), and letting that dictate
        // the frame would give the page a 3.2:1 letterbox instead of the shot it lays out for.
        var scale = Bevel.TestSupport.SiteShot.Scale;
        var w = (int)(940 * scale);
        var h = (int)(600 * scale);
        var margin = (int)(26 * scale);

        using var canvas = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        using (var ctx = canvas.CreateDrawingContext())
        {
            // Wallpaper, sampled from the composite this replaces so the page's colour doesn't shift.
            ctx.FillRectangle(
                new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.Parse("#7DB2EC"), 0),
                        new GradientStop(Color.Parse("#285086"), 1),
                    },
                },
                new Rect(0, 0, w, h));

            // Dialog upper-right, Start menu resting on the bar at the left, bar along the bottom —
            // a desktop caught mid-use rather than three controls on a slide.
            var barTop = h - (int)taskbar.Size.Height;
            ctx.DrawImage(window, new Rect(
                w - window.Size.Width - margin * 2, margin * 2.5, window.Size.Width, window.Size.Height));
            ctx.DrawImage(start, new Rect(
                margin, barTop - start.Size.Height, start.Size.Width, start.Size.Height));
            // The bar is wider than the frame, so it is drawn in two slices rather than squashed: its
            // left end (Start button and task buttons) and its right end (tray and clock), each at 1:1.
            // Everything between them is flat bar gradient, so the seam is invisible and the furniture
            // at both ends survives — squashing it to fit would compress the glyphs and the type.
            var barH = taskbar.Size.Height;
            const double leftSlice = 1200;
            var rightSlice = w - leftSlice;
            ctx.DrawImage(taskbar,
                new Rect(0, 0, leftSlice, barH),
                new Rect(0, barTop, leftSlice, barH));
            ctx.DrawImage(taskbar,
                new Rect(taskbar.Size.Width - rightSlice, 0, rightSlice, barH),
                new Rect(leftSlice, barTop, rightSlice, barH));
        }

        canvas.Save(outPath);
    }

    private static Bitmap Load(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        Assert.True(File.Exists(path), $"hero part missing: {path} — run the part tests first");
        return new Bitmap(path);
    }
}
