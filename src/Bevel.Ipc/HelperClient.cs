using System.Net.Sockets;
using Grpc.Net.Client;
using Bevel.Ipc.V1;

namespace Bevel.Ipc;

/// <summary>
/// gRPC client for the BevelHelper supervision service.
/// Wraps the generated <see cref="SupervisionService.SupervisionServiceClient"/>.
/// </summary>
public sealed class HelperClient : IDisposable
{
    /// <summary>Metadata header carrying the per-session authentication nonce (IPC-04).</summary>
    internal const string TokenHeader = "x-bevel-token";

    private GrpcChannel? _channel;
    private SupervisionService.SupervisionServiceClient? _client;
    private string? _token;

    /// <summary>True if currently connected to a helper instance.</summary>
    public bool IsConnected => _channel is not null;

    /// <summary>
    /// Connects to the helper's gRPC server over a Unix domain socket. The optional
    /// <paramref name="token"/> is the per-session nonce sent with every RPC so the helper
    /// can reject callers that did not receive it (the socket path itself is world-visible).
    /// </summary>
    public void Connect(string socketPath, string? token = null)
    {
        Disconnect();
        _token = token;

        // Grpc.Net.Client cannot dial a "unix://" address on its own — it needs a
        // SocketsHttpHandler whose ConnectCallback opens the Unix domain socket. The
        // address is a placeholder used only for HTTP/2 (h2c) framing; the callback does
        // the real connection.
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) => await ConnectUnixSocketAsync(socketPath, ct),
            EnableMultipleHttp2Connections = true,
        };

        _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler,
            Credentials = Grpc.Core.ChannelCredentials.Insecure,
        });
        _client = new SupervisionService.SupervisionServiceClient(_channel);
    }

    /// <summary>
    /// Opens a connected stream to a Unix domain socket. Shared by the gRPC
    /// ConnectCallback and by transport tests.
    /// </summary>
    internal static async ValueTask<Stream> ConnectUnixSocketAsync(string socketPath, CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Sends a Ping to the helper and returns the helper version string.
    /// Throws if not connected or if the helper is unreachable.
    /// </summary>
    public async Task<string> PingAsync(CancellationToken ct = default)
    {
        if (_client is null)
            throw new InvalidOperationException("Not connected to helper. Call Connect() first.");

        var reply = await _client.PingAsync(new PingRequest(), headers: BuildAuthMetadata(_token), cancellationToken: ct);
        return reply.HelperVersion;
    }

    /// <summary>Builds the request metadata carrying the auth nonce (empty when no token).</summary>
    internal static Grpc.Core.Metadata BuildAuthMetadata(string? token)
    {
        var metadata = new Grpc.Core.Metadata();
        if (!string.IsNullOrEmpty(token))
            metadata.Add(TokenHeader, token);
        return metadata;
    }

    /// <summary>Closes the gRPC channel.</summary>
    public void Disconnect()
    {
        _channel?.Dispose();
        _channel = null;
        _client = null;
    }

    public void Dispose()
    {
        Disconnect();
    }
}
