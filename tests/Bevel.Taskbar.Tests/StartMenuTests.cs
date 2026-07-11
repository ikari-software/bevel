using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>Smoke tests for the StartMenu component.</summary>
public class StartMenuTests
{
    [AvaloniaFact]
    public void StartMenu_creates_without_errors()
    {
        var menu = new StartMenu();
        Assert.False(menu.IsOpen);
    }

    [AvaloniaFact]
    public async Task StartMenu_open_and_close_works()
    {
        var button = new Button();
        var window = new Window { Content = button, Width = 400, Height = 100 };
        window.Show();

        var menu = new StartMenu();
        var t = menu.OpenAsync(button);
        Dispatcher.UIThread.RunJobs();
        Assert.True(menu.IsOpen);

        menu.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(menu.IsOpen);
    }
}