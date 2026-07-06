using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Bevel.Core;
using Bevel.Core.Vfs;
using Bevel.Desktop;
using Bevel.FileManager;
using Bevel.FileManager.FileOperations;
using Microsoft.Extensions.DependencyInjection;

namespace Bevel.App;

public partial class App : Application
{
    public static IServiceProvider? Services { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = Services
                ?? throw new InvalidOperationException("DI container not initialized before UI startup.");

            // Desktop window (behind everything, wallpaper + icon grid).
            var desktopWin = new DesktopWindow { Content = new DesktopView() };
            desktopWin.Show();

            // File manager window — resolved from DI so the SAME object graph the modules
            // register actually drives the running app: the VfsRoot has both the file AND
            // computer providers (My Computer works), plus settings and file operations
            // (Folder Options and Undo work).
            var fm = services.GetRequiredService<FileManagerWindow>();
            fm.SetVfsRoot(services.GetRequiredService<VfsRoot>());
            fm.SetSettingsService(services.GetRequiredService<SettingsService>());
            fm.SetController(services.GetRequiredService<FileManagerController>());
            desktop.MainWindow = fm;
        }

        base.OnFrameworkInitializationCompleted();
    }
}