using System.Collections.Generic;
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
    };

    [Theory]
    [InlineData("..82", "org.telegram.Telegram", "1082")]   // macOS keeps the last digits
    [InlineData("…82", "org.telegram.Telegram", "1082")]    // single-char ellipsis variant
    [InlineData("..00", "org.telegram.Telegram", "1082")]   // 1000 renders as "..00"
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
    public void LooksTruncated_recognizes_only_genuine_truncation_markers(string label, bool expected)
    {
        Assert.Equal(expected, MacOSAppBadgeSource.LooksTruncated(label));
    }
}
