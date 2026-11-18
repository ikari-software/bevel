using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Bevel.ShellCore.Ipc;

/// <summary>
/// Thrown by a server's <c>Start()</c> when a LIVE listener already answers on the socket path. The
/// caller is a second instance and must stand down — never unlink the path out from under the
/// incumbent (bevel-wio0).
/// </summary>
public sealed class UdsSocketBusyException(string socketPath)
    : IOException($"A live server is already listening on '{socketPath}'; standing down instead of hijacking it.")
{
    public string SocketPath { get; } = socketPath;
}

/// <summary>
/// The one correct way to take and give back a fixed-path Unix-domain socket (bevel-wio0). Every
/// Bevel server on a well-known path (<c>core.sock</c>, <c>launcher.sock</c>, <c>bevelctl.sock</c>,
/// the per-pid filer sockets) goes through here so they share one policy:
///
/// <list type="bullet">
/// <item><b>Probe-then-bind.</b> A path that already exists is <i>connected to</i> first. A live
/// answer means another instance owns it → <see cref="UdsSocketBusyException"/>, and the file is left
/// alone. Only <c>ECONNREFUSED</c> / <c>ENOENT</c> — a socket whose owner is gone — is reclaimed by
/// unlinking and rebinding, so a crash never wedges the next start. The old code unlinked
/// unconditionally, which let a second core silently take the path over and orphan the first (the
/// five-process settings.db tug-of-war of 2026-09-29).</item>
/// <item><b>Owner-only unlink.</b> On release the listener is closed FIRST, then the path is probed
/// again: if anybody still answers, someone else has since bound it and it is theirs — leave it. A
/// stray/old shell exiting can therefore no longer delete the current shell's live socket. No inode
/// bookkeeping is needed: once our listener is closed, the only thing that can answer on the path is
/// a different live listener.</item>
/// </list>
///
/// <para>Races. Two processes reclaiming the same STALE path at the same instant can still cross
/// (probe → unlink → bind is not atomic); that window is microseconds and only opens for a
/// simultaneous start on a dead path, which the launcher's single-instance lock rules out for the
/// shell's own sockets.</para>
/// </summary>
public static class UdsSocketClaim
{
    /// <summary>
    /// True when a live listener answers on <paramref name="socketPath"/>. False when the path is
    /// absent, its owner is gone (<c>ENOENT</c> / <c>ECONNREFUSED</c>) or it is not a socket at all (<c>ENOTSOCK</c>, a plain file
    /// squatting on the path). Any OTHER connect failure
    /// (backlog full, permission, reset …) is reported as serving: mis-reading a live socket as dead
    /// is the hijack this type exists to prevent, so the safe default is "busy".
    /// </summary>
    /// <summary>What sits at a would-be socket path (bevel-9790). Only a <see cref="Socket"/> inode is ever
    /// reclaimable; <see cref="Other"/> (regular file, directory, symlink, FIFO, or anything this cannot
    /// positively identify) must be left untouched — socket paths can come from environment variables
    /// (<c>BEVEL_CORE_SOCKET</c> …), so a mistyped value may point at a user's file.</summary>
    public enum PathKind { Absent, Socket, Other }

    /// <summary>Classifies <paramref name="path"/> WITHOUT following symlinks. Unix: <c>lstat(2)</c> and
    /// <c>S_IFMT == S_IFSOCK</c> (.NET's FileInfo cannot tell a socket from a regular file — both read
    /// <c>Normal</c>). The <c>st_mode</c> offset is per platform (macOS 4, Linux x64 24, Linux arm64 16);
    /// an unknown platform or a failed lstat reads as <see cref="PathKind.Other"/> — fail CLOSED, never
    /// delete on doubt. Windows: an AF_UNIX socket is a reparse point that is not a symlink/junction
    /// (<c>LinkTarget</c> null); a plain file has no reparse attribute.</summary>
    public static PathKind Classify(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new FileInfo(path);
            if (Directory.Exists(path)) return PathKind.Other;
            if (!info.Exists && info.LinkTarget is null) return PathKind.Absent;
            if (info.LinkTarget is not null) return PathKind.Other;
            return info.Attributes.HasFlag(FileAttributes.ReparsePoint) ? PathKind.Socket : PathKind.Other;
        }

