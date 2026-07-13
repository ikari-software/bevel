using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Proves the diff-aware contract the shell depends on: reconciling a bound collection keeps the
/// existing item instances for surviving keys (so a bound view keeps its containers, scroll and
/// loaded icons) and mutates granularly — it never raises a Reset, which would tear every
/// container down.
/// </summary>
public sealed class ObservableReconcilerTests
{
    private sealed record Row(string Key, string Label);

    private static void Reconcile(ObservableCollection<Row> target, params (string key, string label)[] desired) =>
        ObservableReconciler.Reconcile(
            target, desired,
            keyOf: r => r.Key,
            sourceKeyOf: d => d.key,
            create: d => new Row(d.key, d.label));

    [Fact]
    public void Surviving_keys_keep_their_instance()
    {
        var target = new ObservableCollection<Row>();
        Reconcile(target, ("a", "A"), ("b", "B"), ("c", "C"));
        var a = target[0];
        var c = target[2];

        Reconcile(target, ("a", "A"), ("c", "C"), ("d", "D")); // b removed, d added

        Assert.Same(a, target[0]);                    // same object, not rebuilt
        Assert.Same(c, target[1]);
        Assert.Equal(["a", "c", "d"], target.Select(r => r.Key));
    }

    [Fact]
    public void Reorder_moves_without_recreating()
    {
        var target = new ObservableCollection<Row>();
        Reconcile(target, ("a", "A"), ("b", "B"), ("c", "C"));
        var (a, b, c) = (target[0], target[1], target[2]);

        Reconcile(target, ("c", "C"), ("b", "B"), ("a", "A")); // reverse

        Assert.Same(c, target[0]);
        Assert.Same(b, target[1]);
        Assert.Same(a, target[2]);
    }

    [Fact]
    public void Never_raises_reset()
    {
        var target = new ObservableCollection<Row>();
        Reconcile(target, ("a", "A"), ("b", "B"), ("c", "C"));

        var actions = new List<NotifyCollectionChangedAction>();
        target.CollectionChanged += (_, e) => actions.Add(e.Action);

        Reconcile(target, ("c", "C"), ("a", "A"), ("d", "D"), ("e", "E"));

        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
        Assert.NotEmpty(actions); // it did change the collection
        Assert.Equal(["c", "a", "d", "e"], target.Select(r => r.Key));
    }

    [Fact]
    public void Duplicate_desired_keys_collapse_to_one_without_throwing()
    {
        var target = new ObservableCollection<Row>();
        // Two entries share key "a" — e.g. two installed app copies with the same bundle id. The
        // old Move-based walk threw ArgumentOutOfRangeException here and aborted the reconcile.
        Reconcile(target, ("a", "A1"), ("b", "B"), ("a", "A2"));

        Assert.Equal(["a", "b"], target.Select(r => r.Key)); // first "a" wins, duplicate skipped
        Assert.Same(target[0], target[0]);                   // no crash; a second pass also holds
        Reconcile(target, ("a", "A1"), ("b", "B"), ("a", "A2"));
        Assert.Equal(["a", "b"], target.Select(r => r.Key));
    }

    [Fact]
    public void Update_callback_runs_for_surviving_items_only()
    {
        var target = new ObservableCollection<Row>();
        Reconcile(target, ("a", "A"), ("b", "B"));
        var a = target[0];

        var updated = new List<string>();
        ObservableReconciler.Reconcile(
            target, new[] { ("a", "A"), ("c", "C") },
            keyOf: r => r.Key,
            sourceKeyOf: d => d.Item1,
            create: d => new Row(d.Item1, d.Item2),
            update: (row, d) => updated.Add(d.Item1));

        Assert.Same(a, target[0]);          // survivor kept
        Assert.Equal(["a"], updated);       // update ran for the survivor, not the newly-created "c"
        Assert.Equal(["a", "c"], target.Select(r => r.Key));
    }

