using Bevel.Core;
using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Dev-only visual render of the XP Luna two-column Start menu (bevel-dob) to a PNG, so its fidelity
/// against docs/design/luna/styleguide.html can be eyeballed without live popup/window-level hosting.
/// Applies the Luna theme, drives the theme-based layout selection, detaches the popup content, and
/// captures it. Not a pixel assertion — it proves the Luna layout renders and dumps an image.
/// </summary>
[Collection("TaskbarTheme")]   // serialize theme-mutating tests: they share Application.Current (bevel-hd05)
public class RenderBlue2001StartMenuTest
{
    [AvaloniaFact]
    public async Task Render_luna_start_menu_to_png()
    {
        try
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Blue2001);

            // Real apps with their real icons, from the committed fixture. This used to be four stub
            // entries and a NULL icon provider, so every program drew the shell's "unknown app" plate —
            // four identical grey squares in the landing page's hero, under a line promising the frame
            // was captured straight from Bevel. See HeroApps.
            var appEnv = new HeroAppEnvironment();
            using var model = new ShellModel(null, appEnv, new HeroIconProvider(), usage: TestUsage.Scratch());
            model.Start();
            var vm = new StartMenuViewModel(model);
            for (var i = 0; i < 50 && model.Programs.Count < 4; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }

            var menu = new StartMenu(null, null, programs: vm);

            // A button hosted in a window gives OpenAsync a valid placement target; opening runs the
            // theme-based layout selection (Luna visible, pinned column bound) before we detach.
            var button = new Button { Content = "Start" };
            var host = new Window { SystemDecorations = SystemDecorations.None, Content = button };
            host.Show();
            Dispatcher.UIThread.RunJobs();
            await menu.OpenAsync(button);
            Dispatcher.UIThread.RunJobs();
            menu.Close();

            var content = (Control)menu.MenuPopupControl.Child!;
            menu.MenuPopupControl.Child = null;

            var window = new Window
            {
                SystemDecorations = SystemDecorations.None,
                SizeToContent = SizeToContent.WidthAndHeight,
                Content = content,
            };
            window.Show();
            // Settle before capturing: async icon loads + the theme-based two-column layout selection can
            // leave the first frame narrow under load. The previous settle polled window.Bounds.Width but
            // the assertion is on the CAPTURED FRAME's width — two different things, so the loop could
            // finish happy while the frame was still narrow. It also had only a 60x10ms budget, which a
            // full-solution run (assemblies in parallel) blows straight through. Poll the frame itself,
            // on a time-based deadline generous enough not to flake but still fast when it is ready.
            Avalonia.Media.Imaging.WriteableBitmap? frame = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                frame = window.CaptureRenderedFrame();
                // Width alone is not settled: icons load ASYNCHRONOUSLY, so a frame can be full width
                // with the program rows still drawing the "unknown app" plate. Capturing there made the
                // hero's bytes depend on how fast the decode happened to be, and the drift check caught
                // it. Wait for every program to actually have its icon.
                var iconsIn = model.Programs.Count > 0 && model.Programs.All(p => p.IconSource is not null);
                if (frame is not null && frame.PixelSize.Width >= 340 && iconsIn) break;
                await Task.Delay(10);
            }

            Assert.NotNull(frame);
            Assert.True(frame!.PixelSize.Width >= 340, $"luna menu too narrow: {frame.PixelSize}");

            var outPath = Environment.GetEnvironmentVariable("BEVEL_LUNA_START_OUT")
                          ?? Path.Combine(Path.GetTempPath(), "bevel-luna-startmenu.png");
            frame.Save(outPath);

            // Landing-page hero part. Dumped from HERE rather than rebuilt in the hero test, so the
            // hero inherits this test's settling loop and width assertion instead of re-deriving them.
            if (Environment.GetEnvironmentVariable("BEVEL_HERO_PARTS") is { } heroParts)
                Bevel.TestSupport.SiteShot.Save(window, Path.Combine(heroParts, "start.png"));
        }
        finally
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
        }
    }

    [AvaloniaFact]
    public async Task All_programs_flyout_is_populated_from_the_programs()
    {
        // Regression: the inline XAML flyout came up empty (popup DataContext gap). The code-built
        // MenuFlyout must carry one MenuItem per program, eagerly (not only on Opening).
        try
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Blue2001);
            var appEnv = new StubAppEnvironment(
                new InstalledApp("com.a", "Alpha", null),
                new InstalledApp("com.b", "Beta", null));
            using var model = new ShellModel(null, appEnv, null, usage: TestUsage.Scratch());
            model.Start();
            var vm = new StartMenuViewModel(model);
            for (var i = 0; i < 50 && model.Programs.Count < 2; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }

            var menu = new StartMenu(null, null, programs: vm);
            var button = new Button();
            var host = new Window { SystemDecorations = SystemDecorations.None, Content = button };
            host.Show();
            Dispatcher.UIThread.RunJobs();
            await menu.OpenAsync(button);   // runs ApplyThemeLayout → wires + populates the flyout
            Dispatcher.UIThread.RunJobs();

            var allProg = menu.FindControl<Button>("Blue2001AllProgramsButton");
            Assert.NotNull(allProg);
            var flyout = allProg!.Flyout as Flyout;
            Assert.NotNull(flyout);
            var frame = flyout!.Content as Border;
            Assert.NotNull(frame);
            var scroll = frame!.Child as ScrollViewer;
            Assert.NotNull(scroll);
            var list = scroll!.Content as StackPanel;
            Assert.NotNull(list);
            Assert.Equal(2, list!.Children.Count);   // one row per program
        }
        finally
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999);
        }
    }
}
