using Bevel.Pal.Abstractions;
using Bevel.ShellCore.Ipc;

namespace Bevel.App.ShellCore;

/// <summary>
/// A UI process's single connection to the shell-core owner (bevel-gww.3). Both the window-manager
/// and app-environment client adapters share ONE of these (one UDS connection per UI process): it
/// owns the <see cref="UdsMessageClient"/>, connects lazily on first use, decodes the core's
/// broadcasts into <see cref="CoreEvent"/>s, and fans them to subscribers. Commands go out via
/// <see cref="SendAsync"/> (request/response).
///
/// <para>Session protocol (bevel-4zfs): the core pushes NOTHING on connect — the snapshot race class is
/// deleted by giving the CLIENT the session start. A role wires every adapter (which subscribe in their
/// ctors), then calls <see cref="StartSessionAsync"/>: Hello → the core pushes the four stamped snapshots.
/// Every event carries (epoch, seq); a gap or an epoch change re-baselines via an automatic re-Hello.</para>
///
/// <para>Resilience (bevel — taskbar reconnect): the underlying transport delegates reconnect to
/// its caller, so this owns it. A background supervisor watches for the transport's
/// <see cref="UdsMessageClient.Disconnected"/> signal and re-dials with capped exponential backoff.
/// On success the client re-Hellos, so the snapshot burst rebuilds window/app/tray state on its own.
/// <see cref="IsConnected"/>/<see cref="ConnectionChanged"/> expose the link health so a UI can show
/// a disconnected indicator during the gap.</para>
/// </summary>
public sealed class ShellCoreClient : IShellConnectionStatus, IAsyncDisposable
{
    // First reconnect attempt fires quickly (a dropped pipe is usually reconnectable at once); the
    // delay doubles up to the cap so a core that stays down doesn't spin.
    private const double ReconnectBaseMs = 250;
    private const double ReconnectMaxMs = 5000;

    private readonly UdsMessageClient _client;
    private readonly SemaphoreSlim _connectGate = new(1, 1); // serializes every ConnectAsync
    private readonly SemaphoreSlim _reconnectSignal = new(0); // released once per detected drop
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _supervisor;
    private volatile bool _connected;
    private bool _everConnected; // guarded by _connectGate; distinguishes first connect from a reconnect

    // ── Session protocol (bevel-4zfs) ────────────────────────────────────────────
    // The core pushes NOTHING on connect. This client owns the session start: once the role has wired
    // every adapter it calls StartSessionAsync (Hello); the core replies, then pushes the four stamped
    // snapshots. Every event carries (epoch, seq): a NEW epoch (the core's projection was rebuilt — e.g.
    // the startup seed finished) re-baselines the tracker, and a SEQ GAP means a lost frame — the client
    // re-issues Hello (rate-limited) instead of silently diverging. Reconnects re-Hello automatically: the
    // adapters are still subscribed, so the snapshot burst rebuilds everything.
    private readonly object _sessionGate = new();
    private bool _sessionStarted;   // set by StartSessionAsync; auto-Hello applies from then on
    private int _epoch;             // 0 = no baseline yet (pre-Hello deltas apply but don't gap-check)
    private long _seq;
    private long _lastAutoHelloTick; // Environment.TickCount64 — rate-limits gap-triggered re-Hellos

    /// <summary>Raised whenever the client sends Hello — first session start, reconnect, or gap-triggered
    /// re-sync (bevel-4zfs). Diagnostics/tests.</summary>
    public event Action? HelloSent;

    private const long MinAutoHelloIntervalMs = 2000;

    /// <summary>Raised (on a transport receive-loop thread) for every decoded core broadcast — the
    /// adapters filter by <see cref="CoreEvent.Kind"/> and marshal onto their own dispatcher.</summary>
    public event Action<CoreEvent>? EventReceived;

    /// <inheritdoc />
    public bool IsConnected => _connected;

    /// <inheritdoc />
    public event EventHandler<bool>? ConnectionChanged;

