using System.Buffers;
using System.Buffers.Binary;

namespace Bevel.ShellCore.Ipc;

/// <summary>
/// The kind of a wire frame. The first header byte. Direction is fixed per kind so
/// a reader can reason about a frame without any other context.
/// </summary>
public enum FrameKind : byte
{
    /// <summary>server→client, unsolicited state push. Correlation id is always 0.</summary>
    Broadcast = 1,

    /// <summary>client→server. Correlation id is chosen by the client and echoed on the response.</summary>
    Request = 2,

    /// <summary>server→client, reply to a <see cref="Request"/>. Correlation id echoes the request.</summary>
    Response = 3,

    /// <summary>client→server, the mandatory first frame. Payload is the HMAC auth token.</summary>
    HandshakeHello = 4,

    /// <summary>server→client, handshake accepted; framed traffic may now flow.</summary>
    HandshakeOk = 5,

    /// <summary>server→client, handshake rejected; the connection is closed immediately after.</summary>
    HandshakeReject = 6,
}

/// <summary>A decoded frame: its kind, correlation id, and opaque payload bytes.</summary>
internal readonly struct Frame(FrameKind kind, uint correlationId, byte[] payload)
{
    public FrameKind Kind { get; } = kind;
    public uint CorrelationId { get; } = correlationId;
    public byte[] Payload { get; } = payload;
}

/// <summary>
/// The wire codec. Frame layout is a fixed 9-byte header then the payload:
/// <c>[1: FrameKind][4: correlationId, big-endian uint][4: length, big-endian int][length: payload]</c>.
/// Big-endian (network order) so the format is stable regardless of host endianness.
/// Reads use <see cref="Stream.ReadExactlyAsync(Memory{byte}, CancellationToken)"/> so a
/// short TCP/UDS read can never hand back a truncated header or payload.
/// </summary>
internal static class Framing
{
    /// <summary>Fixed header size: kind (1) + correlationId (4) + length (4).</summary>
    public const int HeaderSize = 9;

    /// <summary>
    /// Hard ceiling on a single payload (32 MiB). A negative or larger declared length
    /// is treated as a protocol violation — it caps memory a peer can force us to
    /// allocate, and stops a corrupt/hostile length prefix from wedging the reader.
    /// </summary>
    public const int MaxPayloadLength = 32 * 1024 * 1024;

    /// <summary>
    /// Serializes and writes one frame. The header is built on the stack and the payload
    /// is written straight from the caller's buffer, so a whole frame reaches the stream
    /// in two writes. Callers MUST serialize concurrent writes to the same stream
    /// (a per-connection lock or single-writer channel) — this method does not.
    /// Rejects an over-limit payload BEFORE touching the stream, so a bad size can never
    /// leave a half-written frame and desync the peer.
    /// </summary>
    public static async ValueTask WriteFrameAsync(
        Stream stream, FrameKind kind, uint correlationId, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length > MaxPayloadLength)
            throw new ArgumentOutOfRangeException(
                nameof(payload), $"Payload {payload.Length} B exceeds the {MaxPayloadLength} B frame limit.");

        var header = new byte[HeaderSize];
        header[0] = (byte)kind;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1, 4), correlationId);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(5, 4), payload.Length);

        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        if (!payload.IsEmpty)
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads exactly one frame, blocking (asynchronously) until the full header and payload
    /// have arrived. Throws <see cref="EndOfStreamException"/> if the peer closes mid-frame
    /// and <see cref="InvalidDataException"/> on an out-of-range declared length. The payload
    /// buffer is rented from the shared pool for the read, then copied into a right-sized
    /// array handed back to the caller — the rented buffer never escapes.
    /// </summary>
    public static async ValueTask<Frame> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);

        var kind = (FrameKind)header[0];
        var correlationId = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1, 4));
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(5, 4));

        if (length < 0 || length > MaxPayloadLength)
            throw new InvalidDataException(
                $"Declared payload length {length} is outside [0, {MaxPayloadLength}].");

        if (length == 0)
            return new Frame(kind, correlationId, Array.Empty<byte>());

        // Rent scratch space for the wire read, then hand the caller a private copy so the
        // rented buffer (which may be larger than the payload) can go back to the pool.
        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await stream.ReadExactlyAsync(rented.AsMemory(0, length), ct).ConfigureAwait(false);
            var payload = new byte[length];
            Array.Copy(rented, payload, length);
            return new Frame(kind, correlationId, payload);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
