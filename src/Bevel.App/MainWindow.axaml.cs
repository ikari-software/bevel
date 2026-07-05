using Bevel.Desktop;
using Bevel.FileManager;
using Bevel.Pal.Abstractions;
using Bevel.Taskbar;
using Bevel.UI;
using Microsoft.Extensions.DependencyInjection;

namespace Bevel.App;

public partial class MainWindow : BevelWindow
{
    public MainWindow()
    {
        InitializeComponent();

        // Drop the three feature-module placeholders into their slots. Resolve from
        // the container when it exists; fall back to direct construction so the window
        // still works under headless tests that don't build a host.
        var services = App.Services;
        DesktopSlot.Content = services?.GetService<DesktopView>() ?? new DesktopView();
        TaskbarSlot.Content = services?.GetService<TaskbarView>() ?? new TaskbarView();
        FileManagerSlot.Content = services?.GetService<FileManagerView>() ?? new FileManagerView();

        var pal = services?.GetService<IShellSession>();
        PalStatus.Text = pal is null
            ? "PAL: none (no host)"
            : $"PAL wired · shell available={pal.Capabilities.Available}";
    }
}
