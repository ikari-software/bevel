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
/// <para>Resilience (bevel — taskbar reconnect): the underlying transport delegates reconnect to
/// its caller, so this owns it. A background supervisor watches for the transport's
/// <see cref="UdsMessageClient.Disconnected"/> signal and re-dials with capped exponential backoff.
/// On success the core re-pushes its on-connect snapshot, so window/app state re-syncs on its own.
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
            EventReceived?.Invoke(evt);
        };
        _client.Disconnected += OnTransportDisconnected;
        _supervisor = ReconnectSupervisorAsync(_lifetime.Token);
    }

    /// <summary>Connects (idempotent, retry-safe). Subscribers should attach to <see cref="EventReceived"/>
    /// BEFORE the first connect so the core's on-connect snapshot isn't missed.</summary>
    public async Task EnsureConnectedAsync(CancellationToken ct = default)
    {
        if (_connected) return;
        await ConnectOnceAsync(ct).ConfigureAwait(false);
    }

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
            // Log on the connected transition (not per path), so a reconnect via EITHER the lazy
            // command path or the supervisor reports once. First connect is silent.
            if (_everConnected)
                Console.Error.WriteLine("[shellcore] link restored");
            _everConnected = true;
            ConnectionChanged?.Invoke(this, true);
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

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try { await _supervisor.ConfigureAwait(false); }
        catch { /* supervisor faults on teardown are expected */ }
        _lifetime.Dispose();
        _connectGate.Dispose();
        _reconnectSignal.Dispose();
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
