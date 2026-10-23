using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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

    /// <summary>
    /// The Appearance tab at the exact size the landing page ships (site/shots/theme-win2000.png).
    /// That shot used to be a one-off hand-run with no test behind it, so it silently went stale: it was
    /// harvested while text still rendered ALIASED and stayed that way for months while every sibling shot
    /// was re-harvested antialiased. A shot the suite can reproduce is a shot that cannot drift.
    /// </summary>
    [AvaloniaFact]
    public void Render_appearance_tab_to_png()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bevel-onb-appearance-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new SettingsService(dir);
            var win = new OnboardingWindow(settings) { Width = 470, Height = 500 };
            win.Show();
            Dispatcher.UIThread.RunJobs();

            // Select by HEADER, not by index: a new tab inserted ahead of it would silently re-point a
            // magic number at the wrong page, and the shot would go wrong without anything failing.
            var tabs = win.GetVisualDescendants().OfType<TabControl>().First();
            var appearance = tabs.Items.OfType<TabItem>().First(t => (t.Header as string) == "Appearance");
            tabs.SelectedItem = appearance;
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(win.GetVisualDescendants().OfType<CheckBox>()
                .FirstOrDefault(c => c.Name == "CrispBevelsCheck"));   // we are really on Appearance

            var frame = win.CaptureRenderedFrame();
            Assert.NotNull(frame);

            // never-disable-AA, asserted on the PIXELS rather than on a RenderOptions property: the mode is
            // set by FontUtils on a template element deep in the window, so the root reads Unspecified and
            // an assert there proves nothing. What the shot needs is that glyph edges carry a ramp.
            Assert.True(RampedEdgeFraction(frame!) > 0.25,
                "text rendered without antialiasing — the harvested shot would show staircase glyphs");

            var outPath = Environment.GetEnvironmentVariable("BEVEL_ONB_APPEARANCE_OUT")
                          ?? Path.Combine(Path.GetTempPath(), "bevel-onboarding-appearance.png");
            // scale: 1 — see SiteShot.Save. This dialog's TabControl content forces an intermediate
            // composition layer, which Avalonia mis-scales above 1x (chrome at 1x, content magnified and
            // clipped). Tracked separately; the shot stays honest rather than broken.
            Bevel.TestSupport.SiteShot.Save(win, outPath, scale: 1);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best-effort cleanup */ } }
    }

    [AvaloniaFact]
    public void Render_properties_luna_to_png()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bevel-onb-luna-" + Guid.NewGuid().ToString("N"));
        try
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Blue2001);
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
            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
            try { Directory.Delete(dir, true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Fraction of high-contrast horizontal edges that carry an intermediate pixel. Aliased (bilevel) text
    /// steps straight from ink to paper and scores near zero; antialiased text scores well above a third.
    /// Written against the frame because that is the artifact the site ships.
    /// </summary>
    private static double RampedEdgeFraction(Avalonia.Media.Imaging.WriteableBitmap frame)
    {
        using var fb = frame.Lock();
        int w = fb.Size.Width, h = fb.Size.Height, hard = 0, ramped = 0;

        int Lum(int x, int y)
        {
            var p = fb.Address + y * fb.RowBytes + x * 4;
            int b0 = System.Runtime.InteropServices.Marshal.ReadByte(p, 0);
            int b1 = System.Runtime.InteropServices.Marshal.ReadByte(p, 1);
            int b2 = System.Runtime.InteropServices.Marshal.ReadByte(p, 2);
            var (r, g, b) = fb.Format == Avalonia.Platform.PixelFormat.Rgba8888 ? (b0, b1, b2) : (b2, b1, b0);
            return (r * 299 + g * 587 + b * 114) / 1000;
        }

        for (var y = 0; y < h; y++)
            for (var x = 1; x < w - 2; x++)
            {
                int a = Lum(x, y), b = Lum(x + 1, y);
                if (Math.Abs(a - b) <= 70) continue;
                int lo = Math.Min(a, b), hi = Math.Max(a, b), l = Lum(x - 1, y), r = Lum(x + 2, y);
                if ((l > lo + 15 && l < hi - 15) || (r > lo + 15 && r < hi - 15)) ramped++;
                else hard++;
            }

        return hard + ramped == 0 ? 1.0 : (double)ramped / (hard + ramped);
    }
}
