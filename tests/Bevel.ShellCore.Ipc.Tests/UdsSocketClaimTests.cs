using System.Net.Sockets;
using System.Security.Cryptography;
using Bevel.ShellCore.Ipc;
using Xunit;

namespace Bevel.ShellCore.Ipc.Tests;

/// <summary>
/// The socket-claim policy of bevel-wio0 over real Unix domain sockets: a second server on a LIVE
/// path stands down (and never unlinks the incumbent), a STALE path (owner gone) is reclaimed, a
/// server only ever unlinks a path it still owns, and the authenticated probe tells "my server" apart
/// from "some server". Short socket paths — macOS <c>sun_path</c> is 104 bytes.
/// </summary>
public sealed class UdsSocketClaimTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static CancellationToken Ct => new CancellationTokenSource(Timeout).Token;

    private static string NewSocketPath()
        => Path.Combine(Path.GetTempPath(), $"bvlclm-{Guid.NewGuid():N}"[..15] + ".sock");

    private static byte[] NewNonce() => RandomNumberGenerator.GetBytes(32);

    private static UdsMessageServer NewServer(string path, byte[] nonce) =>
        new(path, nonce, (_, _, _) => ValueTask.FromResult(Array.Empty<byte>()));

    /// <summary>A bare listener on the path. Disposing it closes the fd and leaves the file behind —
    /// what a crashed owner leaves. (A managed <c>Socket.Bind</c> would NOT do: .NET deletes a bound
    /// UDS path on Dispose, which is precisely the by-path unlink this claim exists to avoid.)</summary>
    private static Socket RawListener(string path) => UdsSocketClaim.BindListener(path, backlog: 4);

    private static async Task AssertConnects(string path, byte[] nonce)
    {
        await using var client = new UdsMessageClient(path, nonce, "shell");
        await client.ConnectAsync(Ct);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Second_server_on_a_live_path_stands_down_and_the_first_keeps_serving()
    {
        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var first = NewServer(path, nonce);
        first.Start();

        // "Two launches against one socket dir": the second must NOT take the path over.
        var second = NewServer(path, NewNonce());
        var busy = Assert.Throws<UdsSocketBusyException>(second.Start);
        Assert.Equal(path, busy.SocketPath);

        // The incumbent is untouched: still the same file, still answering with ITS nonce.
        await AssertConnects(path, nonce);

        // Disposing the stood-down instance must not unlink the incumbent's socket either — the
        // "old shell exiting kills bevelctl for the current shell" half of the bug.
        await second.DisposeAsync();
        Assert.True(File.Exists(path));
        await AssertConnects(path, nonce);
    }

    [Fact]
    public async Task Stale_socket_of_a_dead_owner_is_reclaimed()
    {
        var path = NewSocketPath();
        RawListener(path).Dispose(); // owner "crashed": file present, nobody behind it
        Assert.True(File.Exists(path));
        Assert.False(UdsSocketClaim.IsServing(path));

        var nonce = NewNonce();
        await using var server = NewServer(path, nonce);
        server.Start(); // must not throw — a crash never wedges the next start

        await AssertConnects(path, nonce);
    }

    [Fact]
    public async Task Dispose_unlinks_a_path_the_server_still_owns()
    {
        var path = NewSocketPath();
        var server = NewServer(path, NewNonce());
        server.Start();
        Assert.True(File.Exists(path));

        await server.DisposeAsync();

        Assert.False(File.Exists(path)); // clean exit leaves a clean path for the next start
    }

    [Fact]
    public async Task Dispose_leaves_a_path_another_live_listener_has_since_taken()
    {
        var path = NewSocketPath();
        var server = NewServer(path, NewNonce());
        server.Start();

        // An old binary (unconditional unlink + rebind) hijacks the path while we're up.
        File.Delete(path);
        using var hijacker = RawListener(path);
        Assert.True(UdsSocketClaim.IsServing(path));

        // Our exit must not take the hijacker's live socket down with us.
        await server.DisposeAsync();

        Assert.True(File.Exists(path));
        Assert.True(UdsSocketClaim.IsServing(path));
    }

    [Fact]
    public void IsServing_distinguishes_absent_stale_and_live()
    {
        var path = NewSocketPath();
        Assert.False(UdsSocketClaim.IsServing(path)); // absent

        var live = RawListener(path);
        Assert.True(UdsSocketClaim.IsServing(path));  // live

        live.Dispose();
        Assert.True(File.Exists(path));
        Assert.False(UdsSocketClaim.IsServing(path)); // stale: file present, ECONNREFUSED
        File.Delete(path);
    }

    [Fact]
    public async Task Probe_is_true_only_for_the_matching_nonce_and_never_registers_a_client()
    {
        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = NewServer(path, nonce);
        var snapshotsPushed = 0;
        server.ClientConnected += _ => Interlocked.Increment(ref snapshotsPushed);
        server.Start();

        Assert.True(await UdsMessageClient.ProbeAsync(path, nonce, Ct));

        // A probe is answered and closed at the handshake: no client registered, no snapshot pushed —
        // it must be cheap enough for a supervisor to fire every second.
        await Task.Delay(50);
        Assert.Equal(0, server.ClientCount);
        Assert.Equal(0, snapshotsPushed);

        // The hijack case: a live server that is NOT ours (different session nonce) is not "healthy".
        Assert.False(await UdsMessageClient.ProbeAsync(path, NewNonce(), Ct));

        // A real client is unaffected by the probe path.
        await AssertConnects(path, nonce);
    }

    [Fact]
    public async Task Probe_is_false_for_absent_and_stale_paths()
    {
        var path = NewSocketPath();
        Assert.False(await UdsMessageClient.ProbeAsync(path, NewNonce(), Ct)); // absent

        RawListener(path).Dispose();
        Assert.False(await UdsMessageClient.ProbeAsync(path, NewNonce(), Ct)); // stale
        File.Delete(path);
    }
}
