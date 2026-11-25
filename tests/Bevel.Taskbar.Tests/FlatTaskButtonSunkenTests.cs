using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Bevel.Core;
using Bevel.UI;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>The Flat theme's active task button is SUNKEN (as in Win95): its face is shaded darker at the top than at
/// the bottom, in the native Blue look and in every variant that re-colours the brush.</summary>
[Collection("TaskbarTheme")]
public class FlatTaskButtonSunkenTests
{
    private static double Luma(Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

    private static void AssertTopDarker()
    {
        Assert.True(Application.Current!.TryGetResource("Flat.Brush.TaskButtonChecked", Application.Current.ActualThemeVariant, out var r));
        var g = Assert.IsAssignableFrom<LinearGradientBrush>(r);
        var top = g.GradientStops[0];
        var bottom = g.GradientStops[^1];
        Assert.True(top.Offset < bottom.Offset);
        Assert.True(Luma(top.Color) < Luma(bottom.Color), $"top {top.Color} should be darker than bottom {bottom.Color}");
    }

    [AvaloniaTheory]
    [InlineData("Blue")]
    [InlineData("Ergonomic")]
    [InlineData("Silver")]
    [InlineData("Amber")]
    public void The_checked_face_is_darker_at_the_top(string variant)
    {
        ThemeService.Apply(ThemeIds.Flat);
        try
        {
            FlatVariantService.Apply(new BevelSettings { FlatVariant = variant });
            AssertTopDarker();
        }
        finally { FlatVariantService.Clear(); }
    }
}
