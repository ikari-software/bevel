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
    private static int _restartRequested;

    public static bool RestartRequested => Volatile.Read(ref _restartRequested) != 0;

    /// <summary>Requests a clean Avalonia shutdown. Safe to call before the UI is up (no-op).
    /// Marshals to the UI thread because signal handlers run on a foreign thread.</summary>
    public static void RequestExit()
    {
        var lifetime = _lifetime;
        if (lifetime is null) return;
        // Signal handlers execute on a non-UI thread; Avalonia shutdown must run on the UI thread.
        Dispatcher.UIThread.Post(() => lifetime.Shutdown());
    }

    /// <summary>
    /// Requests a clean shutdown followed by relaunch. Program performs the relaunch only after
    /// the host has stopped, so the old helper and shell windows cannot overlap the new version.
    /// </summary>
    public static void RequestRestart()
    {
        Interlocked.Exchange(ref _restartRequested, 1);
        RequestExit();
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _lifetime = desktop;
            var services = Services
                ?? throw new InvalidOperationException("DI container not initialized before UI startup.");

            // Settings were loaded in Program.Main BEFORE Avalonia took over this thread as the
            // UI thread, so nothing blocks the dispatcher here (core rule: never block the UI
            // thread). Apply the whitelisted theme overrides (bevel-wym) from the loaded snapshot.
            var settings = services.GetRequiredService<SettingsService>();
            UI.ThemeOptions.ApplyCrispBevels(
                this, settings.ThemeOverridesFor(settings.Current.ThemeId).CrispBevels ?? false);

            // Desktop window (behind everything, wallpaper + icon grid).
            var desktopWin = new DesktopWindow { Content = new DesktopView() };
            desktopWin.Show();

            // Taskbar window (bottom bar: Start, window buttons, clock). The view can't
            // resolve PAL services itself (ARCH-03), so hand them over here; the window
            // manager's reconciliation poll is what feeds the window-button list.
            // The window-button strip binds to the background ShellModel via TaskbarViewModel
            // (bevel-d2z) — set as DataContext before Show() so the view picks it up on load.
            // Initialize still hands the Start menu its app-env + icon provider (and the max
            // button width); the window list no longer flows through the view.
            var taskbarView = new Taskbar.TaskbarView
            {
                DataContext = services.GetRequiredService<Taskbar.TaskbarViewModel>(),
            };
            taskbarView.Initialize(
                services.GetService<Bevel.Pal.Abstractions.IAppEnvironment>(),
                services.GetService<Bevel.Pal.Abstractions.IIconProvider>(),
                buttonWidth: settings.Current.TaskbarButtonWidth,
                quit: RequestExit,
                restart: RequestRestart);
            // Start the background shell model (subscribes to window events + enumerates installed
            // apps off-thread) BEFORE the window manager's stream/poll, so its initial snapshot is
            // captured; then start the poll so events flow into the model.
            var shellModel = services.GetRequiredService<Taskbar.ShellModel>();
            shellModel.Start();
            // Stop the reconcile loop + PAL event subscriptions at exit, before the DI container is
            // torn down — otherwise they keep posting to the dispatcher into the shutdown window
            // (the class this project already hit as the bevel-fu5 SIGTERM shutdown race).
            desktop.Exit += (_, _) => shellModel.Dispose();
            if (services.GetService<Bevel.Pal.Abstractions.IWindowManager>() is Pal.MacOS.MacOSWindowManager macWm)
                _ = macWm.StartPollAsync();
            var taskbarWin = new Taskbar.TaskbarWindow(
                services.GetService<Bevel.Pal.Abstractions.IDockController>(),
                settings.Current.TaskbarRows)
            {
                Content = taskbarView,
            };
            // Persist the row count when the user drags the bar taller/shorter (bevel-0ml).
            taskbarWin.RowsChanged += rows => _ = settings.UpdateAsync(s => s.TaskbarRows = rows);
            taskbarWin.Show();

            // Work-area overlap mitigation (bevel-m2.13): in the default Nudge strategy,
            // shrink windows whose bottom edge crosses the taskbar band so they sit above it.
            // The band comes from the taskbar window in the window manager's coordinate space
            // (top-left global points). Feature-detection, per-window rate-limiting, and
            // drag-suspension all live inside the engine; the poll is dormant on PALs that
            // can't reposition. Disposed on app exit so the loop stops cleanly.
            if (services.GetService<Bevel.Pal.Abstractions.IWindowManager>() is { } mitigationWm)
            {
                var mitigator = new Taskbar.WorkAreaMitigator(
                    mitigationWm, settings, taskbarWin.GetWorkAreaBand);
                mitigator.Start();
                // A resolution/arrangement change re-anchors the bar and refreshes the band but
                // moves no window, so kick an immediate re-nudge instead of waiting on the poll.
                taskbarWin.WorkAreaChanged += mitigator.RequestMitigation;
                desktop.Exit += (_, _) => mitigator.Dispose();
            }

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