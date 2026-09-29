using System;
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
        var logical = window.CaptureRenderedFrame()?.PixelSize
                      ?? throw new InvalidOperationException($"nothing rendered for {path}");

        using var bitmap = new RenderTargetBitmap(
            new PixelSize((int)(logical.Width * scale), (int)(logical.Height * scale)),
            new Vector(96 * scale, 96 * scale));
        bitmap.Render(window);
        bitmap.Save(path);
        return logical;
    }
}
