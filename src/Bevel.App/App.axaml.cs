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

    /// <summary>Which surface(s) this process hosts. Set in <see cref="Program"/> before the lifetime
    /// starts; <see cref="ShellRole.All"/> (the default) is the classic single-process shell.</summary>
    public static ShellRole Role { get; set; } = ShellRole.All;

    /// <summary>Captured desktop lifetime, used to drive a clean shutdown from a signal handler.</summary>
    private static IClassicDesktopStyleApplicationLifetime? _lifetime;
    private static int _restartRequested;

    public static bool RestartRequested => Volatile.Read(ref _restartRequested) != 0;

    /// <summary>Requests a clean Avalonia shutdown. Safe to call before the UI is up (no-op).
    /// Marshals to the UI thread because signal handlers run on a foreign thread.</summary>
    public static void RequestExit()
    {
        // Under a launcher (split shell), quitting means quitting the WHOLE shell: hand it to the
        // launcher, which tears every process down. Standalone (all-in-one) falls through to this
        // process's own Avalonia shutdown.
        if (Supervision.LauncherControl.TrySend(Supervision.LauncherControl.Command.Quit))
            return;

        ShutdownLocal();
    }

    /// <summary>
    /// Shuts down THIS process's Avalonia lifetime directly, without fanning out to the launcher. This
    /// is what a supervised child does on SIGTERM: the launcher already decided to stop it, so it must
    /// tear its own windows down cleanly (restoring the Dock, stopping the helper) rather than telling
    /// the launcher to quit again. Safe before the UI is up (no-op).
    /// </summary>
    public static void ShutdownLocal()
    {
        var lifetime = _lifetime;
        if (lifetime is null) return;
        // Signal handlers execute on a non-UI thread; Avalonia shutdown must run on the UI thread.
        Dispatcher.UIThread.Post(() => lifetime.Shutdown());
    }

    /// <summary>
    /// Requests a clean shutdown followed by relaunch. Under a launcher this fans out to EVERY process
    /// (each re-execs the current binary, so the whole shell comes back on the latest build). Standalone,
    /// Program performs the in-place relaunch after the host stops, so the old helper and shell windows
    /// cannot overlap the new version.
    /// </summary>
    public static void RequestRestart()
    {
        if (Supervision.LauncherControl.TrySend(Supervision.LauncherControl.Command.RestartAll))
            return;

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
            // All roles render themed UI, so this is common to every surface.
            var settings = services.GetRequiredService<SettingsService>();
            // Theme token bundle first (PKG-03) — the baseline the user overrides layer on top of.
            UI.ThemeService.Apply(settings.Current.ThemeId);
            UI.ThemeOptions.ApplyCrispBevels(
                this, settings.ThemeOverridesFor(settings.Current.ThemeId).CrispBevels ?? false);
            // The active theme's appearance variant (W2K-01 colour scheme / Luna colour+gloss). The
            // theme owns its options via ThemeVariants, which routes to the right engine and clears the
            // others so shared chrome keys don't bleed across themes.
            UI.ThemeVariants.Apply(settings.Current);
            // UI font override (FNT-01) — top-level, so it wins over the theme's default face.
            UI.FontService.Apply(settings.Current.UiFontFamily);

            // Cross-process live re-theming (bevel-dob): the Settings window persists ThemeId and bumps
            // the settings-DB version in ITS process; every OTHER role polls the shared DB for that
            // external write and re-applies theme/scheme/font live — so a theme switch reskins every
            // shell surface (taskbar, desktop, …), not just the process that owns Settings.
            // Folder Options is an app-wide flag read by every ItemViewModel; seed it before the first
            // explorer window lists a directory so a persisted "hide extensions" is honoured on first paint.
            Bevel.FileManager.Components.ItemViewModel.HideKnownExtensions = settings.Current.HideKnownExtensions;
            settings.Changed += () =>
            {
                var s = settings.Current;
                UI.ThemeService.Apply(s.ThemeId);
                UI.ThemeVariants.Apply(s);
                UI.FontService.Apply(s.UiFontFamily);
                UI.ThemeOptions.ApplyCrispBevels(this, settings.ThemeOverridesFor(s.ThemeId).CrispBevels ?? false);

                // Folder Options → apply live to every open file-manager window: update the app-wide
                // extension-hiding flag and re-list each window (also re-runs the hidden-file filter).
                // Null in non-FM roles (taskbar/desktop), where the fan-out is a no-op.
                Bevel.FileManager.Components.ItemViewModel.HideKnownExtensions = s.HideKnownExtensions;
                if (services.GetService<FileManagerWindowRegistry>() is { } fmReg)
                    foreach (var w in fmReg.All())
                        w.ApplyFolderOptions();   // info-pane style + column + re-list
            };
            var reloadTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
            var reloadInFlight = false;
            reloadTimer.Tick += async (_, _) =>
            {
                // A DB read that overruns the 750ms interval must not overlap the next tick — two
                // ReloadIfChangedAsync calls would race on the service's _version/_raw. Tick runs on the
                // UI thread, so this plain-bool gate is single-threaded and race-free. (The read itself is
                // cheap now: ReloadIfChangedAsync probes only the version int on the no-change tick — bevel-6nve.)
                if (reloadInFlight) return;
                reloadInFlight = true;
                try { await settings.ReloadIfChangedAsync(); }   // raises Changed on an external write
                catch { /* transient DB contention; the next tick retries */ }
                finally { reloadInFlight = false; }
            };
            reloadTimer.Start();
            // Stop the poll before the container tears the SettingsService (+ its SQLite connection)
            // down — same shutdown discipline as shellModel/mitigator below (bevel-fu5).
            desktop.Exit += (_, _) => reloadTimer.Stop();

            // Create only this process's surface(s). In the default all-in-one role every block
            // runs (unchanged single-process shell); a split launch (--role=…) runs exactly one.
            // Creation order for All matches the pre-split app: desktop behind, then taskbar, then
            // the file manager, which is set last as MainWindow. Each single role sets MainWindow to
            // its own window. Only the taskbar role resolves the window manager / app environment, so
            // in the other roles those singletons (and the helper) are never constructed.
            var role = Role;

            // Split-mode chrome (the taskbar and desktop processes) is the environment, not an app:
            // it must have no Dock tile and stay out of Cmd-Tab. Marking the process .accessory also
            // fixes bevel-nji — the Swift helper enumerates only .regular apps' windows, so the
            // chrome's own transient popups (tooltips, menus) stop leaking into the taskbar's
            // foreign-window list (where they registered as windows, shifted the bar, and dismissed
            // themselves). NOT applied to All: that single process also hosts the file-manager window,
            // which SHOULD appear in the taskbar, and activation policy can't distinguish it from a
            // tooltip in the same process.
            if (OperatingSystem.IsMacOS() && role is ShellRole.Taskbar or ShellRole.Desktop)
                Pal.MacOS.ShellActivation.HideFromDock();

            if (role is ShellRole.All or ShellRole.Desktop)
                CreateDesktopSurface(desktop);
            if (role is ShellRole.All or ShellRole.Taskbar)
                CreateTaskbarSurface(services, settings, desktop);
            if (role is ShellRole.All or ShellRole.Explorer)
                CreateExplorerSurface(services, desktop);

            // bevel:// URL handler (M4-D.2 / bevel-6dc) + inbound Apple Events (M4-C / bevel-376):
            // only in the FM-hosting roles, where the router's window verbs resolve to a live surface.
            if (role is ShellRole.All or ShellRole.Explorer)
            {
                UrlActivation.Wire(this, services);
                AppleEventBridge.Wire(services);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Desktop surface: wallpaper + icon grid, behind everything. Self-contained (no PAL
    /// services resolved here — the window does its own in-process AppKit work).</summary>
    private static void CreateDesktopSurface(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var desktopWin = new DesktopWindow { Content = new DesktopView() };
        desktopWin.Show();
        desktop.MainWindow = desktopWin;
    }

    /// <summary>Taskbar right-click → "Lock the Taskbar": flips the setting and pushes it onto the live
    /// bar (bevel-cust.ctxmenu). async void, so the settings I/O is guarded — a SaveAsync failure must
    /// not crash the shell from a context-menu click (review: reliability).</summary>
    private static async void ToggleTaskbarLock(SettingsService settings, Taskbar.TaskbarView taskbarView)
    {
        try
        {
            await settings.UpdateAsync(s => s.TaskbarLocked = !s.TaskbarLocked);
            taskbarView.ApplyLiveSettings(settings.Current);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[app] ToggleTaskbarLock failed (swallowed): {ex.Message}");
        }
    }

    /// <summary>Opens the Taskbar Properties dialog (Start ▸ Settings ▸ Taskbar and Start Menu…),
    /// wiring it to apply clock changes to THIS live taskbar instantly — the dialog and the clock live
    /// in the same process, so no cross-process settings broadcast is needed. A fresh transient window
    /// each time is fine: it's a modeless properties sheet.</summary>
    private static void OpenTaskbarSettings(IServiceProvider services, Taskbar.TaskbarView taskbarView)
    {
        var win = services.GetRequiredService<Taskbar.OnboardingWindow>();
        // ShellModel is a singleton, so this is the same instance driving the live Start menu. The
        // frequent-count cap lives on it, and settings.Changed only fires on EXTERNAL writes — so route
        // the dialog's live-apply through here too, otherwise a local frequent-count change wouldn't
        // re-cap the Start menu until a restart.
        var shellModel = services.GetRequiredService<Taskbar.ShellModel>();
        win.ApplyLive = s =>
        {
            taskbarView.ApplyLiveSettings(s);
            shellModel.FrequentCap = s.TaskbarStartMenuFrequentCount;
        };
        win.Show();
        win.Activate();
    }

    /// <summary>Taskbar surface: the bottom bar plus the machinery that drives window management
    /// (the ShellModel, the window-manager poll, and the work-area mitigator). This is the only
    /// role that resolves <c>IWindowManager</c>/<c>IAppEnvironment</c>, so only here does the helper
    /// spin up.</summary>
    private static void CreateTaskbarSurface(
        IServiceProvider services, SettingsService settings, IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Apply the button-height tier before any TaskbarWindow/HeightForRows geometry is computed
        // (bevel-m2.10.1) — it's a startup-wide metric read by the window and the work-area band.
        Taskbar.TaskbarTheme.Configure(settings.Current.TaskbarButtonSize);

        // The view can't resolve PAL services itself (ARCH-03), so hand them over here; the window
        // manager's reconciliation poll is what feeds the window-button list. The window-button strip
        // binds to the background ShellModel via TaskbarViewModel (bevel-d2z) — set as DataContext
        // before Show() so the view picks it up on load. Initialize still hands the Start menu its
        // app-env + icon provider (and the max button width); the window list no longer flows through it.
        var taskbarView = new Taskbar.TaskbarView
        {
            DataContext = services.GetRequiredService<Taskbar.TaskbarViewModel>(),
        };
        taskbarView.Initialize(
            services.GetService<Bevel.Pal.Abstractions.IAppEnvironment>(),
            services.GetService<Bevel.Pal.Abstractions.IIconProvider>(),
            buttonWidth: settings.Current.TaskbarButtonWidth,
            quit: RequestExit,
            restart: RequestRestart,
            widthMode: settings.Current.TaskbarButtonWidthMode,
            minButtonWidth: settings.Current.TaskbarMinButtonWidth,
            buttonSize: settings.Current.TaskbarButtonSize,
            grouping: settings.Current.TaskbarGrouping,
            buttonLabels: settings.Current.TaskbarButtonLabels,
            middleClickCloses: settings.Current.TaskbarMiddleClickCloses,
            sort: settings.Current.TaskbarWindowSort,
            windowlessLast: settings.Current.WindowlessAppsLast,
            showStart: settings.Current.TaskbarShowStart,
            startLabel: settings.Current.TaskbarStartLabel,
            openSettings: () => OpenTaskbarSettings(services, taskbarView),
            toggleLock: () => ToggleTaskbarLock(settings, taskbarView),
            showClock: settings.Current.TaskbarShowClock,
            clock24Hour: settings.Current.TaskbarClock24Hour,
            clockShowSeconds: settings.Current.TaskbarClockShowSeconds,
            clockShowDate: settings.Current.TaskbarClockShowDate,
            fontSize: settings.Current.TaskbarFontSize,
            backgroundColor: settings.Current.TaskbarBackgroundColor,
            opacity: settings.Current.TaskbarOpacity,
            trayOverflowCap: settings.Current.TaskbarTrayOverflowCap,
            trayIconSize: settings.Current.TaskbarTrayIconSize,
            locked: settings.Current.TaskbarLocked,
            alwaysOnTop: settings.Current.TaskbarAlwaysOnTop,
            showDesktopButton: settings.Current.TaskbarShowDesktopButton,
            // Start-menu "places" (My Documents/Pictures/Music/Computer) open a Bevel Explorer window
            // at that folder — in-process in the single-process shell, or as its own --role=explorer
            // process in a split launch (the taskbar process has no explorer surface).
            openFolder: path => OpenExplorerAt(services, path));
        // Start the background shell model (subscribes to window events + enumerates installed
        // apps off-thread) BEFORE the window manager's stream/poll, so its initial snapshot is
        // captured; then start the poll so events flow into the model.
        var shellModel = services.GetRequiredService<Taskbar.ShellModel>();
        shellModel.FrequentCap = settings.Current.TaskbarStartMenuFrequentCount;
        settings.Changed += () => shellModel.FrequentCap = settings.Current.TaskbarStartMenuFrequentCount;
        shellModel.Start();
        // Stop the reconcile loop + PAL event subscriptions at exit, before the DI container is
        // torn down — otherwise they keep posting to the dispatcher into the shutdown window
        // (the class this project already hit as the bevel-fu5 SIGTERM shutdown race).
        desktop.Exit += (_, _) => shellModel.Dispose();
        if (services.GetService<Bevel.Pal.Abstractions.IWindowManager>() is Pal.MacOS.MacOSWindowManager macWm)
            _ = macWm.StartPollAsync();
        // Start the tray Changes stream where the tray is helper-backed (all-in-one / core), so the
        // mirrored menu-bar items flow to the taskbar's tray (bevel-m3.1).
        if (services.GetService<Bevel.Pal.Abstractions.ISystemTrayHost>() is Pal.MacOS.MacOSSystemTrayHost macTray)
            _ = macTray.StartPollAsync();
        var taskbarWin = new Taskbar.TaskbarWindow(
            services.GetService<Bevel.Pal.Abstractions.IDockController>(),
            settings.Current.TaskbarRows)
        {
            Content = taskbarView,
        };
        // Persist the row count when the user drags the bar taller/shorter (bevel-0ml).
        taskbarWin.RowsChanged += rows => _ = settings.UpdateAsync(s => s.TaskbarRows = rows);
        taskbarWin.Show();
        desktop.MainWindow = taskbarWin;

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
    }

    /// <summary>Explorer surface: the initial file-manager window (opened at the user's home) plus
    /// the Ctrl+N new-window wiring. Built via the shared factory so the SAME object graph the
    /// modules register drives the running app (VfsRoot with file + computer providers, settings,
    /// file operations).</summary>
    /// <summary>Opens a Bevel Explorer window at <paramref name="path"/>. In the single-process shell the
    /// explorer surface lives in THIS process, so open in-process; in a split launch the taskbar has no
    /// explorer surface, so spawn the file manager as its own --role=explorer process (correct window +
    /// menu setup) instead of building a malformed window in the taskbar process.</summary>
    private static void OpenExplorerAt(IServiceProvider services, VfsPath path)
    {
        if (Role is ShellRole.All or ShellRole.Explorer)
            services.GetRequiredService<FileManagerWindowFactory>().Create(path);
        else
            Program.SpawnExplorer(path.Value);
    }

    private static void CreateExplorerSurface(
        IServiceProvider services, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var factory = services.GetRequiredService<FileManagerWindowFactory>();
        // A spawned explorer process (Start-menu "places") passes the folder to open via --open-path;
        // otherwise land on the user's home.
        var openArg = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--open-path=", StringComparison.OrdinalIgnoreCase));
        var startPath = !string.IsNullOrEmpty(openArg)
            ? new VfsPath("file", openArg.Substring("--open-path=".Length))
            : new VfsPath("file", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var fm = factory.Create(startPath);
        desktop.MainWindow = fm;

        // File > New Window (Ctrl+N): FileManagerWindow lives in Bevel.FileManager, which
        // Bevel.App references but not vice versa, so it cannot call the factory directly.
        // It instead raises this static event with the directory the new window should
        // open at (its current directory); every window's request is served by the same
        // factory, reusing the shared VfsRoot/SettingsService with fresh per-window
        // navigation/undo state. (Cross-process Ctrl+N — spawning a new explorer PROCESS — is
        // wired in the supervision phase; in-process spawning stays correct for the all-in-one
        // and single-explorer-process roles.) New Tab (Ctrl+T) is out of scope.
        FileManagerWindow.NewWindowRequested += path => factory.Create(path);
    }
}