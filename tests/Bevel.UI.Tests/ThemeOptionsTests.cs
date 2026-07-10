using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.UI;
using Classic.Avalonia.Theme;
using Xunit;

namespace Bevel.UI.Tests;

/// <summary>
/// The "Crisp bevels" runtime override (bevel-wym): ThemeOptions shadows the theme's
/// Bevel.Edge.Rendering resource at Application level, and every decorator bound through the
/// Win2000Theme style flips live — on AND back off (removing the shadow restores the theme's
/// Smooth default through the same DynamicResource).
/// </summary>
public sealed class ThemeOptionsTests
{
    [AvaloniaFact]
    public void Crisp_override_flips_live_decorators_and_reverting_restores_the_theme_default()
    {
        var decorator = new ClassicBorderDecorator();
        var bevelBorder = new BevelBorder();
        var window = new Window { Content = new StackPanel { Children = { decorator, bevelBorder } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            // Theme default (theme.json): Smooth, delivered via the style's DynamicResource.
            Assert.Equal(EdgeRendering.Smooth, decorator.EdgeRendering);
            Assert.Equal(EdgeRendering.Smooth, bevelBorder.EdgeRendering);

            ThemeOptions.ApplyCrispBevels(Application.Current!, crisp: true);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(EdgeRendering.Crisp, decorator.EdgeRendering);
            Assert.Equal(EdgeRendering.Crisp, bevelBorder.EdgeRendering);

            ThemeOptions.ApplyCrispBevels(Application.Current!, crisp: false);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(EdgeRendering.Smooth, decorator.EdgeRendering);
            Assert.Equal(EdgeRendering.Smooth, bevelBorder.EdgeRendering);
        }
        finally
        {
            // Never leak the app-level shadow into other tests sharing this headless app.
            ThemeOptions.ApplyCrispBevels(Application.Current!, crisp: false);
            window.Close();
        }
    }
}
