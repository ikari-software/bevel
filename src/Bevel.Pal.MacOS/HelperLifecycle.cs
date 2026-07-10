using System.Diagnostics;
using System.Security.Cryptography;
using Bevel.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bevel.Pal.MacOS;

/// <summary>
/// Manages the BevelHelper process lifecycle: launch, gRPC connection, crash
/// detection, and automatic restart. Runs as an IHostedService so it starts
/// with the app and stops cleanly on shutdown.
///
/// M0 acceptance: helper kill -9 → detected ≤ 2 s, session re-established
/// without app restart.
/// </summary>
public class HelperLifecycle : IHostedService, IDisposable
{
    private readonly ILogger<HelperLifecycle> _logger;
    // Resolved lazily at first launch (not in the ctor) so a fail-closed resolution error
    // surfaces through the monitor's retry loop instead of aborting construction (bevel-tyv).
    private string? _helperBinaryPath;
    private readonly string _socketDir;
    private Process? _helperProcess;
    private HelperClient? _client;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private bool _disposed;

    /// <summary>Nonce token generated per session for helper authentication.</summary>
    public string NonceToken { get; }

    /// <summary>Unix domain socket path for the current helper instance.</summary>
    public string SocketPath { get; private set; } = string.Empty;

    /// <summary>The gRPC client, available after <see cref="StartAsync"/> completes.</summary>
    public HelperClient Client => _client ?? throw new InvalidOperationException("Helper not started.");

    /// <summary>True if the helper process is currently alive.</summary>
    public bool IsRunning => IsHelperAlive;

    /// <summary>Liveness check for the monitor loop. Overridable for tests.</summary>
    protected virtual bool IsHelperAlive => _helperProcess is { HasExited: false };

    /// <summary>How often the monitor checks helper health. Overridable for tests.</summary>
    protected virtual TimeSpan PollInterval => TimeSpan.FromSeconds(1);

    public HelperLifecycle(ILogger<HelperLifecycle> logger)
    {
        _logger = logger;
        NonceToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _socketDir = Path.Combine(Path.GetTempPath(), "bevel-helper");
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
            // Wait for the monitor to fully exit before killing the helper, so KillHelper()
            // can't race the monitor mutating _helperProcess mid-relaunch (review #13). The
            // cancel above unblocks any in-flight launch/Ping promptly; the cap is a
            // worst-case backstop covering the readiness timeout.
            try { await _monitorTask.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        }
        KillHelper();
        _client?.Dispose();
    }

    /// <summary>
    /// Launches the helper process and waits for it to be reachable via gRPC.
    /// Throws on failure; the monitor loop owns retry/restart. Overridable for tests.
    /// </summary>
    protected virtual async Task LaunchAndConnectAsync(CancellationToken ct)
    {
        // Resolve (and cache) the trusted helper path on first launch. Fail-closed: this throws
        // rather than exec an attacker-controlled binary, and the throw is owned by the monitor's
        // retry loop (bevel-tyv).
        var helperPath = _helperBinaryPath ??= ResolveHelperBinary(
            Environment.GetEnvironmentVariable("BEVEL_HELPER_PATH"),
            AppDomain.CurrentDomain.BaseDirectory,
            AllowEnvOverride,
            File.Exists);
        _logger.LogInformation("HelperLifecycle: resolved helper binary {Path}", helperPath);

        HardenSocketDirectory(_socketDir);
        SocketPath = Path.Combine(_socketDir, $"helper-{Guid.NewGuid():N}.sock");

        if (File.Exists(SocketPath))
            File.Delete(SocketPath);

        var psi = new ProcessStartInfo
        {
            FileName = helperPath,
            Arguments = $"--socket {SocketPath} --parent-pid {Environment.ProcessId}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // Pass the auth nonce via the environment, not argv: command-line arguments are
        // world-readable through `ps`/KERN_PROCARGS2 to any same-user process, whereas a
        // child's environment is not shown by default `ps` (review #10).
        psi.Environment["BEVEL_HELPER_TOKEN"] = NonceToken;

        _helperProcess = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start helper process.");

        _logger.LogInformation("HelperLifecycle: helper started, PID={PID}", _helperProcess.Id);

        // Subscribe to exit event for crash detection.
        _helperProcess.EnableRaisingEvents = true;
        _helperProcess.Exited += OnHelperExited;

        // Wait for the helper to be reachable (poll gRPC endpoint).
        _client = new HelperClient();
        await WaitForReadyAsync(ct);

        _logger.LogInformation("HelperLifecycle: helper connected, version={Version}", await _client.PingAsync(ct));
    }

    /// <summary>
    /// Creates the socket directory restricted to the owner (0700). Defense-in-depth for
    /// IPC-04: even without the per-session nonce, no other user can reach the helper's
    /// Unix socket. Idempotent — re-hardens an existing directory.
    /// </summary>
    internal static void HardenSocketDirectory(string dir)
    {
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// Polls the helper's gRPC Ping until it responds, with exponential backoff.
    /// Times out after 5 seconds.
    /// </summary>
    private async Task WaitForReadyAsync(CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(5);
        var sw = Stopwatch.StartNew();
        var delay = 50;

        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();

            // Bound each attempt: a helper that accepts the socket but never answers the
            // Ping must not hang the poll. The outer 5s budget only checks between attempts,
            // so a single wedged call needs its own deadline (review #4).
            using (var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                attemptCts.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    _client!.Connect(SocketPath, NonceToken);
                    _ = await _client.PingAsync(attemptCts.Token);
                    return; // Ready.
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // app/monitor shutting down — propagate, don't keep polling
                }
                catch
                {
                    _client!.Disconnect();
                }
            }

            await Task.Delay(delay, ct);
            delay = Math.Min(delay * 2, 500);
        }

        throw new TimeoutException(
            $"Helper did not become ready within {timeout.TotalSeconds}s. " +
            $"Socket: {SocketPath}. Process exit code: {_helperProcess?.ExitCode}");
    }

