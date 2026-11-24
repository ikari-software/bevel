using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Bevel.ShellCore.Ipc;

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
        Bevel.Core.BevelConfigDir.Path, "bevelctl.sock");

    /// <summary>bevel-wcqr: the per-session nonce file that sits NEXT TO the socket
    /// (<c>&lt;socket&gt;.token</c>, 0600). The server writes it at <c>Start()</c>; clients read it to
    /// build the HMAC hello. Same pattern as the shell-core <c>core.token</c>.</summary>
    public static string TokenPathFor(string socketPath) => socketPath + ".token";

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
    /// <summary>Capability string the bevelctl client presents in its hello frame.</summary>
    public const string ClientCapability = "bevelctl";

    private readonly string _path;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<CommandResult>> _handler;
    private Socket? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private byte[]? _nonce;
    private string? _tokenPath;
    private bool _bound;

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
        // bevel-wcqr: mint the per-session nonce and drop it as hex in a 0600 token file NEXT TO
        // the socket BEFORE binding, so a client that connects the moment the listener appears
        // never reads a stale token. Only when the path is free — a live incumbent owns both files,
        // and this instance must not clobber them before standing down.
        if (!UdsSocketClaim.IsServing(_path))
        {
            _nonce = RandomNumberGenerator.GetBytes(16);
            _tokenPath = AutomationSocket.TokenPathFor(_path);
            File.WriteAllText(_tokenPath, Convert.ToHexString(_nonce));
            TrySetOwnerOnly(_tokenPath);
        }
        // Probe-then-bind (bevel-wio0): a stale socket from a crashed run is reclaimed, a LIVE one
        // (another shell instance's bevelctl endpoint) throws UdsSocketBusyException so this instance
        // stands down instead of unlinking the incumbent's socket out from under it.
        _listener = UdsSocketClaim.BindListener(_path, backlog: 16);
        _bound = true;
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
            // bevel-wcqr: the FIRST frame must be a valid HMAC hello keyed by this session's token.
            // No hello / wrong nonce → nothing runs; answer "not authorized" and close.
            var hello = await AutomationSocket.ReadFrameAsync(stream, ct);
            if (_nonce is null || Handshake.ValidateHello(_nonce, hello) is null)
            {
                await AutomationSocket.WriteFrameAsync(stream, AutomationSocket.EncodeResult(
                    new CommandResult(ExitCodes.ShellNotRunning, "bevel: not authorized")), ct);
                return;
            }

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
        // Owner-only unlink: closes the listener, then removes the path only if nobody else answers
        // there; a never-bound (stood-down) instance leaves the file alone.
        UdsSocketClaim.ReleaseListener(_listener, _path);
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; } catch { /* shutting down */ }
        }
        _cts?.Dispose();
        // bevel-wcqr: take the token file with us only if THIS instance bound the socket (a
        // stood-down instance never wrote one and must not delete the incumbent's).
        if (_bound && _tokenPath is not null)
        {
            try { File.Delete(_tokenPath); } catch { /* best effort */ }
        }
    }
}

/// <summary>The bevelctl client side: connect, prove the session token, send argv, read back the result.</summary>
public static class AutomationSocketClient
{
    public static async Task<CommandResult> SendAsync(string path, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        // bevel-wcqr: read the per-session token the server dropped next to the socket. A missing
        // token means no shell is running (or it never bound), so surface that as a connect failure
        // — bevelctl prints its "shell isn't running" line for SocketException.
        var tokenPath = AutomationSocket.TokenPathFor(path);
        if (!File.Exists(tokenPath))
            throw new SocketException((int)SocketError.ConnectionRefused);
        var nonce = Convert.FromHexString(File.ReadAllText(tokenPath).Trim());

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct);
        await using var stream = new NetworkStream(socket, ownsSocket: true);

        await AutomationSocket.WriteFrameAsync(stream,
            Handshake.BuildHelloPayload(nonce, AutomationSocketServer.ClientCapability), ct);
        await AutomationSocket.WriteFrameAsync(stream, AutomationSocket.EncodeArgs(args), ct);
        var response = await AutomationSocket.ReadFrameAsync(stream, ct);
        return AutomationSocket.DecodeResult(response);
    }
}
