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
    private readonly Func<Guid, ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]>> _onRequest;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, ClientConnection> _clients = new();

    private Socket? _listener;
    private Task? _acceptLoop;
    private int _disposed;

    /// <summary>
    /// Raised once per client, right after a successful handshake and before any request from
    /// that client is processed. The handler is given a <c>sendToThisClient</c> delegate that
    /// enqueues a Broadcast-kind frame to ONLY the new client — a transport-level hook. The
    /// shell-core protocol no longer pushes state here: its session layer gates the snapshot behind a
    /// Hello REQUEST (bevel-4zfs) because push-on-connect is exactly the race that ate snapshots
    /// when a client's subscribers attached late. The snapshot is enqueued ahead of the request loop.
    /// later <see cref="Broadcast"/>, so a just-connected UI sees "full state, then deltas".
    /// </summary>
    public event Action<Func<ReadOnlyMemory<byte>, ValueTask>>? ClientConnected;

    /// <param name="socketPath">Filesystem path to bind the listening UDS to.</param>
    /// <param name="nonce">Per-session shared secret; the HMAC key both sides must agree on.</param>
    /// <param name="onRequest">
    /// Invoked once per client <see cref="FrameKind.Request"/>, with the CLIENT ID of the requester; its
    /// returned bytes are sent back as a <see cref="FrameKind.Response"/> carrying the request's
    /// correlation id. The id also targets <see cref="SendToClient"/> — the per-client push the session
    /// protocol needs (bevel-4zfs).
    /// </param>
    public UdsMessageServer(
        string socketPath,
        byte[] nonce,
        Func<Guid, ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]>> onRequest)
    {
        _socketPath = socketPath ?? throw new ArgumentNullException(nameof(socketPath));
        _nonce = nonce ?? throw new ArgumentNullException(nameof(nonce));
        _onRequest = onRequest ?? throw new ArgumentNullException(nameof(onRequest));
    }

    /// <summary>Number of currently-authenticated clients. Exposed mainly for tests/diagnostics.</summary>
    public int ClientCount => _clients.Count;

    /// <summary>The ids of currently-authenticated clients — the targets <see cref="SendToClient"/> and
    /// the server's per-client session stamping iterate (bevel-4zfs).</summary>
    public IReadOnlyCollection<Guid> ClientIds => _clients.Keys.ToArray();

    /// <summary>
    /// The handshake capability that means "just tell me you're here". A hello presenting it gets
    /// <see cref="FrameKind.HandshakeOk"/> and is then closed WITHOUT being registered as a client —
    /// no <see cref="ClientConnected"/>, no state snapshot — so a supervisor can ask every second
    /// "is a server that shares MY nonce serving this path?" for the price of one HMAC. That question
    /// (not "does the file exist", not "does anything connect") is what distinguishes a healthy core
    /// from a hijacked or foreign one (bevel-wio0 × bevel-hprv). See <see cref="UdsMessageClient.ProbeAsync"/>.
    /// </summary>
    public const string ProbeCapability = "probe";

    /// <summary>
    /// Claims the UDS path (<see cref="UdsSocketClaim.BindListener"/>: probe first, reclaim only a
    /// stale file, never a live one) and starts accepting. Throws <see cref="UdsSocketBusyException"/>
    /// when another instance is already serving the path — the caller must stand down. The parent
    /// directory is created 0700 — the socket is an authenticated control channel, so it must not be
    /// reachable by other users on the box.
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

        _listener = UdsSocketClaim.BindListener(_socketPath, backlog: 128);
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

    /// <summary>
    /// Sends a Broadcast frame to ONE client (by the id the request handler received) — the per-client
    /// push the session protocol needs: Hello's snapshot burst goes to the ASKING client alone, never
    /// to the whole set (bevel-4zfs). Enqueue-only and quiet on an unknown/vanished id, like
    /// <see cref="Broadcast"/>.
    /// </summary>
    public void SendToClient(Guid clientId, ReadOnlyMemory<byte> payload)
    {
        if (_clients.TryGetValue(clientId, out var client))
            client.Enqueue(FrameKind.Broadcast, correlationId: 0, payload.ToArray());
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
                Handshake.ValidateHello(_nonce, hello.Payload) is not { } capability)
            {
                await Framing.WriteFrameAsync(stream, FrameKind.HandshakeReject, 0, ReadOnlyMemory<byte>.Empty, ct)
                    .ConfigureAwait(false);
                stream.Dispose();
                return;
            }

            await Framing.WriteFrameAsync(stream, FrameKind.HandshakeOk, 0, ReadOnlyMemory<byte>.Empty, ct)
                .ConfigureAwait(false);

            // A liveness probe is answered and closed here — never registered, never snapshotted
            // (see ProbeCapability). The Ok it just read is the whole answer.
            if (capability == ProbeCapability)
            {
                stream.Dispose();
                return;
            }

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
            response = await _onRequest(conn.Id, frame.Payload, ct).ConfigureAwait(false);
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
    /// Stops accepting, tears down every client, and unlinks the socket file — but only if this
    /// server still owns it (<see cref="UdsSocketClaim.ReleaseListener"/>): a path that another live
    /// listener has since taken over, or that this instance never bound (a stood-down second
    /// instance), is left untouched.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();
        // Closes the listener (unblocks AcceptAsync) and unlinks the path only if nobody else answers there.
        UdsSocketClaim.ReleaseListener(_listener, _socketPath);

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch { /* accept loop swallows its own shutdown faults */ }
        }

        foreach (var conn in _clients.Values)
            conn.Dispose();
        _clients.Clear();

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