    /// <summary>
    /// The single crash-monitor loop for the helper's lifetime. Performs the initial
    /// launch and every subsequent (re)launch itself — it is started exactly once (in
    /// <see cref="StartAsync"/>) and never spawns another loop, so crashes cannot
    /// accumulate concurrent monitors or duplicate helper processes (bevel-mox).
    /// Healthy checks run every <see cref="PollInterval"/> (detection ≤ 2 s); repeated
    /// launch failures back off up to 30 s so a missing binary does not spin hot.
    /// </summary>
    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        var retryDelay = PollInterval;
        var maxRetryDelay = TimeSpan.FromSeconds(30);

        while (!ct.IsCancellationRequested)
        {
            if (!IsHelperAlive)
            {
                // Guard the pre-relaunch cleanup: this loop is a fire-and-forget task, so an
                // exception escaping here would tear down the monitor permanently and disable
                // crash-restart for the rest of the session (bevel review R2).
                try
                {
                    KillHelper();
                    _client?.Dispose();
                    _client = null;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "HelperLifecycle: error cleaning up before relaunch; continuing");
                }

                try
                {
                    await LaunchAndConnectAsync(ct);
                    _logger.LogInformation("HelperLifecycle: helper (re)established");
                    retryDelay = PollInterval; // healthy again — reset backoff
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // A launch that started the process but failed readiness (a wedged,
                    // unresponsive helper) leaves _helperProcess alive, which would make
                    // IsHelperAlive mask it forever and prevent relaunch. Kill it so the next
                    // iteration starts a fresh one (review #4).
                    _logger.LogError(ex, "HelperLifecycle: helper (re)launch failed; retrying in {Delay}", retryDelay);
                    try { KillHelper(); } catch { /* already gone */ }
                    if (await DelayOrCancelled(retryDelay, ct)) break;
                    retryDelay = retryDelay < maxRetryDelay
                        ? TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, maxRetryDelay.Ticks))
                        : maxRetryDelay;
                    continue;
                }
            }

            if (await DelayOrCancelled(PollInterval, ct)) break;
        }
    }

    /// <summary>Delays for <paramref name="delay"/>; returns true if cancellation was requested.</summary>
    private static async Task<bool> DelayOrCancelled(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    private void OnHelperExited(object? sender, EventArgs e)
    {
        // The monitor loop handles restart. This callback just logs.
        _logger.LogWarning("HelperLifecycle: OnHelperExited fired (code={Code})", _helperProcess?.ExitCode);
    }

    private void KillHelper()
    {
        if (_helperProcess is null) return;

        try
        {
            if (!_helperProcess.HasExited)
            {
                _logger.LogInformation("HelperLifecycle: killing helper PID={PID}", _helperProcess.Id);
                _helperProcess.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }

        _helperProcess.Exited -= OnHelperExited;
        _helperProcess.Dispose();
        _helperProcess = null;
    }

    /// <summary>
    /// Whether the <c>BEVEL_HELPER_PATH</c> development override is honored. Only in DEBUG builds:
    /// a shipped (Release) app ignores the env entirely, so a hostile environment can never
    /// redirect the privileged helper's trust anchor (SEC1 / bevel-tyv).
    /// </summary>
    private static bool AllowEnvOverride =>
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>
    /// Resolves the path to the trusted BevelHelper binary. Fail-closed: returns only an
    /// ABSOLUTE, existing path and throws when none is found — it never falls back to an
    /// unqualified name resolved off <c>$PATH</c>, which a hostile PATH could hijack (SEC1).
    ///
    /// Search order:
    ///   1. BEVEL_HELPER_PATH — dev builds only (<paramref name="allowEnvOverride"/>), and only
    ///      when absolute + existing; a relative or missing value is a hard error, not a fallthrough.
    ///   2. App bundle: Contents/MacOS/BevelHelper.
    ///   3. Sibling dev build outputs (native/helper-macos/.build/{debug,release}/BevelHelper).
    /// </summary>
    /// <param name="fileExists">File-existence probe (injected so the resolver is unit-testable).</param>
    internal static string ResolveHelperBinary(
        string? envPath, string baseDirectory, bool allowEnvOverride, Func<string, bool> fileExists)
    {
        // 1. Development environment override — ignored outside DEBUG so it can't be a shipped
        //    attack surface. When honored it must be absolute (a relative path would resolve
        //    against the attacker-influenced CWD) and must exist; otherwise fail loudly.
        if (allowEnvOverride && !string.IsNullOrEmpty(envPath))
        {
            if (!Path.IsPathRooted(envPath) || !fileExists(envPath))
                throw new InvalidOperationException(
                    $"BEVEL_HELPER_PATH must be an absolute path to an existing helper binary; got '{envPath}'.");
            return envPath;
        }

        // 2. App bundle (absolute via GetFullPath).
        var appBundlePath = Path.GetFullPath(Path.Combine(baseDirectory, "..", "MacOS", "BevelHelper"));
        if (fileExists(appBundlePath))
            return appBundlePath;

        // 3. Dev build outputs next to the repo (absolute).
        var repoRoot = FindRepoRoot(baseDirectory, fileExists);
        if (repoRoot is not null)
        {
            var devDebug = Path.Combine(repoRoot, "native", "helper-macos", ".build", "debug", "BevelHelper");
            if (fileExists(devDebug))
                return devDebug;

            var devRelease = Path.Combine(repoRoot, "native", "helper-macos", ".build", "release", "BevelHelper");
            if (fileExists(devRelease))
                return devRelease;
        }

        // 4. No PATH fallback: refusing to guess is the security property. A missing helper is a
        //    clear configuration error, never an exec of an attacker-controlled name off $PATH.
        throw new InvalidOperationException(
            "Could not locate the BevelHelper binary in the app bundle or dev build outputs. " +
            "In a development build set BEVEL_HELPER_PATH to an absolute path, or ensure the app " +
            "bundle contains Contents/MacOS/BevelHelper.");
    }

    /// <summary>
    /// Walks up from <paramref name="startDir"/> looking for Bevel.sln to find the repo root.
    /// </summary>
    private static string? FindRepoRoot(string startDir, Func<string, bool> fileExists)
    {
        var dir = startDir;
        for (var i = 0; i < 10; i++)
        {
            if (fileExists(Path.Combine(dir, "Bevel.sln")))
                return dir;
            dir = Path.GetDirectoryName(dir);
            if (dir is null) break;
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _monitorCts?.Cancel();
        _monitorTask?.Wait(TimeSpan.FromSeconds(8)); // let the monitor exit before KillHelper (review #13)
        KillHelper();
        _client?.Dispose();
        _monitorCts?.Dispose();
    }
}
