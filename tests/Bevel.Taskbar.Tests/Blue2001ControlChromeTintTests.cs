using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Bevel.Core;
using Bevel.UI.Blue2001;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>The beige control chrome (progress well + border, group frame, tab faces) follows the Luna colourway by
/// TINT. It was once pushed through the blue-chrome hue rotation, which turned Purple's beige (49 deg) into GREEN
/// (49 + 46 = 95) and darkened it with the chrome's LightMul. Pins hue family and lightness per colourway.</summary>
[Collection("TaskbarTheme")]
public class Blue2001ControlChromeTintTests
{
    private static readonly string[] Keys =
    {
        "Blue2001.Brush.ProgressWell", "Blue2001.Brush.ProgressWellBorder",
        "Blue2001.Brush.GroupFrame", "Blue2001.Brush.TabFace",
    };

    private static Color Solid(string key)
        => Assert.IsType<SolidColorBrush>(Application.Current!.Resources[key]).Color;

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min, l = (max + min) / 2, h = 0, s = 0;
        if (d > 0)
        {
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h *= 60;
        }
        return (h, s, l);
    }

    private static (double H, double S, double L) Hsl(string key) => ToHsl(Solid(key));

    [AvaloniaFact]
    public void Purple_control_chrome_is_violet_not_green_and_stays_light()
    {
        try
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Blue2001);
            Blue2001VariantService.Apply("Purple", "Hybrid");

            // Frame and tab faces follow the violet tint (not the 95 deg green the chrome rotation gave).
            foreach (var key in new[] { "Blue2001.Brush.GroupFrame", "Blue2001.Brush.TabFace" })
            {
                var (h, _, _) = Hsl(key);
                Assert.True(h is > 250 and < 300, $"{key} hue {h:F0}");
            }
            // The well is pinned WARM (orange blocks on lavender turn muddy): cream, light, orange-family hue.
            var (wh, _, wl) = Hsl("Blue2001.Brush.ProgressWell");
            Assert.True(wh is > 15 and < 50 && wl > 0.88, $"well hue {wh:F0} L={wl:F2}");
            Assert.True(Hsl("Blue2001.Brush.GroupFrame").L > 0.7);
            Assert.True(Hsl("Blue2001.Brush.TabFace").L > 0.7);
        }
        finally
        {
            Blue2001VariantService.Clear();
            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
        }
    }

    [AvaloniaFact]
    public void Purple_progress_blocks_are_orange_and_other_colourways_stay_green()
    {
        static double FillHue(string variant)
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Blue2001);
            Blue2001VariantService.Apply(variant, "Hybrid");
            var g = Assert.IsType<LinearGradientBrush>(Application.Current!.Resources["Blue2001.Brush.Progress"]);
            return ToHsl(g.GradientStops[1].Color).H;
        }
        try
        {
            Assert.InRange(FillHue("Purple"), 15, 50);
            foreach (var v in new[] { "Blue", "Silver", "Black" })
                Assert.InRange(FillHue(v), 90, 150);
        }
        finally
        {
            Blue2001VariantService.Clear();
            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
        }
    }

    [AvaloniaTheory]
    [InlineData("Silver")]
    [InlineData("Black")]
    public void Neutral_colourways_never_tint_the_control_chrome_green(string variant)
    {
        try
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Blue2001);
            Blue2001VariantService.Apply(variant, "Hybrid");
            foreach (var key in Keys)
            {
                var (h, s, _) = Hsl(key);
                Assert.True(s < 0.2 || h is < 60 or > 180, $"{key} hue {h:F0} sat {s:F2}");
            }
        }
        finally
        {
            Blue2001VariantService.Clear();
            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
        }
    }

    [AvaloniaFact]
    public void Render_purple_progress_bar_for_eyeballing()
    {
        var dir = Environment.GetEnvironmentVariable("BEVEL_PURPLE_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Blue2001);
            Blue2001VariantService.Apply("Purple", "Hybrid");
            var win = new Window { Width = 360, Height = 150, Content = new StackPanel { Margin = new Thickness(16), Spacing = 10, Children =
            {
                new ProgressBar { Value = 60, Height = 20 },
                new Border { BorderThickness = new Thickness(1), Padding = new Thickness(6), BorderBrush = (IBrush)Application.Current!.Resources["Blue2001.Brush.GroupFrame"]!, Child = new TextBlock { Text = "group frame" } },
                new TabControl { Items = { new TabItem { Header = "One", Content = new TextBlock { Text = "x" } }, new TabItem { Header = "Two" } } },
            } } };
            win.Show();
            Bevel.TestSupport.SiteShot.Save(win, Path.Combine(dir, "purple-progress.png"), 1);
        }
        finally
        {
            Blue2001VariantService.Clear();
            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
        }
    }
}