    public ShellCoreClient(string socketPath, byte[] nonce, string capability = "shellcore")
    {
        _client = new UdsMessageClient(socketPath, nonce, capability);
        _client.BroadcastReceived += raw =>
        {
            CoreEvent evt;
            try { evt = CoreProtocol.Deserialize<CoreEvent>(raw); }
            catch { return; } // a frame we can't parse (version skew) is dropped, not fatal
            TrackSequence(evt);
            EventReceived?.Invoke(evt);
        };
        _client.Disconnected += OnTransportDisconnected;
        _supervisor = ReconnectSupervisorAsync(_lifetime.Token);
    }

    /// <summary>Connects (idempotent, retry-safe). Connecting alone receives NOTHING (bevel-4zfs) — attach
    /// subscribers to <see cref="EventReceived"/> and then call <see cref="StartSessionAsync"/>, which is
    /// what makes the core push the snapshots.</summary>
    public async Task EnsureConnectedAsync(CancellationToken ct = default)
    {
        if (_connected) return;
        try
        {
            await ConnectOnceAsync(ct).ConfigureAwait(false);
        }
        catch when (!ct.IsCancellationRequested)
        {
            // The FIRST connect can fail if a client dials before the core's socket is bound — e.g. the
            // settings client connects in RemoteSettingsService.LoadAsync at startup, racing core boot, and
            // gets EADDRNOTAVAIL. The reconnect supervisor is otherwise only armed by the DROP of an
            // ESTABLISHED link (OnTransportDisconnected), so a client that never connected once would retry
            // NEVER and stay stuck disconnected forever (the taskbar then boots on DEFAULT settings — the
            // core's real snapshot never reaches it). Wake the supervisor so it keeps re-dialing in the
            // background; still rethrow so the caller's immediate fallback runs, and the core's on-connect
            // snapshot corrects state once the supervisor gets through.
            if (!_connected) _reconnectSignal.Release();
            throw;
        }
    }

    /// <summary>Starts the session (bevel-4zfs): Hello → the core pushes the four stamped snapshots to
    /// THIS client. Call ONCE, after every adapter (windows/apps/tray/settings) is constructed and
    /// subscribed — that ordering is the whole point: the client owns when state arrives, so the snapshot
    /// can never be eaten by a too-early connect. Idempotent; a reconnect re-Hellos on its own.</summary>
    public async Task StartSessionAsync(CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        lock (_sessionGate) _sessionStarted = true;
        await SendHelloAsync().ConfigureAwait(false);
    }

    private async Task SendHelloAsync()
    {
        HelloSent?.Invoke();
        await _client.RequestAsync(CoreProtocol.Serialize(new CoreCommand(CoreCommandKind.Hello)), _lifetime.Token)
            .ConfigureAwait(false);
    }

    /// <summary>Session-sequence tracking (bevel-4zfs). A stamped event from a NEW epoch re-baselines (it is
    /// the head of a snapshot burst — the core rebuilt its projection); within the tracked epoch a seq gap
    /// means a lost frame — re-Hello, rate-limited. Unstamped events (an older core) apply without tracking.
    /// Runs on the transport receive loop; the re-Hello is fire-and-forget off-thread so the loop never
    /// blocks on its own request.</summary>
    private void TrackSequence(CoreEvent evt)
    {
        if (evt.Seq is not { } seq || evt.Epoch is not { } epoch) return;
        var resync = false;
        lock (_sessionGate)
        {
            if (epoch != _epoch)
            {
                _epoch = epoch;
                _seq = seq;
                return;
            }
            if (seq != _seq + 1)
            {
                resync = true;
                _seq = seq;
            }
            else
            {
                _seq = seq;
            }
        }
        if (resync)
            _ = Task.Run(async () =>
            {
                try
                {
                    lock (_sessionGate)
                    {
                        if (Environment.TickCount64 - _lastAutoHelloTick < MinAutoHelloIntervalMs) return;
                        _lastAutoHelloTick = Environment.TickCount64;
                    }
                    await SendHelloAsync().ConfigureAwait(false);
                }
                catch { /* rate-limited retry on the next gap; the transport's own reconnect handles the dead-link case */ }
            });
    }

