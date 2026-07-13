using System.Security.Cryptography;
using System.Text;

namespace Bevel.ShellCore.Ipc;

/// <summary>
/// Shared-secret handshake, mirroring the existing Bevel helper scheme
/// (<c>src/Bevel.Ipc/HelperClient.cs</c> ComputeHmac/BuildAuthMetadata): the client proves
/// it knows a per-session <c>nonce</c> without ever putting the nonce on the wire. The
/// <see cref="FrameKind.HandshakeHello"/> payload is UTF-8 <c>"&lt;capability&gt;:&lt;hmacHex&gt;"</c>
/// where <c>hmacHex = HMAC-SHA256(key: nonce, message: capability)</c>, lowercase hex.
/// The nonce plumbing (env var, launch arg, …) is the caller's job, not this library's.
/// </summary>
internal static class Handshake
{
    /// <summary>
    /// Computes <c>HMAC-SHA256(key: nonce, message: capability)</c> as lowercase hex.
    /// The capability string is the HMAC message and the nonce bytes are the key —
    /// same construction as the helper client, so both sides agree byte-for-byte.
    /// </summary>
    public static string ComputeHmac(byte[] nonce, string capability)
    {
        using var hmac = new HMACSHA256(nonce);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(capability));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Builds the HandshakeHello payload: UTF-8 <c>"capability:hmacHex"</c>.</summary>
    public static byte[] BuildHelloPayload(byte[] nonce, string capability)
        => Encoding.UTF8.GetBytes($"{capability}:{ComputeHmac(nonce, capability)}");

    /// <summary>
    /// Validates a HandshakeHello payload against the shared nonce. Returns the presented
    /// capability on success, or <c>null</c> if the frame is malformed or the HMAC does not
    /// verify. The comparison decodes the presented hex to bytes and uses
    /// <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>
    /// so a timing side channel can't be used to forge a token digit by digit.
    /// </summary>
    public static string? ValidateHello(byte[] nonce, ReadOnlySpan<byte> payload)
    {
        string text;
        try { text = Encoding.UTF8.GetString(payload); }
        catch { return null; }

        // Split on the FIRST ':' — a capability could in principle contain more, though
        // the hex digest never does.
        var sep = text.IndexOf(':');
        if (sep <= 0 || sep == text.Length - 1)
            return null;

        var capability = text[..sep];
        var presentedHex = text[(sep + 1)..];

        byte[] presented;
        try { presented = Convert.FromHexString(presentedHex); }
        catch { return null; }

        var expected = Convert.FromHexString(ComputeHmac(nonce, capability));
        return CryptographicOperations.FixedTimeEquals(presented, expected) ? capability : null;
    }
}
