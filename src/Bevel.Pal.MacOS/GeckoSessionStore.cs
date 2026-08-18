using System.Text.Json;

namespace Bevel.Pal.MacOS;

/// <summary>
/// Reads a Gecko profile's session store (bevel-l17f / bevel-y3cl): the AX tab tree exposes NO
/// URLs (probed live — AXHelp empty, no AXURL), but the profile's
/// <c>sessionstore-backups/recovery.jsonlz4</c> holds every open tab's title AND url, rewritten
/// ~15 s after any tab change. Titles bridge the two worlds: an AX-enumerated tab gets its url
/// (and thence its favicon) by exact title match. Ambiguity is dropped, not guessed — two tabs
/// sharing a title but not a url stay url-less rather than risk the wrong favicon.
///
/// The file is mozLz4: an 8-byte "mozLz40\0" magic, a little-endian uncompressed size, then one
/// raw LZ4 block. The ~60-line block decoder below beats a native lz4 dependency for a shell that
/// reads one ~2 MB file per right-click at most.
/// </summary>
internal static class GeckoSessionStore
{
    private static readonly byte[] Magic = "mozLz40\0"u8.ToArray();

    /// <summary>Title → url for every open tab in the freshest profile's saved session, or an empty
    /// map when there is no readable store. Duplicate titles with DIFFERENT urls map to null and are
    /// removed (refuse-to-guess, same policy as the tab-press fallback).</summary>
    public static IReadOnlyDictionary<string, string> TitleToUrl(string bundleId)
    {
        try
        {
            if (ResolveRecoveryFile(bundleId) is not { } path) return EmptyMap;
            var json = DecodeMozLz4(File.ReadAllBytes(path));
            if (json is null) return EmptyMap;
            return ParseTitleToUrl(json);
        }
        catch
        {
            return EmptyMap;   // torn write mid-read, malformed store — enrichment is best-effort
        }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyMap = new Dictionary<string, string>();

    /// <summary>The profile directory whose session store was written most recently — that is the
    /// running instance's profile for any single-instance browser.</summary>
    internal static string? ResolveRecoveryFile(string bundleId)
    {
        // The Gecko data root (e.g. "zen"/"Firefox") lives in the shared TabBrowserRegistry now
        // (bevel-gxrq) — for the Gecko family the favicon root and the session-store root are the
        // same Application Support dir. Non-Gecko / unknown ids have no session store.
        var entry = TabBrowserRegistry.For(bundleId);
        if (entry is not { IsGecko: true, FaviconRoot: { } rootName }) return null;
        var profiles = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", rootName, "Profiles");
        if (!Directory.Exists(profiles)) return null;

        string? best = null;
        var bestTime = DateTime.MinValue;
        foreach (var dir in Directory.EnumerateDirectories(profiles))
        {
            var candidate = Path.Combine(dir, "sessionstore-backups", "recovery.jsonlz4");
            if (!File.Exists(candidate)) continue;
            var t = File.GetLastWriteTimeUtc(candidate);
            if (t <= bestTime) continue;
            bestTime = t;
            best = candidate;
        }
        return best;
    }

    // ── mozLz4 ───────────────────────────────────────────────────────────────

    internal static byte[]? DecodeMozLz4(byte[] raw)
    {
        if (raw.Length < 12 || !raw.AsSpan(0, 8).SequenceEqual(Magic)) return null;
        var size = BitConverter.ToUInt32(raw, 8);
        // The size field is untrusted disk input feeding an allocation. Two independent caps: a
        // session store is single-digit MBs (64 MB is generous), and LZ4's maximum ratio is 255:1,
        // so any size beyond compressed*256 is provably a lie — refuse before allocating.
        if (size > 64 * 1024 * 1024 || size > (uint)(raw.Length - 12) * 256L) return null;
        return Lz4BlockDecompress(raw.AsSpan(12), (int)size);
    }

    /// <summary>Raw LZ4 block format: per sequence a token (literal-length nibble / match-length
    /// nibble, 15 ⇒ continue in extension bytes), the literals, then a 2-byte little-endian match
    /// offset + match copy. Byte-wise match copy is REQUIRED — overlapping matches (offset shorter
    /// than length) are how LZ4 encodes runs.</summary>
    internal static byte[]? Lz4BlockDecompress(ReadOnlySpan<byte> src, int dstSize)
    {
        var dst = new byte[dstSize];
        int i = 0, o = 0;
        while (i < src.Length)
        {
            int token = src[i++];
            var lit = token >> 4;
            if (lit == 15)
            {
                int b;
                do { if (i >= src.Length) return null; b = src[i++]; lit += b; } while (b == 255);
                if (lit < 0) return null;   // extension-byte overflow — hostile input
            }
            if (i + lit > src.Length || o + lit > dst.Length) return null;
            src.Slice(i, lit).CopyTo(dst.AsSpan(o));
            i += lit; o += lit;
            if (i >= src.Length) break;   // final sequence carries no match part

            if (i + 2 > src.Length) return null;
            int offset = src[i] | (src[i + 1] << 8);
            i += 2;
            if (offset == 0 || offset > o) return null;
            var mlen = (token & 0xF) + 4;
            if ((token & 0xF) == 15)
            {
                int b;
                do { if (i >= src.Length) return null; b = src[i++]; mlen += b; } while (b == 255);
                if (mlen < 0) return null;   // extension-byte overflow — hostile input
            }
            if (o + mlen > dst.Length) return null;
            var from = o - offset;
            for (var k = 0; k < mlen; k++) dst[o + k] = dst[from + k];
            o += mlen;
        }
        return o == dstSize ? dst : null;
    }

    // ── Session JSON ─────────────────────────────────────────────────────────

    internal static IReadOnlyDictionary<string, string> ParseTitleToUrl(byte[] json)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("windows", out var windows)) return EmptyMap;
        foreach (var window in windows.EnumerateArray())
        {
            if (!window.TryGetProperty("tabs", out var tabs)) continue;
            foreach (var tab in tabs.EnumerateArray())
            {
                if (!tab.TryGetProperty("entries", out var entries)) continue;
                var count = entries.GetArrayLength();
                if (count == 0) continue;
                // "index" is the 1-based position in the tab's history; the entry there is what
                // the tab currently shows.
                var index = tab.TryGetProperty("index", out var idx) && idx.TryGetInt32(out var v)
                    ? Math.Clamp(v, 1, count) : count;
                var current = entries[index - 1];
                var title = current.TryGetProperty("title", out var t) ? t.GetString() : null;
                var url = current.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(url)) continue;

                if (map.TryGetValue(title, out var existing))
                {
                    if (existing != url) map[title] = null;   // ambiguous — poison the entry
                }
                else
                {
                    map[title] = url;
                }
            }
        }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (title, url) in map)
            if (url is not null) result[title] = url;
        return result;
    }
}
