using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The Downloads-stack flyout as a macOS-style preview GRID (bevel-9elh), rendered to real pixels via
/// Skia: it asserts that content previews actually PAINT (not just that the view-model claims them),
/// and dumps a PNG that can be harvested for the landing page. The Fake PAL supplies deterministic
/// previews, so this needs no Quick Look and no fixture images.
/// </summary>
[Collection("TaskbarTheme")]   // serialize theme-mutating tests: they share Application.Current (bevel-hd05)
public class RenderStackGridTest : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "bevel-stackgrid-" + Guid.NewGuid().ToString("N")[..10]);

    public RenderStackGridTest()
    {
        Directory.CreateDirectory(_dir);
        // A believable Downloads folder: previewable files (photos, PDFs) plus ones that only have a
        // type icon, so the render shows BOTH branches honestly side by side.
        var names = new[]
        {
            "sunset.png", "invoice.pdf", "screenshot.png", "logo.jpg",
            "notes.txt", "archive.zip", "diagram.png", "report.pdf",
        };
        for (var i = 0; i < names.Length; i++)
        {
            var path = Path.Combine(_dir, names[i]);
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-i));
        }
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [AvaloniaFact]
    public void Render_stack_preview_grid_to_png()
    {
        var icons = new IconLoader(new Bevel.Pal.Fake.FakeIconProvider());
        using var stack = new StackViewModel(_dir, appEnv: null, icons,
            new PreviewLoader(icons, new Bevel.Pal.Fake.FakeThumbnailProvider()));

        var view = new StackFlyoutView { DataContext = stack };
        var window = new Window
        {
            SystemDecorations = SystemDecorations.None,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = new SolidColorBrush(Color.Parse("#D4D0C8")),  // Win2000 flyout face
            Content = view,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        stack.Refresh();
        Pump(() => stack.Items.Count == 8 && stack.Items.All(i => i.PreviewSource is not null));

        // 3 PNGs + 1 JPG + 2 PDFs preview; the .txt and .zip honestly show type icons instead.
        Assert.Equal(8, stack.Items.Count);
        Assert.Equal(6, stack.Items.Count(i => i.HasContentPreview));

        var frame = Capture(window);
        Assert.True(frame.PixelSize.Width > 200, $"grid too narrow to be a grid: {frame.PixelSize}");
        Assert.True(frame.PixelSize.Height < frame.PixelSize.Width, "a grid, not a one-per-row list");
        // The previews are really on screen: the flyout face is grey, so any strongly coloured pixel is
        // painted preview content. Six cells' worth is thousands of pixels, not a stray antialiased edge.
        Assert.True(ColouredPixels(frame) > 2000, "content previews did not paint into the grid");

        var outPath = Environment.GetEnvironmentVariable("BEVEL_STACKGRID_RENDER_OUT")
                      ?? Path.Combine(Path.GetTempPath(), "bevel-stack-grid.png");
        frame.Save(outPath);
    }

    /// <summary>Pumps the dispatcher until <paramref name="ready"/> holds — the off-thread preview
    /// loads land through <c>Dispatcher.UIThread.InvokeAsync</c>.</summary>
    private static void Pump(Func<bool> ready)
    {
        for (var i = 0; i < 400 && !ready(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Captures the settled frame. The first capture after a batch of bitmaps lands can still
    /// be the pre-bitmap composite, so compose once more and keep the second frame.</summary>
    private static WriteableBitmap Capture(Window window)
    {
        window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        return frame!;
    }

    /// <summary>Pixels whose channels differ enough to be painted image content rather than the grey
    /// Win2000 chrome (which is neutral: R≈G≈B).</summary>
    private static int ColouredPixels(WriteableBitmap frame)
    {
        using var buffer = frame.Lock();
        var row = new byte[buffer.RowBytes];
        var count = 0;
        for (var y = 0; y < frame.PixelSize.Height; y++)
        {
            Marshal.Copy(buffer.Address + y * buffer.RowBytes, row, 0, row.Length);
            for (var x = 0; x < frame.PixelSize.Width; x++)
            {
                var i = x * 4;
                if (Math.Abs(row[i] - row[i + 1]) > 20 || Math.Abs(row[i + 1] - row[i + 2]) > 20)
                    count++;
            }
        }
        return count;
    }
}
