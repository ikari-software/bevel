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
/// the per-pid explorer sockets) goes through here so they share one policy:
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
    /// absent or its owner is gone (<c>ENOENT</c> / <c>ECONNREFUSED</c>). Any OTHER connect failure
    /// (backlog full, permission, reset …) is reported as serving: mis-reading a live socket as dead
    /// is the hijack this type exists to prevent, so the safe default is "busy".
    /// </summary>
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

        if (File.Exists(socketPath))
            File.Delete(socketPath); // owner is gone — reclaim the path

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
            if (!IsServing(socketPath) && File.Exists(socketPath))
                File.Delete(socketPath);
        }
        catch
        {
            // Best effort — a leftover stale file is reclaimed by the next BindListener.
        }
    }

    private static bool IsOwnerGone(SocketException ex) =>
        ex.SocketErrorCode is SocketError.ConnectionRefused // socket file present, no listener behind it
            or SocketError.AddressNotAvailable;              // ENOENT: unlinked between Exists and connect
}
