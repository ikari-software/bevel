using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Bevel.ShellCore.Ipc;
using Xunit;

namespace Bevel.ShellCore.Ipc.Tests;

/// <summary>
/// End-to-end transport tests over a real Unix domain socket. Each test binds a fresh, SHORT
/// socket path under the temp dir — macOS <c>sun_path</c> is only 104 bytes, so a long random
/// GUID path can silently overflow; a short random stem keeps us well inside the limit.
/// </summary>
public sealed class UdsMessageTransportTests
{
    // A generous-but-bounded ceiling so a hung await fails the run instead of stalling CI.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static string NewSocketPath()
        // 8 hex chars keeps the whole path short; /tmp on macOS is itself a short prefix.
        => Path.Combine(Path.GetTempPath(), $"bvlipc-{Guid.NewGuid():N}"[..14] + ".sock");

    private static byte[] NewNonce() => RandomNumberGenerator.GetBytes(32);

    private static UdsMessageServer StartServer(
        string path, byte[] nonce,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]>>? onRequest = null)
    {
        var server = new UdsMessageServer(
            path, nonce,
            onRequest ?? ((_, _) => ValueTask.FromResult(Array.Empty<byte>())));
        server.Start();
        return server;
    }

    private static CancellationToken Ct => new CancellationTokenSource(Timeout).Token;

    /// <summary>Waits for a condition to hold, polling briefly — avoids racy fixed sleeps.</summary>
    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(15);
        Assert.True(condition(), "condition not met within timeout");
    }

    // 1. Handshake success: a correct-nonce client connects and receives a subsequent broadcast.
    [Fact]
    public async Task Handshake_Success_ClientReceivesBroadcast()
    {
        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = StartServer(path, nonce);

        await using var client = new UdsMessageClient(path, nonce, "shell");
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.BroadcastReceived += b => received.TrySetResult(b);

        await client.ConnectAsync(Ct);
        await WaitFor(() => server.ClientCount == 1);

        var payload = Encoding.UTF8.GetBytes("hello-world");
        server.Broadcast(payload);

        var got = await received.Task.WaitAsync(Timeout);
        Assert.Equal(payload, got);
    }

    // 2. Handshake rejection: a wrong-nonce client is rejected and cannot receive broadcasts.
    [Fact]
    public async Task Handshake_WrongNonce_IsRejected()
    {
        var path = NewSocketPath();
        await using var server = StartServer(path, NewNonce());

        await using var client = new UdsMessageClient(path, NewNonce() /* wrong */, "shell");
        var gotBroadcast = false;
        client.BroadcastReceived += _ => gotBroadcast = true;

        await Assert.ThrowsAsync<IOException>(() => client.ConnectAsync(Ct));

        // Server stays healthy; a broadcast to zero authenticated clients is a no-op.
        server.Broadcast(Encoding.UTF8.GetBytes("nope"));
        await Task.Delay(100);
        Assert.False(gotBroadcast);
        Assert.Equal(0, server.ClientCount);
    }

    // 3. Broadcast fan-out: 3 clients each receive an N-message burst, intact and in order.
    [Fact]
    public async Task Broadcast_FanOut_AllClientsReceiveBurstInOrder()
    {
        const int clientCount = 3;
        const int messageCount = 50;
        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = StartServer(path, nonce);

        var clients = new List<UdsMessageClient>();
        var inboxes = new List<ConcurrentQueue<int>>();
        var completions = new List<TaskCompletionSource>();

        for (var i = 0; i < clientCount; i++)
        {
            var inbox = new ConcurrentQueue<int>();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var client = new UdsMessageClient(path, nonce, "shell");
            client.BroadcastReceived += b =>
            {
                inbox.Enqueue(BitConverter.ToInt32(b));
                if (inbox.Count == messageCount) done.TrySetResult();
            };
            await client.ConnectAsync(Ct);
            clients.Add(client);
            inboxes.Add(inbox);
            completions.Add(done);
        }

        try
        {
            await WaitFor(() => server.ClientCount == clientCount);

            for (var i = 0; i < messageCount; i++)
                server.Broadcast(BitConverter.GetBytes(i));

            await Task.WhenAll(completions.Select(c => c.Task)).WaitAsync(Timeout);

            foreach (var inbox in inboxes)
                Assert.Equal(Enumerable.Range(0, messageCount), inbox.ToArray());
        }
        finally
        {
            foreach (var c in clients) await c.DisposeAsync();
        }
    }

    // 4. Request/response: several concurrent in-flight requests each get their matching response.
    [Fact]
    public async Task RequestResponse_ConcurrentRequests_CorrelateCorrectly()
    {
        var path = NewSocketPath();
        var nonce = NewNonce();
        // Handler transforms the payload so we can assert the RIGHT reply came back per request:
        // it appends a byte and, to stress correlation, adds a small variable delay per request.
        await using var server = StartServer(path, nonce, async (req, ct) =>
        {
            var n = req.Span[0];
            await Task.Delay(n % 7, ct); // jitter so responses can complete out of send order
            return [n, (byte)(n + 1)];
        });

        await using var client = new UdsMessageClient(path, nonce, "shell");
        await client.ConnectAsync(Ct);

        var tasks = Enumerable.Range(0, 40).Select(async i =>
        {
            var reply = await client.RequestAsync(new byte[] { (byte)i }, Ct);
            Assert.Equal(new byte[] { (byte)i, (byte)(i + 1) }, reply);
        });
        await Task.WhenAll(tasks).WaitAsync(Timeout);
    }

    // 5. Disconnect cleanup: a client disposing mid-stream is dropped; the server keeps serving.
    [Fact]
    public async Task ClientDisconnect_IsCleanedUp_ServerStaysHealthy()
    {
        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = StartServer(path, nonce);

        var doomed = new UdsMessageClient(path, nonce, "shell");
        await doomed.ConnectAsync(Ct);

        await using var survivor = new UdsMessageClient(path, nonce, "shell");
        var survivorReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        survivor.BroadcastReceived += b => survivorReceived.TrySetResult(b);
        await survivor.ConnectAsync(Ct);

        await WaitFor(() => server.ClientCount == 2);

        // Drop one client; the server's pump must notice and evict it.
        await doomed.DisposeAsync();
        await WaitFor(() => server.ClientCount == 1);

        // A later broadcast must not throw and must still reach the survivor.
        server.Broadcast(Encoding.UTF8.GetBytes("still-alive"));
        var got = await survivorReceived.Task.WaitAsync(Timeout);
        Assert.Equal("still-alive", Encoding.UTF8.GetString(got));
    }

    // 6. Large payload round-trips intact; an over-limit frame is rejected without stream corruption.
    [Fact]
    public async Task LargePayload_RoundTrips_AndOverLimitIsRejectedCleanly()
    {
        var path = NewSocketPath();
        var nonce = NewNonce();
        // Echo handler.
        await using var server = StartServer(path, nonce, (req, _) => ValueTask.FromResult(req.ToArray()));

        await using var client = new UdsMessageClient(path, nonce, "shell");
        await client.ConnectAsync(Ct);

        // 1 MiB round-trip, content-checked.
        var big = RandomNumberGenerator.GetBytes(1024 * 1024);
        var echoed = await client.RequestAsync(big, Ct);
        Assert.Equal(big, echoed);

        // Over the 32 MiB limit: rejected by the codec BEFORE any bytes hit the wire, so the
        // connection is not corrupted...
        var tooBig = new byte[32 * 1024 * 1024 + 1];
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.RequestAsync(tooBig, Ct));

        // ...and a normal request still works on the same connection afterward.
        var after = await client.RequestAsync(Encoding.UTF8.GetBytes("after"), Ct);
        Assert.Equal("after", Encoding.UTF8.GetString(after));
    }

    // 7. ClientConnected snapshot: the hook pushes an initial message to ONLY the new client.
    [Fact]
    public async Task ClientConnected_Hook_PushesInitialSnapshotToNewClient()
    {
        var path = NewSocketPath();
        var nonce = NewNonce();
        var snapshot = Encoding.UTF8.GetBytes("SNAPSHOT");
        await using var server = StartServer(path, nonce);
        server.ClientConnected += send => _ = send(snapshot);

        await using var client = new UdsMessageClient(path, nonce, "shell");
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.BroadcastReceived += b => received.TrySetResult(b);
        await client.ConnectAsync(Ct);

        var got = await received.Task.WaitAsync(Timeout);
        Assert.Equal(snapshot, got);
    }

    // Bonus: the connect snapshot is delivered BEFORE any subsequent broadcast (ordering guarantee).
    [Fact]
    public async Task ClientConnected_Snapshot_PrecedesLaterBroadcasts()
    {
        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = StartServer(path, nonce);
        server.ClientConnected += send => _ = send(Encoding.UTF8.GetBytes("0-snapshot"));

        var order = new ConcurrentQueue<string>();
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = new UdsMessageClient(path, nonce, "shell");
        client.BroadcastReceived += b =>
        {
            order.Enqueue(Encoding.UTF8.GetString(b));
            if (order.Count == 2) second.TrySetResult();
        };
        await client.ConnectAsync(Ct);
        await WaitFor(() => server.ClientCount == 1);

        server.Broadcast(Encoding.UTF8.GetBytes("1-delta"));
        await second.Task.WaitAsync(Timeout);

        Assert.Equal(new[] { "0-snapshot", "1-delta" }, order.ToArray());
    }

    // Review (correctness): IsConnected must go false once the receive loop faults, not stay true
    // forever. Drop the server and the client's connection state must flip.
    [Fact]
    public async Task IsConnected_GoesFalse_AfterServerDrop()
    {
        var path = NewSocketPath();
        var nonce = NewNonce();
        var server = StartServer(path, nonce);

        await using var client = new UdsMessageClient(path, nonce, "shell");
        await client.ConnectAsync(Ct);
        Assert.True(client.IsConnected);

        await server.DisposeAsync();   // kills the client's receive loop
        await WaitFor(() => !client.IsConnected);
        Assert.False(client.IsConnected);
    }

    // Review (adversarial): a throwing BroadcastReceived handler must NOT tear down the connection —
    // only genuine stream faults should. A second broadcast still arrives at a well-behaved handler.
    [Fact]
    public async Task ThrowingBroadcastHandler_DoesNotDropConnection()
    {
        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = StartServer(path, nonce);

        await using var client = new UdsMessageClient(path, nonce, "shell");
        var secondSeen = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        client.BroadcastReceived += b =>
        {
            if (first) { first = false; throw new InvalidOperationException("poison handler"); }
            secondSeen.TrySetResult(b);
        };
        await client.ConnectAsync(Ct);
        await WaitFor(() => server.ClientCount == 1);

        server.Broadcast(Encoding.UTF8.GetBytes("boom"));   // handler throws, connection must survive
        server.Broadcast(Encoding.UTF8.GetBytes("survived"));

        var got = await secondSeen.Task.WaitAsync(Timeout);
        Assert.Equal("survived", Encoding.UTF8.GetString(got));
        Assert.True(client.IsConnected);
    }
}
