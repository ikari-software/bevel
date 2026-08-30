using System.Text;
using Bevel.Pal.MacOS;
using Xunit;

namespace Bevel.Pal.MacOS.Tests;

// Pure-logic coverage for the Gecko session-store reader (bevel-l17f): the LZ4 block decoder, the
// mozLz4 envelope, and the title→url extraction with its refuse-to-guess duplicate policy.
public class GeckoSessionStoreTests
{
    // Literals-only LZ4 block encoder — enough to wrap arbitrary payloads for the decoder tests.
    private static byte[] Lz4Literals(byte[] payload)
    {
        var outBuf = new List<byte>();
        var len = payload.Length;
        outBuf.Add((byte)(Math.Min(len, 15) << 4));
        if (len >= 15)
        {
            var rest = len - 15;
            while (rest >= 255) { outBuf.Add(255); rest -= 255; }
            outBuf.Add((byte)rest);
        }
        outBuf.AddRange(payload);
        return outBuf.ToArray();
    }

    private static byte[] MozLz4(byte[] payload)
    {
        var outBuf = new List<byte>();
        outBuf.AddRange("mozLz40\0"u8.ToArray());
        outBuf.AddRange(BitConverter.GetBytes((uint)payload.Length));
        outBuf.AddRange(Lz4Literals(payload));
        return outBuf.ToArray();
    }

    [Fact]
    public void Literals_only_block_round_trips()
    {
        var payload = Encoding.UTF8.GetBytes(new string('j', 300) + " json-ish tail");
        var decoded = GeckoSessionStore.Lz4BlockDecompress(Lz4Literals(payload), payload.Length);
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void Overlapping_match_expands_a_run()
    {
        // "a" literal + match(offset 1, len 6) ⇒ "aaaaaaa" — offset shorter than length is how LZ4
        // encodes runs; a memmove-style copy corrupts this, byte-wise is required.
        var block = new byte[] { 0x12, (byte)'a', 0x01, 0x00 };
        var decoded = GeckoSessionStore.Lz4BlockDecompress(block, 7);
        Assert.Equal("aaaaaaa", Encoding.UTF8.GetString(decoded!));
    }

    [Fact]
    public void Match_length_extension_bytes_decode()
    {
        // token low nibble 15 ⇒ match length continues in extension bytes: "a" + match(offset 1,
        // len 15+4+3=22) ⇒ 23 a's. Pins the nibble==15 continuation path.
        var block = new byte[] { 0x1F, (byte)'a', 0x01, 0x00, 0x03 };
        var decoded = GeckoSessionStore.Lz4BlockDecompress(block, 23);
        Assert.Equal(new string('a', 23), Encoding.UTF8.GetString(decoded!));
    }

    [Theory]
    [InlineData(new byte[] { 0x10 }, 1)]                 // literal promised, none present
    [InlineData(new byte[] { 0x12, (byte)'a', 0x05, 0x00 }, 7)]   // match offset beyond output
    [InlineData(new byte[] { 0x12, (byte)'a', 0x01, 0x00 }, 99)]  // output shorter than promised
    public void Malformed_blocks_return_null_not_garbage(byte[] block, int dstSize)
        => Assert.Null(GeckoSessionStore.Lz4BlockDecompress(block, dstSize));

    [Fact]
    public void MozLz4_envelope_is_validated_and_decoded()
    {
        var payload = Encoding.UTF8.GetBytes("{\"windows\":[]}");
        Assert.Equal(payload, GeckoSessionStore.DecodeMozLz4(MozLz4(payload)));
        Assert.Null(GeckoSessionStore.DecodeMozLz4(Encoding.UTF8.GetBytes("not-mozlz4-at-all")));
    }

    // ── title→url extraction ─────────────────────────────────────────────────

    private static IReadOnlyDictionary<string, string> Parse(string json)
        => GeckoSessionStore.ParseTitleToUrl(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Current_history_entry_is_selected_by_index()
    {
        // Two history entries, index=1 ⇒ the FIRST entry is what the tab shows (user went back).
        var map = Parse("""
            {"windows":[{"tabs":[
              {"index":1,"entries":[{"title":"Old","url":"https://old.test/"},{"title":"New","url":"https://new.test/"}]}
            ]}]}
            """);
        Assert.Equal("https://old.test/", map["Old"]);
        Assert.False(map.ContainsKey("New"));
    }

    [Fact]
    public void Duplicate_titles_with_different_urls_are_dropped_not_guessed()
    {
        var map = Parse("""
            {"windows":[{"tabs":[
              {"index":1,"entries":[{"title":"Dash","url":"https://a.test/"}]},
              {"index":1,"entries":[{"title":"Dash","url":"https://b.test/"}]},
              {"index":1,"entries":[{"title":"Same","url":"https://s.test/"}]},
              {"index":1,"entries":[{"title":"Same","url":"https://s.test/"}]}
            ]}]}
            """);
        Assert.False(map.ContainsKey("Dash"));          // ambiguous — poisoned
        Assert.Equal("https://s.test/", map["Same"]);   // duplicate but consistent — kept
    }

    [Fact]
    public void Tabs_without_entries_or_titles_are_skipped()
    {
        var map = Parse("""
            {"windows":[{"tabs":[
              {"index":1,"entries":[]},
              {"index":1,"entries":[{"url":"https://untitled.test/"}]},
              {"index":1,"entries":[{"title":"OK","url":"https://ok.test/"}]}
            ]}]}
            """);
        var pair = Assert.Single(map);
        Assert.Equal("OK", pair.Key);
    }
}
