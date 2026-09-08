using System.Security.Cryptography;
using Bevel.ShellCore.Ipc;

namespace Bevel.App.Supervision;

/// <summary>
/// The control channel a UI child uses to drive its launcher (bevel-gww.4): the taskbar's quit/restart
/// buttons must fan out to the WHOLE shell, not just their own process. The launcher listens on a small
/// UDS control server (reusing the shell-core transport + nonce handshake); it hands each child the
/// socket path and nonce via the environment, so a child launched standalone (no launcher) simply finds
/// no env and falls back to its in-process behaviour. .NET's managed signal API can't receive
/// SIGUSR1/SIGUSR2, which is why this is a message channel rather than a signal.
/// </summary>
internal static class LauncherControl
{
    public const string SocketEnv = "BEVEL_LAUNCHER_SOCKET";
    public const string TokenEnv = "BEVEL_LAUNCHER_TOKEN";

    /// <summary>Control verbs. One byte on the wire; the reply is a single 0x01 ack — except
    /// <see cref="QueryDesktop"/>, whose reply byte carries the desktop's running state (1/0).</summary>
    public enum Command : byte
    {
        RestartAll = 1,   // kill+respawn everything → whole shell on the latest binary
        RestartCore = 2,  // swap just the shell-core owner (update-on-demand)
        Quit = 3,         // tear the whole shell down
        SpawnDesktop = 4, // launch the --role=desktop child on demand (Start ▸ Show Desktop, bevel-gdie)
        CloseDesktop = 5, // terminate + de-supervise the desktop child (Start ▸ Hide Desktop)
        QueryDesktop = 6, // ask whether the desktop child is running; reply byte is 1 (up) / 0 (down)
    }

    private static string DefaultSocketPath =>
        BevelRuntimeDir.GuardSocketPath(Path.Combine(BevelRuntimeDir.CoreDir, "launcher.sock"));

    /// <summary>True when this process was launched under a supervisor (control env is present), so
    /// quit/restart should fan out to the launcher instead of acting in-process.</summary>
    public static bool IsSupervised =>
        Environment.GetEnvironmentVariable(SocketEnv) is { Length: > 0 };

    /// <summary>Launcher side: mint the socket path + nonce and export them for children to inherit.</summary>
    public static (string SocketPath, byte[] Nonce) CreateServerEndpoint()
    {
        var socketPath = Environment.GetEnvironmentVariable(SocketEnv) is { Length: > 0 } s ? s : DefaultSocketPath;
        var dir = Path.GetDirectoryName(socketPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var nonce = Environment.GetEnvironmentVariable(TokenEnv) is { Length: > 0 } t
            ? Convert.FromHexString(t)
            : RandomNumberGenerator.GetBytes(16);
        return (socketPath, nonce);
    }

    /// <summary>Launcher side: the environment pairs to inject into every child so it can reach us.</summary>
    public static IReadOnlyDictionary<string, string> ChildEnvironment(string socketPath, byte[] nonce) =>
        new Dictionary<string, string>
        {
            [SocketEnv] = socketPath,
            [TokenEnv] = Convert.ToHexString(nonce),
        };

    /// <summary>
    /// Child side: send one control command to the launcher and return whether it was delivered. Returns
    /// false immediately when unsupervised (no env). Fully synchronous with a short timeout — callers are
    /// the taskbar's quit/restart hooks (UI thread), so it must not block long or deadlock; it runs the
    /// send off the thread pool and waits with a bounded budget.
    /// </summary>
    public static bool TrySend(Command command)
    {
        var socketPath = Environment.GetEnvironmentVariable(SocketEnv);
        var token = Environment.GetEnvironmentVariable(TokenEnv);
        if (string.IsNullOrEmpty(socketPath) || string.IsNullOrEmpty(token))
            return false;

        try
        {
            var nonce = Convert.FromHexString(token);
            // Task.Run detaches from any UI SynchronizationContext so the blocking wait can't deadlock.
            return Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await using var client = new UdsMessageClient(socketPath, nonce, "launcher");
                await client.ConnectAsync(cts.Token).ConfigureAwait(false);
                await client.RequestAsync(new[] { (byte)command }, cts.Token).ConfigureAwait(false);
                return true;
            }).GetAwaiter().GetResult();
        }
        catch
        {
            return false; // launcher gone / socket stale — caller falls back to in-process behaviour
        }
    }

    /// <summary>
    /// Child side: ask the launcher whether the desktop child is currently supervised/running.
    /// Returns <c>true</c>/<c>false</c> from the launcher's reply byte, or <c>null</c> when
    /// unsupervised (no control env) or the launcher is unreachable — so a caller can default its
    /// "Show Desktop" label without crashing. Mirrors <see cref="TrySend"/>'s bounded off-thread
    /// guard (deadlock-safe from any thread), but the outer <c>GetResult()</c> still blocks the
    /// caller for up to the timeout, so callers on the UI thread MUST invoke this off the UI thread.
    /// </summary>
    public static bool? QueryDesktopRunning()
    {
        var socketPath = Environment.GetEnvironmentVariable(SocketEnv);
        var token = Environment.GetEnvironmentVariable(TokenEnv);
        if (string.IsNullOrEmpty(socketPath) || string.IsNullOrEmpty(token))
            return null; // unsupervised (all-in-one / no launcher) — caller degrades to a default

        try
        {
            var nonce = Convert.FromHexString(token);
            return Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await using var client = new UdsMessageClient(socketPath, nonce, "launcher");
                await client.ConnectAsync(cts.Token).ConfigureAwait(false);
                var reply = await client.RequestAsync(new[] { (byte)Command.QueryDesktop }, cts.Token).ConfigureAwait(false);
                return (bool?)(reply.Length >= 1 && reply[0] == 1);
            }).GetAwaiter().GetResult();
        }
        catch
        {
            return null; // launcher gone / socket stale — treat as "unknown", caller shows the default label
        }
    }
}
