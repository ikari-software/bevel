using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Grpc.Net.Client;
using Bevel.Ipc.V1;

namespace Bevel.Ipc;

/// <summary>
/// gRPC client for the BevelHelper supervision service.
/// Wraps the generated <see cref="SupervisionService.SupervisionServiceClient"/>.
/// Capability-scoped authentication: each RPC carries an HMAC-derived token
/// specific to its capability domain (U2 / bevel-l3o).
/// </summary>
public sealed class HelperClient : IDisposable
{
    /// <summary>Metadata header carrying the capability-scoped auth token (IPC-04).</summary>
    internal const string TokenHeader = "x-bevel-token";

    private GrpcChannel? _channel;
    private SupervisionService.SupervisionServiceClient? _client;
    private string? _token;
    private IReadOnlySet<string> _capabilities = new HashSet<string>();

    /// <summary>True if currently connected to a helper instance.</summary>
    public bool IsConnected => _channel is not null;

    /// <summary>Returns the underlying gRPC channel for creating typed service clients.</summary>
    public GrpcChannel? GetChannel() => _channel;

    /// <summary>
    /// Connects to the helper's gRPC server over a Unix domain socket. The optional
    /// <paramref name="token"/> is the per-session nonce used as the HMAC key for
    /// capability-scoped authentication. If <paramref name="capabilities"/> is provided,
    /// only those capabilities are enabled for this client.
    /// </summary>
    public void Connect(string socketPath, string? token = null, IReadOnlySet<string>? capabilities = null)
    {
        Disconnect();
        _token = token;
        _capabilities = capabilities ?? new HashSet<string>();

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
            // A window list carries a small icon per window; on a busy desktop the
            // aggregated ListWindows reply can exceed the 4 MB default and fail with
            // ResourceExhausted. Icons are rendered at 16x16 (~1 KB) so this is
            // headroom, not a crutch.
            MaxReceiveMessageSize = 16 * 1024 * 1024,
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

        var reply = await _client.PingAsync(new PingRequest(),
            headers: BuildAuthMetadata(_token, "supervision"),
            cancellationToken: ct);
        return reply.HelperVersion;
    }

    /// <summary>Builds request metadata carrying the capability-scoped HMAC token.</summary>
    public static Grpc.Core.Metadata BuildAuthMetadata(string? token, string capability)
    {
        var metadata = new Grpc.Core.Metadata();
        if (!string.IsNullOrEmpty(token))
        {
            var hmac = ComputeHmac(token, capability);
            metadata.Add(TokenHeader, $"{capability}:{hmac}");
        }
        return metadata;
    }

    /// <summary>Computes HMAC-SHA256(key, message) and returns the lowercase hex digest.</summary>
    internal static string ComputeHmac(string key, string message)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        return Convert.ToHexStringLower(hash);
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
