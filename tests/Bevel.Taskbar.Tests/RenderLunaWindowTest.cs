using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Dev-only visual render of a framed <see cref="Bevel.UI.BevelWindow"/> under the Luna theme, so the
/// window caption (drawn by Classic's AutoAttachTitleBar off the overridden caption SystemColors) can be
/// eyeballed against the styleguide. Dumps a PNG; not a pixel assertion.
/// </summary>
public class RenderLunaWindowTest
{
    [AvaloniaFact]
    public void Render_luna_framed_window_to_png()
    {
        try
        {
            Bevel.UI.ThemeService.Apply("luna");

            var body = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
            body.Children.Add(new TextBlock { Text = "A framed Bevel window under Luna." });
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new Button { Content = "OK", MinWidth = 75 });
            row.Children.Add(new Button { Content = "Cancel", MinWidth = 75 });
            body.Children.Add(row);

            var window = new Bevel.UI.BevelWindow
            {
                Title = "Bevel — Luna window",
                Width = 360,
                Height = 180,
                Background = new SolidColorBrush(Color.Parse("#ECE9D8")),
                Content = body,
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);

            var outPath = Environment.GetEnvironmentVariable("BEVEL_LUNA_WINDOW_OUT")
                          ?? Path.Combine(Path.GetTempPath(), "bevel-luna-window.png");
            frame!.Save(outPath);
        }
        finally
        {
            Bevel.UI.ThemeService.Apply("win2000");
        }
    }
}
