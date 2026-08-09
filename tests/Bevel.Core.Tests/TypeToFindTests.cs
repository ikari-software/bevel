using Bevel.Core.Input;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>
/// The shared type-ahead matcher (bevel-p3v3). These tests pin the four behaviours the old inline
/// ItemView.TypeAhead got wrong: it was Key-enum only (digits/accents dead), wrapped backwards, and never
/// cycled. Timing uses a hand-cranked clock so the reset window is deterministic.
/// </summary>
public sealed class TypeToFindTests
{
    private long _now;
    private TypeToFind New(int resetMs = 700) => new(() => _now, resetMs);

    private static readonly string[] Fruit =
        { "apple", "avocado", "banana", "cherry", "cranberry", "cucumber" };

    [Fact]
    public void First_letter_selects_the_first_match_going_forward()
    {
        var t = New();
        Assert.Equal(3, t.Match("c", Fruit, -1));   // cherry — forward, NOT the backwards-wrap the old loop did
    }

    [Fact]
    public void Repeating_a_letter_cycles_forward_through_matches_and_wraps_once()
    {
        var t = New();
        Assert.Equal(3, t.Match("c", Fruit, -1));   // cherry
        Assert.Equal(4, t.Match("c", Fruit, 3));    // cranberry
        Assert.Equal(5, t.Match("c", Fruit, 4));    // cucumber
        Assert.Equal(3, t.Match("c", Fruit, 5));    // wraps back to cherry
        Assert.Equal("c", t.Buffer);                // repeats never grew into "cccc"
    }

    [Fact]
    public void Extending_the_prefix_refines_in_place_rather_than_cycling()
    {
        var t = New();
        Assert.Equal(3, t.Match("c", Fruit, -1));   // "c"  → cherry
        Assert.Equal(4, t.Match("r", Fruit, 3));    // "cr" → cranberry
        Assert.Equal("cr", t.Buffer);
    }

    [Fact]
    public void A_digit_character_matches()   // impossible with the old dead Key-digit map
    {
        var items = new[] { "clip", "2nd-take", "3rd-take" };
        var t = New();
        Assert.Equal(1, t.Match("2", items, -1));   // "2nd-take"
    }

    [Fact]
    public void An_accented_character_matches()   // impossible via a Key-enum decode
    {
        var items = new[] { "apple", "élan", "zebra" };
        var t = New();
        Assert.Equal(1, t.Match("é", items, -1));   // "élan"
    }

    [Fact]
    public void Characters_typed_within_the_reset_window_accumulate()
    {
        var t = New();
        Assert.Equal(0, t.Match("a", Fruit, -1));   // apple
        _now += 100;
        Assert.Equal(1, t.Match("v", Fruit, 0));    // "av" → avocado
        Assert.Equal("av", t.Buffer);
    }

    [Fact]
    public void An_idle_gap_beyond_the_reset_window_starts_a_fresh_prefix()
    {
        var t = New(resetMs: 700);
        Assert.Equal(0, t.Match("a", Fruit, -1));   // "a" → apple
        _now += 1000;                               // longer than the reset window
        Assert.Equal(3, t.Match("c", Fruit, 0));    // fresh "c" → cherry, NOT "ac"
        Assert.Equal("c", t.Buffer);
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var t = New();
        Assert.Equal(0, t.Match("A", Fruit, -1));   // "A" matches "apple"
    }

    [Fact]
    public void No_match_returns_minus_one()
    {
        var t = New();
        Assert.Equal(-1, t.Match("z", Fruit, -1));
    }

    [Fact]
    public void Empty_input_or_empty_list_is_a_no_op()
    {
        var t = New();
        Assert.Equal(-1, t.Match("", Fruit, 2));
        Assert.Equal(-1, t.Match("a", System.Array.Empty<string>(), -1));
    }

    [Fact]
    public void Generic_overload_projects_items_to_their_label()
    {
        var items = new[] { (Id: 1, Name: "banana"), (Id: 2, Name: "cherry") };
        var t = New();
        Assert.Equal(1, t.Match("c", items, x => x.Name, -1));
    }

    [Fact]
    public void Reset_forgets_the_accumulated_prefix()
    {
        var t = New();
        t.Match("a", Fruit, -1);                     // buffer "a"
        Assert.Equal("a", t.Buffer);
        t.Reset();
        Assert.Equal("", t.Buffer);
        // Without the reset, "c" here would extend to "ac" (no match); after it, "c" is a fresh prefix.
        Assert.Equal(3, t.Match("c", Fruit, 0));     // cherry
        Assert.Equal("c", t.Buffer);
    }

    [Fact]
    public void The_same_letter_in_mixed_case_still_cycles()
    {
        var t = New();
        Assert.Equal(3, t.Match("C", Fruit, -1));    // "C" → cherry
        Assert.Equal(4, t.Match("c", Fruit, 3));     // lower "c" is the SAME letter → cycles, not "Cc"
        Assert.Equal("c", t.Buffer);
    }
}
