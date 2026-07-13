using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Dev-only visual render of all three Start-button OS badges (Windows flag / Apple / Tux) side by
/// side to a PNG, so the hand-drawn vector geometry can be eyeballed on any host OS — the live app
/// only ever shows the current platform's badge. Also a smoke test that each variant renders.
/// </summary>
public class RenderStartLogoTest
{
    [AvaloniaFact]
    public void Render_all_three_badges_to_png()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 24,
            Margin = new Avalonia.Thickness(24),
        };
        foreach (var kind in new[] { StartLogo.Kind.Windows, StartLogo.Kind.Apple, StartLogo.Kind.Tux })
            row.Children.Add(StartLogo.Build(kind, 96));

        var window = new Window
        {
            SystemDecorations = SystemDecorations.None,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = Brushes.Silver, // classic Start-button face, so black marks read
            Content = row,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(frame!.PixelSize.Width > 250, $"row too narrow: {frame.PixelSize}");

        var outPath = Environment.GetEnvironmentVariable("BEVEL_RENDER_OUT")
                      ?? Path.Combine(Path.GetTempPath(), "bevel-startlogo-render.png");
        frame.Save(outPath);
    }
}
