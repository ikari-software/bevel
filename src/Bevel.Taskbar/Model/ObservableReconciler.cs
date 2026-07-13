using System.Collections.ObjectModel;

namespace Bevel.Taskbar;

/// <summary>
/// Diff-aware, in-place reconciliation of an <see cref="ObservableCollection{T}"/> against a
/// freshly-computed desired sequence — the list analogue of a virtual-DOM diff, and the shared
/// primitive behind the shell's "async-load but keep the view's state" rule.
///
/// An item whose key already exists is KEPT — the same view-model object stays in the collection,
/// so its bound container, scroll offset, selection, in-flight animation and already-decoded icon
/// all survive — and is merely updated and moved into place. Only genuinely new keys are inserted,
/// only vanished keys removed. Nothing is ever cleared, so a bound <see cref="System.Collections.Specialized.INotifyCollectionChanged"/>
/// view rebuilds only the rows that actually changed instead of tearing the whole list down.
///
/// Mutations are granular (Remove / Insert / Move / in-place Update) rather than a Reset, which is
/// exactly what lets Avalonia's container generator recycle surviving containers. O(n²) in the
/// worst case (IndexOf/Move), which is irrelevant at taskbar/Start-menu sizes and buys simplicity.
/// </summary>
public static class ObservableReconciler
{
    /// <summary>
    /// Reconciles <paramref name="target"/> to match <paramref name="desired"/> in both membership
    /// and order, keyed by <paramref name="sourceKeyOf"/> / <paramref name="keyOf"/>. Surviving
    /// items are updated via <paramref name="update"/> (if supplied) and moved; new items are built
    /// with <paramref name="create"/>; absent items are removed. Must run on the collection's
    /// owning (UI) thread.
    ///
    /// <paramref name="onRemove"/> is an optional take-over for the removal of a vanished item. When
    /// null (the Programs default) the item is removed immediately. When supplied it receives the
    /// leaving item plus a "commit the removal" continuation and may defer — the taskbar uses this to
    /// play an XP-style exit animation and only commit ~160ms later, meanwhile keeping the item in the
    /// collection so a same-key re-add within the grace window revives it in place instead of
    /// rebuilding it. The continuation removes by identity, so it stays correct even after later
    /// inserts/moves have shifted indices.
    /// </summary>
    public static void Reconcile<TItem, TSource, TKey>(
        ObservableCollection<TItem> target,
        IReadOnlyList<TSource> desired,
        Func<TItem, TKey> keyOf,
        Func<TSource, TKey> sourceKeyOf,
        Func<TSource, TItem> create,
        Action<TItem, TSource>? update = null,
        Action<TItem, Action>? onRemove = null)
        where TKey : notnull
    {
        var existing = new Dictionary<TKey, TItem>(target.Count);
        foreach (var item in target)
            existing[keyOf(item)] = item;

        var desiredKeys = new HashSet<TKey>(desired.Count);
        foreach (var s in desired)
            desiredKeys.Add(sourceKeyOf(s));

        // 1. Prune: drop every surviving-collection item whose key is no longer desired. Reverse
        //    walk so RemoveAt indices stay valid. Raises granular Remove events (container torn
        //    down only for the vanished rows). With an onRemove hook the caller owns the removal —
        //    it may defer (animate out, commit later), so the item stays put and later indices in
        //    this walk remain valid regardless.
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (desiredKeys.Contains(keyOf(target[i])))
                continue;
            if (onRemove is null)
            {
                target.RemoveAt(i);
            }
            else
            {
                var leaving = target[i];
                onRemove(leaving, () => target.Remove(leaving)); // identity-keyed commit — index-shift safe
            }
        }

        // 2. Recompute the deferred-leaving items still present after the hook ran (it may have
        //    committed synchronously, or deferred). These are "pinned": they must hold their current
        //    slot so a button animating out stays where it is instead of being shuffled to the end of
        //    the strip when the survivors below compact past it — the visible glitch on any
        //    backstop-caught close. Captured in ascending index order (the coordinate space the
        //    splice below rebuilds into). Null/empty for the immediate-remove (Programs) path.
        List<(int Index, TItem Item)>? pinned = null;
        if (onRemove is not null)
        {
            for (var i = 0; i < target.Count; i++)
                if (!desiredKeys.Contains(keyOf(target[i])))
                    (pinned ??= new()).Add((i, target[i]));
        }

        // 3. Assemble the final order as an explicit list, then apply it (phase 4). Desired items in
        //    desired order — keep+update the existing instance, or create a new one. `placed` skips a
        //    duplicate key (e.g. two installed apps sharing a bundle id) so the first occurrence wins
        //    rather than driving a Move/Insert past the end (which once threw and aborted the pass).
        var finalOrder = new List<TItem>(desired.Count + (pinned?.Count ?? 0));
        var placed = new HashSet<TKey>(desired.Count);
        foreach (var s in desired)
        {
            var key = sourceKeyOf(s);
            if (!placed.Add(key))
                continue; // duplicate key already placed this pass — first occurrence wins

            if (existing.TryGetValue(key, out var item))
            {
                update?.Invoke(item, s);
                finalOrder.Add(item);
            }
            else
            {
                var created = create(s);
                existing[key] = created;
                finalOrder.Add(created);
            }
        }

        // 3b. Splice each pinned (deferred-leaving) item back at its original index, so it keeps its
        //     place in the strip. Ascending insertion means each splice accounts for the ones already
        //     re-inserted, reproducing the pre-prune positions; clamp guards a shrunken desired list.
        if (pinned is not null)
            foreach (var (index, item) in pinned)
                finalOrder.Insert(Math.Min(index, finalOrder.Count), item);

        // 4. Reorder `target` in place to match `finalOrder` by identity: everything left of `slot`
        //    is already final. New items are inserted (IndexOf < 0), survivors and pinned items moved
        //    only when out of position — so an undisturbed strip (survivors in order, a pinned item
        //    holding its slot) produces ZERO moves. Granular Insert/Move events recycle containers.
        for (var slot = 0; slot < finalOrder.Count; slot++)
        {
            var item = finalOrder[slot];
            var current = target.IndexOf(item);
            if (current < 0)
                target.Insert(slot, item);
            else if (current != slot)
                target.Move(current, slot); // reorder in place — container preserved
        }
    }
}
