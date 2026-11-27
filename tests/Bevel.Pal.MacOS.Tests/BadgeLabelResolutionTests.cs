using Bevel.Pal.MacOS;
using Xunit;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>
/// The de-ellipsization rule in <see cref="MacOSAppBadgeSource.ResolveLabel"/>: the Dock truncates
/// wide badge counts (<c>"..82"</c> for 1082) and usernoted's db holds the raw integer. A
/// truncated-looking label with a raw count behind it is replaced in full (4+ digits included —
/// the pill has no cap); everything else passes through verbatim.
/// </summary>
public class BadgeLabelResolutionTests
{
    private static readonly IReadOnlyDictionary<string, int> Counts = new Dictionary<string, int>
    {
        ["org.telegram.Telegram"] = 1082,
        ["net.whatsapp.whatsapp"] = 12,
        ["com.example.capped"] = 143,
        ["com.example.thousand"] = 1000,
        ["com.example.zero"] = 0,
        ["com.example.negative"] = -5,
    };

    [Theory]
    [InlineData("..82", "org.telegram.Telegram", "1082")]   // macOS keeps the last digits
    [InlineData("…82", "org.telegram.Telegram", "1082")]    // single-char ellipsis variant
    [InlineData("..00", "com.example.thousand", "1000")]   // 1000 renders as "..00"
    [InlineData("99+", "com.example.capped", "143")]        // plus-cap variant
    public void Truncated_labels_are_replaced_with_the_raw_count(string axLabel, string bundleId, string expected)
    {
        Assert.Equal(expected, MacOSAppBadgeSource.ResolveLabel(axLabel, bundleId, Counts));
    }

    [Theory]
    [InlineData("12")]     // plain count: already exact, never rewritten
    [InlineData("24")]     // Calendar-style custom string badge
    [InlineData("•")]      // dot badge
    [InlineData("New")]    // word badge
    public void Full_labels_pass_through_verbatim_even_when_a_count_exists(string axLabel)
    {
        // whatsapp has a raw count of 12; a plain "12" label must stay exactly what the Dock said.
        Assert.Equal(axLabel, MacOSAppBadgeSource.ResolveLabel(axLabel, "net.whatsapp.whatsapp", Counts));
        Assert.Equal(axLabel, MacOSAppBadgeSource.ResolveLabel(axLabel, "org.telegram.Telegram", Counts));
    }

    [Fact]
    public void Truncated_label_without_a_bundle_id_stays_verbatim()
    {
        Assert.Equal("..82", MacOSAppBadgeSource.ResolveLabel("..82", null, Counts));
    }

    [Fact]
    public void Truncated_label_for_an_unknown_app_stays_verbatim()
    {
        Assert.Equal("..82", MacOSAppBadgeSource.ResolveLabel("..82", "com.unknown.app", Counts));
    }

    [Fact]
    public void Truncated_label_with_no_raw_counts_at_all_stays_verbatim()
    {
        var empty = new Dictionary<string, int>();
        Assert.Equal("..82", MacOSAppBadgeSource.ResolveLabel("..82", "org.telegram.Telegram", empty));
    }

    [Theory]
    [InlineData("..99", "org.telegram.Telegram", "..99")]   // tail is not a suffix of 1082 → stale row → verbatim
    [InlineData("..999", "org.telegram.Telegram", "..999")] // neither is a non-matching tail
    public void Ellipsized_label_whose_tail_does_not_match_the_raw_count_stays_verbatim(
        string axLabel, string bundleId, string expected)
    {
        // The Dock keeps the count's LAST digits; a mismatch means the db row disagrees with what
        // the Dock is painting, so the Dock's label wins.
        Assert.Equal(expected, MacOSAppBadgeSource.ResolveLabel(axLabel, bundleId, Counts));
    }

    [Theory]
    [InlineData("..10", "com.example.zero")]     // zero count is not a badge
    [InlineData("..10", "com.example.negative")] // neither is a negative one
    public void Non_positive_raw_count_never_substitutes(string axLabel, string bundleId)
    {
        Assert.Equal(axLabel, MacOSAppBadgeSource.ResolveLabel(axLabel, bundleId, Counts));
    }

    [Fact]
    public void Bundle_id_matching_is_case_insensitive_because_usernoted_lowercases_identifiers()
    {
        // The db stores 'one.ayugram.ayugramdesktop' (all lowercase); CFBundleGetIdentifier returns
        // 'one.ayugram.AyuGramDesktop'. The lookup must still hit.
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["one.ayugram.ayugramdesktop"] = 190,
        };
        Assert.Equal("190", MacOSAppBadgeSource.ResolveLabel("..90", "one.ayugram.AyuGramDesktop", counts));
    }

    [Fact]
    public void When_the_db_read_fails_the_dock_ellipsis_falls_back_verbatim()
    {
        // The end-to-end fallback chain: a missing/unreadable usernoted db yields an empty map
        // (never a throw), and the resolver then keeps the Dock's ellipsized label as-is.
        var missing = Path.Combine(Path.GetTempPath(), "bevel-no-such-" + Guid.NewGuid().ToString("N"));
        var counts = UserNotedBadgeStore.ReadCounts(missing);
        Assert.Empty(counts);
        Assert.Equal("..82", MacOSAppBadgeSource.ResolveLabel("..82", "org.telegram.Telegram", counts));
        Assert.Equal("12", MacOSAppBadgeSource.ResolveLabel("12", "net.whatsapp.whatsapp", counts));
    }

    [Theory]
    [InlineData("..82", true)]
    [InlineData("…82", true)]
    [InlineData("99+", true)]
    [InlineData("12", false)]
    [InlineData("..", false)]       // no digits after the ellipsis
    [InlineData("..ab", false)]
    [InlineData("+", false)]
    [InlineData("•", false)]
    [InlineData("1+", false)]       // too short to be a truncation marker
    [InlineData("...", false)]
    [InlineData("..2147483648", false)]  // digit tail overflows int → not a truncation marker
    public void LooksTruncated_recognizes_only_genuine_truncation_markers(string label, bool expected)
    {
        Assert.Equal(expected, MacOSAppBadgeSource.LooksTruncated(label));
    }
}
