using Bevel.FileManager.Components;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// List-view column width follows the folder's names, like Explorer's (bevel-3fgd). The sizer ranks
/// names by a cheap character-count estimate and only ever measures the 8 longest for real.
/// </summary>
public sealed class NameColumnSizerTests
{
    static double SixPx(string s) => s.Length * 6;

    [Fact]
    public void Width_is_the_widest_of_the_tracked_names_plus_chrome()
    {
        var z = new NameColumnSizer();
        z.Add(new string('a', 20));
        z.Add(new string('b', 30));
        Assert.Equal(30 * 6 + 34, z.Width(SixPx));
    }

    [Fact]
    public void Clamps_to_min_and_max()
    {
        var z = new NameColumnSizer();
        Assert.Equal(100, z.Width(SixPx));          // no names
        z.Add("a.txt");
        Assert.Equal(100, z.Width(SixPx));          // 30 + 34 < 100
        z.Add(new string('x', 100));
        Assert.Equal(360, z.Width(SixPx));          // 600 + 34 > 360
    }

    [Fact]
    public void Keeps_only_the_eight_longest()
    {
        var z = new NameColumnSizer();
        z.Add("short");
        for (int n = 10; n < 18; n++) z.Add(new string('n', n));
        var measured = new List<string>();
        z.Width(s => { measured.Add(s); return SixPx(s); });
        Assert.Equal(8, measured.Count);
        Assert.DoesNotContain("short", measured);

        z.Add(new string('L', 40));                  // displaces the shortest tracked (10 chars)
        measured.Clear();
        z.Width(s => { measured.Add(s); return SixPx(s); });
        Assert.Equal(8, measured.Count);
        Assert.Contains(new string('L', 40), measured);
        Assert.DoesNotContain(new string('n', 10), measured);
    }

    [Fact]
    public void Wide_characters_rank_as_two()
    {
        var z = new NameColumnSizer();
        for (int i = 0; i < 8; i++) z.Add($"abc{i}");   // estimate 4 each
        z.Add("日本語");                                  // estimate 6: outranks them
        var measured = new List<string>();
        z.Width(s => { measured.Add(s); return SixPx(s); });
        Assert.Contains("日本語", measured);
    }

    [Fact]
    public void Reset_forgets()
    {
        var z = new NameColumnSizer();
        z.Add(new string('x', 50));
        z.Reset();
        z.Add("a.txt");
        Assert.Equal(100, z.Width(SixPx));
    }
}
