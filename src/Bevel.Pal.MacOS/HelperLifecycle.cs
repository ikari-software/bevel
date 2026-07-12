using Bevel.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bevel.Pal.MacOS;

/// <summary>
/// Manages the BevelHelper process lifecycle: launch, gRPC connection, crash
/// detection, and automatic restart. Runs as an IHostedService so it starts
/// with the app and stops cleanly on shutdown.
///
/// This class is pure restart POLICY (bevel-vgg / review AD7): the process/connection
/// mechanics live behind <see cref="IHelperProcessHost"/> and time behind <see cref="IClock"/>,
/// both constructor-injected so tests drive the monitor loop deterministically.
///
/// M0 acceptance: helper kill -9 → detected ≤ 2 s, session re-established
/// without app restart.
/// </summary>
public class HelperLifecycle : IHostedService, IDisposable
{
    private readonly ILogger<HelperLifecycle> _logger;
    private readonly IHelperProcessHost _host;
    private readonly IClock _clock;
    private readonly TimeSpan _pollInterval;
    private readonly IReadOnlySet<string> _capabilities;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private bool _disposed;

    /// <summary>Nonce token generated per session for helper authentication.</summary>
    public string NonceToken => _host.NonceToken;

    /// <summary>Unix domain socket path for the current helper instance.</summary>
    public string SocketPath => _host.SocketPath;

    /// <summary>The gRPC client, available after <see cref="StartAsync"/> completes.</summary>
    public HelperClient Client => _host.Client ?? throw new InvalidOperationException("Helper not started.");

    /// <summary>True if the helper process is currently alive.</summary>
    public bool IsRunning => _host.IsAlive;

    public HelperLifecycle(ILogger<HelperLifecycle> logger)
        : this(logger, new HelperProcessHost(logger), SystemClock.Instance, pollInterval: TimeSpan.FromSeconds(1),
              capabilities: new HashSet<string> { "supervision" })
    {
    }

    /// <summary>Composition seam for tests: a fake host + manual clock make the monitor
    /// loop's behavior (relaunch-per-crash, backoff, stop) fully deterministic.</summary>
    internal HelperLifecycle(
        ILogger<HelperLifecycle> logger, IHelperProcessHost host, IClock clock, TimeSpan pollInterval,
        IReadOnlySet<string>? capabilities = null)
    {
        _logger = logger;
        _host = host;
        _clock = clock;
        _pollInterval = pollInterval;
        _capabilities = capabilities ?? new HashSet<string> { "supervision" };
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("HelperLifecycle: starting helper monitor");

        // Start the single crash-monitor loop and return immediately. The loop performs
        // the initial launch on its first iteration, so a missing/broken helper never
        // blocks or aborts application startup — it degrades and keeps retrying (bevel-c0y).
        _monitorCts = new CancellationTokenSource();
        _monitorTask = MonitorLoopAsync(_monitorCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("HelperLifecycle: stopping helper");
        _monitorCts?.Cancel();
        if (_monitorTask is not null)
        {
            // Wait for the monitor to fully exit before killing the helper, so Kill()
            // can't race the monitor mutating host state mid-relaunch (review #13). The
            // cancel above unblocks any in-flight launch/Ping promptly; the cap is a
            // worst-case backstop covering the readiness timeout.
            // ConfigureAwait(false): this is library/hosted-service code and must not resume on
            // a caller's SynchronizationContext (host shutdown blocks the UI thread — bevel-fu5).
            try { await _monitorTask.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        }
        _host.Kill();
    }

    /// <summary>
    /// The single crash-monitor loop for the helper's lifetime. Performs the initial
    /// launch and every subsequent (re)launch itself — it is started exactly once (in
    /// <see cref="StartAsync"/>) and never spawns another loop, so crashes cannot
    /// accumulate concurrent monitors or duplicate helper processes (bevel-mox).
    /// Healthy checks run every poll interval (detection ≤ 2 s); repeated launch
    /// failures back off up to 30 s so a missing binary does not spin hot.
    /// </summary>
    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        var retryDelay = _pollInterval;
        var maxRetryDelay = TimeSpan.FromSeconds(30);

        while (!ct.IsCancellationRequested)
        {
            if (!_host.IsAlive)
            {
                // Guard the pre-relaunch cleanup: this loop is a fire-and-forget task, so an
                // exception escaping here would tear down the monitor permanently and disable
                // crash-restart for the rest of the session (bevel review R2).
                try
                {
                    _host.Kill();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "HelperLifecycle: error cleaning up before relaunch; continuing");
                }

                try
                {
                    await _host.LaunchAndConnectAsync(ct, _capabilities);
                    _logger.LogInformation("HelperLifecycle: helper (re)established");
                    retryDelay = _pollInterval; // healthy again — reset backoff
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // A launch that started the process but failed readiness (a wedged,
                    // unresponsive helper) leaves the process alive, which would make
                    // IsAlive mask it forever and prevent relaunch. Kill it so the next
                    // iteration starts a fresh one (review #4).
                    _logger.LogError(ex, "HelperLifecycle: helper (re)launch failed; retrying in {Delay}", retryDelay);
                    try { _host.Kill(); } catch { /* already gone */ }
                    if (await DelayOrCancelled(retryDelay, ct)) break;
                    retryDelay = retryDelay < maxRetryDelay
                        ? TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, maxRetryDelay.Ticks))
                        : maxRetryDelay;
                    continue;
                }
            }

            if (await DelayOrCancelled(_pollInterval, ct)) break;
        }
    }

    /// <summary>Delays for <paramref name="delay"/> on the injected clock; returns true if cancelled.</summary>
    private async Task<bool> DelayOrCancelled(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await _clock.Delay(delay, ct);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _monitorCts?.Cancel();
        _monitorTask?.Wait(TimeSpan.FromSeconds(8)); // let the monitor exit before Kill (review #13)
        _host.Kill();
        _host.Dispose();
        _monitorCts?.Dispose();
    }
}
