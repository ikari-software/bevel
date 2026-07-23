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
public class RenderLunaStartMenuTest
{
    [AvaloniaFact]
    public async Task Render_luna_start_menu_to_png()
    {
        try
        {
            Bevel.UI.ThemeService.Apply("luna");

            var appEnv = new StubAppEnvironment(
                new InstalledApp("com.files", "Bevel Files", null),
                new InstalledApp("com.term", "Terminal", null),
                new InstalledApp("com.paint", "Paint", null),
                new InstalledApp("com.web", "Web", null));
            using var model = new ShellModel(null, appEnv, null);
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
            Dispatcher.UIThread.RunJobs();

            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.True(frame!.PixelSize.Width >= 340, $"luna menu too narrow: {frame.PixelSize}");

            var outPath = Environment.GetEnvironmentVariable("BEVEL_LUNA_START_OUT")
                          ?? Path.Combine(Path.GetTempPath(), "bevel-luna-startmenu.png");
            frame.Save(outPath);
        }
        finally
        {
            Bevel.UI.ThemeService.Apply("win2000");
        }
    }

    [AvaloniaFact]
    public async Task All_programs_flyout_is_populated_from_the_programs()
    {
        // Regression: the inline XAML flyout came up empty (popup DataContext gap). The code-built
        // MenuFlyout must carry one MenuItem per program, eagerly (not only on Opening).
        try
        {
            Bevel.UI.ThemeService.Apply("luna");
            var appEnv = new StubAppEnvironment(
                new InstalledApp("com.a", "Alpha", null),
                new InstalledApp("com.b", "Beta", null));
            using var model = new ShellModel(null, appEnv, null);
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

            var allProg = menu.FindControl<Button>("LunaAllProgramsButton");
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
            Bevel.UI.ThemeService.Apply("win2000");
        }
    }
}
