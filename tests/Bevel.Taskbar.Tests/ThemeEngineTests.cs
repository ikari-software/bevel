using Bevel.Core;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Classic.Avalonia.Theme;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Engine coverage for runtime theme + font switching (PKG-03 / FNT-01, bevel-9js). The headless
/// harness boots the real <c>Bevel.App.App</c>, so the Win2000 theme's token layer is live and these
/// assert the Application-level resource cascade end-to-end, including the Pastel Styles set.
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
    public void Flat_theme_keeps_its_original_token_preview()
    {
        Assert.Contains(ThemeIds.Flat, Bevel.UI.ThemeService.Themes.Select(t => t.Id));
        Assert.Contains(ThemeIds.Pastel, Bevel.UI.ThemeService.Themes.Select(t => t.Id));

        try
        {
            Assert.True(Bevel.UI.ThemeService.Apply(ThemeIds.Flat));
            Assert.Equal(EdgeRendering.Smooth, Resolve("Bevel.Edge.Rendering"));
            Assert.Equal(Color.Parse("#FFFFFF"), Resolve("Bevel.Color.Window"));
            Assert.Equal(Color.Parse("#FFFFFF"), Assert.IsType<SolidColorBrush>(Resolve("Bevel.Brush.Window")).Color);
            Assert.Equal(26d, Resolve("Bevel.Metric.CaptionHeight"));
            Assert.NotNull(Resolve("Bevel.Theme.ScrollBar"));
            Assert.True(Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999));
            Assert.Equal(18d, Resolve("Bevel.Metric.CaptionHeight"));
            Assert.Equal(EdgeRendering.Smooth, Resolve("Bevel.Edge.Rendering"));
        }
        finally
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
        }
    }

    [AvaloniaFact]
    public void Pastel_theme_swaps_palette_surfaces_and_metrics_then_restores()
    {
        Assert.Equal(EdgeRendering.Smooth, Resolve("Bevel.Edge.Rendering"));

        try
        {
            Assert.True(Bevel.UI.ThemeService.Apply(ThemeIds.Pastel));
            Assert.Equal(Color.Parse("#FBF7F1"), Resolve("Bevel.Color.Window"));
            Assert.Equal(Color.Parse("#3A3550"), Resolve("Bevel.Color.WindowText"));
            Assert.NotNull(Resolve("Bevel.Theme.CheckBox"));
            Assert.NotNull(Resolve("Bevel.Theme.RadioButton"));
            Assert.NotNull(Resolve("Bevel.Theme.TextBox"));
            Assert.NotNull(Resolve("Bevel.Theme.ComboBox"));
            Assert.NotNull(Resolve("Bevel.Theme.Slider"));
            Assert.NotNull(Resolve("Bevel.Theme.TabItem"));
            Assert.NotNull(Resolve("Bevel.Theme.TabControl"));
            Assert.NotNull(Resolve("Bevel.Theme.ScrollBar"));
            Assert.NotNull(Resolve("Bevel.Theme.Menu"));
            Assert.NotNull(Resolve("Bevel.Theme.MenuItem"));
            Assert.NotNull(Resolve("Bevel.Theme.HeaderedContentControl"));
            Assert.Equal(26d, Resolve("Bevel.Metric.CaptionHeight"));
            Assert.Equal(34d, Resolve("Bevel.Metric.TaskbarHeight"));
            Assert.IsAssignableFrom<IBrush>(Resolve("Bevel.Brush.TaskbarBackground"));
            Assert.True(Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999));
            Assert.Equal(18d, Resolve("Bevel.Metric.CaptionHeight"));
        }
        finally
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
        }
    }

    [Fact]
    public void Flat_exposes_the_requested_source_variations()
    {
        Assert.Equal(new[] { "Blue", "Ergonomic", "Silver", "Amber" },
            Bevel.UI.FlatVariantService.Variants.Select(v => v.Id));
    }

    [AvaloniaFact]
    public void Flat_and_pastel_supply_every_builtin_control_theme()
    {
        var keys = new[]
        {
            "Bevel.Theme.Button", "Bevel.Theme.ToggleButton", "Bevel.Theme.CheckBox",
            "Bevel.Theme.RadioButton", "Bevel.Theme.ProgressBar", "Bevel.Theme.Slider",
            "Bevel.Theme.HeaderedContentControl", "Bevel.Theme.TabItem", "Bevel.Theme.ComboBox",
            "Bevel.Theme.ScrollBar", "Bevel.Theme.TextBox", "Bevel.Theme.Menu",
            "Bevel.Theme.MenuItem", "Bevel.Theme.TabControl",
        };
        foreach (var theme in new[] { ThemeIds.Flat, ThemeIds.Pastel })
        {
            try
            {
                Assert.True(Bevel.UI.ThemeService.Apply(theme));
                foreach (var key in keys)
                    Assert.NotNull(Resolve(key));
            }
            finally
            {
                Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
            }
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
            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
        }
    }

    [AvaloniaFact]
    public void Apply_returns_true_on_a_successful_swap_and_on_a_noop_reapply()
    {
        // The return value gates the theme-coupled colour engine in App: true means "template is on
        // this theme, safe to apply its variant" (ce-review theme-swap atomicity).
        try
        {
            Assert.True(Bevel.UI.ThemeService.Apply(ThemeIds.Pastel)); // real swap
            Assert.True(Bevel.UI.ThemeService.Apply(ThemeIds.Pastel)); // no-op re-apply is still "in place" = true
            Assert.True(Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999)); // swap back
        }
        finally
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
        }
    }

    [AvaloniaFact]
    public void Text_is_always_antialiased_even_under_win2000()
    {
        // never-disable-AA rule: text stays smooth in EVERY skin. Win2000 used to inherit the Classic base's
        // aliased default (FontAliasingKey = True → TextRenderingMode.Alias); it must now resolve False.
        try
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
            Assert.True(Application.Current!.TryGetResource(
                Classic.CommonControls.SystemParameters.FontAliasingKey, null, out var win2000));
            Assert.False((bool)win2000!);

            Bevel.UI.ThemeService.Apply(ThemeIds.Blue2001);
            Assert.True(Application.Current!.TryGetResource(
                Classic.CommonControls.SystemParameters.FontAliasingKey, null, out var luna));
            Assert.False((bool)luna!);

            Bevel.UI.ThemeService.Apply(ThemeIds.Pastel);
            Assert.True(Application.Current!.TryGetResource(
                Classic.CommonControls.SystemParameters.FontAliasingKey, null, out var pastel));
            Assert.False((bool)pastel!);
        }
        finally
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
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
