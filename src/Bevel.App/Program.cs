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
            .AddBevelModules();

        using var host = builder.Build();
        App.Services = host.Services;

        // The shell-core owner runs HEADLESS — no Avalonia, no window. It owns the single window/app
        // projection and serves it to the UI role processes over the shell-core IPC.
        if (role == ShellRole.Core)
        {
            RunShellCore(host);
            return;
        }

        // Let a termination signal (SIGTERM / SIGINT / Ctrl-C) drive a clean Avalonia
        // shutdown so window OnClosed handlers and hosted-service Dispose run (e.g. the
        // Dock controller restores the user's Dock preference). .NET on macOS does NOT
        // surface SIGTERM via Console.CancelKeyPress/AppDomain.ProcessExit, so register a
        // POSIX signal handler explicitly (bevel-3kz).
        var appBuilder = BuildAvaloniaApp();
        // Keep the registrations rooted for the whole process lifetime: PosixSignalRegistration
        // unregisters its handler once the instance is garbage-collected.
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => App.RequestExit());
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => App.RequestExit());

        // Start the host so IHostedServices run (e.g. the macOS HelperLifecycle). This is
        // non-blocking — hosted services degrade gracefully rather than aborting boot.
        host.Start();

        // Load persisted settings on THIS thread, before StartWithClassicDesktopLifetime turns it
        // into the Avalonia UI thread. A synchronous read here blocks nothing (no dispatcher is
        // pumping yet); doing it inside OnFrameworkInitializationCompleted blocked — and once
        // deadlocked — the UI thread instead (core rule: never block the UI thread).
        host.Services.GetRequiredService<Bevel.Core.SettingsService>()
            .LoadAsync().GetAwaiter().GetResult();

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

        // Warm the window stream BEFORE the server enumerates (same ordering as the all-in-one path):
        // the poll subscribes to the helper and primes the first enumerate.
        if (windows is Pal.MacOS.MacOSWindowManager macWm)
            _ = macWm.StartPollAsync();

        var (socketPath, nonce) = ShellCore.ShellCoreEndpoint.ForServer();
        var server = new ShellCore.ShellCoreServer(windows, apps, socketPath, nonce);
        server.StartAsync().GetAwaiter().GetResult();

        // Park until SIGTERM/SIGINT. The supervisor (bevel-gww.4) signals this to swap the core to a
        // newer binary; a bare shell sends it on quit. Cancel the default action so .NET does NOT
        // terminate the process before the ordered teardown below runs (else the helper orphans).
        using var stop = new ManualResetEventSlim(false);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; stop.Set(); });
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; stop.Set(); });
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
        // Endpoints the launcher owns and hands to its children via env: the shell-core socket + nonce
        // (so core and taskbar share one authenticated channel with no token-file race) and the
        // launcher's own control socket + nonce (so the taskbar can reach us to quit/restart).
        var coreSocket = ShellCore.ShellCoreEndpoint.SocketPath;
        var coreToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var (controlSocket, controlNonce) = LauncherControl.CreateServerEndpoint();

        var childEnv = new Dictionary<string, string>
        {
            ["BEVEL_CORE_SOCKET"] = coreSocket,
            ["BEVEL_CORE_TOKEN"] = coreToken,
            [LauncherControl.SocketEnv] = controlSocket,
            [LauncherControl.TokenEnv] = Convert.ToHexString(controlNonce),
        };

        // Dependency + z-order: the shell-core owner (brings up the helper + owns window/app state)
        // first, then the UI surfaces — desktop behind, taskbar in front (mirroring the all-in-one
        // creation order). Explorer stays on-demand (a window the user opens), not a supervised surface.
        IRoleProcess[] processes =
        {
            new RoleProcess(ShellRole.Core, CreateRoleStartInfo(ShellRole.Core, args, childEnv)),
            new RoleProcess(ShellRole.Desktop, CreateRoleStartInfo(ShellRole.Desktop, args, childEnv)),
            new RoleProcess(ShellRole.Taskbar, CreateRoleStartInfo(ShellRole.Taskbar, args, childEnv)),
        };

        var supervisor = new RoleProcessSupervisor(
            processes,
            pollInterval: TimeSpan.FromSeconds(1),
            coreReadyProbe: ct => WaitForFileAsync(coreSocket, TimeSpan.FromSeconds(5), ct),
            log: msg => Console.Error.WriteLine($"[launcher] {msg}"));

        supervisor.StartAsync().GetAwaiter().GetResult();

        using var stop = new ManualResetEventSlim(false);

        // Control server: the taskbar's quit/restart buttons arrive here and fan out to the whole shell.
        var control = new UdsMessageServer(controlSocket, controlNonce, async (payload, ct) =>
        {
            if (payload.Length >= 1)
            {
                switch ((LauncherControl.Command)payload.Span[0])
                {
                    case LauncherControl.Command.RestartAll:
                        await supervisor.RestartAllAsync(ct).ConfigureAwait(false); break;
                    case LauncherControl.Command.RestartCore:
                        await supervisor.RestartCoreAsync(ct).ConfigureAwait(false); break;
                    case LauncherControl.Command.Quit:
                        stop.Set(); break;
                }
            }
            return new byte[] { 1 }; // ack
        });
        control.Start();

        // Cancel the default signal action so the ordered teardown below actually runs — without this,
        // .NET terminates the launcher before it can reap its children, orphaning the whole shell.
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; stop.Set(); });
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; stop.Set(); });
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

    /// <summary>Builds the start info for one child role process: the launcher's argv with its own
    /// <c>--role</c> replaced by the child's, plus the inherited control/shell-core environment.</summary>
    internal static ProcessStartInfo CreateRoleStartInfo(
        ShellRole role, IReadOnlyList<string> launcherArgs, IReadOnlyDictionary<string, string> env)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine process path to launch role processes.");
        var entryAssemblyPath = EntryAssemblyLocation();

        var childArgs = launcherArgs
            .Where(a => !a.StartsWith("--role=", StringComparison.OrdinalIgnoreCase))
            .Append("--role=" + RoleToArg(role))
            .ToList();

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
        ShellRole.Explorer => "explorer",
        ShellRole.Desktop => "desktop",
        _ => "all",
    };

    /// <summary>Polls for a file to appear (the shell-core socket) up to <paramref name="timeout"/>.</summary>
    private static async Task WaitForFileAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!File.Exists(path) && DateTime.UtcNow < deadline)
            await Task.Delay(50, ct).ConfigureAwait(false);
    }

    private static void Relaunch(IReadOnlyList<string> args)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath)) return;

        try
        {
            var entryAssemblyPath = EntryAssemblyLocation();
            var startInfo = CreateRestartStartInfo(processPath, entryAssemblyPath, args);
            startInfo.WorkingDirectory = !string.IsNullOrEmpty(entryAssemblyPath)
                ? Path.GetDirectoryName(entryAssemblyPath) ?? startInfo.WorkingDirectory
                : Path.GetDirectoryName(processPath) ?? startInfo.WorkingDirectory;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                startInfo = CreateDetachedMacOSStartInfo(startInfo);

            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            var logPath = Path.Combine(Path.GetTempPath(), "bevel-restart.log");
            File.AppendAllText(logPath, $"[{DateTimeOffset.Now:u}] Bevel restart failed: {ex}\n");
            Console.Error.WriteLine($"Bevel restart failed: {ex.Message} (see {logPath})");
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
        command.Append($" >>{logPath} 2>&1 &");

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
