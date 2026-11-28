using Bevel.App.ShellCore;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Direct coverage of <see cref="ProjectionMirror{T}"/> (bevel-wheq), the bookkeeping both shell-core adapters
/// (windows and tray) share: what a snapshot reports as gone, and the order-safety of a pull that overlaps
/// removals. Items are plain (Id, Label) pairs keyed by Id.
/// </summary>
public sealed class ProjectionMirrorTests
{
    private sealed record Item(string Id, string Label = "");

    private static ProjectionMirror<Item> NewMirror() => new(i => i.Id);

    private static string[] Ids(IEnumerable<Item> items) => items.Select(i => i.Id).ToArray();

    [Fact]
    public void A_snapshot_reports_what_it_omits_and_a_removed_item_is_not_reported_again()
    {
        var m = NewMirror();
        Assert.Empty(m.ApplySnapshot([new("a"), new("b"), new("c")]).Gone);

        Assert.Equal(["a", "c"], Ids(m.ApplySnapshot([new("b")]).Gone));

        m.Remove(new("b"));
        Assert.Empty(m.ApplySnapshot([]).Gone);   // the delta already forgot "b"
    }

    // bevel-adu8: a reconnect snapshot must not re-announce known items as new (buttons flash), but must still
    // hand them back as Known so their changed state is delivered.
    [Fact]
    public void A_snapshot_splits_what_it_lists_into_added_and_known()
    {
        var m = NewMirror();
        var first = m.ApplySnapshot([new("a"), new("b")]);
        Assert.Equal(["a", "b"], Ids(first.Added));
        Assert.Empty(first.Known);

        var second = m.ApplySnapshot([new("b", "renamed"), new("c")]);
        Assert.Equal(["a"], Ids(second.Gone));
        Assert.Equal(["c"], Ids(second.Added));
        Assert.Equal([new Item("b", "renamed")], second.Known);
    }

    [Fact]
    public void A_snapshot_removal_during_an_in_flight_pull_is_filtered_from_the_pull()
    {
        var m = NewMirror();
        m.ApplySnapshot([new("a"), new("b")]);

        var pull = m.BeginPull();
        Assert.Equal(["a"], Ids(m.ApplySnapshot([new("b")]).Gone));   // "a" gone while the pull is in flight
        var merged = m.MergePull(pull, [new("a"), new("b")]);          // the pull is older: still lists "a"
        m.EndPull();

        Assert.Equal(["b"], Ids(merged));
        Assert.Empty(m.ApplySnapshot([new("b")]).Gone);                // "a" was not resurrected in the mirror
    }

    [Fact]
    public void Overlapping_pulls_keep_the_removal_record_until_the_last_one_ends()
    {
        var m = NewMirror();
        m.ApplySnapshot([new("a")]);

        var first = m.BeginPull();
        var second = m.BeginPull();
        m.Remove(new("a"));

        Assert.Empty(m.MergePull(first, [new("a")]));
        m.EndPull();                                               // first ends; second still in flight
        Assert.Empty(m.MergePull(second, [new("a")]));            // the record must survive for it
        m.EndPull();

        // Both ended: the record is cleared, and a pull begun now is newer than that removal.
        var third = m.BeginPull();
        Assert.Equal(["a"], Ids(m.MergePull(third, [new("a")])));
        m.EndPull();
    }

    [Fact]
    public void A_pull_begun_after_a_removal_is_not_filtered_by_it()
    {
        var m = NewMirror();
        m.ApplySnapshot([new("a")]);

        var older = m.BeginPull();
        m.Remove(new("a"));
        var newer = m.BeginPull();                                 // started after the removal: sees the truth

        Assert.Empty(m.MergePull(older, [new("a")]));
        Assert.Equal(["a"], Ids(m.MergePull(newer, [new("a")])));
        m.EndPull();
        m.EndPull();
    }

    [Fact]
    public void An_upsert_after_a_removal_inside_a_pull_is_kept()
    {
        var m = NewMirror();
        m.ApplySnapshot([new("a")]);

        var pull = m.BeginPull();
        m.Remove(new("a"));
        m.Upsert(new("a", "back"));                                // the item came back for real
        var merged = m.MergePull(pull, [new("a", "pulled")]);
        m.EndPull();

        // The newer upsert wins over the stale pull's value — for the caller AND in the mirror.
        Assert.Equal([new Item("a", "back")], merged);
        Assert.Equal([new Item("a", "back")], m.ApplySnapshot([]).Gone);
    }

    [Fact]
    public void An_upsert_during_a_pull_keeps_its_value_over_the_stale_pull()
    {
        var m = NewMirror();
        var pull = m.BeginPull();
        m.Upsert(new("a", "newer"));                               // a delta lands while the pull is in flight
        var merged = m.MergePull(pull, [new("a", "pulled"), new("b", "pulled")]);
        m.EndPull();

        Assert.Equal([new Item("a", "newer"), new Item("b", "pulled")], merged);
        Assert.Equal([new Item("a", "newer"), new Item("b", "pulled")], m.ApplySnapshot([]).Gone);
    }

    // A pull that ended BEFORE a newer upsert must not overwrite that upsert's value: the newer delta wins.
    [Fact]
    public void A_newer_upsert_value_survives_an_earlier_pull()
    {
        var m = NewMirror();
        var pull = m.BeginPull();
        m.MergePull(pull, [new("a", "pulled")]);
        m.EndPull();
        m.Upsert(new("a", "newer"));

        Assert.Equal([new Item("a", "newer")], m.ApplySnapshot([]).Gone);
    }
}
