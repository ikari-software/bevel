namespace Bevel.Pal.Abstractions;

/// <summary>
/// Helpers for treating a full-state snapshot as a reconcile rather than an upsert-only replay
/// (bevel-ri8c). A consumer that only adds/updates from a snapshot leaves ghosts behind for anything
/// that vanished while the link was down; the fix is to also remove what the snapshot omits.
/// </summary>
public static class SnapshotReconcile
{
    /// <summary>
    /// The items in <paramref name="known"/> whose key does not appear in <paramref name="snapshot"/>,
    /// i.e. what the consumer must remove to match the snapshot. Snapshot membership is decided with an
    /// ordinal string set; order follows <paramref name="known"/>'s enumeration order.
    /// </summary>
    public static IReadOnlyList<T> Absent<T>(
        IReadOnlyDictionary<string, T> known, IEnumerable<T> snapshot, Func<T, string> keyOf)
    {
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(keyOf);
        if (known.Count == 0) return Array.Empty<T>();

        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in snapshot)
            present.Add(keyOf(item));

        List<T>? absent = null;
        foreach (var (key, item) in known)
            if (!present.Contains(key))
                (absent ??= new List<T>()).Add(item);
        return absent ?? (IReadOnlyList<T>)Array.Empty<T>();
    }
}
