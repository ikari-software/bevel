using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Bevel.Core;
using Bevel.FileManager.Components;
using Bevel.UI;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// The Filer's rasterized toolbar icons and the XP info pane's copied palette follow the ONE app-wide
/// "palette changed" signal, Glyphs.ThemeCacheInvalidated (bevel-yf7c): the toolbar used to bake its bitmaps
/// once in the constructor, and the info pane listened to a private Blue2001-only event, so neither
/// recoloured on a theme or colour-scheme switch.
/// </summary>
[Collection("TaskbarTheme")]   // overrides Application-level brush tokens and raises the global signal
public sealed class ToolbarThemeTests
{
    static Avalonia.Media.Imaging.Bitmap? Icon(Toolbar t) => ((Classic.CommonControls.ToolBarButton)t.Views).SmallIcon;

    [AvaloniaFact]
    public void Toolbar_icons_are_re_rasterized_when_the_theme_cache_is_invalidated()
    {
        var toolbar = new Toolbar();
        var window = new Window { Content = toolbar, Width = 800, Height = 60 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var before = Icon(toolbar);
            Assert.NotNull(before);

            Glyphs.InvalidateThemeCache();
            Dispatcher.UIThread.RunJobs();

            var after = Icon(toolbar);
            Assert.NotNull(after);
            Assert.NotSame(before, after);
            Assert.Equal("Back", AutomationProperties.GetName(toolbar.BackButton));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void A_detached_toolbar_stops_listening()
    {
        var toolbar = new Toolbar();
        var window = new Window { Content = toolbar, Width = 800, Height = 60 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.Content = null;   // detach
        Dispatcher.UIThread.RunJobs();
        var before = Icon(toolbar);

        Glyphs.InvalidateThemeCache();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(before, Icon(toolbar));
        window.Close();
    }

    [AvaloniaFact]
    public void Xp_info_pane_recopies_its_palette_on_the_global_signal()
    {
        var pane = new InfoPane { Style = InfoPaneStyle.Tasks2001, Width = 200, Height = 380 };
        var window = new Window { Content = pane, Width = 200, Height = 380 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var resources = Application.Current!.Resources;
        const string key = "Blue2001.Brush.InfoPaneHeader";
        bool had = resources.TryGetValue(key, out var old);
        var red = new SolidColorBrush(Colors.Red);
        resources[key] = red;
        try
        {
            Glyphs.InvalidateThemeCache();   // what ThemeService / ColorSchemeService / variant services raise
            Dispatcher.UIThread.RunJobs();
            Assert.Same(red, pane.Resources["InfoPane.Xp.Header"]);
        }
        finally
        {
            if (had) resources[key] = old; else resources.Remove(key);
            Glyphs.InvalidateThemeCache();
            Dispatcher.UIThread.RunJobs();
            window.Close();
        }
    }
}
