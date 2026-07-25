using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;

namespace ThemeStudio;

/// <summary>
/// Standalone Application that mirrors Bevel.App's App.axaml theme contract WITHOUT booting the shell
/// surfaces: the Win2000 base theme plus the per-control <c>Theme = {DynamicResource Bevel.Theme.*}</c>
/// swap keys, so ThemeService/LunaVariantService re-template live controls in place. Built in code to
/// avoid a XAML build dependency in the tool.
/// </summary>
public sealed class App : Application
{
    // (control type, Bevel.Theme.* swap key) — the exact set from Bevel.App/App.axaml.
    private static readonly (Type Type, string Key)[] ThemeContract =
    {
        (typeof(Button), "Bevel.Theme.Button"),
        (typeof(ToggleButton), "Bevel.Theme.ToggleButton"),
        (typeof(CheckBox), "Bevel.Theme.CheckBox"),
        (typeof(RadioButton), "Bevel.Theme.RadioButton"),
        (typeof(ProgressBar), "Bevel.Theme.ProgressBar"),
        (typeof(Slider), "Bevel.Theme.Slider"),
        (typeof(HeaderedContentControl), "Bevel.Theme.HeaderedContentControl"),
        (typeof(TabItem), "Bevel.Theme.TabItem"),
        (typeof(ComboBox), "Bevel.Theme.ComboBox"),
        (typeof(ScrollBar), "Bevel.Theme.ScrollBar"),
        (typeof(TextBox), "Bevel.Theme.TextBox"),
        (typeof(Menu), "Bevel.Theme.Menu"),
        (typeof(MenuItem), "Bevel.Theme.MenuItem"),
        (typeof(TabControl), "Bevel.Theme.TabControl"),
    };

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light; // Win2000 is light-locked (matches App.axaml).

        Resources["ContentControlThemeFontFamily"] =
            new FontFamily("avares://Bevel.Themes.Win2000/Assets/#Noto Sans");

        // Base theme = Win2000 (Luna is layered on at runtime by ThemeService.Apply).
        Styles.Add(new StyleInclude(new Uri("avares://ThemeStudio/"))
        {
            Source = new Uri("avares://Bevel.Themes.Win2000/Win2000Theme.axaml"),
        });

        foreach (var (type, key) in ThemeContract)
        {
            var style = new Style(x => x.OfType(type));
            style.Setters.Add(new Setter(StyledElement.ThemeProperty, new DynamicResourceExtension(key)));
            Styles.Add(style);
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Start on Luna so the studio opens on the theme we're actively tuning.
            Bevel.UI.ThemeService.Apply("luna");
            Bevel.UI.Luna.LunaVariantService.Apply("Blue", "Gloss");

            var studio = new StudioWindow();
            desktop.MainWindow = studio;
            studio.Show();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
