namespace Bevel.FileManager.Components;

/// <summary>
/// Picks the List-view column width from the folder's names, as Explorer does (bevel-3fgd). Every name
/// is ranked by a cheap estimate (one unit per character, two for wide CJK/fullwidth characters), O(1)
/// per name; only the 8 highest-ranked names are ever measured with a real text layout, in
/// <see cref="Width"/>. Pure: no Avalonia types.
/// </summary>
internal sealed class NameColumnSizer
{
    const int Tracked = 8;

    /// <summary>Icon (16) + gap (4) + highlight padding (2 + 2) + gutter between columns (10).</summary>
    internal const double Chrome = 34;

    readonly List<(string Name, int Estimate)> _top = new(Tracked);

    public void Reset() => _top.Clear();

    public void Add(string name)
    {
        int est = Estimate(name);
        int minAt = -1;
        for (int i = 0; i < _top.Count; i++)
        {
            if (_top[i].Name == name) return;   // a reconcile re-feeds names already seen
            if (minAt < 0 || _top[i].Estimate < _top[minAt].Estimate) minAt = i;
        }
        if (_top.Count < Tracked) _top.Add((name, est));
        else if (est > _top[minAt].Estimate) _top[minAt] = (name, est);
    }

    /// <summary>The column width: the widest tracked name (by <paramref name="measure"/>) plus
    /// <see cref="Chrome"/>, clamped to [<paramref name="min"/>, <paramref name="max"/>].</summary>
    public double Width(Func<string, double> measure, double min = 100, double max = 360)
    {
        double widest = 0;
        foreach (var (name, _) in _top) widest = Math.Max(widest, measure(name));
        return Math.Clamp(Math.Ceiling(widest + Chrome), min, max);
    }

    static int Estimate(string name)
    {
        int n = 0;
        foreach (char c in name) n += c >= '\u2E80' ? 2 : 1;
        return n;
    }
}
