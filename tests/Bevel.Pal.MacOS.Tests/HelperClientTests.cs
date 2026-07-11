using System.Net.Sockets;
using Bevel.Ipc;
using Xunit;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>
/// Tests for HelperClient's Unix-domain-socket transport (bevel-cwv): the ConnectCallback
/// primitive must actually dial a UDS, and the channel must be constructable.
/// </summary>
public class HelperClientTests
{
    [Fact]
    public async Task ConnectUnixSocketAsync_connects_to_a_listening_socket_and_streams()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bevel-uds-{Guid.NewGuid():N}.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);

        try
        {
            var acceptTask = listener.AcceptAsync();
            await using var clientStream = await HelperClient.ConnectUnixSocketAsync(path, CancellationToken.None);
            using var server = await acceptTask;

            await clientStream.WriteAsync(new byte[] { 1, 2, 3 });
            var buf = new byte[3];
            var read = await server.ReceiveAsync(buf);

            Assert.Equal(3, read);
            Assert.Equal(new byte[] { 1, 2, 3 }, buf);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task ConnectUnixSocketAsync_throws_when_no_socket_is_listening()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bevel-absent-{Guid.NewGuid():N}.sock");
        await Assert.ThrowsAnyAsync<SocketException>(
            () => HelperClient.ConnectUnixSocketAsync(path, CancellationToken.None).AsTask());
    }

    [Fact]
    public void Connect_builds_the_channel_and_Disconnect_tears_it_down()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bevel-{Guid.NewGuid():N}.sock");
        using var client = new HelperClient();

        client.Connect(path); // lazy — configures the UDS channel without dialing yet
        Assert.True(client.IsConnected);

        client.Disconnect();
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task PingAsync_without_connect_throws()
    {
        using var client = new HelperClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PingAsync());
    }

    [Fact]
    public void BuildAuthMetadata_carries_capability_scoped_token()
    {
        // U2: the token is now "capability:<hmac>" not the raw nonce.
        var md = HelperClient.BuildAuthMetadata("nonce-key", "supervision");
        var entry = Assert.Single(md);
        Assert.Equal(HelperClient.TokenHeader, entry.Key);
        Assert.StartsWith("supervision:", entry.Value);
        // The HMAC portion must be non-empty hex.
        var parts = entry.Value!.Split(':');
        Assert.Equal(2, parts.Length);
        Assert.Equal("supervision", parts[0]);
        Assert.Equal(64, parts[1].Length); // SHA256 → 32 bytes → 64 hex chars
    }

    [Fact]
    public void BuildAuthMetadata_different_capabilities_produce_different_tokens()
    {
        var mdSuper = HelperClient.BuildAuthMetadata("key", "supervision");
        var mdWindows = HelperClient.BuildAuthMetadata("key", "windows");
        Assert.NotEqual(mdSuper.GetValue(HelperClient.TokenHeader),
                        mdWindows.GetValue(HelperClient.TokenHeader));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildAuthMetadata_is_empty_without_a_token(string? token)
    {
        Assert.Empty(HelperClient.BuildAuthMetadata(token, "supervision"));
    }
}
