using Bevel.Pal.Abstractions;

namespace Bevel.App.ShellCore;

/// <summary>
/// The client half of a reconciling snapshot (bevel-ri8c tray, bevel-wheq windows): what a shell-core
/// adapter has raised and not yet removed, by id. A snapshot is a full replace — anything in here that the
/// snapshot omits vanished while the link was down, and the adapter must raise its removal.
///
/// <para>Thread-safe (core events arrive on the transport thread, pulls on any thread). It only does the
/// bookkeeping and RETURNS what to raise; the adapter raises its events after the call, so no event handler
/// ever runs under this lock.</para>
///
/// <para>Pull order-safety: a pull is a point-in-time read that can land AFTER a newer change to one of its
/// items. While any pull is in flight, every change (removal, upsert, snapshot) is recorded with a sequence
/// number, and <see cref="MergePull"/> lets the newer change win for any id changed since the pull began: an
/// id removed since is dropped from the pull's result (a stale pull cannot resurrect it), and an id upserted
/// since keeps its newer value — here AND in the returned list, which the caller upserts.</para>
/// </summary>
internal sealed class ProjectionMirror<T>
{
    private readonly Func<T, string> _keyOf;
    private readonly object _gate = new();
    private readonly Dictionary<string, T> _known = new(StringComparer.Ordinal);
    private int _pullsInFlight;
    private long _changeSeq;
    private readonly Dictionary<string, (long Seq, bool Removed)> _changedDuringPull = new(StringComparer.Ordinal);

    public ProjectionMirror(Func<T, string> keyOf) => _keyOf = keyOf;

    /// <summary>Apply an authoritative snapshot: forget what it omits (returned, in known order, for the
    /// caller to raise as removed) and record everything it lists.</summary>
    public IReadOnlyList<T> ApplySnapshot(IReadOnlyList<T> snapshot)
    {
        lock (_gate)
        {
            var gone = SnapshotReconcile.Absent(_known, snapshot, _keyOf);
            foreach (var g in gone) RemoveLocked(_keyOf(g));
            foreach (var item in snapshot) UpsertLocked(item);
            return gone;
        }
    }

    /// <summary>An add/update/foreground delta: the item exists (again).</summary>
    public void Upsert(T item)
    {
        lock (_gate) UpsertLocked(item);
    }

    private void UpsertLocked(T item)
    {
        var key = _keyOf(item);
        _known[key] = item;
        if (_pullsInFlight > 0) _changedDuringPull[key] = (++_changeSeq, Removed: false);
    }

    /// <summary>A remove delta: forget the item, so a later snapshot does not report it removed again.</summary>
    public void Remove(T item)
    {
        lock (_gate) RemoveLocked(_keyOf(item));
    }

    private void RemoveLocked(string key)
    {
        _known.Remove(key);
        if (_pullsInFlight > 0) _changedDuringPull[key] = (++_changeSeq, Removed: true);
    }

    /// <summary>Start a pull. Returns the token to pass to <see cref="MergePull"/>; every call must be
    /// paired with <see cref="EndPull"/> (in a <c>finally</c>).</summary>
    public long BeginPull()
    {
        lock (_gate)
        {
            _pullsInFlight++;
            return _changeSeq;
        }
    }

    /// <summary>Record a pull's result so a later snapshot can reap it — except where a change since the pull
    /// began is newer: a removed id is dropped, an upserted one keeps (and returns) its newer value. Returns the
    /// list for the caller.</summary>
    public IReadOnlyList<T> MergePull(long startToken, IReadOnlyList<T> items)
    {
        var fresh = new List<T>(items.Count);
        lock (_gate)
            foreach (var item in items)
            {
                var key = _keyOf(item);
                if (_changedDuringPull.TryGetValue(key, out var change) && change.Seq > startToken)
                {
                    if (!change.Removed && _known.TryGetValue(key, out var newer)) fresh.Add(newer);
                    continue;
                }
                _known[key] = item;
                fresh.Add(item);
            }
        return fresh;
    }

    public void EndPull()
    {
        lock (_gate)
            if (--_pullsInFlight == 0) _changedDuringPull.Clear();
    }
}
