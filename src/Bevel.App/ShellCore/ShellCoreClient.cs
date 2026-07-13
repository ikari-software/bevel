using Bevel.ShellCore.Ipc;

namespace Bevel.App.ShellCore;

/// <summary>
/// A UI process's single connection to the shell-core owner (bevel-gww.3). Both the window-manager
/// and app-environment client adapters share ONE of these (one UDS connection per UI process): it
/// owns the <see cref="UdsMessageClient"/>, connects lazily on first use, decodes the core's
/// broadcasts into <see cref="CoreEvent"/>s, and fans them to subscribers. Commands go out via
/// <see cref="SendAsync"/> (request/response).
/// </summary>
public sealed class ShellCoreClient : IAsyncDisposable
{
    private readonly UdsMessageClient _client;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private volatile bool _connected;

    /// <summary>Raised (on a transport receive-loop thread) for every decoded core broadcast — the
    /// adapters filter by <see cref="CoreEvent.Kind"/> and marshal onto their own dispatcher.</summary>
    public event Action<CoreEvent>? EventReceived;

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
    }

    /// <summary>Connects (idempotent, retry-safe). Subscribers should attach to <see cref="EventReceived"/>
    /// BEFORE the first connect so the core's on-connect snapshot isn't missed.</summary>
    public async Task EnsureConnectedAsync(CancellationToken ct = default)
    {
        if (_connected) return;
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_connected)
            {
                await _client.ConnectAsync(ct).ConfigureAwait(false);
                _connected = true;
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>Sends a command and awaits the core's correlated response, connecting first if needed.</summary>
    public async Task<CoreResponse> SendAsync(CoreCommand cmd, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var raw = await _client.RequestAsync(CoreProtocol.Serialize(cmd), ct).ConfigureAwait(false);
        return CoreProtocol.Deserialize<CoreResponse>(raw);
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
