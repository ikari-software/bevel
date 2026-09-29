using Avalonia;
using Bevel.App.Supervision;
using Bevel.ShellCore.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Bevel.App;

internal static class Program
{
    // Avalonia needs an STA thread on Windows; harmless elsewhere.
    [STAThread]
    public static void Main(string[] args)
    {
        var pal = PalSelector.FromArgs(args);
        var role = RoleSelector.FromArgs(args);
        // Stash the role for App.OnFrameworkInitializationCompleted (same process, set before the
        // lifetime starts — mirrors App.Services). Restart replays argv verbatim, so --role survives.
        App.Role = role;
        RestartDiag.Log($"boot: role={role} pal={pal}");

        // Last-ditch crash forensics. An unhandled exception in a peer process abandons the managed stack
        // entirely: the OS crash report shows only IL_Throw -> DispatchManagedException -> PROCAbort with
        // the managed frames unsymbolicated, so "which exception, from where" is unrecoverable unless the
        // child's stderr happened to be captured. Mirror it into the persistent diag log, which every role
        // can write and which survives the process (bevel-ejon / bevel-bxol were diagnosed only because a
        // LaunchAgent's StandardErrorPath caught them by luck).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            RestartDiag.Log($"FATAL unhandled ({role}): {e.ExceptionObject}");
            if (role != ShellRole.Launcher)
                RoleHeartbeatStore.ReportFailed(role, e.ExceptionObject?.ToString() ?? "unhandled");
        };

        if (role != ShellRole.Launcher)
            RoleHeartbeatStore.ReportStarting(role);

        // The launcher supervises OTHER processes and hosts no PAL/DI/UI of its own — branch before the
        // host is even built so it never constructs platform services.
        if (role == ShellRole.Launcher)
        {
            RunLauncher(args);
            return;
        }

        // Compose via Microsoft.Extensions.Hosting (DI-01). The PAL is selected here,
        // at the composition root, and nowhere else (DI-02).
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services
            .AddBevelPlatform(pal, role)
            .AddBevelModules(role);

        using var host = builder.Build();
        App.Services = host.Services;

        // The shell-core owner runs HEADLESS — no Avalonia, no window. It owns the single window/app
        // projection and serves it to the UI role processes over the shell-core IPC.
        if (role == ShellRole.Core)
        {
            RunShellCore(host);
            return;
        }

        // Kick the persisted-settings load off-thread NOW so its SQLite open + WAL + parse overlaps the
        // Avalonia app construction and hosted-service start below, instead of running serially just
        // before first paint (bevel-gwb4). It's awaited before StartWithClassicDesktopLifetime — still on
        // THIS thread, before the dispatcher pumps — so it's loaded by the time theme apply reads it, and
        // nothing else touches the service until then (no concurrency, no UI-thread block).
        var settingsLoad = Task.Run(() =>
            host.Services.GetRequiredService<Bevel.Core.ISettingsService>().LoadAsync());

