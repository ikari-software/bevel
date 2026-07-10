using Avalonia;
using Classic.Avalonia.Theme;

namespace Bevel.UI;

/// <summary>
/// Runtime theme options (bevel-wym): applies whitelisted user overrides on top of the active
/// theme by shadowing theme resources at Application level — Application.Resources wins over
/// theme-style resources for DynamicResource lookups, so every bound control flips live.
/// </summary>
public static class ThemeOptions
{
    /// <summary>
    /// Applies the "Crisp bevels" Display-settings override (chrome spec §8): Crisp restores
    /// pixel-authentic hard edge bands; false returns to the theme's default (Smooth for
    /// Win2000) by removing the shadow so the theme resource shows through again.
    /// </summary>
    public static void ApplyCrispBevels(Application app, bool crisp)
    {
        if (crisp)
            app.Resources[ThemeTokens.EdgeRendering] = EdgeRendering.Crisp;
        else
            app.Resources.Remove(ThemeTokens.EdgeRendering);
    }
}
