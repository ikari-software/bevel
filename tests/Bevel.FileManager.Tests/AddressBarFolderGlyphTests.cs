using Avalonia;
using Avalonia.Controls;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.FileManager.Components;
using Bevel.UI;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// The address bar's folder icon is THE shared folder glyph, hosted (bevel-i9iu) — same geometry AND
/// the same token brushes as every folder in the list beside it — and it re-resolves when a recolour
/// engine drops those brushes, which an <c>x:Static</c> binding to a cached brush would not.
/// </summary>
public sealed class AddressBarFolderGlyphTests
{
    private static Color Token(string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out var v), key);
        return Assert.IsType<Color>(v);
    }

    private static (Path back, Path front) FolderPaths(AddressBar bar)
    {
        var icon = Assert.Single(bar.GetVisualDescendants().OfType<GlyphIcon>());
        var paths = icon.GetVisualDescendants().OfType<Path>().ToList();
        Assert.Equal(2, paths.Count);
        return (paths[0], paths[1]);
    }

    [AvaloniaFact]
    public void Folder_icon_is_the_shared_glyph_drawn_from_the_theme_tokens()
    {
        var bar = new AddressBar();
        var window = new Window { Content = bar, Width = 600, Height = 60 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var (back, front) = FolderPaths(bar);

        // Geometry: the one definition in Glyphs, not a transcription.
        Assert.Same(Glyphs.FolderBackGeometry, back.Data);
        Assert.Same(Glyphs.FolderFrontGeometry, front.Data);

        // Colours: the folder tokens, not hex literals.
        var backFill = Assert.IsType<LinearGradientBrush>(back.Fill);
        Assert.Equal(Token(ThemeTokens.ColorIconFolderBackTop), backFill.GradientStops[0].Color);
        Assert.Equal(Token(ThemeTokens.ColorIconFolderBackBottom), backFill.GradientStops[1].Color);
        var frontFill = Assert.IsType<LinearGradientBrush>(front.Fill);
        Assert.Equal(Token(ThemeTokens.ColorIconFolderFrontTop), frontFill.GradientStops[0].Color);
        Assert.Equal(Token(ThemeTokens.ColorIconFolderFrontBottom), frontFill.GradientStops[1].Color);
        Assert.Equal(Token(ThemeTokens.ColorIconFolderEdge), Assert.IsType<SolidColorBrush>(back.Stroke).Color);
        Assert.Equal(Token(ThemeTokens.ColorIconFolderEdge), Assert.IsType<SolidColorBrush>(front.Stroke).Color);

        window.Close();
    }

    [AvaloniaFact]
    public void Folder_icon_re_resolves_when_a_recolour_engine_drops_the_cached_brushes()
    {
        var bar = new AddressBar();
        var window = new Window { Content = bar, Width = 600, Height = 60 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var (before, _) = FolderPaths(bar);
        var original = Assert.IsType<LinearGradientBrush>(before.Fill).GradientStops[0].Color;
        Assert.NotEqual(Colors.Red, original);

        // A recolour engine changes a token at Application level, then invalidates Glyphs' brush cache —
        // exactly what Blue2001VariantService / ColorSchemeService / ThemeService do on a switch.
        var resources = Application.Current!.Resources;
        resources[ThemeTokens.ColorIconFolderBackTop] = Colors.Red;
        try
        {
            Glyphs.InvalidateThemeCache();
            Dispatcher.UIThread.RunJobs();

            var (after, _) = FolderPaths(bar);
            Assert.NotSame(before, after);   // rebuilt, not the stale path with the evicted brush
            Assert.Equal(Colors.Red, Assert.IsType<LinearGradientBrush>(after.Fill).GradientStops[0].Color);
        }
        finally
        {
            resources.Remove(ThemeTokens.ColorIconFolderBackTop);
            Glyphs.InvalidateThemeCache();
        }

        // And back again once the override is gone — nothing latched.
        Dispatcher.UIThread.RunJobs();
        var (restored, _) = FolderPaths(bar);
        Assert.Equal(original, Assert.IsType<LinearGradientBrush>(restored.Fill).GradientStops[0].Color);
        window.Close();
    }
}
