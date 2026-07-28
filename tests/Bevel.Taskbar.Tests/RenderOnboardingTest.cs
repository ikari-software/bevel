using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Core;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Dev-only visual render of the Taskbar Properties dialog's Start tab, to eyeball the new Start-menu
/// "Programs to show" control. Vector controls in a BevelWindow → crisp at any DPI. Dumps a PNG.
/// </summary>
[Collection("TaskbarTheme")]   // serialize theme-mutating tests: they share Application.Current (bevel-hd05)
public class RenderOnboardingTest
{
    [AvaloniaFact]
    public void Render_start_tab_to_png()
    {
        // Root settings at a TEMP dir: loading the window fires its control handlers, which PERSIST —
        // pointing that at the real ~/.config/bevel/settings.db would clobber the user's live settings
        // (themeId, taskbar layout, …) on every test run. Renders under the default theme (no global
        // ThemeService toggle — that races with the parallel Luna tests).
        var dir = Path.Combine(Path.GetTempPath(), "bevel-onb-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new SettingsService(dir);
            var win = new OnboardingWindow(settings) { Width = 470, Height = 440 };
            win.Show();
            Dispatcher.UIThread.RunJobs();

            // Select the "Start" tab (General=0, Start=1).
            var tabs = win.GetVisualDescendants().OfType<TabControl>().First();
            tabs.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();

            var count = win.GetVisualDescendants().OfType<Slider>()
                .FirstOrDefault(s => s.Name == "FrequentCountSlider");
            Assert.NotNull(count);              // the new control exists on the Start tab
            Assert.True(count!.Value >= 1);     // seeded from the setting

            var frame = win.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var outPath = Environment.GetEnvironmentVariable("BEVEL_ONB_OUT")
                          ?? Path.Combine(Path.GetTempPath(), "bevel-onboarding-start.png");
            frame!.Save(outPath);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best-effort cleanup */ } }
    }

    [AvaloniaFact]
    public void Render_properties_luna_to_png()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bevel-onb-luna-" + Guid.NewGuid().ToString("N"));
        try
        {
            Bevel.UI.ThemeService.Apply("luna");
            var settings = new SettingsService(dir);
            var win = new OnboardingWindow(settings) { Width = 470, Height = 460 };
            win.Show();
            Dispatcher.UIThread.RunJobs();

            var frame = win.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var outPath = Environment.GetEnvironmentVariable("BEVEL_ONB_LUNA_OUT")
                          ?? Path.Combine(Path.GetTempPath(), "bevel-onboarding-luna.png");
            frame!.Save(outPath);
        }
        finally
        {
            Bevel.UI.ThemeService.Apply("win2000");
            try { Directory.Delete(dir, true); } catch { /* best-effort cleanup */ }
        }
    }
}
