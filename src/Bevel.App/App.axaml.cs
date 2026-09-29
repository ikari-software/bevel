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

    /// <summary>Which surface this process hosts. Always set in <see cref="Program"/> from the resolved
    /// <c>--role</c> before the lifetime starts; each surface process runs exactly one role. The Taskbar
    /// default is just a placeholder for the never-used unset case — Program always overwrites it.</summary>
    public static ShellRole Role { get; set; } = ShellRole.Taskbar;

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
        // Latch quit intent FIRST so a failed control send (stale launcher.sock) cannot look
        // like a taskbar crash — the monitor honors the marker and will not respawn (bevel-0md2).
        Supervision.QuitRequest.Write();
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
    /// <summary>
    /// Targeted repair for a lost core link (bevel-corepulse): ask the launcher to respawn ONLY the
    /// shell-core process. That rebinds <c>core.sock</c>, which is what a peer needs to reconnect — a
    /// core whose socket path was unlinked keeps running and serving nobody, so the taskbar shows no
    /// windows and no tray while the process looks perfectly healthy in <c>ps</c>.
    ///
    /// <para>Returns false when there is no launcher to ask, so the caller can offer the whole-shell
    /// restart instead rather than silently doing nothing.</para>
    /// </summary>
    public static bool RequestRestartCore()
    {
        if (Supervision.LauncherControl.TrySend(Supervision.LauncherControl.Command.RestartCore))
        {
            RestartDiag.Log("RequestRestartCore: launcher-control send=true → core respawned in place");
            return true;
        }

        RestartDiag.Log("RequestRestartCore: send failed (supervised=" +
                        Supervision.LauncherControl.IsSupervised + ") → caller should offer a full restart");
        return false;
    }

    public static void RequestRestart()
    {
        if (Supervision.LauncherControl.TrySend(Supervision.LauncherControl.Command.RestartAll))
        {
            RestartDiag.Log("RequestRestart: launcher-control send=true → launcher restarts children in-place");
            return;
        }

        // The send didn't land. A launcher-SUPERVISED child must NOT re-exec itself — that spawns an
        // orphan standalone process (e.g. a taskbar with no core → localhost:80 gRPC failures) that races
        // the launcher's own respawn, which is exactly the intermittent "restart crashed" (bevel-1fvn).
        // Just exit; the launcher's crash-monitor brings us back on the current binary. Only a TRULY
        // standalone process (no launcher) does the in-place re-exec.
        if (Supervision.LauncherControl.IsSupervised)
        {
            // Exit THIS process only. Do NOT go through RequestExit — that writes the quit
            // marker (bevel-0md2) and the monitor would refuse to bring us back.
            RestartDiag.Log("RequestRestart: supervised but send failed → exit only (launcher monitor respawns); NO standalone re-exec");
            ShutdownLocal();
            return;
        }

        RestartDiag.Log("RequestRestart: standalone (no launcher) → in-place re-exec after host stop");
        Interlocked.Exchange(ref _restartRequested, 1);
        RequestExit();
    }

    /// <summary>
    /// Start ▸ "Show/Hide Desktop" (bevel-gdie): flip the desktop child on demand via the launcher control
    /// socket. Runs entirely OFF the UI thread — both the state query and the spawn/close command do a
    /// bounded synchronous UDS round-trip (up to a few seconds each), and the caller is a Start-menu click
    /// on the UI thread. Fire-and-forget: the desktop process appears/disappears under the launcher's
    /// supervision, nothing here awaits it. Unsupervised (all-in-one / no launcher): the query returns null
    /// and the send returns false, so this degrades to a harmless no-op instead of crashing.
    /// </summary>
    public static void ToggleDesktop()
    {
        Task.Run(() =>
        {
            var running = Supervision.LauncherControl.QueryDesktopRunning() ?? false;
            Supervision.LauncherControl.TrySend(running
                ? Supervision.LauncherControl.Command.CloseDesktop
                : Supervision.LauncherControl.Command.SpawnDesktop);
        });
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
            var settings = services.GetRequiredService<ISettingsService>();

            // Cross-process live re-theming (bevel-dob / core-owns-settings bevel-6nve): a peer role's
            // Settings/Onboarding dialog sends its change to the shell core (sole writer), which applies it
            // and BROADCASTS a fresh snapshot to every UI process; RemoteSettingsService decodes it and
            // raises Changed here, so a theme switch reskins every shell surface (taskbar, desktop, …), not
            // just the process that owns the dialog. The 750 ms DB poll this used to ride is retired —
            // the push replaces it; no process polls settings.db any more.
            //
            // Changed is raised on the shell-core TRANSPORT thread (the receive loop), and ThemeService /
            // ThemeVariants / FontService mutate Application resources + Styles, which re-templates live
            // controls — UI-thread-only work. Marshal every apply, and subscribe BEFORE the startup apply
            // below: a peer paints from its on-disk settings cache (bevel-7s9n) and the core's live snapshot
            // can land at any moment after LoadAsync — including between this subscription and the startup
            // read of Current. Subscribing first means such a snapshot is applied by the (posted) handler
            // right after this method returns; the same theme applied twice is a no-op in ThemeService.
            settings.Changed += () => Dispatcher.UIThread.Post(() =>
            {
                var s = settings.Current;
                // Gate the theme-coupled engines on the template swap succeeding (see the startup
                // apply below) so a failed live re-template can't desync the recolour engines.
                if (UI.ThemeService.Apply(s.ThemeId))
                {
                    UI.ThemeVariants.Apply(s);
                    UI.ThemeOptions.ApplyCrispBevels(this, settings.ThemeOverridesFor(s.ThemeId).CrispBevels ?? false);
                }
                UI.FontService.Apply(s.UiFontFamily);

                // Folder Options → apply live to every open file-manager window: update the app-wide
                // extension-hiding flag and re-list each window (also re-runs the hidden-file filter).
                // Null in non-FM roles (taskbar/desktop), where the fan-out is a no-op.
                Bevel.FileManager.Components.ItemViewModel.HideKnownExtensions = s.HideKnownExtensions;
                if (services.GetService<FileManagerWindowRegistry>() is { } fmReg)
                    foreach (var w in fmReg.All())
                        w.ApplyFolderOptions();   // info-pane style + column + re-list
            });

            // Theme token bundle first (PKG-03) — the baseline the user overrides layer on top of.
            // Gate the theme-COUPLED engines (colour variant + crisp bevels) on the template swap
            // actually succeeding: applying a theme's colour variant while its template failed to load
            // leaves the two recolour engines disagreeing about the active theme (ce-review).
            if (UI.ThemeService.Apply(settings.Current.ThemeId))
            {
                UI.ThemeOptions.ApplyCrispBevels(
                    this, settings.ThemeOverridesFor(settings.Current.ThemeId).CrispBevels ?? false);
                // The active theme's appearance variant (W2K-01 colour scheme / Luna colour+gloss). The
                // theme owns its options via ThemeVariants, which routes to the right engine and clears
                // the others so shared chrome keys don't bleed across themes.
                UI.ThemeVariants.Apply(settings.Current);
            }
            // UI font override (FNT-01) — orthogonal to the theme, so applied regardless.
            UI.FontService.Apply(settings.Current.UiFontFamily);
            // Folder Options is an app-wide flag read by every ItemViewModel; seed it before the first
            // explorer window lists a directory so a persisted "hide extensions" is honoured on first paint.
            Bevel.FileManager.Components.ItemViewModel.HideKnownExtensions = settings.Current.HideKnownExtensions;

            // Create only this process's surface. The shell always runs split (--role=…), so exactly
            // one block below runs and sets MainWindow to its own window. Only the taskbar role resolves
            // the window manager / app environment, so in the other roles those singletons (and the
            // helper) are never constructed.
            var role = Role;

            // Split-mode chrome (the taskbar and desktop processes) is the environment, not an app:
            // it must have no Dock tile and stay out of Cmd-Tab. Marking the process .accessory also
            // fixes bevel-nji — the Swift helper enumerates only .regular apps' windows, so the
            // chrome's own transient popups (tooltips, menus) stop leaking into the taskbar's
            // foreign-window list (where they registered as windows, shifted the bar, and dismissed
            // themselves). NOT applied to the Explorer role: its file-manager window SHOULD appear in
            // the taskbar.
            if (OperatingSystem.IsMacOS() && role is ShellRole.Taskbar or ShellRole.Desktop)
                Pal.MacOS.ShellActivation.HideFromDock();

            if (role is ShellRole.Desktop)
                CreateDesktopSurface(desktop);
            if (role is ShellRole.Taskbar)
            {
                // Publish the taskbar↔Explorer channel's rendezvous dir + shared nonce into this
                // process's env BEFORE any Explorer is spawned (Start-menu places, open/reveal), so every
                // spawned Explorer inherits the same discovery root + secret and its control server binds
                // where this taskbar's client will look (bevel-uldj).
                ShellCore.ExplorerControlEndpoint.PublishForChildren();
                CreateTaskbarSurface(services, settings, desktop);
            }
            if (role is ShellRole.Explorer)
                CreateExplorerSurface(services, desktop);

            // bevel:// URL handler (M4-D.2 / bevel-6dc) + inbound Apple Events (M4-C / bevel-376): wired
            // in the PERSISTENT host — the always-up TASKBAR that also serves the bevelctl socket
            // (bevel-e7a7) — so both funnel through the one AutomationCommandRouter with NO Explorer
            // window required. The router's window verbs resolve to SpawningShellSurface here (open/reveal
            // spawn an Explorer); filesystem/program verbs run directly. Wiring these only in the taskbar
            // (never the on-demand Explorer) keeps exactly one process handling inbound automation.
            if (role is ShellRole.Taskbar)
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
        try
        {
            var desktopWin = new DesktopWindow { Content = new DesktopView() };
            desktopWin.Show();
            desktop.MainWindow = desktopWin;
            var connected = Services?.GetService<Bevel.Pal.Abstractions.IShellConnectionStatus>();
            Supervision.RoleHeartbeatStore.ReportReady(ShellRole.Desktop, connected?.IsConnected ?? true);
            if (connected is not null)
                connected.ConnectionChanged += (_, c) => Supervision.RoleHeartbeatStore.ReportCore(c);
        }
        catch (Exception ex)
        {
            Supervision.RoleHeartbeatStore.ReportFailed(ShellRole.Desktop, ex.ToString());
            throw;
        }
    }

    /// <summary>Taskbar right-click → "Lock the Taskbar": flips the setting and pushes it onto the live
    /// bar (bevel-cust.ctxmenu). async void, so the settings I/O is guarded — a SaveAsync failure must
    /// not crash the shell from a context-menu click (review: reliability).</summary>
    private static async void ToggleTaskbarLock(ISettingsService settings, Taskbar.TaskbarView taskbarView)
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

    /// <summary>Persist the taskbar row count off a resize drag. async void, guarded — a SaveAsync
    /// fault must not surface as an unobserved task exception (ce-review: reliability).</summary>
    private static async void PersistRows(ISettingsService settings, int rows)
    {
        try { await settings.UpdateAsync(s => s.TaskbarRows = rows); }
        catch (Exception ex) { Console.Error.WriteLine($"[app] persist TaskbarRows failed (swallowed): {ex.Message}"); }
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
        IServiceProvider services, ISettingsService settings, IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            CreateTaskbarSurfaceCore(services, settings, desktop);
        }
        catch (Exception ex)
        {
            Supervision.RoleHeartbeatStore.ReportFailed(ShellRole.Taskbar, ex.ToString());
            throw;
        }
    }

    private static void CreateTaskbarSurfaceCore(
        IServiceProvider services, ISettingsService settings, IClassicDesktopStyleApplicationLifetime desktop)
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
            settings.Current,
            services.GetService<Bevel.Pal.Abstractions.IAppEnvironment>(),
            services.GetService<Bevel.Pal.Abstractions.IIconProvider>(),
            quit: RequestExit,
            restart: RequestRestart,
            // Only offered when a launcher is actually there to respawn the core; otherwise the repair
            // menu falls back to the whole-shell restart above.
            restartCore: Supervision.LauncherControl.IsSupervised ? () => RequestRestartCore() : null,
            openSettings: () => OpenTaskbarSettings(services, taskbarView),
            toggleLock: () => ToggleTaskbarLock(settings, taskbarView),
            // Start-menu "places" (My Documents/Pictures/Music/Computer) open a Bevel Explorer window
            // at that folder — in-process in the single-process shell, or as its own --role=explorer
            // process in a split launch (the taskbar process has no explorer surface).
            openFolder: path => OpenExplorerAt(services, path),
            // Start ▸ Search → open a Bevel Explorer already in Find mode (bevel-x6pv).
            openSearch: () => OpenExplorerSearch(services),
            // Start ▸ Show/Hide Desktop (bevel-gdie): spawn/kill the --role=desktop child via the launcher,
            // and a state probe so the item labels itself "Show" (hidden) vs "Hide" (shown) on each open.
            toggleDesktop: ToggleDesktop,
            desktopRunning: Supervision.LauncherControl.QueryDesktopRunning,
            // Tab enumeration for the task-button menu's Tabs section (bevel-a40b).
            tabProvider: services.GetService<Bevel.Pal.Abstractions.ITabProvider>(),
            // A peer's settings.Current can be stale at this exact moment (a cold/stale on-disk cache,
            // or the core connect still in flight) — handing the service itself lets TaskbarView catch
            // up on its OWN ISettingsService.Changed the moment the real snapshot lands, instead of
            // staying frozen at whatever TaskbarRows/etc. Initialize saw here (bevel-kclq regression).
            settingsService: settings);
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
            settings.Current.TaskbarRows,
            services.GetService<Bevel.Pal.Abstractions.IDesktopEnvironment>())
        {
            Content = taskbarView,
        };

        // bevel-hprv: a core that is alive-but-not-serving never trips the crash-monitor (it
        // only watches HasExited). After a sustained disconnect, ask the launcher to respawn
        // just the core — same verb as the pulsing-indicator click, without waiting for one.
        var connection = services.GetService<Bevel.Pal.Abstractions.IShellConnectionStatus>();
        if (Supervision.LauncherControl.IsSupervised && connection is not null)
        {
            var autoRepair = new Supervision.LostCoreAutoRepair(
                connection, RequestRestartCore, log: RestartDiag.Log);
            desktop.Exit += (_, _) => autoRepair.Dispose();
        }
        // Persist the row count when the user drags the bar taller/shorter (bevel-0ml). Guarded like
        // ToggleTaskbarLock — a bare `_ = UpdateAsync(...)` swallowed a SaveAsync fault into an
        // unobserved task exception (ce-review: reliability).
        taskbarWin.RowsChanged += rows => PersistRows(settings, rows);
        // Session protocol (bevel-4zfs): the core pushes NOTHING on connect. Every adapter (windows /
        // apps / tray / settings) is now constructed and subscribed, so START THE SESSION — the Hello
        // snapshot burst lands on a fully-wired client and can never be eaten by a too-early connect.
        // (The connection is the ShellCoreClient only in split mode; on the Fake PAL it's the always-
        // connected stub and this is a no-op.) Fire-and-forget: the burst arrives on the transport
        // thread and the adapters marshal onto their own dispatchers.
        if (connection is ShellCore.ShellCoreClient coreSession)
            _ = coreSession.StartSessionAsync();
        taskbarWin.Show();
        desktop.MainWindow = taskbarWin;
        Supervision.RoleHeartbeatStore.ReportReady(ShellRole.Taskbar, connection?.IsConnected ?? true);
        if (connection is not null)
            connection.ConnectionChanged += (_, c) => Supervision.RoleHeartbeatStore.ReportCore(c);

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
        if (Role is ShellRole.Explorer)
            services.GetRequiredService<FileManagerWindowFactory>().Create(path);
        else
            Program.SpawnExplorer(path.Value);
    }

    /// <summary>Start ▸ Search → "For Files or Folders": open a Bevel Explorer at Home already in Find
    /// mode (bevel-x6pv). In-process the factory hands back the window so we focus its Find pane; in a
    /// split launch the explorer spawns with --search and enters Find mode itself.</summary>
    private static void OpenExplorerSearch(IServiceProvider services)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Role is ShellRole.Explorer)
            services.GetRequiredService<FileManagerWindowFactory>().Create(new VfsPath("file", home)).BeginSearch();
        else
            Program.SpawnExplorer(home, search: true);
    }

    private static void CreateExplorerSurface(
        IServiceProvider services, IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            CreateExplorerSurfaceCore(services, desktop);
        }
        catch (Exception ex)
        {
            Supervision.RoleHeartbeatStore.ReportFailed(ShellRole.Explorer, ex.ToString());
            throw;
        }
    }

    private static void CreateExplorerSurfaceCore(
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
        // Spawned via Start ▸ Search → open straight into Find mode (bevel-x6pv).
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--search", StringComparison.OrdinalIgnoreCase)))
            fm.BeginSearch();
        // Spawned by the automation `reveal` verb (bevel-e7a7): --select=<item> queues a selection that
        // the FileManagerWindow applies once the target folder's listing finishes (SelectAfterLoad) —
        // the same model→view highlight the in-process reveal uses. Split-mode `reveal` is thus a plain
        // Explorer spawn: no live in-process window required, no cross-process IPC.
        var selectArg = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--select=", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(selectArg))
            fm.SelectAfterLoad(new[] { new VfsPath("file", selectArg.Substring("--select=".Length)) });
        desktop.MainWindow = fm;

        // File > New Window (Ctrl+N): FileManagerWindow lives in Bevel.FileManager, which
        // Bevel.App references but not vice versa, so it cannot call the factory directly.
        // It instead raises this static event with the directory the new window should
        // open at (its current directory); every window's request is served by the same
        // factory, reusing the shared VfsRoot/SettingsService with fresh per-window
        // navigation/undo state. (Cross-process Ctrl+N — spawning a new explorer PROCESS — is
        // wired in the supervision phase; in-process spawning stays correct within the explorer
        // process.) New Tab (Ctrl+T) is out of scope.
        FileManagerWindow.NewWindowRequested += path => factory.Create(path);
        var connected = services.GetService<Bevel.Pal.Abstractions.IShellConnectionStatus>();
        Supervision.RoleHeartbeatStore.ReportReady(ShellRole.Explorer, connected?.IsConnected ?? true);
        if (connected is not null)
            connected.ConnectionChanged += (_, c) => Supervision.RoleHeartbeatStore.ReportCore(c);
    }
}