        // Let a termination signal (SIGTERM / SIGINT / Ctrl-C) drive a clean Avalonia
        // shutdown so window OnClosed handlers and hosted-service Dispose run (e.g. the
        // Dock controller restores the user's Dock preference). .NET on macOS does NOT
        // surface SIGTERM via Console.CancelKeyPress/AppDomain.ProcessExit, so register a
        // POSIX signal handler explicitly (bevel-3kz).
        var appBuilder = BuildAvaloniaApp();
        // Keep the registrations rooted for the whole process lifetime: PosixSignalRegistration
        // unregisters its handler once the instance is garbage-collected.
        // A SIGTERM here means "shut THIS process down" — from the launcher tearing the shell down, or
        // a bare kill. Shut down locally (don't fan back out to the launcher, which sent it): the child
        // must run its own window teardown so the Dock is restored and the helper stopped.
        // ctx.Cancel = true is REQUIRED (matching the headless sites below): it cancels .NET's
        // default SIGTERM action, which would otherwise terminate the process right after this
        // handler returns — BEFORE Avalonia's dispatcher processes the Shutdown() that ShutdownLocal
        // posts, so window OnClosed / hosted-service Dispose (Dock restore, helper stop) never run
        // (sigterm-poll-signal-cancel; this UI-process handler was the one site missing it).
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; App.ShutdownLocal(); });
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; App.ShutdownLocal(); });
        // Windows has no SIGTERM a parent can send a headless child (bevel-ncfp.2): the launcher signals
        // a named event instead. Drive the SAME local teardown so window OnClosed / hosted-service Dispose
        // still run. No-op off Windows or when unsupervised.
        using var winStop = Supervision.WindowsShutdownSignal.Register(App.ShutdownLocal);

        // Start the host so IHostedServices run (e.g. the macOS HelperLifecycle). This is
        // non-blocking — hosted services degrade gracefully rather than aborting boot.
        host.Start();

        // Ensure the settings load (kicked off-thread above) has finished before StartWithClassicDesktop-
        // Lifetime turns this into the Avalonia UI thread — theme apply in OnFrameworkInitializationCompleted
        // reads them. Blocking here is safe: no dispatcher is pumping yet (doing it INSIDE
        // OnFrameworkInitializationCompleted blocked — and once deadlocked — the UI thread instead).
        settingsLoad.GetAwaiter().GetResult();

        try
        {
            appBuilder.StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // Stop hosted services cleanly on exit (kills the helper, restores the Dock).
            // Run on the thread pool, NOT this thread: after the desktop lifetime returns,
            // Avalonia's SynchronizationContext is still installed here, so a plain
            // .GetResult() would post StopAsync's await-continuations back to this blocked
            // main thread and deadlock — the helper never stops and the process hangs on
            // SIGTERM (only kill -9 works). Task.Run detaches from that context (bevel-fu5).
            Task.Run(() => host.StopAsync(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult();
        }

        if (App.RestartRequested)
        {
            RestartDiag.Log("Program: RestartRequested flag set → in-place re-exec after host stop");
            // Let the old helper socket and NSWindows tear down before the child connects.
            Thread.Sleep(400);
            Relaunch(args);
        }
    }

    /// <summary>
    /// The <c>--role=core</c> entry point: a headless process that owns the single live window/app
    /// projection and serves it to the UI roles over the shell-core IPC. No Avalonia, no dispatcher —
    /// window management runs off the Swift helper's background gRPC stream and NSWorkspace, neither of
    /// which needs an AppKit UI run loop. The main thread simply parks on a termination signal.
    /// </summary>
    private static void RunShellCore(IHost host)
    {
        // Start hosted services — for Core that's HelperLifecycle, which brings up the Swift helper the
        // window manager talks to. Non-blocking; the helper degrades gracefully if it can't start.
        host.Start();

        var services = host.Services;
        var windows = services.GetRequiredService<Bevel.Pal.Abstractions.IWindowManager>();
        var apps = services.GetRequiredService<Bevel.Pal.Abstractions.IAppEnvironment>();
        var tray = services.GetRequiredService<Bevel.Pal.Abstractions.ISystemTrayHost>();

        // The core is the sole opener of settings.db (core-owns-settings, bevel-6nve): load it here so the
        // server can hand every UI process a settings snapshot on connect and apply their write patches as
        // the single writer. Blocking is safe — this is the headless core's main thread, no dispatcher.
        var settings = services.GetRequiredService<Bevel.Core.ISettingsService>();
        settings.LoadAsync().GetAwaiter().GetResult();

        // Warm the window + tray streams BEFORE the server enumerates (same ordering as all-in-one):
        // the polls subscribe to the helper and prime the first enumerate.
        if (windows is Pal.MacOS.MacOSWindowManager macWm)
            _ = macWm.StartPollAsync();
        if (tray is Pal.MacOS.MacOSSystemTrayHost macTray)
            _ = macTray.StartPollAsync();

        var (socketPath, nonce) = ShellCore.ShellCoreEndpoint.ForServer();
        var server = new ShellCore.ShellCoreServer(windows, apps, tray, settings, socketPath, nonce);
        try
        {
            server.StartAsync().GetAwaiter().GetResult();
        }
        catch (UdsSocketBusyException ex)
        {
            // Another core is LIVE on this path (bevel-wio0). Standing down is the whole fix: the old
            // code unlinked and rebound, orphaning the incumbent with settings.db still open. A
            // supervised core reports the failure so its launcher Holds + alerts instead of
            // respawning into the same wall; a bare `--role=core` just says so and exits.
            var msg = $"another shell-core is already serving {ex.SocketPath}; this core is standing down";
            Console.Error.WriteLine($"[core] {msg}");
            RestartDiag.Log($"core: {msg}");
            if (LauncherControl.IsSupervised)
                RoleHeartbeatStore.ReportFailed(ShellRole.Core, msg);
            Task.Run(() => server.DisposeAsync().AsTask()).GetAwaiter().GetResult(); // never bound → never unlinks
            Task.Run(() => host.StopAsync(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult();
            Environment.ExitCode = 3;
            return;
        }
        RoleHeartbeatStore.ReportReady(ShellRole.Core, coreConnected: true);

        // Park until SIGTERM/SIGINT. The supervisor (bevel-gww.4) signals this to swap the core to a
        // newer binary; a bare shell sends it on quit. Cancel the default action so .NET does NOT
        // terminate the process before the ordered teardown below runs (else the helper orphans).
        using var stop = new ManualResetEventSlim(false);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; stop.Set(); });
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; stop.Set(); });
        using var winStop = Supervision.WindowsShutdownSignal.Register(stop.Set); // Windows graceful-stop (bevel-ncfp.2)
        stop.Wait();

        // Ordered teardown: stop serving + drop PAL subscriptions first, then stop the helper. Both
        // run off the thread pool to dodge any lingering synchronization context (bevel-fu5 pattern).
        Task.Run(() => server.DisposeAsync().AsTask()).GetAwaiter().GetResult();
        Task.Run(() => host.StopAsync(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult();
    }

    /// <summary>
    /// The <c>--role=launcher</c> entry point (bevel-gww.4): the multi-process shell's parent. It spawns
    /// the shell-core owner and the UI role processes in order, crash-restarts them, and listens on a
    /// control socket so a taskbar-initiated quit/restart fans out to the whole set. A restart re-execs
    /// this same binary for every child, so the shell comes back on the latest build.
    /// </summary>
    private static void RunLauncher(string[] args)
    {
        // Single instance, FIRST (bevel-wio0): everything below — clearing the quit marker and
        // heartbeats, binding launcher.sock, spawning a core that opens settings.db — would trample a
        // shell that is already running. The lock is held for this process's lifetime and released by
        // the kernel on any exit, so a crash never wedges the next launch.
        using var instance = TryTakeInstanceLock(out var incumbentPid, out var lockUnavailable);
        if (instance is null && !lockUnavailable)
        {
            var who = incumbentPid > 0 ? $" (launcher pid {incumbentPid})" : "";
            Console.Error.WriteLine($"[launcher] Bevel is already running{who}; this launch is exiting.");
            RestartDiag.Log($"launcher: instance lock held by another shell{who} — exiting");
            return;
        }

        // Endpoints the launcher owns and hands to its children via env: the shell-core socket + nonce
        // (so core and taskbar share one authenticated channel with no token-file race) and the
        // launcher's own control socket + nonce (so the taskbar can reach us to quit/restart).
        var coreSocket = ShellCore.ShellCoreEndpoint.SocketPath;
        var coreNonce = RandomNumberGenerator.GetBytes(16);
        var coreToken = Convert.ToHexString(coreNonce);
        var (controlSocket, controlNonce) = LauncherControl.CreateServerEndpoint();

        var childEnv = new Dictionary<string, string>
        {
            ["BEVEL_CORE_SOCKET"] = coreSocket,
            ["BEVEL_CORE_TOKEN"] = coreToken,
            [LauncherControl.SocketEnv] = controlSocket,
            [LauncherControl.TokenEnv] = Convert.ToHexString(controlNonce),
            [BuildStamp.EnvVar] = BuildStamp.Current(),
        };

        // The launcher is ALSO a spawn parent (bevel-t48y: Filer windows it opens via SpawnFiler inherit
        // this process's environment, NOT the per-child childEnv injection). Export the shell-control
        // pairs into our OWN env so launcher-spawned children get everything a supervised child gets —
        // without this, a launcher-spawned Filer found no core token (the supervised flow never writes
        // the core.token file) and died after ReadNonce's 5s poll (the pid-46808 SIGABRT). Idempotent for
        // the supervised children: CreateRoleStartInfo re-applies the same values.
        foreach (var (key, value) in childEnv)
            Environment.SetEnvironmentVariable(key, value);
        // Same for the taskbar↔Filer rendezvous: the LAUNCHER now spawns Filers, so IT must publish the
        // shared dir + nonce the taskbar dials with (PublishForChildren is idempotent — the taskbar
        // reuses the inherited pair instead of minting its own, so both parents agree).
        ShellCore.FilerControlEndpoint.PublishForChildren();

        // Dependency + z-order: the shell-core owner (brings up the helper + owns window/app state)
        // first, then the UI surfaces — desktop behind, taskbar in front (the full shell the user
        // expects). Filer stays on-demand (a window the user opens), not a supervised surface.
        //
        // The desktop surface is OFF by default (user preference: it's not useful today and just gets in
        // the way — it's launched on demand from the Start menu, bevel-gdie). Set BEVEL_ENABLE_DESKTOP=1
        // to spawn it at boot. The core is always required (the taskbar is an IPC client of it); if the
        // desktop is enabled it sits at index 1 — behind the taskbar, in front of the core.
        ShellRole[] roles = Environment.GetEnvironmentVariable("BEVEL_ENABLE_DESKTOP") == "1"
            ? [ShellRole.Core, ShellRole.Desktop, ShellRole.Taskbar]
            : [ShellRole.Core, ShellRole.Taskbar];

        var processes = roles
            .Select(r => (IRoleProcess)new RoleProcess(r, CreateRoleStartInfo(r, args, childEnv)))
            .ToArray();

        // A leftover quit marker / heartbeat from a previous session must not suppress crash-respawn
        // or look like a live child this boot.
        QuitRequest.Clear();
        RoleHeartbeatStore.ClearAll();

        var supervisor = new RoleProcessSupervisor(
            processes,
            pollInterval: TimeSpan.FromSeconds(1),
            coreReadyProbe: ct => CoreSocketProbe.WaitUntilServingAsync(coreSocket, coreNonce, TimeSpan.FromSeconds(5), ct),
            log: msg => Console.Error.WriteLine($"[launcher] {msg}"),
            // bevel-hprv: process-alive is not "serving". A core that lost core.sock stays up in ps
            // while every peer shows the disconnected indicator. And (bevel-wio0) a socket that
            // EXISTS — even one that accepts connections — is not proof it is OUR core: the probe is
            // an authenticated handshake with this session's nonce, so a hijacked/foreign core.sock
            // reads as unhealthy instead of being certified.
            coreHealthyProbe: ct => CoreSocketProbe.IsServingAsync(coreSocket, coreNonce, ct),
            quitRequested: QuitRequest.Exists,
            health: new ShellHealthMonitor(BuildStamp.Current),
            onAlert: ShellHealthAlert.Show);

        supervisor.StartAsync().GetAwaiter().GetResult();

        using var stop = new ManualResetEventSlim(false);

        // Control server: the taskbar's quit/restart buttons arrive here and fan out to the whole shell.
        var control = new UdsMessageServer(controlSocket, controlNonce, async (_, payload, ct) =>
        {
            if (payload.Length >= 1)
            {
                switch ((LauncherControl.Command)payload.Span[0])
                {
                    case LauncherControl.Command.RestartAll:
                        RestartDiag.Log("launcher: received RestartAll → supervisor.RestartAllAsync");
                        await supervisor.RestartAllAsync(ct).ConfigureAwait(false);
                        RestartDiag.Log("launcher: RestartAllAsync completed (children respawned in-place)");
                        break;
                    case LauncherControl.Command.RestartCore:
                        await supervisor.RestartCoreAsync(ct).ConfigureAwait(false); break;
                    case LauncherControl.Command.Quit:
                        // Latch BEFORE waking the waiter — without RequestStop the monitor can
                        // respawn a child that exits during teardown, and Quit looks like Restart
                        // (bevel-0md2 / the bevel-ply race on the quit path only).
                        supervisor.RequestStop();
                        stop.Set();
                        break;
                    // Desktop Show/Hide toggle (bevel-gdie): spawn/kill the --role=desktop child on demand.
                    // The factory re-uses CreateRoleStartInfo so the runtime desktop inherits the EXACT same
                    // core socket/token + control env as a boot-time desktop (BEVEL_ENABLE_DESKTOP=1) — it's
                    // a client of the already-running core, torn down cleanly by the supervisor on Hide/quit.
                    case LauncherControl.Command.SpawnDesktop:
                        RestartDiag.Log("launcher: received SpawnDesktop → supervisor.SpawnRoleAsync(Desktop)");
                        await supervisor.SpawnRoleAsync(
                            ShellRole.Desktop,
                            () => new RoleProcess(ShellRole.Desktop, CreateRoleStartInfo(ShellRole.Desktop, args, childEnv)),
                            ct).ConfigureAwait(false);
                        break;
                    case LauncherControl.Command.CloseDesktop:
                        RestartDiag.Log("launcher: received CloseDesktop → supervisor.CloseRoleAsync(Desktop)");
                        await supervisor.CloseRoleAsync(ShellRole.Desktop, ct).ConfigureAwait(false);
                        break;
                    case LauncherControl.Command.QueryDesktop:
                        // Reply byte carries state (1 up / 0 down) instead of the plain ack below.
                        return new[] { (byte)(await supervisor.IsRoleRunningAsync(ShellRole.Desktop, ct).ConfigureAwait(false) ? 1 : 0) };
                    // bevel-t48y: the taskbar (or any supervised role) asks the launcher to open a Filer.
                    // TRANSITIONAL (Task 2): spawn via the same helper the taskbar used, now LAUNCHER-owned
                    // (attribution: child of the app process, not a UI child) — Task 3 replaces this with
                    // the FilerSupervisor (liveness ack + captured stderr + teardown/restart). An
                    // undecodable payload NACKs (0) so the caller's fallback runs instead of a silent loss.
                    case LauncherControl.Command.SpawnFiler:
                        if (LauncherControl.DecodeSpawnFiler(payload.Span) is { } open)
                        {
                            RestartDiag.Log($"launcher: received SpawnFiler → {open.OpenPath} (search={open.Search}, select={open.SelectPath ?? "-"})");
                            SpawnFiler(open.OpenPath, open.Search, open.SelectPath);
                        }
                        else
                        {
                            RestartDiag.Log("launcher: SpawnFiler payload undecodable — NACK");
                            return new byte[] { 0 };
                        }
                        break;
                }
            }
            return new byte[] { 1 }; // ack
        });
        control.Start();

        // Cancel the default signal action so the ordered teardown below actually runs — without this,
        // .NET terminates the launcher before it can reap its children, orphaning the whole shell.
        // RequestStop() latches the supervisor closed SYNCHRONOUSLY here so it can't respawn a child that
        // exited on its own SIGTERM before the async teardown runs (bevel-ply).
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; supervisor.RequestStop(); stop.Set(); });
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; supervisor.RequestStop(); stop.Set(); });
        using var winStop = Supervision.WindowsShutdownSignal.Register(() => { supervisor.RequestStop(); stop.Set(); }); // bevel-ncfp.2
        stop.Wait();

        // Ordered teardown off the thread pool (no lingering sync context — bevel-fu5): kill every child
        // FIRST (supervisor stop), so a slow control-server disposal can never leave the shell running
        // headless; then dispose the control server.
        Task.Run(async () =>
        {
            await supervisor.DisposeAsync().ConfigureAwait(false);
            await control.DisposeAsync().ConfigureAwait(false);
        }).GetAwaiter().GetResult();
    }

    /// <summary>Takes the single-instance lock. Contention (another shell) returns null with the
    /// incumbent's pid. A lock that cannot be created AT ALL (unwritable config dir, exotic filesystem)
    /// also returns null but flags <paramref name="unavailable"/>: the shell then boots unguarded with a
    /// logged warning rather than refusing to start over a permissions quirk.</summary>
    private static ShellInstanceLock? TryTakeInstanceLock(out int incumbentPid, out bool unavailable)
    {
        unavailable = false;
        try
        {
            return ShellInstanceLock.TryAcquire(ShellInstanceLock.DefaultPath, ShellInstanceLock.DefaultWait, out incumbentPid);
        }
        catch (Exception ex) when (ex is not IOException)
        {
            incumbentPid = 0;
            unavailable = true;
            Console.Error.WriteLine($"[launcher] single-instance lock unavailable ({ex.Message}); continuing unguarded");
            RestartDiag.Log($"launcher: instance lock unavailable: {ex}");
            return null;
        }
    }

    /// <summary>Builds the start info for one child role process: the launcher's argv with its own
    /// <c>--role</c> replaced by the child's, plus the inherited control/shell-core environment.
    /// <paramref name="extraChildArgs"/> (bevel-t48y) appends role-specific extras — a filer child's
    /// <c>--open-path</c>/<c>--search</c>/<c>--select</c> — AFTER the role arg, so the supervised spawn
    /// path composes children with the same builder as core/taskbar/desktop.</summary>
    internal static ProcessStartInfo CreateRoleStartInfo(
        ShellRole role, IReadOnlyList<string> launcherArgs, IReadOnlyDictionary<string, string> env,
        IReadOnlyList<string>? extraChildArgs = null)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine process path to launch role processes.");
        var entryAssemblyPath = EntryAssemblyLocation();

        var childArgs = launcherArgs
            .Where(a => !a.StartsWith("--role=", StringComparison.OrdinalIgnoreCase))
            .Append("--role=" + RoleToArg(role))
            .ToList();
        if (extraChildArgs is not null)
            childArgs.AddRange(extraChildArgs);

        var startInfo = CreateRestartStartInfo(processPath, entryAssemblyPath, childArgs);
        startInfo.WorkingDirectory = !string.IsNullOrEmpty(entryAssemblyPath)
            ? Path.GetDirectoryName(entryAssemblyPath) ?? startInfo.WorkingDirectory
            : Path.GetDirectoryName(processPath) ?? startInfo.WorkingDirectory;

        foreach (var (key, value) in env)
            startInfo.Environment[key] = value;

        return startInfo;
    }

    // Assembly.Location is empty under single-file / NativeAOT (IL3000) — which is exactly the signal
    // the relaunch path wants: no side-by-side managed DLL to re-invoke through `dotnet`, so the empty
    // string flows to CreateRestartStartInfo, which then launches the native ProcessPath directly.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "Empty Location under single-file/AOT is handled: relaunch falls back to ProcessPath.")]
    private static string EntryAssemblyLocation() => Assembly.GetEntryAssembly()?.Location ?? "";

    private static string RoleToArg(ShellRole role) => role switch
    {
        ShellRole.Core => "core",
        ShellRole.Taskbar => "taskbar",
        ShellRole.Filer => "filer",
        ShellRole.Desktop => "desktop",
        ShellRole.Launcher => "launcher",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown shell role"),
    };

    private static void Relaunch(IReadOnlyList<string> args)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath)) return;

        try
        {
            // macOS packaged .app: relaunch through LaunchServices (`open -n`) so the new instance keeps
            // the app's TCC identity. A raw re-exec (posix_spawn via nohup) is attributed to the spawning
            // shell, not to pl.ikari.bevel, so a granted Accessibility/Screen-Recording stops applying
            // after a restart — the tray dies and it re-prompts. `open` launches it as a real app.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && TryGetAppBundle(processPath) is { } bundle)
            {
                var open = new ProcessStartInfo { FileName = "/usr/bin/open", UseShellExecute = false };
                open.ArgumentList.Add("-n");        // new instance even though one is (briefly) still exiting
                open.ArgumentList.Add(bundle);
                if (args.Count > 0)
                {
                    open.ArgumentList.Add("--args");
                    foreach (var a in args) open.ArgumentList.Add(a);
                }
                RestartDiag.Log($"Relaunch: LaunchServices `open -n {bundle}` (preserves TCC identity)");
                Process.Start(open);
                return;
            }

            var entryAssemblyPath = EntryAssemblyLocation();
            var startInfo = CreateRestartStartInfo(processPath, entryAssemblyPath, args);
            startInfo.WorkingDirectory = !string.IsNullOrEmpty(entryAssemblyPath)
                ? Path.GetDirectoryName(entryAssemblyPath) ?? startInfo.WorkingDirectory
                : Path.GetDirectoryName(processPath) ?? startInfo.WorkingDirectory;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                startInfo = CreateDetachedMacOSStartInfo(startInfo);

            RestartDiag.Log($"Relaunch: exec FileName={startInfo.FileName} args=[{string.Join(' ', startInfo.ArgumentList)}] cwd={startInfo.WorkingDirectory}");
            var child = Process.Start(startInfo);
            RestartDiag.Log($"Relaunch: Process.Start returned pid={(child?.Id.ToString() ?? "null")}");
        }
        catch (Exception ex)
        {
            RestartDiag.Log($"Relaunch FAILED: {ex}");
            Console.Error.WriteLine($"Bevel restart failed: {ex.Message} (see {Path.Combine(Path.GetTempPath(), "bevel-restart.log")})");
        }
    }

    /// <summary>Builds a filer child's argv from the parent's own: the parent's args minus the
    /// filer-scoped switches, plus <c>--role=filer</c> and the open target (and find mode / reveal-select
    /// when given). ONE argv builder shared by the in-process fallback (<see cref="SpawnFiler"/>) and —
    /// bevel-t48y — the launcher's supervised spawn, so both paths construct identical children.</summary>
    internal static IReadOnlyList<string> BuildFilerArgs(
        IEnumerable<string> parentArgs, string filePath, bool search = false, string? selectPath = null)
    {
        var childArgs = parentArgs
            .Where(a => !a.StartsWith("--role=", StringComparison.OrdinalIgnoreCase)
                     && !a.StartsWith("--open-path=", StringComparison.OrdinalIgnoreCase)
                     && !a.StartsWith("--select=", StringComparison.OrdinalIgnoreCase)
                     && !a.Equals("--search", StringComparison.OrdinalIgnoreCase))
            .Append("--role=filer")
            .Append("--open-path=" + filePath)
            .ToList();
        if (search) childArgs.Add("--search");   // open the new window straight into Find mode (bevel-x6pv)
        if (!string.IsNullOrEmpty(selectPath))
            childArgs.Add("--select=" + selectPath);   // highlight this item once its folder loads (bevel-e7a7)
        return childArgs;
    }

    /// <summary>Opens a Bevel Filer window at <paramref name="filePath"/> as its OWN
    /// <c>--role=filer</c> process — the way the split shell hosts the file manager. Called from the
    /// taskbar's Start-menu "places" and from the automation command model's window verbs
    /// (<c>open</c>/<c>reveal</c>, bevel-e7a7): creating the window in the taskbar process instead gives
    /// it none of the filer surface setup, so its menu mis-renders. Reuses this process's argv (minus
    /// role / open-path / select), so the child inherits the same PAL + control/shell-core environment.
    /// <paramref name="selectPath"/> (a <c>reveal</c> target) is handed to the child via
    /// <c>--select=</c>, which <see cref="App.CreateFilerSurface"/> turns into a
    /// <c>SelectAfterLoad</c> once the folder lists.</summary>
    internal static void SpawnFiler(string filePath, bool search = false, string? selectPath = null)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath)) return;

        var childArgs = BuildFilerArgs(Environment.GetCommandLineArgs().Skip(1), filePath, search, selectPath);

        try
        {
            var entryAssemblyPath = EntryAssemblyLocation();
            var startInfo = CreateRestartStartInfo(processPath, entryAssemblyPath, childArgs);
            startInfo.WorkingDirectory = !string.IsNullOrEmpty(entryAssemblyPath)
                ? Path.GetDirectoryName(entryAssemblyPath) ?? startInfo.WorkingDirectory
                : Path.GetDirectoryName(processPath) ?? startInfo.WorkingDirectory;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                startInfo = CreateDetachedMacOSStartInfo(startInfo);

            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Bevel filer spawn failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Reconstructs the current launch for both a published app executable and a framework-
    /// dependent <c>dotnet Bevel.App.dll</c> invocation, preserving the original arguments.
    /// </summary>
    internal static ProcessStartInfo CreateRestartStartInfo(
        string processPath,
        string entryAssemblyPath,
        IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory,
        };

        if (string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(entryAssemblyPath))
        {
            startInfo.ArgumentList.Add(entryAssemblyPath);
        }

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        return startInfo;
    }

    /// <summary>If <paramref name="processPath"/> lives inside a macOS .app bundle
    /// (…/Foo.app/Contents/MacOS/Foo), returns the bundle path (…/Foo.app); otherwise null (e.g. a dev
    /// <c>dotnet run</c>). Used so a restart of the packaged app relaunches via LaunchServices and keeps
    /// its TCC grants, instead of a raw re-exec that loses them.</summary>
    private static string? TryGetAppBundle(string processPath)
    {
        const string marker = ".app/Contents/MacOS/";
        var idx = processPath.IndexOf(marker, StringComparison.Ordinal);
        return idx >= 0 ? processPath[..(idx + ".app".Length)] : null;
    }

    /// <summary>
    /// Wraps a launch in <c>nohup … &amp;</c> so the relaunched shell survives the exiting
    /// parent process (macOS process-group / terminal session teardown).
    /// </summary>
    internal static ProcessStartInfo CreateDetachedMacOSStartInfo(ProcessStartInfo inner)
    {
        var command = new System.Text.StringBuilder("nohup ");
        command.Append(QuoteShellArgument(inner.FileName));
        foreach (var arg in inner.ArgumentList)
        {
            command.Append(' ');
            command.Append(QuoteShellArgument(arg));
        }
        var logPath = Path.Combine(Path.GetTempPath(), "bevel-restart.log");
        command.Append($" >>{QuoteShellArgument(logPath)} 2>&1 &");   // quote: TMPDIR may contain spaces/metachars (ce-review)

        return new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            WorkingDirectory = inner.WorkingDirectory,
            ArgumentList = { "-c", command.ToString() },
        };
    }

    internal static string QuoteShellArgument(string value)
        => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    // Referenced by name by the Avalonia XAML previewer/designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
