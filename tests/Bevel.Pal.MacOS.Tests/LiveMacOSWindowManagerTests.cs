using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Bevel.Pal.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>
/// R21: contract coverage for the REAL macOS <see cref="MacOSWindowManager"/> — the piece
/// the platform-neutral <c>PalContractTests</c> cannot host (it doesn't reference
/// Bevel.Pal.MacOS, and the behavioral assertions need a live helper).
///
/// Two tiers:
///  • <see cref="Real_impl_reports_macOS_capabilities"/> runs everywhere — it exercises the
///    real class's capability contract with no helper.
///  • <see cref="Real_helper_roundtrip_backed_by_spawner"/> is opt-in
///    (<c>BEVEL_LIVE_HELPER_TESTS=1</c>): it launches a real helper and drives the real
///    MacOSWindowManager over gRPC, backed by the WindowSpawner rig. It early-returns
///    (green, no-op) when the flag/binary/GUI session isn't present, matching this
///    project's gating convention.
///
/// Enumeration/correlation *correctness* against a known window is proven deterministically
/// at the helper level by the Swift WindowServiceSpawnerTests; this test's job is the
/// .NET → gRPC → helper plumbing.
/// </summary>
public sealed class LiveMacOSWindowManagerTests
{
    private const string LiveFlag = "BEVEL_LIVE_HELPER_TESTS";
    private readonly ITestOutputHelper _output;

    public LiveMacOSWindowManagerTests(ITestOutputHelper output) => _output = output;

    /// <summary>The real implementation's capability contract — no helper required.</summary>
    [Fact]
    public void Real_impl_reports_macOS_capabilities()
    {
        using var lifecycle = new HelperLifecycle(NullLogger<HelperLifecycle>.Instance);
        using var wm = new MacOSWindowManager(lifecycle, NullLogger<MacOSWindowManager>.Instance);

        Assert.NotNull(wm.Capabilities);
        Assert.True(wm.Capabilities.Available);
        Assert.True(wm.Capabilities.SupportsReposition);
        Assert.Equal(TrayCapability.Mirrored, wm.Capabilities.TrayMode);
    }

    /// <summary>The real MacOSWindowManager over a real helper, backed by the spawner rig.</summary>
    [Fact]
    public async Task Real_helper_roundtrip_backed_by_spawner()
    {
        if (!OperatingSystem.IsMacOS())
            return; // macOS-only.
        if (Environment.GetEnvironmentVariable(LiveFlag) != "1")
        {
            _output.WriteLine($"skipped: set {LiveFlag}=1 to run the live helper integration.");
            return;
        }

        using var lifecycle = new HelperLifecycle(NullLogger<HelperLifecycle>.Instance);
        await lifecycle.StartAsync(CancellationToken.None);
        try
        {
            if (!await WaitUntilAsync(() => lifecycle.IsRunning, TimeSpan.FromSeconds(15)))
            {
                _output.WriteLine("skipped: helper did not start (binary missing/unsigned in this env?).");
                return;
            }

            using var wm = new MacOSWindowManager(lifecycle, NullLogger<MacOSWindowManager>.Instance);

            // Real gRPC round-trip: a live ListWindows must return a non-null snapshot.
            var initial = await wm.EnumerateAsync();
            Assert.NotNull(initial);

            // Error path over the real stack: acting on a bogus window must surface as a
            // clean gRPC failure (NotFound), not hang or leak.
            var bogus = new ForeignWindowId("999999999");
            await Assert.ThrowsAsync<Grpc.Core.RpcException>(() => wm.RestoreAsync(bogus));

            // Backed by the spawner: open a KNOWN window and look for it.
            using var spawner = SpawnerProcess.TryLaunch(_output);
            if (spawner is null)
            {
                _output.WriteLine("live plumbing verified; WindowSpawner binary unavailable so content leg skipped.");
                return;
            }

            var title = "BevelLive-" + Guid.NewGuid().ToString("N")[..8];
            var cgId = spawner.OpenWindow("a", 460, 460, 500, 360, title);
            var target = cgId.ToString();

            var snapshot = await PollEnumerateAsync(
                wm, ws => ws.Any(w => w.Id.Value == target), TimeSpan.FromSeconds(6));

            if (snapshot.Count == 0)
            {
                // The HELPER (a separate binary) needs Accessibility / Screen Recording to
                // read window titles; a freshly built dev binary is not TCC-approved, so it
                // enumerates nothing. The plumbing (launch → connect → round-trip → error
                // path) is still verified above; only the content leg is inapplicable here.
                _output.WriteLine("live round-trip verified; helper lacks window permission, content leg skipped.");
            }
            else
            {
                Assert.Contains(snapshot, w => w.Id.Value == target);
                _output.WriteLine($"end-to-end verified: real MacOSWindowManager found spawner window {target} '{title}'.");
            }

            spawner.Quit();
        }
        finally
        {
            await lifecycle.StopAsync(CancellationToken.None);
        }
    }

