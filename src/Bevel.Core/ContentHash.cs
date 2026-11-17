namespace Bevel.Core;

/// <summary>
/// Cheap, stable content keys for opaque blobs — chiefly the icon PNGs that travel the shell.
///
/// <para>Three places need the same answer for the same bytes: the taskbar's window-icon cache and
/// favicon cache (decode once per distinct image rather than once per use), and the core's delta
/// broadcast (send an icon only when it actually changed — bevel-ougs). They were about to be three
/// copies of the same six lines, so it lives here instead.</para>
///
/// <para>FNV-1a, NOT a cryptographic hash: these keys decide "same image as before", never trust or
/// identity, and the cost is paid on every window event. A collision would reuse one decoded bitmap
/// for two different icons or skip one re-send — not a safety problem. Don't repurpose it for
/// anything where an adversary picks the input.</para>
/// </summary>
public static class ContentHash
{
    /// <summary>FNV-1a 64-bit over <paramref name="data"/>. Empty input hashes to the offset basis,
    /// so callers that mean "no blob" should test for that case themselves rather than relying on a
    /// sentinel value.</summary>
    public static long Fnv1a64(ReadOnlySpan<byte> data)
    {
        const ulong offset = 14695981039346656037UL, prime = 1099511628211UL;
        var h = offset;
        foreach (var b in data) { h ^= b; h *= prime; }
        return unchecked((long)h);
    }
}
