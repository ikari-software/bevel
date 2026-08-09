using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Dev-only: renders the Luna Start-menu session icons (Restart / Quit / Log Off / Turn Off) through the
/// REAL Border+Viewbox+Path used in the menu, but at 10× logical size — so their glyph centering can be
/// eyeballed crisply and faithfully (real Avalonia Viewbox measure, not an SVG replica). Set
/// BEVEL_SESSION_ICONS_DIR to dump one PNG per icon.
/// </summary>
public class RenderSessionIconsTest
{
    [AvaloniaFact]
    public void Render_session_icons_10x()
    {
        var dir = Environment.GetEnvironmentVariable("BEVEL_SESSION_ICONS_DIR");
        if (string.IsNullOrEmpty(dir)) return; // opt-in
        Directory.CreateDirectory(dir);

        // (label, tile colour, tile px, corner radius px, viewbox px, path data) — 10× the real icons.
        var icons = new (string Label, string Color, double Tile, double Corner, double Vb, string Data)[]
        {
            ("restart", "#E8A23C", 240, 40,  140, "M12.7,8 A4.7,4.7 0 1 1 8,3.3 M8,1.6 L10.8,3.3 L8,5"),
            ("quit",    "#D94A2E", 240, 40,  140, "M11 6 V11 M7 8 A5 5 0 1 0 15 8"),
            ("logoff",  "#E8A23C", 220, 110, 130, "M10,4 L4,4 L4,16 L10,16 M7,10 L14,10 M11.5,7.5 L14,10 L11.5,12.5"),
            ("turnoff", "#D94A2E", 220, 110, 130, "M11 6 V11 M7 8 A5 5 0 1 0 15 8"),
        };

        foreach (var ic in icons)
        {
            var path = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(ic.Data),
                Stroke = Brushes.White,
                StrokeThickness = 2,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
            };
            var vb = new Viewbox
            {
                Width = ic.Vb, Height = ic.Vb, Stretch = Stretch.Uniform, Child = path,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var border = new Border
            {
                Width = ic.Tile, Height = ic.Tile,
                CornerRadius = new CornerRadius(ic.Corner),
                Background = new SolidColorBrush(Color.Parse(ic.Color)),
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
            window.CaptureRenderedFrame()?.Save(System.IO.Path.Combine(dir, $"{ic.Label}.png"));
        }
    }
}
