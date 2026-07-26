using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
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
        Bevel.UI.ThemeService.Apply("win2000");

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
    }
}
