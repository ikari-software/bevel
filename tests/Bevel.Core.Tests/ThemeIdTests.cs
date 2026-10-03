using Bevel.Core;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>
/// The theme ids, and the deliberate DECISION NOT to map the vendor-derived ones a previous build wrote
/// ("luna" was Microsoft's codename for the XP visual style; "win2000" is a product name).
///
/// A back-compat map existed briefly and was removed on purpose. v0.1.0 was the only build that ever
/// wrote the old ids; it had six downloads across all three platforms; and an unrecognised id resolves to
/// the default through the ordinary path, so the consequence was one wrong theme on first launch. Keeping
/// the map meant keeping the exact names the rename existed to delete, permanently, for that.
///
/// These tests pin the decision rather than the mechanism, so that the absence of a mapping reads as a
/// choice to the next person instead of looking like an oversight worth "fixing".
/// </summary>
public class ThemeIdTests
{
    /// <summary>The removed map, asserted as REMOVED. A legacy id is now merely unknown: handed straight
    /// back, for the caller's IsKnown to reject. If someone reinstates a mapping, this fails and they
    /// have to come read why it went.</summary>
    [Theory]
    [InlineData("luna")]
    [InlineData("win2000")]
    public void Legacy_ids_are_not_mapped_they_are_simply_unknown(string legacy)
        => Assert.Equal(legacy, ThemeIds.OrDefault(legacy));

    [Theory]
    [InlineData(ThemeIds.Blue2001)]
    [InlineData(ThemeIds.Industrial1999)]
    [InlineData(ThemeIds.Flat)]
    public void Current_ids_pass_through_unchanged(string id)
        => Assert.Equal(id, ThemeIds.OrDefault(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_resolves_to_the_default(string? id)
        => Assert.Equal(ThemeIds.Default, ThemeIds.OrDefault(id));

    /// <summary>An id we have never heard of is handed back as-is, NOT silently defaulted — the caller's
    /// IsKnown check decides, so a typo stays visible instead of being laundered into a valid theme.</summary>
    [Fact]
    public void Unknown_ids_are_passed_through_not_defaulted()
        => Assert.Equal("something-else", ThemeIds.OrDefault("something-else"));

    /// <summary>The rename must not have left a vendor name in the ids themselves. This is the assertion
    /// that actually has to keep holding — it is the whole point of the exercise.</summary>
    [Fact]
    public void No_current_id_carries_a_vendor_name()
    {
        foreach (var id in new[] { ThemeIds.Industrial1999, ThemeIds.Blue2001, ThemeIds.Flat, ThemeIds.Default })
        {
            Assert.DoesNotContain("luna", id, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("win2000", id, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("whistler", id, System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
