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
/// <para>Pull order-safety: a pull is a point-in-time read that can land AFTER a removal of one of its
/// items. While any pull is in flight, removals (by delta or snapshot) are recorded, and
/// <see cref="MergePull"/> drops those ids from the pull's result — so a stale pull cannot resurrect them,
/// neither here nor in the caller that upserts the returned list. An upsert clears the record (the item
/// is back for real).</para>
/// </summary>
internal sealed class ProjectionMirror<T>
{
    private readonly Func<T, string> _keyOf;
    private readonly object _gate = new();
    private readonly Dictionary<string, T> _known = new(StringComparer.Ordinal);
    private int _pullsInFlight;
    private long _removalSeq;
    private readonly Dictionary<string, long> _removedDuringPull = new(StringComparer.Ordinal);

    public ProjectionMirror(Func<T, string> keyOf) => _keyOf = keyOf;

    /// <summary>Apply an authoritative snapshot: forget what it omits (returned, in known order, for the
    /// caller to raise as removed) and record everything it lists.</summary>
    public IReadOnlyList<T> ApplySnapshot(IReadOnlyList<T> snapshot)
    {
        lock (_gate)
        {
            var gone = SnapshotReconcile.Absent(_known, snapshot, _keyOf);
            foreach (var g in gone) RemoveLocked(_keyOf(g));
            foreach (var item in snapshot) _known[_keyOf(item)] = item;
            return gone;
        }
    }

    /// <summary>An add/update/foreground delta: the item exists (again).</summary>
    public void Upsert(T item)
    {
        lock (_gate)
        {
            var key = _keyOf(item);
            _known[key] = item;
            _removedDuringPull.Remove(key);
        }
    }

    /// <summary>A remove delta: forget the item, so a later snapshot does not report it removed again.</summary>
    public void Remove(T item)
    {
        lock (_gate) RemoveLocked(_keyOf(item));
    }

    private void RemoveLocked(string key)
    {
        _known.Remove(key);
        if (_pullsInFlight > 0) _removedDuringPull[key] = ++_removalSeq;
    }

    /// <summary>Start a pull. Returns the token to pass to <see cref="MergePull"/>; every call must be
    /// paired with <see cref="EndPull"/> (in a <c>finally</c>).</summary>
    public long BeginPull()
    {
        lock (_gate)
        {
            _pullsInFlight++;
            return _removalSeq;
        }
    }

    /// <summary>Record a pull's result so a later snapshot can reap it, minus anything removed since the
    /// pull began (that removal is newer than the pull). Returns the filtered list for the caller.</summary>
    public IReadOnlyList<T> MergePull(long startToken, IReadOnlyList<T> items)
    {
        var fresh = new List<T>(items.Count);
        lock (_gate)
            foreach (var item in items)
            {
                var key = _keyOf(item);
                if (_removedDuringPull.TryGetValue(key, out var at) && at > startToken) continue;
                _known[key] = item;
                fresh.Add(item);
            }
        return fresh;
    }

    public void EndPull()
    {
        lock (_gate)
            if (--_pullsInFlight == 0) _removedDuringPull.Clear();
    }
}
