using Bevel.Core;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>
/// The theme ids were renamed off their vendor-derived names ("luna" is Microsoft's codename for the XP
/// visual style; "win2000" is a product name). Every install that predates the rename still has the OLD
/// value in its settings.db, and an id the loader does not recognise resolves silently to the default —
/// so without this mapping every user of the 2001 Blue skin would have opened the shell to find it reset,
/// with nothing on screen to explain it.
///
/// These tests exist because that failure is INVISIBLE: it looks like a working shell, just not the one
/// the user chose. The same class of silent reset has already cost real debugging time on this project.
/// </summary>
public class ThemeIdMigrationTests
{
    [Theory]
    [InlineData("luna", ThemeIds.Blue2001)]
    [InlineData("win2000", ThemeIds.Industrial1999)]
    [InlineData("LUNA", ThemeIds.Blue2001)]        // case-insensitive: ids have been written by hand
    [InlineData("Win2000", ThemeIds.Industrial1999)]
    public void Legacy_ids_still_resolve_to_their_replacement(string persisted, string expected)
        => Assert.Equal(expected, ThemeIds.Canonical(persisted));

    [Theory]
    [InlineData(ThemeIds.Blue2001)]
    [InlineData(ThemeIds.Industrial1999)]
    [InlineData(ThemeIds.Flat)]
    public void Current_ids_pass_through_unchanged(string id)
        => Assert.Equal(id, ThemeIds.Canonical(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_resolves_to_the_default(string? id)
        => Assert.Equal(ThemeIds.Default, ThemeIds.Canonical(id));

    /// <summary>An id we have never heard of is handed back as-is, NOT silently defaulted — the caller's
    /// IsKnown check decides, so a typo stays visible instead of being laundered into a valid theme.</summary>
    [Fact]
    public void Unknown_ids_are_passed_through_not_defaulted()
        => Assert.Equal("something-else", ThemeIds.Canonical("something-else"));

    /// <summary>The rename must not have left a vendor name in the ids themselves.</summary>
    [Fact]
    public void No_current_id_carries_a_vendor_name()
    {
        foreach (var id in new[] { ThemeIds.Industrial1999, ThemeIds.Blue2001, ThemeIds.Flat, ThemeIds.Default })
        {
            Assert.DoesNotContain("luna", id, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("win2000", id, System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
