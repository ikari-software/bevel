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
    /// The settled frame of <paramref name="window"/>: polls until a capture exists, satisfies
    /// <paramref name="ready"/> (when given) and is byte-identical to the capture before it, then returns it.
    /// Throws when that does not happen within <paramref name="timeout"/> (default 10 s), naming what never
    /// became ready, so a render that never finished fails loudly instead of saving a half-drawn frame.
    ///
    /// This is the one settle loop for every render test (bevel-i145). The hand-rolled ones it replaces each
    /// stopped at the first frame that passed a size check: CaptureRenderedFrame can still return the
    /// PREVIOUS frame, or transiently null under a loaded full-suite run, so what they saved depended on
    /// timing. Synchronous on purpose: an async settle loop resumes on whatever dispatcher is current after
    /// the await, which is how a test ends up driving a dead one (bevel-2nn2).
    ///
    /// Stability is checked on the RENDERED FRAME rather than on layout state, because that is what gets
    /// written to disk. The short sleep between polls is not a settle delay: it yields to the thread-pool
    /// work some renders wait on (icon decodes), and the loop exits on the first stable frame.
    /// </summary>
    public static WriteableBitmap CaptureSettled(Window window, Func<WriteableBitmap, bool>? ready = null,
        TimeSpan? timeout = null, string? what = null)
    {
        if (TryCaptureSettled(window, ready, timeout ?? TimeSpan.FromSeconds(10), out var last) is { } settled)
            return settled;
        var name = what ?? window.Title ?? "window";
        throw new InvalidOperationException(last is null
            ? $"{name}: no frame became ready within the timeout"
            : $"{name}: the frame never settled (kept changing) within the timeout");
    }

    /// <summary>The settle loop: the settled frame, or null on timeout with <paramref name="last"/> set to the
    /// last ready frame seen (null if none was).</summary>
    private static WriteableBitmap? TryCaptureSettled(Window window, Func<WriteableBitmap, bool>? ready,
        TimeSpan timeout, out WriteableBitmap? last)
    {
        byte[]? previous = null;
        last = null;
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var frame = window.CaptureRenderedFrame();
            if (frame is not null && (ready is null || ready(frame)))
            {
                using var buffer = new MemoryStream();
                frame.Save(buffer);
                var bytes = buffer.ToArray();
                if (previous is not null && bytes.AsSpan().SequenceEqual(previous)) return frame;
                previous = bytes;
                last = frame;
            }
            else
            {
                previous = null;   // a not-ready frame breaks the run of identical ones
            }
            Thread.Sleep(15);
        }
        return null;
    }

    /// <summary>
    /// The settle loop for <see cref="Save"/>, which keeps its old contract: a frame that never settles is
    /// still saved at the last size seen (the shot is then reported by the site's drift check, not lost), and
    /// only a window that never rendered at all throws.
    /// </summary>
    private static PixelSize WaitForStableFrame(Window window, string path)
    {
        var settled = TryCaptureSettled(window, ready: null, TimeSpan.FromSeconds(5), out var last);
        return (settled ?? last)?.PixelSize ?? throw new InvalidOperationException($"nothing rendered for {path}");
    }
}
