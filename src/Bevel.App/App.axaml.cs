using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
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

    /// <summary>Captured desktop lifetime, used to drive a clean shutdown from a signal handler.</summary>
    private static IClassicDesktopStyleApplicationLifetime? _lifetime;

    /// <summary>Requests a clean Avalonia shutdown. Safe to call before the UI is up (no-op).
    /// Marshals to the UI thread because signal handlers run on a foreign thread.</summary>
    public static void RequestExit()
    {
        var lifetime = _lifetime;
        if (lifetime is null) return;
        // Signal handlers execute on a non-UI thread; Avalonia shutdown must run on the UI thread.
        Dispatcher.UIThread.Post(() => lifetime.Shutdown());
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _lifetime = desktop;
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

            // Taskbar window (bottom bar: Start, window buttons, clock). The view can't
            // resolve PAL services itself (ARCH-03), so hand them over here; the window
            // manager's reconciliation poll is what feeds the window-button list.
            var taskbarView = new Taskbar.TaskbarView();
            taskbarView.Initialize(
                services.GetService<Bevel.Pal.Abstractions.IAppEnvironment>(),
                services.GetService<Bevel.Pal.Abstractions.IWindowManager>(),
                services.GetService<Bevel.Pal.Abstractions.IIconProvider>(),
                buttonWidth: settings.Current.TaskbarButtonWidth);
            if (services.GetService<Bevel.Pal.Abstractions.IWindowManager>() is Pal.MacOS.MacOSWindowManager macWm)
                _ = macWm.StartPollAsync();
            var taskbarWin = new Taskbar.TaskbarWindow(
                services.GetService<Bevel.Pal.Abstractions.IDockController>())
            {
                Content = taskbarView,
            };
            taskbarWin.Show();

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