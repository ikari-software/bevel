using Xunit;

namespace Bevel.Core.Tests;

/// <summary>bevel-4pgk: favorites list math — idempotent add, order-preserving, clamped move.</summary>
public class FilerFavoritesTests
{
    [Fact]
    public void Add_is_idempotent_and_keeps_order()
    {
        var favs = Array.Empty<string>();
        favs = FilerFavorites.Add(favs, "vfs://file//a");
        favs = FilerFavorites.Add(favs, "vfs://file//b");
        Assert.Equal(new[] { "vfs://file//a", "vfs://file//b" }, favs);

        favs = FilerFavorites.Add(favs, "vfs://file//a");   // duplicate → unchanged
        Assert.Equal(new[] { "vfs://file//a", "vfs://file//b" }, favs);
    }

    [Fact]
    public void Remove_drops_only_the_named_entry()
    {
        var favs = new[] { "vfs://file//a", "vfs://file//b", "vfs://file//c" };
        Assert.Equal(new[] { "vfs://file//a", "vfs://file//c" }, FilerFavorites.Remove(favs, "vfs://file//b"));
        Assert.Equal(favs, FilerFavorites.Remove(favs, "vfs://file//zzz")); // unknown → unchanged
    }

    [Fact]
    public void Move_reorders_and_clamps_out_of_range_indices()
    {
        var favs = new[] { "a", "b", "c" };
        Assert.Equal(new[] { "b", "a", "c" }, FilerFavorites.Move(favs, 0, 1));
        Assert.Equal(new[] { "c", "a", "b" }, FilerFavorites.Move(favs, 2, 0));
        Assert.Equal(favs, FilerFavorites.Move(favs, 0, 0));     // no-op
        Assert.Equal(favs, FilerFavorites.Move(favs, -1, 1));    // out of range → unchanged
        Assert.Equal(favs, FilerFavorites.Move(favs, 0, 99));
    }
}
