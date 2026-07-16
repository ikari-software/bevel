using System.Collections.Concurrent;
using System.Net.Sockets;

namespace Bevel.ShellCore.Ipc;

/// <summary>
/// The "UI" side of the transport: connects to a <see cref="UdsMessageServer"/>, receives its
/// broadcasts, and issues request/response calls. Payload-agnostic — opaque <c>byte[]</c> in,
/// opaque <c>byte[]</c> out.
///
/// <para>Broadcast delivery uses a plain <see cref="BroadcastReceived"/> event rather than an
/// <c>IAsyncEnumerable</c>. A shell UI wants a fire-and-forget push it can marshal onto its own
/// dispatcher; an async stream would add buffering and back-pressure semantics (what happens
/// when the consumer lags?) that this layer deliberately doesn't want to own. Handlers should be
/// cheap and non-blocking — they run on the receive-loop thread.</para>
///
/// <para>Reconnect is intentionally the CALLER's concern: on disconnect the receive loop ends
/// and in-flight requests fault, but the socket state is fully reset, so a caller may simply call
/// <see cref="ConnectAsync"/> again on a fresh or reused instance.</para>
/// </summary>
public sealed class UdsMessageClient : IAsyncDisposable
{
    private readonly string _socketPath;
    private readonly byte[] _nonce;
    private readonly string _capability;

    // One write lock serializes RequestAsync writes (and the handshake) onto the stream; the
    // receive loop only ever reads, so reads and writes never contend.
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<byte[]>> _pending = new();

    private Socket? _socket;
    private NetworkStream? _stream;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveLoop;
    private uint _correlationCounter; // Interlocked; ids start at 1 (0 is reserved for broadcasts)
    private int _disposed;

    /// <summary>
    /// Raised for every server broadcast. Runs on the receive-loop thread — keep the handler
    /// quick and hand heavy work off to your own scheduler.
    /// </summary>
    public event Action<byte[]>? BroadcastReceived;

    /// <summary>
    /// Raised when the receive loop ends because the connection FAULTED (server dropped, pipe
    /// broke) — not on a deliberate <see cref="ResetAsync"/>/<see cref="DisposeAsync"/> teardown.
    /// Reconnect is the caller's concern (see the type remarks); this is the push signal that a
    /// reconnect is due. Runs on the receive-loop thread.
    /// </summary>
    public event Action<Exception>? Disconnected;

    /// <param name="socketPath">Path of the server's UDS.</param>
    /// <param name="nonce">Per-session shared secret; must match the server's.</param>
    /// <param name="capability">Capability string presented in the handshake and HMAC'd with the nonce.</param>
    public UdsMessageClient(string socketPath, byte[] nonce, string capability)
    {
        _socketPath = socketPath ?? throw new ArgumentNullException(nameof(socketPath));
        _nonce = nonce ?? throw new ArgumentNullException(nameof(nonce));
        _capability = capability ?? throw new ArgumentNullException(nameof(capability));
    }

    /// <summary>True once the handshake has completed and the receive loop is running.</summary>
    public bool IsConnected => _receiveLoop is not null;

    /// <summary>
    /// Opens the UDS and performs the handshake. Throws <see cref="IOException"/> if the server
    /// rejects the handshake, and honours <paramref name="ct"/> for connect/handshake timeout.
    /// Safely retryable: any half-open state from a prior failed attempt is torn down first.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await ResetAsync().ConfigureAwait(false); // make a retry after a failure a clean slate

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        NetworkStream stream;
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), ct).ConfigureAwait(false);
            stream = new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        try
        {
            var hello = Handshake.BuildHelloPayload(_nonce, _capability);
            await Framing.WriteFrameAsync(stream, FrameKind.HandshakeHello, 0, hello, ct).ConfigureAwait(false);

            var reply = await Framing.ReadFrameAsync(stream, ct).ConfigureAwait(false);
            if (reply.Kind != FrameKind.HandshakeOk)
                throw new IOException($"Handshake rejected by server (reply kind = {reply.Kind}).");
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        _socket = socket;
        _stream = stream;
        _receiveCts = new CancellationTokenSource();
        _receiveLoop = ReceiveLoopAsync(_stream, _receiveCts.Token);
    }

    /// <summary>
    /// Sends a request and awaits its matching response. A fresh correlation id is allocated per
    /// call and the awaiting <see cref="TaskCompletionSource{T}"/> is parked in a pending map
    /// keyed by that id, so many requests can be in flight at once and each resolves against its
    /// own response — arrival order doesn't matter. Cancelling <paramref name="ct"/> unparks just
    /// this call; a disconnect faults all pending calls so none hangs forever.
    /// </summary>
    public async Task<byte[]> RequestAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var stream = _stream ?? throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

        // Skip 0 on wrap — it's reserved for broadcasts and would collide in the pending map.
        uint corr;
        do { corr = Interlocked.Increment(ref _correlationCounter); }
        while (corr == 0);

        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[corr] = tcs;

        await using var reg = ct.Register(static state =>
        {
            var (self, id) = ((UdsMessageClient, uint))state!;
            if (self._pending.TryRemove(id, out var pending))
                pending.TrySetCanceled();
        }, (this, corr)).ConfigureAwait(false);

        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await Framing.WriteFrameAsync(stream, FrameKind.Request, corr, payload, ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch
        {
            _pending.TryRemove(corr, out _); // never wrote it — don't leak the pending entry
            throw;
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await Framing.ReadFrameAsync(stream, ct).ConfigureAwait(false);
                switch (frame.Kind)
                {
                    case FrameKind.Broadcast:
                        BroadcastReceived?.Invoke(frame.Payload);
                        break;
                    case FrameKind.Response:
                        if (_pending.TryRemove(frame.CorrelationId, out var tcs))
                            tcs.TrySetResult(frame.Payload);
                        break;
                    // Handshake frames are only valid pre-loop; anything else is ignored.
                }
            }
        }
        catch (Exception ex)
        {
            // Disconnect or protocol fault: wake every waiter so no RequestAsync hangs.
            FailAllPending(ex);
            // A cancelled token means a deliberate teardown (ResetAsync/DisposeAsync) — not a
            // fault, so don't cry disconnect. Any other exit is a real drop worth reconnecting.
            if (!ct.IsCancellationRequested)
                Disconnected?.Invoke(ex);
        }
    }

    private void FailAllPending(Exception ex)
    {
        foreach (var corr in _pending.Keys)
            if (_pending.TryRemove(corr, out var tcs))
                tcs.TrySetException(new IOException("Connection closed before a response arrived.", ex));
    }

    private async Task ResetAsync()
    {
        if (_receiveCts is not null)
        {
            await _receiveCts.CancelAsync().ConfigureAwait(false);
            if (_receiveLoop is not null)
            {
                try { await _receiveLoop.ConfigureAwait(false); }
                catch { /* loop faults are expected on teardown */ }
            }
            _receiveCts.Dispose();
        }

        _stream?.Dispose();
        _socket?.Dispose();
        FailAllPending(new IOException("Client reset."));

        _receiveCts = null;
        _receiveLoop = null;
        _stream = null;
        _socket = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await ResetAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
