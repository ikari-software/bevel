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
/// Dev-only: renders the Luna session icons at HIGH resolution (20×) in their real shapes, using the
/// exact Border+Viewbox+Canvas+Path structure the Start menu uses. Each glyph is authored in ONE shared
/// 24×24 grid (matched size, circle centred at 12,12) and rendered via a fixed 24-Canvas, so the grid maps
/// to the button — same size for every glyph, circle centre on the button centre. Set BEVEL_SESSION_ICONS_DIR.
/// </summary>
public class RenderSessionIconsTest
{
    // (label, tile colour, tile px, corner radius px, grid path) — restart/quit are 24px squares, the
    // footer log-off/turn-off are 22px circles; here at 20× (480 square, 440 circle) for crisp inspection.
    private static readonly (string Label, string Color, double Tile, double Corner, string Data)[] Icons =
    {
        ("restart", "#E8A23C", 480, 80,  "M18,12 A6,6 0 1 1 12,6 M12,3.83 L15.58,6 L12,8.17"),
        ("quit",    "#D94A2E", 480, 80,  "M12 4.8 V12 M7.2 8.4 A6 6 0 1 0 16.8 8.4"),
        ("logoff",  "#E8A23C", 440, 220, "M13,6 L7,6 L7,18 L13,18 M10,12 L17,12 M14.5,9.5 L17,12 L14.5,14.5"),
        ("turnoff", "#D94A2E", 440, 220, "M12 4.8 V12 M7.2 8.4 A6 6 0 1 0 16.8 8.4"),
    };

    [AvaloniaFact]
    public void Render_session_icons_hires()
    {
        var dir = Environment.GetEnvironmentVariable("BEVEL_SESSION_ICONS_DIR");
        if (string.IsNullOrEmpty(dir)) return; // opt-in
        Directory.CreateDirectory(dir);

        foreach (var (label, color, tile, corner, data) in Icons)
        {
            var path = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(data),
                Stroke = Brushes.White,
                StrokeThickness = 2,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
            };
            var canvas = new Canvas { Width = 24, Height = 24 };
            canvas.Children.Add(path);
            var vb = new Viewbox
            {
                Width = tile, Height = tile, Stretch = Stretch.Uniform, Child = canvas,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var border = new Border
            {
                Width = tile, Height = tile,
                CornerRadius = new CornerRadius(corner),
                Background = new SolidColorBrush(Color.Parse(color)),
                Child = vb,
            };
            var window = new Window
            {
                SystemDecorations = SystemDecorations.None,
                SizeToContent = SizeToContent.WidthAndHeight,
                Content = border,
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Save(System.IO.Path.Combine(dir, $"{label}.png"));
        }
    }
}
