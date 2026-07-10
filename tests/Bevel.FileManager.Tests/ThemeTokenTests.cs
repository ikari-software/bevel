using System.Reflection;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Bevel.UI;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// Locks the semantic theme-token layer (bevel-38y): every key declared in
/// <see cref="ThemeTokens"/> resolves in the running app's resources with the right type, the
/// Win2000 palette matches the spec table (docs/spec/05-theming.md §8.1) exactly, the metrics
/// match §3's Win2000 column, and each Bevel.Brush.* is wired to its Bevel.Color.* value.
/// TestAppBuilder boots the real Bevel.App, so this exercises the actual Win2000Theme merge.
/// </summary>
public sealed class ThemeTokenTests
{
    private static object Resolve(string key)
    {
        Assert.True(
            Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out var value),
            $"theme does not define token '{key}'");
        return value!;
    }

    private static IEnumerable<(string Name, string Key)> DeclaredTokens()
        => typeof(ThemeTokens)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (f.Name, (string)f.GetRawConstantValue()!));

    [AvaloniaFact]
    public void Every_declared_token_resolves_with_its_expected_type()
    {
        foreach (var (name, key) in DeclaredTokens())
        {
            var value = Resolve(key);
            switch (name)
            {
                case var n when n.StartsWith("Color"): Assert.IsType<Color>(value); break;
                case var n when n.StartsWith("Brush"): Assert.IsAssignableFrom<IBrush>(value); break;
                case var n when n.StartsWith("Font"): Assert.IsType<FontFamily>(value); break;
                case "MetricCornerRadius": Assert.IsType<CornerRadius>(value); break;
                case "MetricButtonPadding": Assert.IsType<Thickness>(value); break;
                case var n when n.StartsWith("Metric"): Assert.IsType<double>(value); break;
                default: Assert.Fail($"unclassified token constant '{name}'"); break;
            }
        }
    }

    [AvaloniaFact]
    public void Win2000_palette_matches_the_spec_table()
    {
        // docs/spec/05-theming.md §8.1 — "Windows Standard" scheme, verbatim.
        var expected = new Dictionary<string, string>
        {
            [ThemeTokens.ColorButtonFace] = "#FFD4D0C8",
            [ThemeTokens.ColorButtonHighlight] = "#FFFFFFFF",
            [ThemeTokens.ColorButtonLight] = "#FFD4D0C8",
            [ThemeTokens.ColorButtonShadow] = "#FF808080",
            [ThemeTokens.ColorButtonDkShadow] = "#FF404040",
            [ThemeTokens.ColorWindow] = "#FFFFFFFF",
            [ThemeTokens.ColorWindowText] = "#FF000000",
            [ThemeTokens.ColorWindowFrame] = "#FF000000",
            [ThemeTokens.ColorGrayText] = "#FF808080",
            [ThemeTokens.ColorAppWorkspace] = "#FF808080",
            [ThemeTokens.ColorDesktop] = "#FF3A6EA5",
            [ThemeTokens.ColorMenu] = "#FFD4D0C8",
            [ThemeTokens.ColorMenuText] = "#FF000000",
            [ThemeTokens.ColorScrollbar] = "#FFD4D0C8",
            [ThemeTokens.ColorActiveTitle] = "#FF0A246A",
            [ThemeTokens.ColorGradientActiveTitle] = "#FFA6CAF0",
            [ThemeTokens.ColorInactiveTitle] = "#FF808080",
            [ThemeTokens.ColorGradientInactiveTitle] = "#FFC0C0C0",
            [ThemeTokens.ColorActiveTitleText] = "#FFFFFFFF",
            [ThemeTokens.ColorInactiveTitleText] = "#FFD4D0C8",
            [ThemeTokens.ColorHighlight] = "#FF0A246A",
            [ThemeTokens.ColorHighlightText] = "#FFFFFFFF",
            [ThemeTokens.ColorHotTracking] = "#FF000080",
            [ThemeTokens.ColorInfoWindow] = "#FFFFFFE1",
            [ThemeTokens.ColorInfoText] = "#FF000000",
        };

        foreach (var (key, hex) in expected)
            Assert.Equal(Color.Parse(hex), (Color)Resolve(key));
    }

    [AvaloniaFact]
    public void Win2000_metrics_match_the_spec_table()
    {
        // docs/spec/05-theming.md §3 — Win2000 column, logical px at 1.0 scale.
        Assert.Equal(18d, Resolve(ThemeTokens.MetricCaptionHeight));
        Assert.Equal(16d, Resolve(ThemeTokens.MetricCaptionButtonWidth));
        Assert.Equal(14d, Resolve(ThemeTokens.MetricCaptionButtonHeight));
        Assert.Equal(4d, Resolve(ThemeTokens.MetricResizeBorder));
        Assert.Equal(2d, Resolve(ThemeTokens.MetricEdgeThickness));
        Assert.Equal(16d, Resolve(ThemeTokens.MetricScrollBarSize));
        Assert.Equal(19d, Resolve(ThemeTokens.MetricMenuBarHeight));
        Assert.Equal(17d, Resolve(ThemeTokens.MetricMenuItemHeight));
        Assert.Equal(28d, Resolve(ThemeTokens.MetricTaskbarHeight));
        Assert.Equal(75d, Resolve(ThemeTokens.MetricIconGridCellWidth));
        Assert.Equal(75d, Resolve(ThemeTokens.MetricIconGridCellHeight));
        Assert.Equal(1d, Resolve(ThemeTokens.MetricFocusRectInset));
        Assert.Equal(new CornerRadius(0), Resolve(ThemeTokens.MetricCornerRadius));
        Assert.Equal(new Thickness(6, 1), Resolve(ThemeTokens.MetricButtonPadding));
    }

    [AvaloniaFact]
    public void Every_brush_token_is_wired_to_its_color_token()
    {
        // Naming convention: Bevel.Brush.X pairs with Bevel.Color.X. A brush drifting from its
        // color would silently break scheme recolorability (W2K-01).
        var brushes = DeclaredTokens().Where(t => t.Name.StartsWith("Brush"));
        foreach (var (name, key) in brushes)
        {
            var colorKey = "Bevel.Color." + key["Bevel.Brush.".Length..];
            var color = (Color)Resolve(colorKey);
            var brush = Assert.IsAssignableFrom<ISolidColorBrush>(Resolve(key));
            Assert.Equal(color, brush.Color);
        }
    }
}
