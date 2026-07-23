using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace Bevel.Interop.Cli;

/// <summary>
/// The bevelctl ⇄ core wire transport (08-os-interop.md §3.2): a per-user Unix-domain socket over
/// which the CLI ships its raw argv and gets back an exit code + output. Framing is length-prefixed
/// binary (AOT-trivial, and safe for output containing newlines). The socket is chmod 0600 (INT-10),
/// and lives with the core process — the one that hosts <see cref="IShellAutomation"/>.
/// </summary>
public static class AutomationSocket
{
    /// <summary>Default per-user socket path (alongside the settings db under ~/.config/bevel).</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "bevel", "bevelctl.sock");

    private const int MaxFrame = 16 * 1024 * 1024;

    // ── Payload codecs (request = argv, response = exit code + output) ────────────────────────

    internal static byte[] EncodeArgs(IReadOnlyList<string> args)
    {
        using var ms = new MemoryStream();
        Span<byte> i4 = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(i4, args.Count);
        ms.Write(i4);
        foreach (var a in args)
        {
            var b = Encoding.UTF8.GetBytes(a);
            BinaryPrimitives.WriteInt32BigEndian(i4, b.Length);
            ms.Write(i4);
            ms.Write(b);
        }
        return ms.ToArray();
    }

    internal static IReadOnlyList<string> DecodeArgs(byte[] payload)
    {
        var pos = 0;
        int ReadInt() { var v = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(pos)); pos += 4; return v; }

        var count = ReadInt();
        if (count < 0 || count > 4096) throw new IOException("bad argv count");
        var list = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var len = ReadInt();
            if (len < 0 || pos + len > payload.Length) throw new IOException("bad argv frame");
            list.Add(Encoding.UTF8.GetString(payload, pos, len));
            pos += len;
        }
        return list;
    }

    internal static byte[] EncodeResult(CommandResult result)
    {
        var output = Encoding.UTF8.GetBytes(result.Output);
        var buf = new byte[4 + output.Length];
        BinaryPrimitives.WriteInt32BigEndian(buf, result.ExitCode);
        output.CopyTo(buf, 4);
        return buf;
    }

    internal static CommandResult DecodeResult(byte[] payload) =>
        new(BinaryPrimitives.ReadInt32BigEndian(payload), Encoding.UTF8.GetString(payload, 4, payload.Length - 4));

    // ── Length-prefixed framing ──────────────────────────────────────────────────────────────

    internal static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken ct)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    internal static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || length > MaxFrame) throw new IOException($"bad frame length {length}");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct);
        return payload;
    }
}

/// <summary>
/// Listens on the bevelctl socket and dispatches each connection's argv through an injected handler
/// (the core wires this to BevelCtlParser + <see cref="AutomationCommandRouter"/>). Kept
/// handler-agnostic so it round-trips in tests without DI or a running shell.
/// </summary>
public sealed class AutomationSocketServer : IAsyncDisposable
{
    private readonly string _path;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<CommandResult>> _handler;
    private Socket? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public AutomationSocketServer(string path, Func<IReadOnlyList<string>, CancellationToken, Task<CommandResult>> handler)
    {
        _path = path;
        _handler = handler;
    }

    public void Start()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            // 0700 the dir: even in the brief window before the socket itself is chmod 0600, a
            // non-traversable parent keeps other local users off it regardless of umask (INT-10 review).
            TrySetDirOwnerOnly(dir);
        }
        if (File.Exists(_path)) File.Delete(_path);   // clear a stale socket from a crashed run

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_path));
        _listener.Listen(16);
        TrySetOwnerOnly(_path);

        _cts = new CancellationTokenSource();
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket connection;
            try { connection = await _listener!.AcceptAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { break; }
            _ = HandleConnectionAsync(connection, ct);
        }
    }

    private async Task HandleConnectionAsync(Socket connection, CancellationToken ct)
    {
        try
        {
            await using var stream = new NetworkStream(connection, ownsSocket: true);
            var request = await AutomationSocket.ReadFrameAsync(stream, ct);
            var args = AutomationSocket.DecodeArgs(request);

            CommandResult result;
            try { result = await _handler(args, ct); }
            catch (Exception ex) { result = new CommandResult(ExitCodes.NotFound, ex.Message); }

            await AutomationSocket.WriteFrameAsync(stream, AutomationSocket.EncodeResult(result), ct);
        }
        catch { /* connection-level error — drop it, keep serving */ }
    }

    private static void TrySetOwnerOnly(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { /* best effort; the socket dir is already user-scoped */ }
    }

    private static void TrySetDirOwnerOnly(string dir)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch { /* best effort */ }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _listener?.Dispose();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; } catch { /* shutting down */ }
        }
        try { if (File.Exists(_path)) File.Delete(_path); } catch { /* best effort */ }
        _cts?.Dispose();
    }
}

/// <summary>The bevelctl client side: connect, send argv, read back the result.</summary>
public static class AutomationSocketClient
{
    public static async Task<CommandResult> SendAsync(string path, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct);
        await using var stream = new NetworkStream(socket, ownsSocket: true);

        await AutomationSocket.WriteFrameAsync(stream, AutomationSocket.EncodeArgs(args), ct);
        var response = await AutomationSocket.ReadFrameAsync(stream, ct);
        return AutomationSocket.DecodeResult(response);
    }
}
