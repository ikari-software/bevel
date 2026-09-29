using Bevel.App;
using Bevel.App.Supervision;
using Bevel.ShellCore.Ipc;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The SpawnFiler launcher verb (bevel-t48y Task 2): payload codec, a live round-trip through the UDS
/// control transport, and the <see cref="LauncherFilerSpawner"/> routing — supervised opens go to the
/// launcher; unsupervised or unreachable launcher falls back to the in-process spawner, so an open
/// never silently dies. Verbs 1–6 stay byte-identical (one byte in, one ack byte out).
/// </summary>
public sealed class LauncherControlTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static CancellationToken Ct => new CancellationTokenSource(Timeout).Token;

    private readonly string _socketPath;
    private readonly byte[] _nonce;

    public LauncherControlTests()
    {
        // Short stem: macOS sun_path is 104 bytes.
        _socketPath = Path.Combine(Path.GetTempPath(), $"bvllc-{Guid.NewGuid():N}"[..12] + ".sock");
        _nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        Environment.SetEnvironmentVariable(LauncherControl.SocketEnv, _socketPath);
        Environment.SetEnvironmentVariable(LauncherControl.TokenEnv, Convert.ToHexString(_nonce));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(LauncherControl.SocketEnv, null);
        Environment.SetEnvironmentVariable(LauncherControl.TokenEnv, null);
    }

    // ── Payload codec ────────────────────────────────────────────────────

    [Theory]
    [InlineData("/Users/ikari/Documents", false, null)]
    [InlineData("/", true, null)]
    [InlineData("/tmp/ünïcode path", false, "/tmp/ünïcode path/a-file.txt")]
    public void SpawnFiler_payload_round_trips(string openPath, bool search, string? selectPath)
    {
        var payload = LauncherControl.EncodeSpawnFiler(openPath, search, selectPath);
        Assert.Equal((byte)LauncherControl.Command.SpawnFiler, payload[0]);

        var decoded = LauncherControl.DecodeSpawnFiler(payload);
        Assert.NotNull(decoded);
        Assert.Equal(openPath, decoded!.Value.OpenPath);
        Assert.Equal(search, decoded.Value.Search);
        Assert.Equal(selectPath, decoded.Value.SelectPath);
    }

    [Fact]
    public void SpawnFiler_decode_rejects_malformed_payloads()
    {
        Assert.Null(LauncherControl.DecodeSpawnFiler(Array.Empty<byte>()));
        Assert.Null(LauncherControl.DecodeSpawnFiler(new byte[] { (byte)LauncherControl.Command.Quit, 0, (byte)'x' }));
        Assert.Null(LauncherControl.DecodeSpawnFiler(new byte[] { (byte)LauncherControl.Command.SpawnFiler, 2, 0x41 })); // has-select but no NUL
        Assert.Null(LauncherControl.DecodeSpawnFiler(new byte[] { (byte)LauncherControl.Command.SpawnFiler, 0 }));     // no path at all
    }

    // ── Live round-trip ─────────────────────────────────────────────────

    [Fact]
    public async Task TrySpawnFiler_round_trips_through_the_launcher_transport()
    {
        byte[]? seen = null;
        await using var server = new UdsMessageServer(_socketPath, _nonce,
            (_, payload, _) =>
            {
                seen = payload.ToArray();
                return ValueTask.FromResult(new byte[] { 1 });
            });
        server.Start();

        Assert.True(LauncherControl.TrySpawnFiler("/tmp/target", search: true, selectPath: "/tmp/target/one.txt"));
        var decoded = Assert.NotNull(LauncherControl.DecodeSpawnFiler(seen));
        Assert.Equal("/tmp/target", decoded.OpenPath);
        Assert.True(decoded.Search);
        Assert.Equal("/tmp/target/one.txt", decoded.SelectPath);
    }

    [Fact]
    public async Task TrySpawnFiler_reads_a_NACK_as_false()
    {
        await using var server = new UdsMessageServer(_socketPath, _nonce,
            (_, _, _) => ValueTask.FromResult(new byte[] { 0 }));
        server.Start();
        Assert.False(LauncherControl.TrySpawnFiler("/tmp/x"));
    }

    // ── LauncherFilerSpawner routing ─────────────────────────────────────

    private sealed class RecordingSpawner : IFilerSpawner
    {
        public List<(string Path, string? Select, bool Search)> Calls { get; } = new();
        public void Spawn(string path, string? selectPath = null, bool search = false)
            => Calls.Add((path, selectPath, search));
    }

    [Fact]
    public async Task Supervised_spawn_with_ack_does_not_fall_back()
    {
        var fallback = new RecordingSpawner();
        await using var server = new UdsMessageServer(_socketPath, _nonce,
            (_, _, _) => ValueTask.FromResult(new byte[] { 1 }));
        server.Start();

        new LauncherFilerSpawner(fallback).Spawn("/tmp/a", selectPath: "/tmp/a/b", search: true);

        Assert.Empty(fallback.Calls);   // the launcher owned the open
    }

    [Fact]
    public void Unreachable_or_unsupervised_launcher_falls_back_in_process()
    {
        // Supervised but nothing listening at the socket → fallback (no launcher is the next case).
        var fallback = new RecordingSpawner();
        new LauncherFilerSpawner(fallback).Spawn("/tmp/a");
        Assert.Single(fallback.Calls);

        // Unsupervised entirely (env cleared) → fallback.
        Environment.SetEnvironmentVariable(LauncherControl.SocketEnv, null);
        Environment.SetEnvironmentVariable(LauncherControl.TokenEnv, null);
        fallback.Calls.Clear();
        new LauncherFilerSpawner(fallback).Spawn("/tmp/b", search: true);
        Assert.Equal(("/tmp/b", (string?)null, true), fallback.Calls.Single());
    }
}
