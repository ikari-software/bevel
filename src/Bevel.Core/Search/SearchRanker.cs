namespace Bevel.Core.Search;

/// <summary>
/// The ONE place ranking lives (bevel-6sk6): moved here from the taskbar's StartMenuFilter so every
/// section (apps, files, …) ranks identically. Pure string logic, no I/O — safe on a keystroke.
/// </summary>
public static class SearchRanker
{
    /// <summary>
    /// Ranks <paramref name="items"/> against <paramref name="query"/>, best first, dropping non-matches:
    /// name-prefix matches ("fi" → Finder) come before word-start matches ("code" → Visual Studio Code)
    /// which come before plain substring matches. Ties keep the incoming order, so a list never jitters
    /// between keystrokes.
    /// </summary>
    public static List<T> Rank<T>(IEnumerable<T> items, Func<T, string?> label, string query)
    {
        var results = new List<T>();
        if (string.IsNullOrEmpty(query)) return results;

        var ranked = new List<(int Rank, int Order, T Item)>();
        var order = 0;
        foreach (var item in items)
        {
            var rank = RankOf(label(item) ?? "", query);
            if (rank >= 0) ranked.Add((rank, order, item));
            order++;
        }
        ranked.Sort(static (a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Order.CompareTo(b.Order));
        foreach (var r in ranked) results.Add(r.Item);
        return results;
    }

    /// <summary>0 = name prefix, 1 = word start, 2 = substring, -1 = no match.</summary>
    public static int RankOf(string text, string query)
    {
        if (text.Length == 0) return -1;
        if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 0;
        var at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return -1;
        // A match that begins a word reads as intentional ("code" → Visual Studio *Code*); one that starts
        // mid-word is a weaker, last-resort hit.
        return IsWordBoundary(text, at) ? 1 : 2;
    }

    public static bool IsWordBoundary(string text, int at)
    {
        if (at == 0) return true;
        var prev = text[at - 1];
        return char.IsWhiteSpace(prev) || prev is '-' or '_' or '.' or '(' or '/';
    }
}