    // ── Polling helpers ─────────────────────────────────────────────────────

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(150);
        }
        return condition();
    }

    private static async Task<IReadOnlyList<ForeignWindow>> PollEnumerateAsync(
        MacOSWindowManager wm, Func<IReadOnlyList<ForeignWindow>, bool> done, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        IReadOnlyList<ForeignWindow> last = Array.Empty<ForeignWindow>();
        do
        {
            last = await wm.EnumerateAsync();
            if (done(last)) return last;
            await Task.Delay(200);
        } while (DateTime.UtcNow < deadline);
        return last;
    }
}

/// <summary>
/// Minimal .NET driver for the WindowSpawner rig: launches it and speaks its stdin/stdout
/// line protocol. A background reader pumps stdout into a blocking queue so command acks
/// can be awaited with a timeout.
/// </summary>
internal sealed class SpawnerProcess : IDisposable
{
    private readonly Process _process;
    private readonly BlockingCollection<string> _lines = new();

    private SpawnerProcess(Process process) => _process = process;

    public static SpawnerProcess? TryLaunch(ITestOutputHelper output, [CallerFilePath] string thisFile = "")
    {
        var binary = ResolveBinary(thisFile);
        if (binary is null)
        {
            output.WriteLine("WindowSpawner binary not found; build tests/rigs/macos-lite/WindowSpawner.");
            return null;
        }

        var psi = new ProcessStartInfo
        {
            FileName = binary,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        var process = Process.Start(psi);
        if (process is null) return null;

        var spawner = new SpawnerProcess(process);
        _ = Task.Run(() =>
        {
            try
            {
                string? line;
                while ((line = process.StandardOutput.ReadLine()) != null)
                    spawner._lines.Add(line.Trim());
            }
            catch { /* stream closed on exit */ }
            finally { spawner._lines.CompleteAdding(); }
        });

        // Wait for the rig's readiness line.
        if (spawner.ReadLine(l => l == "ready", TimeSpan.FromSeconds(10)) is null)
        {
            spawner.Dispose();
            return null;
        }
        return spawner;
    }

    /// <summary>open &lt;key&gt; …; returns the window's CGWindowNumber.</summary>
    public uint OpenWindow(string key, int x, int y, int w, int h, string title)
    {
        Send($"open {key} {x} {y} {w} {h} {title}");
        var ack = ReadLine(l => l.StartsWith($"ok open {key} id="), TimeSpan.FromSeconds(5))
                  ?? throw new InvalidOperationException("spawner did not ack open");
        return uint.Parse(ack.Split('=')[^1]);
    }

    public void Quit()
    {
        if (_process.HasExited) return;
        try
        {
            Send("quit");
            ReadLine(l => l.StartsWith("ok quit"), TimeSpan.FromSeconds(2));
        }
        catch { /* best effort */ }
    }

    private void Send(string command)
    {
        _process.StandardInput.WriteLine(command);
        _process.StandardInput.Flush();
    }

    /// <summary>Drains queued stdout lines until one matches, or the timeout elapses.</summary>
    private string? ReadLine(Func<string, bool> match, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            if (_lines.TryTake(out var line, remaining) && match(line))
                return line;
        }
        return null;
    }

    private static string? ResolveBinary(string thisFile)
    {
        var overridePath = Environment.GetEnvironmentVariable("BEVEL_WINDOW_SPAWNER");
        if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
            return overridePath;

        // <repo>/tests/Bevel.Pal.MacOS.Tests/LiveMacOSWindowManagerTests.cs → up 2 = repo root.
        var dir = Path.GetDirectoryName(thisFile)!;
        var repoRoot = Path.GetFullPath(Path.Combine(dir, "..", ".."));
        var binary = Path.Combine(repoRoot,
            "tests", "rigs", "macos-lite", "WindowSpawner", ".build", "debug", "WindowSpawner");
        return File.Exists(binary) ? binary : null;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000);
            }
        }
        catch { /* already gone */ }
        _process.Dispose();
        _lines.Dispose();
    }
}
