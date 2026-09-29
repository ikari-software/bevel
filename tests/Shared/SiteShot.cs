using System;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;

namespace Bevel.TestSupport;

/// <summary>
/// Saves a laid-out window as a landing-page screenshot, rendered at HiDPI.
///
/// The page used to ship every shot at scale 1.0, so on a Retina display each one was smeared across
/// 2x2 device pixels — the worst possible asset for a page whose subject is crisp bevels and vector
/// chrome. Bevel's whole premise is the classic design at modern quality, not a pixel-accurate
/// reproduction of a 2001 CRT, and the screenshots have to make that argument rather than undermine it.
///
/// Avalonia headless has no scaling knob (AvaloniaHeadlessPlatformOptions exposes only
/// UseHeadlessDrawing and FrameBufferFormat), so this goes through RenderTargetBitmap, whose DPI
/// argument scales the render. That is a genuine re-rasterisation and not an upscale: glyph stems come
/// out hinted and clean, and the output carries FEWER unique colours than a Lanczos upscale of the 1x
/// (2260 vs 6821 when measured) because real rendering makes clean edges where interpolation invents
/// intermediate values.
///
/// The logical size comes from a 1x capture rather than from Bounds/ClientSize, so the result is
/// exactly 2x what the page used to ship. index.html keeps declaring the LOGICAL size: the browser
/// lays out to the attributes and spends the extra pixels on HiDPI displays.
/// </summary>
public static class SiteShot
{
    /// <summary>Device-pixel ratio the shots are rendered for. One constant, every shot.</summary>
    public const double Scale = 2.0;

    /// <summary>
    /// Renders <paramref name="window"/> at <paramref name="scale"/> (default <see cref="Scale"/>) and
    /// writes a PNG.
    ///
    /// A window whose content forces an intermediate composition layer — a TabControl's presenter, for
    /// instance — renders WRONG above 1x here: Avalonia allocates that layer at the bitmap's device size
    /// and then blits it into logical space, so the chrome comes out at 1x while the content is magnified
    /// and clipped. Nine of the ten landing-page shots are unaffected; the one that isn't passes scale: 1
    /// rather than have product code bent around a screenshot.
    /// </summary>
    public static PixelSize Save(Window window, string path, double scale = Scale)
    {
        var logical = WaitForStableFrame(window, path);

        using var bitmap = new RenderTargetBitmap(
            new PixelSize((int)(logical.Width * scale), (int)(logical.Height * scale)),
            new Vector(96 * scale, 96 * scale));
        bitmap.Render(window);
        bitmap.Save(path);
        return logical;
    }

    /// <summary>
    /// Polls until two consecutive captures are byte-identical, and returns the settled logical size.
    ///
    /// A shot captured mid-transition is a shot whose bytes depend on timing, and the site's drift check
    /// compares bytes — so it reports a stale screenshot on a run where nothing changed, and everyone
    /// learns to ignore it. The Luna task button's hover gradient was doing exactly that: same content,
    /// slightly different highlight from one run to the next.
    ///
    /// Stability is checked on the RENDERED FRAME rather than on layout state, because that is what gets
    /// written to disk — the same reason the start menu's settle loop polls the frame, not Bounds.
    /// </summary>
    private static PixelSize WaitForStableFrame(Window window, string path)
    {
        byte[]? previous = null;
        var size = default(PixelSize);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var frame = window.CaptureRenderedFrame();
            if (frame is not null)
            {
                size = frame.PixelSize;
                using var buffer = new MemoryStream();
                frame.Save(buffer);
                var bytes = buffer.ToArray();
                if (previous is not null && bytes.AsSpan().SequenceEqual(previous)) return size;
                previous = bytes;
            }
            Thread.Sleep(15);
        }

        if (size != default) return size;   // never settled, but something rendered — save it and move on
        throw new InvalidOperationException($"nothing rendered for {path}");
    }
}