    [Fact]
    public void Deferred_remove_hook_owns_when_the_item_actually_leaves()
    {
        var target = new ObservableCollection<Row>();
        Reconcile(target, ("a", "A"), ("b", "B"), ("c", "C"));

        // Desired drops "c". With an onRemove hook the reconciler must NOT remove it itself — it
        // hands us the leaving item and a commit continuation (the taskbar defers this behind an
        // exit animation). "c" is placed last so no survivor Move shuffles past it.
        Action? commit = null;
        Row? left = null;
        ObservableReconciler.Reconcile(
            target, new[] { ("a", "A"), ("b", "B") },
            keyOf: r => r.Key,
            sourceKeyOf: d => d.Item1,
            create: d => new Row(d.Item1, d.Item2),
            onRemove: (row, doRemove) => { left = row; commit = doRemove; });

        Assert.Equal("c", left!.Key);
        Assert.Equal(["a", "b", "c"], target.Select(r => r.Key)); // still present — removal deferred

        commit!(); // caller commits the removal
        Assert.Equal(["a", "b"], target.Select(r => r.Key));
    }

    [Fact]
    public void Deferred_remove_keeps_a_middle_item_in_place_not_at_the_end()
    {
        var target = new ObservableCollection<Row>();
        Reconcile(target, ("a", "A"), ("b", "B"), ("c", "C"));
        var b = target[1];

        // Desired drops the MIDDLE key "b" and the hook defers its removal. b must HOLD slot 1 while
        // it animates out — the survivors a and c must not compact past it, which would teleport the
        // closing button to the end of the strip (the glitch on any backstop-caught close). This is
        // the case the "…owns when the item actually leaves" test can't catch: it drops the last item.
        Action? commit = null;
        ObservableReconciler.Reconcile(
            target, new[] { ("a", "A"), ("c", "C") },
            keyOf: r => r.Key,
            sourceKeyOf: d => d.Item1,
            create: d => new Row(d.Item1, d.Item2),
            onRemove: (_, doRemove) => commit = doRemove);

        Assert.Same(b, target[1]);                                 // held its slot — no shuffle
        Assert.Equal(["a", "b", "c"], target.Select(r => r.Key));

        commit!();                                                 // exit animation done → commit
        Assert.Equal(["a", "c"], target.Select(r => r.Key));
    }

    [Fact]
    public void Deferred_middle_removal_moves_nothing()
    {
        var target = new ObservableCollection<Row>();
        Reconcile(target, ("a", "A"), ("b", "B"), ("c", "C"));

        var actions = new List<NotifyCollectionChangedAction>();
        target.CollectionChanged += (_, e) => actions.Add(e.Action);

        // Drop the middle "b" and defer forever (never commit). An undisturbed strip with one item
        // pinned mid-exit must raise NO collection changes at all — no Move of the survivors.
        ObservableReconciler.Reconcile(
            target, new[] { ("a", "A"), ("c", "C") },
            keyOf: r => r.Key,
            sourceKeyOf: d => d.Item1,
            create: d => new Row(d.Item1, d.Item2),
            onRemove: (_, _) => { });

        Assert.DoesNotContain(NotifyCollectionChangedAction.Move, actions);
        Assert.Empty(actions);
        Assert.Equal(["a", "b", "c"], target.Select(r => r.Key));
    }

    [Fact]
    public void No_remove_hook_removes_immediately()
    {
        var target = new ObservableCollection<Row>();
        Reconcile(target, ("a", "A"), ("b", "B"), ("c", "C"));

        Reconcile(target, ("a", "A"), ("c", "C")); // default (no hook): "b" gone at once

        Assert.Equal(["a", "c"], target.Select(r => r.Key));
    }

    [Fact]
    public void Empty_desired_removes_all_without_reset()
    {
        var target = new ObservableCollection<Row>();
        Reconcile(target, ("a", "A"), ("b", "B"));

        var actions = new List<NotifyCollectionChangedAction>();
        target.CollectionChanged += (_, e) => actions.Add(e.Action);

        Reconcile(target); // desired is empty

        Assert.Empty(target);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
    }
}
