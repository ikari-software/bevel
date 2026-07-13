using System.IO;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Unit tests for StartLogo's flat-fill SVG-subset parser (the Windows/Tux badges). Exercises the
/// parser logic directly via the internal <see cref="StartLogo.Parse"/> seam, independent of the
/// avares asset pipeline — the render smoke test covers the real assets end to end.
/// </summary>
public class StartLogoParseTests
{
    private static StartLogo.ParsedSvg Parse(string svg) =>
        StartLogo.Parse(new MemoryStream(Encoding.UTF8.GetBytes(svg)));

    // ISolidColorBrush, not SolidColorBrush: class/inline fills are mutable SolidColorBrush, but the
    // no-fill fallback is the immutable Brushes.Black — both implement the interface.
    private static Color FillOf(StartLogo.ParsedSvg svg, int i) =>
        ((ISolidColorBrush)svg.Shapes[i].Fill).Color;

    [AvaloniaFact]
    public void Parses_viewbox_paths_polygons_and_class_fills()
    {
        var parsed = Parse(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 40 20\">" +
            "<defs><style>.a { fill: #ff0000; } .b { fill: #00ff00; }</style></defs>" +
            "<path class=\"a\" d=\"M0,0 L10,0 L10,10 Z\" />" +
            "<polygon class=\"b\" points=\"20,0 30,0 30,10\" />" +
            "</svg>");

        Assert.Equal(40, parsed.Width);
        Assert.Equal(20, parsed.Height);
        Assert.Equal(2, parsed.Shapes.Count);           // path + polygon both parsed
        Assert.Equal(Color.Parse("#ff0000"), FillOf(parsed, 0));
        Assert.Equal(Color.Parse("#00ff00"), FillOf(parsed, 1)); // polygon converted + class fill
    }

    [AvaloniaFact]
    public void Inline_fill_wins_over_class_fill()
    {
        var parsed = Parse(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\">" +
            "<defs><style>.a { fill: #ff0000; }</style></defs>" +
            "<path class=\"a\" fill=\"#0000ff\" d=\"M0,0 L5,5 Z\" />" +
            "</svg>");

        Assert.Equal(Color.Parse("#0000ff"), FillOf(parsed, 0));
    }

    [AvaloniaFact]
    public void Class_less_shape_falls_back_to_black()
    {
        var parsed = Parse(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\">" +
            "<path d=\"M0,0 L5,5 Z\" /></svg>");

        Assert.Equal(Colors.Black, FillOf(parsed, 0));
    }

    [AvaloniaFact]
    public void Malformed_viewbox_throws_which_the_badge_builder_catches()
    {
        // A short viewBox makes vb[2] index out of range; StartLogo.Svg()'s try/catch degrades this
        // to an empty badge rather than crashing taskbar construction (verified by that fallback).
        Assert.ThrowsAny<System.Exception>(() => Parse(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0\"><path d=\"M0,0 Z\" /></svg>"));
    }
}
