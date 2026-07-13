using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Bevel.ShellCore.Ipc;

/// <summary>
/// The "core" side of the transport: a Unix-domain-socket server that fans framed state out
/// to any number of UI processes and answers their requests. Payload-agnostic — it moves
/// opaque <c>byte[]</c>; serialization is the caller's concern.
///
/// <para>Concurrency model. Every outbound frame for a given client (broadcasts, the
/// connect snapshot, and request responses) is funnelled through that client's single-writer
/// <see cref="Channel{T}"/>, drained by one pump task. That is what guarantees frames are
/// never interleaved and are delivered in enqueue order, without a lock on the hot path.
/// Inbound requests are handled concurrently — the request handler may be slow, so responses
/// are correlated by id rather than by arrival order.</para>
///
/// <para>Dead clients self-heal: a failed write completes the pump, which removes the client
/// from the set and disposes its socket, so <see cref="Broadcast"/> never throws on a peer
/// that has gone away.</para>
/// </summary>
public sealed class UdsMessageServer : IAsyncDisposable
{
    private readonly string _socketPath;
    private readonly byte[] _nonce;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]>> _onRequest;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, ClientConnection> _clients = new();

    private Socket? _listener;
    private Task? _acceptLoop;
    private int _disposed;

    /// <summary>
    /// Raised once per client, right after a successful handshake and before any request from
    /// that client is processed. The handler is given a <c>sendToThisClient</c> delegate that
    /// enqueues a Broadcast-kind frame to ONLY the new client — the hook for pushing an initial
    /// state snapshot. The snapshot is enqueued ahead of the client's request loop and of any
    /// later <see cref="Broadcast"/>, so a just-connected UI sees "full state, then deltas".
    /// </summary>
    public event Action<Func<ReadOnlyMemory<byte>, ValueTask>>? ClientConnected;

    /// <param name="socketPath">Filesystem path to bind the listening UDS to.</param>
    /// <param name="nonce">Per-session shared secret; the HMAC key both sides must agree on.</param>
    /// <param name="onRequest">
    /// Invoked once per client <see cref="FrameKind.Request"/>; its returned bytes are sent
    /// back as a <see cref="FrameKind.Response"/> carrying the request's correlation id.
    /// </param>
    public UdsMessageServer(
        string socketPath,
        byte[] nonce,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]>> onRequest)
    {
        _socketPath = socketPath ?? throw new ArgumentNullException(nameof(socketPath));
        _nonce = nonce ?? throw new ArgumentNullException(nameof(nonce));
        _onRequest = onRequest ?? throw new ArgumentNullException(nameof(onRequest));
    }

    /// <summary>Number of currently-authenticated clients. Exposed mainly for tests/diagnostics.</summary>
    public int ClientCount => _clients.Count;

    /// <summary>
    /// Binds the UDS path and starts accepting. A stale socket file from a previous crash is
    /// deleted first (a bound path that still exists on disk makes <c>Bind</c> fail with
    /// EADDRINUSE). The parent directory is created 0700 — the socket is an authenticated
    /// control channel, so it must not be reachable by other users on the box.
    /// </summary>
    public void Start()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_socketPath));
        if (!string.IsNullOrEmpty(dir))
        {
            // 0700 on Unix: the socket is an authenticated control channel, keep it owner-only.
            // On Windows (where UDS exists but POSIX modes don't) fall back to a plain create.
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(dir);
            else
                Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        if (File.Exists(_socketPath))
            File.Delete(_socketPath);

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
        _listener.Listen(128);
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Sends a Broadcast frame (correlation id 0) to every currently-authenticated client.
    /// The payload is copied once and the same immutable buffer is shared across all clients'
    /// write queues, so a large snapshot isn't duplicated per subscriber. Enqueue-only and
    /// non-throwing: a client whose queue is already closed (mid-teardown) is simply skipped.
    /// </summary>
    public void Broadcast(ReadOnlyMemory<byte> payload)
    {
        var buffer = payload.ToArray(); // detach from the caller's buffer; frames are read-only downstream
        foreach (var client in _clients.Values)
            client.Enqueue(FrameKind.Broadcast, correlationId: 0, buffer);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener!.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }

            // Fire-and-forget per client; HandleClientAsync owns the socket's whole lifecycle.
            _ = HandleClientAsync(socket, ct);
        }
    }

    private async Task HandleClientAsync(Socket socket, CancellationToken ct)
    {
        var stream = new NetworkStream(socket, ownsSocket: true);
        ClientConnection? conn = null;
        try
        {
            // A client MUST authenticate before any of its frames are honoured. A hello that
            // is the wrong kind or fails the HMAC gets a Reject and the connection is dropped.
            var hello = await Framing.ReadFrameAsync(stream, ct).ConfigureAwait(false);
            if (hello.Kind != FrameKind.HandshakeHello ||
                Handshake.ValidateHello(_nonce, hello.Payload) is null)
            {
                await Framing.WriteFrameAsync(stream, FrameKind.HandshakeReject, 0, ReadOnlyMemory<byte>.Empty, ct)
                    .ConfigureAwait(false);
                stream.Dispose();
                return;
            }

            await Framing.WriteFrameAsync(stream, FrameKind.HandshakeOk, 0, ReadOnlyMemory<byte>.Empty, ct)
                .ConfigureAwait(false);

            conn = new ClientConnection(stream);
            conn.StartPump(this, ct);

            // Snapshot BEFORE the client is visible to Broadcast: the ClientConnected handler
            // enqueues its snapshot first, then registering the client makes later broadcasts
            // land behind it — "full state, then deltas", no interleave, no lost first push.
            var handler = ClientConnected;
            if (handler is not null)
            {
                var target = conn;
                handler(payload =>
                {
                    target.Enqueue(FrameKind.Broadcast, correlationId: 0, payload.ToArray());
                    return ValueTask.CompletedTask;
                });
            }

            _clients[conn.Id] = conn;

            // Request loop. Each request is dispatched concurrently so a slow handler can't
            // head-of-line block the others; the response carries the request's correlation id.
            while (!ct.IsCancellationRequested)
            {
                var frame = await Framing.ReadFrameAsync(stream, ct).ConfigureAwait(false);
                if (frame.Kind == FrameKind.Request)
                    _ = ProcessRequestAsync(conn, frame, ct);
                // Any other kind from a client is not part of the protocol; ignore it.
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or OperationCanceledException
                                       or ObjectDisposedException or InvalidDataException)
        {
            // Normal disconnect or a protocol/IO fault on this one client — never fatal to the
            // server. Fall through to cleanup.
        }
        finally
        {
            if (conn is not null)
                RemoveClient(conn);
            else
                stream.Dispose();
        }
    }

    private async Task ProcessRequestAsync(ClientConnection conn, Frame frame, CancellationToken ct)
    {
        byte[] response;
        try
        {
            response = await _onRequest(frame.Payload, ct).ConfigureAwait(false);
        }
        catch
        {
            // The handler threw. We still owe the client a response so its RequestAsync doesn't
            // hang forever; reply with an empty payload. (Surfacing a typed error frame is a
            // future extension — the caller can encode failures in its own payload today.)
            response = Array.Empty<byte>();
        }

        conn.Enqueue(FrameKind.Response, frame.CorrelationId, response);
    }

    private void RemoveClient(ClientConnection conn)
    {
        _clients.TryRemove(conn.Id, out _);
        conn.Dispose();
    }

    /// <summary>
    /// Stops accepting, tears down every client, and deletes the socket file so the next
    /// <see cref="Start"/> (this process or the next) starts from a clean path.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();
        _listener?.Dispose(); // unblocks AcceptAsync

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch { /* accept loop swallows its own shutdown faults */ }
        }

        foreach (var conn in _clients.Values)
            conn.Dispose();
        _clients.Clear();

        try { if (File.Exists(_socketPath)) File.Delete(_socketPath); }
        catch { /* best effort — a leftover file is handled by the next Start() */ }

        _cts.Dispose();
    }

    /// <summary>
    /// One connected, authenticated client. Owns the socket stream and a single-writer outbound
    /// channel; the pump task is the ONLY writer to the stream, which is how frame ordering and
    /// non-interleaving are enforced.
    /// </summary>
    private sealed class ClientConnection(NetworkStream stream) : IDisposable
    {
        private readonly Channel<(FrameKind Kind, uint Corr, byte[] Payload)> _outbound =
            Channel.CreateUnbounded<(FrameKind, uint, byte[])>(new UnboundedChannelOptions
            {
                SingleReader = true, // exactly one pump drains it
            });

        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>Queues one outbound frame. Returns quietly if the queue is already closed.</summary>
        public void Enqueue(FrameKind kind, uint correlationId, byte[] payload)
            => _outbound.Writer.TryWrite((kind, correlationId, payload));

        public void StartPump(UdsMessageServer server, CancellationToken ct)
            => _ = PumpAsync(server, ct);

        private async Task PumpAsync(UdsMessageServer server, CancellationToken ct)
        {
            try
            {
                await foreach (var f in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    await Framing.WriteFrameAsync(stream, f.Kind, f.Corr, f.Payload, ct).ConfigureAwait(false);
            }
            catch
            {
                // The write failed (client vanished) or we're shutting down. Drop this client so
                // the server's set doesn't grow without bound and Broadcast stays healthy.
                server.RemoveClient(this);
            }
        }

        public void Dispose()
        {
            _outbound.Writer.TryComplete(); // stop the pump
            stream.Dispose();
        }
    }
}
