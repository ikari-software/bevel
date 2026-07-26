using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using System.Linq;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>Dev-only: renders the FileManager MenuBar under the real app styles so the "menu bar renders
/// vertically with right-opening submenus" bug can be eyeballed and a fix verified. Output via
/// BEVEL_FILEMENU_OUT.</summary>
public class RenderFileMenuBarTest
{
    [AvaloniaFact]
    public async Task Render_file_menubar_to_png()
    {
        // Render under Luna + the Purple variant — the exact condition where the bar showed submenu arrows
        // and (after the top-level fix) literal access-key underscores.
        Bevel.UI.ThemeService.Apply("luna");
        Bevel.UI.Luna.LunaVariantService.Apply("Purple", "Hybrid");

        var menuBar = new Bevel.FileManager.Components.MenuBar();
        var window = new Window
        {
            SystemDecorations = SystemDecorations.None,
            Width = 560,
            Height = 260,
            Content = new DockPanel { Children = { menuBar } },
        };
        DockPanel.SetDock(menuBar, Dock.Top);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(30);
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        var outPath = Environment.GetEnvironmentVariable("BEVEL_FILEMENU_OUT")
                      ?? Path.Combine(Path.GetTempPath(), "bevel-filemenu.png");
        frame!.Save(outPath);

        // Don't leak the Luna/Purple state into other tests.
        Bevel.UI.Luna.LunaVariantService.Clear();
        Bevel.UI.ThemeService.Apply("win2000");
    }

    [AvaloniaFact]
    public async Task Reopening_a_menubar_submenu_does_not_crash_under_luna()
    {
        try
        {
            Bevel.UI.ThemeService.Apply("luna");
            Bevel.UI.Luna.LunaVariantService.Apply("Purple", "Hybrid");

            var menuBar = new Bevel.FileManager.Components.MenuBar();
            var window = new Window
            {
                SystemDecorations = SystemDecorations.None,
                Width = 560,
                Height = 300,
                Content = new DockPanel { Children = { menuBar } },
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var menu = menuBar.GetVisualDescendants().OfType<Menu>().First();
            var tools = menu.Items.OfType<MenuItem>().First(m => (m.Header as string) == "_Tools");

            // open → close → reopen: the second open double-parented the XAML-declared submenu items.
            tools.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            tools.IsSubMenuOpen = false;
            Dispatcher.UIThread.RunJobs();
            tools.IsSubMenuOpen = true;   // <-- threw InvalidOperationException before the fix
            Dispatcher.UIThread.RunJobs();

            Assert.True(tools.IsSubMenuOpen);
        }
        finally
        {
            Bevel.UI.Luna.LunaVariantService.Clear();
            Bevel.UI.ThemeService.Apply("win2000");
        }
    }

    [AvaloniaFact]
    public async Task Render_luna_start_menu_under_purple_variant()
    {
        try
        {
            Bevel.UI.ThemeService.Apply("luna");
            Bevel.UI.Luna.LunaVariantService.Apply("Purple", "Hybrid");

            var appEnv = new StubAppEnvironment(
                new InstalledApp("com.files", "Bevel Files", null, "File manager"),
                new InstalledApp("com.term", "Terminal", null, "Developer Tools"),
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
            var outPath = Environment.GetEnvironmentVariable("BEVEL_LUNA_PURPLE_OUT")
                          ?? Path.Combine(Path.GetTempPath(), "bevel-luna-purple.png");
            frame!.Save(outPath);
        }
        finally
        {
            Bevel.UI.Luna.LunaVariantService.Clear();
            Bevel.UI.ThemeService.Apply("win2000");
        }
    }
}
