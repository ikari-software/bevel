using System.Security.Cryptography;

namespace Bevel.App.ShellCore;

/// <summary>
/// Resolves where the shell-core listens and the shared HMAC nonce that authenticates UI clients to
/// it (bevel-gww.3). A supervisor (bevel-gww.4) that launches the core + UI processes passes both via
/// env (<c>BEVEL_CORE_SOCKET</c> / <c>BEVEL_CORE_TOKEN</c>); absent that, the core mints a nonce and
/// drops it in a 0600 token file under a 0700 dir, which clients read back — so a bare
/// <c>--role=core</c> + <c>--role=taskbar</c> pair works without a supervisor too. Mirrors the Swift
/// helper's nonce-via-env + hardened-socket-dir handshake.
/// </summary>
public static class ShellCoreEndpoint
{
    private const string SocketEnv = "BEVEL_CORE_SOCKET";
    private const string TokenEnv = "BEVEL_CORE_TOKEN";

    // Routed through the shared runtime dir so peers agree by construction, and off %TEMP% on Windows
    // (bevel-ncfp.2 / U2). macOS/Linux paths are byte-identical to before.
    private static string Dir => BevelRuntimeDir.CoreDir;
    private static string TokenPath => Path.Combine(Dir, "core.token");

    public static string SocketPath => BevelRuntimeDir.GuardSocketPath(
        Environment.GetEnvironmentVariable(SocketEnv) is { Length: > 0 } s ? s : Path.Combine(Dir, "core.sock"));

    /// <summary>Core side: resolve the socket path + nonce, minting and persisting a nonce (0600 under
    /// a 0700 dir) when the supervisor didn't provide one. UI clients read it back via <see cref="ReadNonce"/>.</summary>
    public static (string SocketPath, byte[] Nonce) ForServer()
    {
        Directory.CreateDirectory(Dir);
        Harden(Dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        if (Environment.GetEnvironmentVariable(TokenEnv) is { Length: > 0 } env)
            return (SocketPath, Convert.FromHexString(env));

        var nonce = RandomNumberGenerator.GetBytes(16);
        File.WriteAllText(TokenPath, Convert.ToHexString(nonce));
        Harden(TokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return (SocketPath, nonce);
    }

    /// <summary>UI side: the nonce, from env (supervisor) or the token file (standalone). Waits briefly
    /// for the file so a UI process that started before the core still connects.</summary>
    public static byte[] ReadNonce()
    {
        if (Environment.GetEnvironmentVariable(TokenEnv) is { Length: > 0 } env)
            return Convert.FromHexString(env);

        for (var i = 0; i < 50; i++) // ~5s: the supervisor starts the core first, but tolerate a race
        {
            if (File.Exists(TokenPath))
                return Convert.FromHexString(File.ReadAllText(TokenPath).Trim());
            Thread.Sleep(100);
        }
        throw new InvalidOperationException(
            $"shell-core token not found at {TokenPath} — is the --role=core process running?");
    }

    /// <summary>UI side: a ready-to-connect client (connects lazily on first use).</summary>
    public static ShellCoreClient CreateClient(string capability = "shellcore") =>
        new(SocketPath, ReadNonce(), capability);

    private static void Harden(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, mode);
    }
}