        var buf = new byte[512];
        int rc;
        try { rc = lstat(path, buf); }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return PathKind.Other;
        }
        if (rc != 0)
            return Marshal.GetLastPInvokeError() == 2 /* ENOENT */ ? PathKind.Absent : PathKind.Other;

        uint mode;
        if (OperatingSystem.IsMacOS()) mode = BitConverter.ToUInt16(buf, 4);
        else if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64) mode = BitConverter.ToUInt32(buf, 24);
        else if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64) mode = BitConverter.ToUInt32(buf, 16);
        else return PathKind.Other;
        return (mode & 0xF000) == 0xC000 ? PathKind.Socket : PathKind.Other; // S_IFMT / S_IFSOCK
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "lstat")]
    private static extern int lstat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buf);

    public static bool IsServing(string socketPath)
    {
        if (!File.Exists(socketPath))
            return false;

        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            // A UDS connect is local and completes immediately; no network timeout applies.
            probe.Connect(new UnixDomainSocketEndPoint(socketPath));
            return true;
        }
        catch (SocketException ex) when (IsOwnerGone(ex))
        {
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    /// <summary>
    /// Probe-then-bind: returns a bound, listening socket on <paramref name="socketPath"/>, reclaiming a
    /// stale file; throws <see cref="UdsSocketBusyException"/> when a live listener already answers.
    /// Directory creation / permissions are the caller's concern (they differ per socket).
    /// </summary>
    public static Socket BindListener(string socketPath, int backlog)
    {
        if (IsServing(socketPath))
            throw new UdsSocketBusyException(socketPath);

        switch (Classify(socketPath))
        {
            case PathKind.Socket:
                File.Delete(socketPath); // a socket inode whose owner is gone — reclaim the path
                break;
            case PathKind.Other:
                throw new IOException(
                    $"'{socketPath}' exists but is not a socket (a regular file, directory or symlink), so it was left untouched. " +
                    "Check the BEVEL_*_SOCKET / BEVEL_FILER_DIR environment, or remove it yourself.");
        }

        if (OperatingSystem.IsWindows())
        {
            // Managed bind. Note .NET's Socket.Dispose() File.Delete()s a bound UDS path by itself
            // (BoundFileName), so on Windows the owner-only guarantee of ReleaseListener does not hold;
            // the launcher's instance lock is the guard there.
            var managed = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                managed.Bind(new UnixDomainSocketEndPoint(socketPath));
                managed.Listen(backlog);
            }
            catch
            {
                managed.Dispose();
                throw;
            }
            return managed;
        }

        return BindRawUnix(socketPath, backlog);
    }

    /// <summary>
    /// socket/bind/listen through libc, wrapped in a <see cref="Socket"/> that never learns the path.
    /// A socket bound by <see cref="Socket.Bind"/> remembers its file and <see cref="Socket.Dispose()"/>
    /// deletes it unconditionally — by PATH, whoever owns it now — which is the exact hijack-on-exit
    /// this type prevents. Wrapping a raw fd keeps .NET's accept machinery and leaves the unlink
    /// decision to <see cref="ReleaseListener"/>. FD_CLOEXEC is set so the listener never leaks into a
    /// spawned child (the core forks the Swift helper), which would keep a dead core's path "live".
    /// </summary>
    private static Socket BindRawUnix(string socketPath, int backlog)
    {
        var address = new UnixDomainSocketEndPoint(socketPath).Serialize();
        var sockaddr = address.Buffer.Span[..address.Size].ToArray(); // platform sockaddr_un layout, by .NET itself

        var fd = socket(AF_UNIX, SOCK_STREAM, 0);
        if (fd < 0)
            throw new IOException($"socket() failed for '{socketPath}': errno {Marshal.GetLastPInvokeError()}");
        try
        {
            if (fcntl(fd, F_SETFD, FD_CLOEXEC) != 0)
                throw new IOException($"fcntl(FD_CLOEXEC) failed for '{socketPath}': errno {Marshal.GetLastPInvokeError()}");
            if (bind(fd, sockaddr, (uint)sockaddr.Length) != 0)
                throw new IOException($"bind() failed for '{socketPath}': errno {Marshal.GetLastPInvokeError()}");
            if (listen(fd, backlog) != 0)
                throw new IOException($"listen() failed for '{socketPath}': errno {Marshal.GetLastPInvokeError()}");
        }
        catch
        {
            close(fd);
            throw;
        }
        return new Socket(new SafeSocketHandle((IntPtr)fd, ownsHandle: true));
    }

    private const int AF_UNIX = 1;     // Linux + macOS
    private const int SOCK_STREAM = 1; // Linux + macOS
    private const int F_SETFD = 2;
    private const int FD_CLOEXEC = 1;

    [DllImport("libc", SetLastError = true)] private static extern int socket(int domain, int type, int protocol);
    [DllImport("libc", SetLastError = true)] private static extern int bind(int fd, byte[] addr, uint addrlen);
    [DllImport("libc", SetLastError = true)] private static extern int listen(int fd, int backlog);
    [DllImport("libc", SetLastError = true)] private static extern int fcntl(int fd, int cmd, int arg);
    [DllImport("libc")] private static extern int close(int fd);

    /// <summary>
    /// Closes <paramref name="listener"/> and unlinks <paramref name="socketPath"/> ONLY if nobody else
    /// answers there afterwards. A null listener (bind never happened, e.g. a stood-down second
    /// instance) never touches the file — the path was never ours.
    /// </summary>
    public static void ReleaseListener(Socket? listener, string socketPath)
    {
        if (listener is null)
            return;

        listener.Dispose();
        try
        {
            if (!IsServing(socketPath) && Classify(socketPath) == PathKind.Socket)
                File.Delete(socketPath);
        }
        catch
        {
            // Best effort — a leftover stale file is reclaimed by the next BindListener.
        }
    }

    private static bool IsOwnerGone(SocketException ex) =>
        ex.SocketErrorCode is SocketError.ConnectionRefused // socket file present, no listener behind it
            or SocketError.AddressNotAvailable               // ENOENT: unlinked between Exists and connect
            or SocketError.NotSocket;                        // ENOTSOCK / WSAENOTSOCK: a plain file squatting on the path (bevel-9790)
}
