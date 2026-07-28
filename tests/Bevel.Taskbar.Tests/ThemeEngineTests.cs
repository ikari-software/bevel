using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Classic.Avalonia.Theme;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Engine coverage for runtime theme + font switching (PKG-03 / FNT-01, bevel-9js). The headless
/// harness boots the real <c>Bevel.App.App</c>, so the Win2000 theme's token layer is live and these
/// assert the Application-level resource cascade end-to-end — including that the standalone
/// <c>ThemeFlat.axaml</c> dictionary actually loads and its overrides win over the base tokens.
/// Each test restores the default statics so it doesn't perturb the shared app for other renders.
/// </summary>
[Collection("TaskbarTheme")]   // serialize theme-mutating tests: they share Application.Current (bevel-hd05)
public class ThemeEngineTests
{
    private static object? Resolve(string key)
    {
        Application.Current!.TryGetResource(key, null, out var value);
        return value;
    }

    [AvaloniaFact]
    public void Flat_theme_swaps_the_token_bundle_then_restores()
    {
        // Baseline: the default Win2000 theme renders eased 3-D bevels.
        Assert.Equal(EdgeRendering.Smooth, Resolve("Bevel.Edge.Rendering"));

        try
        {
            Bevel.UI.ThemeService.Apply("flat");
            // Proves ThemeFlat.axaml loaded AND its override outranks the base tokens.
            Assert.Equal(EdgeRendering.Crisp, Resolve("Bevel.Edge.Rendering"));
            Assert.Equal(22d, Resolve("Bevel.Metric.CaptionHeight"));

            // Switching back removes the merged dict — the base tokens show through again (no stacking).
            Bevel.UI.ThemeService.Apply("win2000");
            Assert.Equal(EdgeRendering.Smooth, Resolve("Bevel.Edge.Rendering"));
            Assert.Equal(18d, Resolve("Bevel.Metric.CaptionHeight"));
        }
        finally
        {
            Bevel.UI.ThemeService.Apply("win2000");
        }
    }

    [AvaloniaFact]
    public void Unknown_theme_falls_back_to_default()
    {
        try
        {
            Bevel.UI.ThemeService.Apply("no-such-theme");
            Assert.Equal(EdgeRendering.Smooth, Resolve("Bevel.Edge.Rendering"));
        }
        finally
        {
            Bevel.UI.ThemeService.Apply("win2000");
        }
    }

    [AvaloniaFact]
    public void Font_override_shadows_the_ui_font_then_restores()
    {
        var themeDefault = Assert.IsAssignableFrom<FontFamily>(Resolve("Bevel.Font.UI"));

        try
        {
            Bevel.UI.FontService.Apply("Menlo");
            var overridden = Assert.IsAssignableFrom<FontFamily>(Resolve("Bevel.Font.UI"));
            Assert.Equal("Menlo", overridden.Name);

            // Empty restores the theme's bundled face by removing the shadow (not pinning a copy).
            Bevel.UI.FontService.Apply("");
            var restored = Assert.IsAssignableFrom<FontFamily>(Resolve("Bevel.Font.UI"));
            Assert.Equal(themeDefault.Name, restored.Name);
        }
        finally
        {
            Bevel.UI.FontService.Apply("");
        }
    }
}
