using Bevel.ShellCore.Ipc;

namespace Bevel.App.Supervision;

/// <summary>
/// The launcher's answer to "is MY core serving core.sock?" (bevel-hprv × bevel-wio0). It is an
/// authenticated handshake with the launcher's own session nonce (<see cref="UdsMessageClient.ProbeAsync"/>),
/// not a <c>File.Exists</c> and not a bare connect: a socket file can outlive a dead core, and a
/// connectable socket can belong to the WRONG core (a foreign <c>--role=core</c> that reclaimed the
/// path while ours was down). Both used to certify as healthy; both now read as unhealthy, so the
/// supervisor's kill+respawn kicks in — and the respawned core, finding a live foreign listener,
/// stands down with a reported failure the launcher Holds and surfaces instead of looping silently.
/// </summary>
internal static class CoreSocketProbe
{
    /// <summary>Upper bound on one probe so a wedged core can never stall the 1 s monitor tick (which
    /// holds the supervisor gate while probing).</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    /// <summary>True iff a server sharing <paramref name="nonce"/> answers on <paramref name="socketPath"/>
    /// within <see cref="Budget"/>. Never throws — timeout, refusal, foreign nonce all read as false.</summary>
    public static async Task<bool> IsServingAsync(string socketPath, byte[] nonce, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Budget);
        try
        {
            return await UdsMessageClient.ProbeAsync(socketPath, nonce, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Polls <see cref="IsServingAsync"/> until it answers true or <paramref name="timeout"/>
    /// elapses — the post-spawn "core is reachable" gate. Returns without throwing either way.</summary>
    public static async Task WaitUntilServingAsync(string socketPath, byte[] nonce, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (await IsServingAsync(socketPath, nonce, ct).ConfigureAwait(false))
                return;
            try { await Task.Delay(50, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
