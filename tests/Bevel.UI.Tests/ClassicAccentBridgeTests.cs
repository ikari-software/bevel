using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using ClassicCommon = Classic.CommonControls;
using Xunit;

namespace Bevel.UI.Tests;

/// <summary>
/// Locks the accent→SystemColors bridge (bevel-sp5 / ENG-01): the Classic theme's generic
/// accent keys (HighlightBrush, ThemeControl*…, bound by the modern-control templates) must
/// follow the scheme-driven SystemColors roles instead of carrying their own off-palette
/// values (upstream shipped a teal #086F9E highlight), and the "Windows Standard" scheme's
/// menu values must be Win2000, not XP (spec 05-theming.md §8.1). Resolved through the real
/// app theme via an attached control so DynamicResource brush colours are actually bound.
/// </summary>
public sealed class ClassicAccentBridgeTests
{
    private static Color ResolveBrushColor(string key)
    {
        // Attach a probe to a window so the theme's DynamicResource-driven brushes resolve.
        var probe = new Border();
        var window = new Window { Content = probe };
        window.Show();
        try
        {
            Assert.True(probe.TryFindResource(key, window.ActualThemeVariant, out var value),
                $"theme does not define '{key}'");
            var brush = Assert.IsAssignableFrom<ISolidColorBrush>(value);
            return brush.Color;
        }
        finally
        {
            window.Close();
        }
    }

    private static Color ResolveSystemColor(object key)
    {
        Assert.True(
            Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out var value),
            $"theme does not define system color '{key}'");
        return Assert.IsType<Color>(value);
    }

    [AvaloniaFact]
    public void Accent_highlight_follows_the_scheme_highlight_not_upstream_teal()
        => Assert.Equal(Color.Parse("#FF0A246A"), ResolveBrushColor("HighlightBrush"));

    [AvaloniaFact]
    public void Accent_control_faces_follow_the_scheme_button_face()
    {
        Assert.Equal(Color.Parse("#FFD4D0C8"), ResolveBrushColor("ThemeControlMidBrush"));
        Assert.Equal(Color.Parse("#FFD4D0C8"), ResolveBrushColor("ThemeControlHighlightLowBrush"));
    }

    [AvaloniaFact]
    public void Accent_background_foreground_and_borders_follow_their_system_roles()
    {
        Assert.Equal(Colors.White, ResolveBrushColor("ThemeBackgroundBrush"));
        Assert.Equal(Colors.Black, ResolveBrushColor("ThemeForegroundBrush"));
        Assert.Equal(Color.Parse("#FF808080"), ResolveBrushColor("ThemeBorderMidBrush"));
        Assert.Equal(Colors.Black, ResolveBrushColor("ThemeBorderHighBrush"));
    }

    [AvaloniaFact]
    public void Standard_scheme_menu_values_are_win2000_not_xp()
    {
        Assert.Equal(
            Color.Parse("#FFD4D0C8"),
            ResolveSystemColor(ClassicCommon.SystemColors.MenuBarColorKey));
        Assert.Equal(
            Color.Parse("#FF0A246A"),
            ResolveSystemColor(ClassicCommon.SystemColors.MenuHighlightColorKey));
    }

    private static double ResolveDouble(object key)
    {
        Assert.True(
            Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out var value),
            $"theme does not define '{key}'");
        return Assert.IsType<double>(value);
    }

    [AvaloniaFact]
    public void Vendored_system_parameters_agree_with_the_bevel_metrics()
    {
        // Both derive from theme.json via ThemeGen (bevel-zhf) — this locks the invariant.
        // Win2000 'Windows Standard': menu bar 19 (upstream shipped 18), caption 18,
        // scrollbars 16 (spec 05-theming.md §3).
        Assert.Equal(
            ResolveDouble(Bevel.UI.ThemeTokens.MetricMenuBarHeight),
            ResolveDouble(ClassicCommon.SystemParameters.MenuBarHeightKey));
        Assert.Equal(19d, ResolveDouble(ClassicCommon.SystemParameters.MenuBarHeightKey));

        Assert.Equal(
            ResolveDouble(Bevel.UI.ThemeTokens.MetricCaptionHeight),
            ResolveDouble(ClassicCommon.SystemParameters.WindowCaptionHeightKey));

        Assert.Equal(
            ResolveDouble(Bevel.UI.ThemeTokens.MetricScrollBarSize),
            ResolveDouble(ClassicCommon.SystemParameters.VerticalScrollBarWidthKey));
    }
}
