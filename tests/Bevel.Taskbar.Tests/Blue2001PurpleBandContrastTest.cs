using Bevel.Core;
using System.Linq;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Bevel.UI.Blue2001;
using Avalonia.Headless;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>Guards the Purple-variant gradient-intensity boost (Blue2001Variant.ColorXform.BandContrast): the
/// Purple colour axis compresses the taskbar/caption light→dark spread (LightMul 0.6) and violet steps
/// read weakly, so the bands are re-expanded. Asserts the generated Purple caption gradient keeps a
/// meaningful lightness spread — a regression to no boost would drop it well below this floor.</summary>
[Collection("TaskbarTheme")]   // serialize theme-mutating tests: they share Application.Current (bevel-hd05)
public class Blue2001PurpleBandContrastTest
{
    // HSL lightness = (max+min)/2 of the normalized RGB channels.
    private static double Lightness(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        return (System.Math.Max(r, System.Math.Max(g, b)) + System.Math.Min(r, System.Math.Min(g, b))) / 2.0;
    }

    private static double LightnessSpread(string key)
    {
        var brush = (LinearGradientBrush)Application.Current!.Resources[key]!;
        var ls = brush.GradientStops.Select(s => Lightness(s.Color)).ToList();
        return ls.Max() - ls.Min();
    }

    [AvaloniaFact]
    public void Purple_caption_and_taskbar_keep_an_intense_gradient()
    {
        try
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Blue2001);
            Blue2001VariantService.Apply("Purple", "Hybrid");

            // Without the boost the Purple caption spread compresses to ~0.11 and the taskbar to ~0.13;
            // with the boost they widen well past this floor. Picks up a revert to BandContrast = 1.0.
            Assert.True(LightnessSpread("Bevel.Brush.CaptionActive") > 0.16,
                $"caption spread={LightnessSpread("Bevel.Brush.CaptionActive"):F3}");
            Assert.True(LightnessSpread("Bevel.Brush.TaskbarBackground") > 0.16,
                $"taskbar spread={LightnessSpread("Bevel.Brush.TaskbarBackground"):F3}");
        }
        finally
        {
            Blue2001VariantService.Clear();
            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
        }
    }
}
