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

            // Load persisted settings BEFORE any window shows and apply the whitelisted theme
            // overrides (bevel-wym) — previously nothing called LoadAsync, so saved settings
            // never survived a restart. Small local-file read; blocking at startup is fine.
            var settings = services.GetRequiredService<SettingsService>();
            settings.LoadAsync().GetAwaiter().GetResult();
            UI.ThemeOptions.ApplyCrispBevels(
                this, settings.ThemeOverridesFor(settings.Current.ThemeId).CrispBevels ?? false);

            // Desktop window (behind everything, wallpaper + icon grid).
            var desktopWin = new DesktopWindow { Content = new DesktopView() };
            desktopWin.Show();

            // File manager window(s) — built via the shared factory so the SAME object graph
            // the modules register actually drives the running app: the VfsRoot has both the
            // file AND computer providers (My Computer works), plus settings and file
            // operations (Folder Options and Undo work). The factory is also how File > New
            // Window (Ctrl+N) spawns additional independent windows below.
            var factory = services.GetRequiredService<FileManagerWindowFactory>();
            var homePath = new VfsPath("file", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            var fm = factory.Create(homePath);
            desktop.MainWindow = fm;

            // File > New Window (Ctrl+N): FileManagerWindow lives in Bevel.FileManager, which
            // Bevel.App references but not vice versa, so it cannot call the factory directly.
            // It instead raises this static event with the directory the new window should
            // open at (its current directory); every window's request is served by the same
            // factory, reusing the shared VfsRoot/SettingsService with fresh per-window
            // navigation/undo state. New Tab (Ctrl+T) is out of scope (see FileManagerWindowFactory remarks).
            FileManagerWindow.NewWindowRequested += path => factory.Create(path);
        }

        base.OnFrameworkInitializationCompleted();
    }
}