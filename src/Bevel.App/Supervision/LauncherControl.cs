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

    /// <summary>Control verbs. One byte on the wire; the reply is a single 0x01 ack.</summary>
    public enum Command : byte
    {
        RestartAll = 1,   // kill+respawn everything → whole shell on the latest binary
        RestartCore = 2,  // swap just the shell-core owner (update-on-demand)
        Quit = 3,         // tear the whole shell down
    }

    private static string DefaultSocketPath =>
        Path.Combine(Path.GetTempPath(), "bevel-core", "launcher.sock");

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
}
