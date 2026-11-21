using System.Text;

namespace Bevel.App;

/// <summary>
/// Single source of truth for Bevel's per-user runtime directory — the parent of the UDS sockets
/// (<c>bevel-core</c>, <c>bevel-filers</c>) and the memory-mapped icon pool. Every consumer routes
/// through here so the peer processes agree on the paths by construction (bevel-ncfp.2 / U2).
///
/// <para>On Windows the root is <c>%LOCALAPPDATA%\bevel</c> rather than <c>%TEMP%</c>: it inherits the
/// user profile's user-only ACL (the "0700 equivalent" — no explicit DACL needed) and, unlike
/// <c>%TEMP%</c>, is not subject to Storage Sense cleanup, which can delete a live AF_UNIX
/// reparse-point socket mid-session. Everywhere else the historical <see cref="Path.GetTempPath"/>
/// location is kept verbatim, so macOS/Linux paths are byte-identical to before.</para>
/// </summary>
internal static class BevelRuntimeDir
{
    /// <summary>The per-user root under which all runtime subdirs live.</summary>
    public static string Root => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "bevel")
        : Path.GetTempPath();

    /// <summary>Shell-core socket + token + icon pool live here (peers must agree).</summary>
    public static string CoreDir => Path.Combine(Root, "bevel-core");

    /// <summary>Taskbar↔Filer control-channel rendezvous dir.</summary>
    public static string FilersDir => Path.Combine(Root, "bevel-filers");

    /// <summary>Per-role heartbeat + health-status files the launcher watches (bevel-9h7n).
    /// Not a socket dir — no sun_path cap. Cleared at launcher boot like the quit marker.</summary>
    public static string HealthDir => Path.Combine(Root, "bevel-health");

    /// <summary>Asserts a UDS path fits the <c>sun_path</c> 108-byte limit (Windows and Linux both cap
    /// there). A bind past it fails obscurely, so fail loudly with the offending path instead
    /// (bevel-ncfp.2 / U2). Returns the path unchanged for fluent use.</summary>
    public static string GuardSocketPath(string path)
    {
        var bytes = Encoding.UTF8.GetByteCount(path);
        if (bytes >= 108)
            throw new InvalidOperationException(
                $"UDS socket path exceeds the {108}-byte sun_path limit ({bytes} bytes): '{path}'. " +
                "Shorten the runtime dir (BevelRuntimeDir.Root) or the socket name.");
        return path;
    }

    /// <summary>
    /// Asserts a socket path is inside Bevel's own runtime directory. Only paths under Root are
    /// reclaimed on bind failure; paths outside (e.g., from a mistyped env var) are rejected to
    /// prevent data loss (bevel-zcgd). Returns the path unchanged for fluent use.
    /// </summary>
    public static string GuardSocketPathInRuntimeDir(string path)
    {
        var normalizedPath = Path.GetFullPath(path);
        var normalizedRoot = Path.GetFullPath(Root);

        if (!normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar) &&
            !normalizedPath.StartsWith(normalizedRoot))
        {
            throw new InvalidOperationException(
                $"Socket path '{path}' is outside Bevel's runtime directory '{Root}'. " +
                "Socket paths must be under the runtime dir to be reclaimed. " +
                "Check environment variables BEVEL_CORE_SOCKET, BEVEL_LAUNCHER_SOCKET, BEVEL_FILER_DIR.");
        }
        return path;
    }
}
