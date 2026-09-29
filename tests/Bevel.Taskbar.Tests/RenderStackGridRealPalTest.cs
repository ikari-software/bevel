using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
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
/// The Downloads-stack grid rendered through the REAL macOS PAL (bevel-9elh), for the landing page.
///
/// <see cref="RenderStackGridTest"/> runs on the Fake PAL, which is right for asserting the grid's
/// behaviour deterministically — but its capture is not a publishable screenshot: FakeIconProvider
/// returns a transparent tile, so the two non-previewable cells come out BLANK, and its "previews" are
/// flat colour blocks. Shipping that on the site would show a feature that looks broken.
///
/// This one writes real PNG bytes (encoded by Avalonia, then decoded back by ImageIO through
/// MacOSThumbnailProvider) and lets MacOSIconProvider supply genuine system type icons for the files
/// that have no preview. Everything in the capture is produced by the shipping code path.
///
/// macOS-gated: it early-returns green elsewhere, matching this repo's gating convention.
/// </summary>
[Collection("TaskbarTheme")]
public class RenderStackGridRealPalTest : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "bevel-stackgrid-real-" + Guid.NewGuid().ToString("N")[..10]);

    public RenderStackGridRealPalTest() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [AvaloniaFact]
    public void Render_stack_preview_grid_through_the_real_pal()
    {
        if (!OperatingSystem.IsMacOS()) return;

        // Real image files: painted here, encoded to PNG, and read back by ImageIO — so the thumbnails
        // in the capture are genuinely decoded, not supplied by a test double.
        WritePng("sunset.png", Color.Parse("#E8743B"), Color.Parse("#FFD66B"));
        WritePng("harbour.png", Color.Parse("#1B5B8A"), Color.Parse("#7FC4E8"));
        WritePng("forest.png", Color.Parse("#245C2E"), Color.Parse("#8FD08A"));
        WritePng("bloom.png", Color.Parse("#8E2F6B"), Color.Parse("#E8A0CF"));
        // No preview engine for these: the grid must fall back to the real system type icon.
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "meeting notes");
        File.WriteAllBytes(Path.Combine(_dir, "archive.zip"), new byte[] { 0x50, 0x4B, 0x05, 0x06 });

        var icons = new IconLoader(new Bevel.Pal.MacOS.MacOSIconProvider());
        using var stack = new StackViewModel(_dir, appEnv: null, icons,
            new PreviewLoader(icons, new Bevel.Pal.MacOS.MacOSThumbnailProvider()));

        var view = new StackFlyoutView { DataContext = stack };
        var window = new Window
        {
            SystemDecorations = SystemDecorations.None,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = new SolidColorBrush(Color.Parse("#D4D0C8")),
            Content = view,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        stack.Refresh();
        Pump(() => stack.Items.Count == 6 && stack.Items.All(i => i.PreviewSource is not null));

        Assert.Equal(6, stack.Items.Count);
        // The four images decode; the txt and zip must NOT claim a content preview — and must still have
        // a bitmap, which on the real PAL is the system's own type icon rather than a blank tile.
        Assert.Equal(4, stack.Items.Count(i => i.HasContentPreview));
        Assert.All(stack.Items, i => Assert.NotNull(i.PreviewSource));

        var frame = Capture(window);
        var outPath = Environment.GetEnvironmentVariable("BEVEL_STACKGRID_REAL_OUT")
                      ?? Path.Combine(Path.GetTempPath(), "bevel-stack-grid-real.png");
        Bevel.TestSupport.SiteShot.Save(window, outPath);
    }

    /// <summary>Paints a two-stop vertical gradient and saves it as a real PNG.</summary>
    private void WritePng(string name, Color top, Color bottom)
    {
        var size = new PixelSize(320, 240);
        using var rtb = new RenderTargetBitmap(size, new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
        {
            ctx.DrawRectangle(
                new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) },
                },
                null, new Rect(0, 0, size.Width, size.Height));
        }
        rtb.Save(Path.Combine(_dir, name));
    }

    private static void Pump(Func<bool> ready)
    {
        for (var i = 0; i < 400 && !ready(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static WriteableBitmap Capture(Window window)
    {
        window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        return frame!;
    }
}
