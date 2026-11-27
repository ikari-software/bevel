using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Bevel.Core;
using Bevel.UI;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Every headless test runs on a FRESH Application, but ThemeService keeps process statics. If one test
/// leaves Blue2001 applied (a missing finally, an exception before it), the statics still say "blue2001"
/// while the next test's Application carries no theme Styles at all. Then Apply(Blue2001) used to return
/// as a no-op and Current reported a theme nobody rendered: the next Luna render came out in the Win2000
/// skin, and the Start menu picked its two-column Luna layout under Win2000 chrome (bevel-i145).
///
/// The tests simulate "a fresh Application" by taking the theme's Styles back out of the current one, which
/// is the same state from ThemeService's point of view: the statics say applied, the Application says not.
/// </summary>
[Collection("TaskbarTheme")]
public class ThemeStaticsFreshAppTests
{
    private const string Blue2001Styles = "avares://Bevel.Themes.Blue2001/Blue2001Theme.axaml";

    private static StyleInclude? ThemeStylesIn(Application app) =>
        app.Styles.OfType<StyleInclude>().FirstOrDefault(s => s.Source?.ToString() == Blue2001Styles);

    [AvaloniaFact]
    public void Apply_reapplies_when_the_application_lost_the_theme()
    {
        var app = Application.Current!;
        try
        {
            Assert.True(ThemeService.Apply(ThemeIds.Blue2001));
            var styles = ThemeStylesIn(app);
            Assert.NotNull(styles);

            app.Styles.Remove(styles!);                    // statics: applied; Application: not
            Assert.True(ThemeService.Apply(ThemeIds.Blue2001));

            Assert.NotNull(ThemeStylesIn(app));
            Assert.Equal(ThemeIds.Blue2001, ThemeService.Current);
        }
        finally
        {
            ThemeService.Apply(ThemeIds.Industrial1999);
        }
    }

    [AvaloniaFact]
    public void Current_reports_the_default_when_the_application_does_not_carry_the_theme()
    {
        var app = Application.Current!;
        try
        {
            ThemeService.Apply(ThemeIds.Blue2001);
            app.Styles.Remove(ThemeStylesIn(app)!);

            // Surfaces (the Start menu's layout choice, the Filer info pane) read Current to pick what to
            // build. It must describe what this Application renders, not what a previous one did.
            Assert.Equal(ThemeService.DefaultTheme, ThemeService.Current);
        }
        finally
        {
            ThemeService.Apply(ThemeIds.Industrial1999);
        }
    }

    [AvaloniaFact]
    public void Switching_back_to_the_default_still_removes_the_theme_styles()
    {
        var app = Application.Current!;
        ThemeService.Apply(ThemeIds.Blue2001);
        Assert.True(ThemeService.Apply(ThemeIds.Industrial1999));
        Assert.Null(ThemeStylesIn(app));
        Assert.Equal(ThemeIds.Industrial1999, ThemeService.Current);
    }
}