    /// <summary>Test seam (InternalsVisibleTo): feeds a synthetic event through the session-sequence
    /// tracker exactly as the transport would — the gap tests inject a lost frame without a real loss,
    /// and assert the auto-Hello. Does NOT raise <see cref="EventReceived"/>, so adapters see nothing.</summary>
    internal void NoteSessionEventForTest(CoreEvent evt) => TrackSequence(evt);

    /// <summary>Sends a command and awaits the core's correlated response, connecting first if needed.</summary>
    public async Task<CoreResponse> SendAsync(CoreCommand cmd, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        // Bound the command on a live-but-stuck core (bevel-dem): the correlated response TCS would
        // otherwise never complete and hang the awaiting UI action forever (a disconnect faults it,
        // but a core that's up-but-wedged never sends the frame). On the 5s deadline the linked token
        // cancels the request; the caller treats the cancellation as "unavailable -> next poll reconciles".
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var raw = await _client.RequestAsync(CoreProtocol.Serialize(cmd), cts.Token).ConfigureAwait(false);
        return CoreProtocol.Deserialize<CoreResponse>(raw);
    }

    /// <summary>One gated connect attempt. The gate serializes attempts from the lazy path
    /// (<see cref="EnsureConnectedAsync"/>) and the reconnect supervisor so they never race on the
    /// transport's socket fields; the double-check means the loser of the race is a no-op.</summary>
    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connected) return;
            await _client.ConnectAsync(ct).ConfigureAwait(false);
            _connected = true;
            // Reconnects re-start the session themselves (bevel-4zfs): the core pushes nothing on
            // connect, and the adapters are still subscribed, so the Hello burst rebuilds everything.
            var rehello = false;
            lock (_sessionGate)
            {
                if (_sessionStarted && _everConnected) rehello = true;
                _epoch = 0; _seq = 0;   // the new connection's burst re-baselines
            }
            // Log on the connected transition (not per path), so a reconnect via EITHER the lazy
            // command path or the supervisor reports once. First connect is silent.
            if (_everConnected)
                Console.Error.WriteLine("[shellcore] link restored");
            _everConnected = true;
            ConnectionChanged?.Invoke(this, true);
            if (rehello)
                _ = Task.Run(async () =>
                {
                    try { await SendHelloAsync().ConfigureAwait(false); }
                    catch { /* the supervisor retries the whole link if this failed */ }
                });
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>Transport receive loop faulted — flip to disconnected and wake the supervisor.
    /// Runs on the transport thread.</summary>
    private void OnTransportDisconnected(Exception ex)
    {
        if (!_connected) return; // already down / a reconnect is in flight
        _connected = false;
        Console.Error.WriteLine($"[shellcore] link lost ({ex.GetType().Name}: {ex.Message}) — reconnecting");
        ConnectionChanged?.Invoke(this, false);
        _reconnectSignal.Release();
    }

    /// <summary>Single long-lived reconnect owner: waits for a drop, then re-dials with capped
    /// exponential backoff until the link is back (or the client is disposed). One loop for the
    /// whole lifetime avoids the start/stop races a per-drop task would have.</summary>
    private async Task ReconnectSupervisorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _reconnectSignal.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            var delayMs = ReconnectBaseMs;
            while (!ct.IsCancellationRequested && !_connected)
            {
                try
                {
                    await ConnectOnceAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch
                {
                    // Core still down — back off and retry. A concurrent lazy connect may beat us,
                    // in which case _connected flips true and the loop condition ends it.
                    try { await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    delayMs = Math.Min(delayMs * 2, ReconnectMaxMs);
                }
            }
        }
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        // Idempotent teardown. This is disposed by BOTH the DI container and the host-shutdown path, so a
        // second pass used to call CancelAsync() on the already-disposed _lifetime CTS below — throwing an
        // ObjectDisposedException that went UNHANDLED during shutdown and crashed the process on
        // restart/quit (Program.Main line ~104), so the shell died mid-teardown and never came back.
        // Guard so the second call is a clean no-op, and treat an already-disposed CTS as done.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await _lifetime.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { /* already cancelled/torn down elsewhere */ }
        try { await _supervisor.ConfigureAwait(false); }
        catch { /* supervisor faults on teardown are expected */ }
        _lifetime.Dispose();
        _connectGate.Dispose();
        _reconnectSignal.Dispose();
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
