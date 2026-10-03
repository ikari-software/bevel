using Avalonia;
using Avalonia.Headless.XUnit;
using Bevel.Core;
using Classic.CommonControls;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>bevel-p3va regression: a Luna→Win2000→Luna round-trip via the FULL switch path (ThemeService
/// + ThemeVariants, i.e. incl. the Win2000 ColorScheme engine) must leave Luna's caption height at 25.
/// Before the fix the Win2000 scheme's WindowCaptionHeightKey=18 lingered (never cleared on switch-away)
/// and outranked Luna's 25 at the Application level, shrinking the title bar on windows opened afterwards.</summary>
[Collection("TaskbarTheme")]   // serialize theme-mutating tests: they share Application.Current (bevel-hd05)
public class ThemeSwitchCaptionTest
{
    private static void Switch(string themeId)
    {
        var s = new BevelSettings
        {
            ThemeId = themeId,
            LunaColor = "Purple",
            LunaGloss = "Hybrid",
            ColorScheme = "StandardWindows",
        };
        Bevel.UI.ThemeService.Apply(themeId);
        Bevel.UI.ThemeVariants.Apply(s);
    }

    private static object? CaptionHeight()
        => Application.Current!.TryGetResource(SystemParameters.WindowCaptionHeightKey, null, out var v) ? v : null;

    [AvaloniaFact]
    public void Luna_caption_height_survives_a_win2000_round_trip()
    {
        try
        {
            Switch(ThemeIds.Blue2001);
            var fresh = CaptionHeight();

            Switch(ThemeIds.Industrial1999);
            Switch(ThemeIds.Blue2001);
            var afterRoundTrip = CaptionHeight();

            Assert.Equal(fresh, afterRoundTrip);   // both Luna's 25
            Assert.Equal(25d, afterRoundTrip);     // and specifically 25, not the Win2000 18
        }
        finally
        {
            Switch(ThemeIds.Industrial1999);
        }
    }
}
