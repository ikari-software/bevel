namespace Bevel.Taskbar;

/// <summary>
/// The Start menu's type-to-search state (bevel-cezo): the query the user has typed while the menu is
/// open, plus the ranking that turns it into a match list over the program index.
///
/// This is a FILTER, not the classic jump-to-letter type-ahead (<see cref="Bevel.Core.Input.TypeToFind"/>).
/// The two are deliberately different operations: <c>TypeToFind</c> moves a selection inside a list that
/// stays whole and drops the prefix after an idle gap; a Start-menu search narrows the program list and
/// must survive the pause while the user reads the results. So the query here only changes on a keystroke —
/// never on a timer — and is cleared explicitly (Backspace to empty, Escape, or the menu closing).
///
/// Pure logic, no UI and no <c>Key</c> decoding: it consumes the platform's real typed text, so digits,
/// symbols and accented / non-Latin characters work for free, and every rule below is unit-testable.
/// </summary>
public sealed class StartMenuFilter
{
    private string _query = "";

    /// <summary>What the user has typed so far (never leading whitespace).</summary>
    public string Query => _query;

    /// <summary>True while a search is narrowing the menu — drives the search strip and Escape's meaning.</summary>
    public bool IsActive => _query.Length > 0;

    /// <summary>
    /// Appends typed text to the query. Returns true when the query actually changed, so the caller only
    /// re-ranks on a real edit. Control characters (Escape, Enter, Tab…) are ignored — they arrive as key
    /// events, not as search text — and so is a leading space, so a stray Space bar never opens a search
    /// for " ".
    /// </summary>
    public bool Append(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var kept = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsControl(c)) continue;
            if (char.IsWhiteSpace(c) && _query.Length == 0 && kept.Length == 0) continue;
            kept.Append(c);
        }
        if (kept.Length == 0) return false;
        _query += kept.ToString();
        return true;
    }

    /// <summary>Drops the last typed character. Returns true when the query changed.</summary>
    public bool Backspace()
    {
        if (_query.Length == 0) return false;
        _query = _query[..^1];
        return true;
    }

    /// <summary>Forgets the query entirely (Escape, or the menu closing).</summary>
    public void Clear() => _query = "";

    /// <summary>
    /// Ranks <paramref name="items"/> against <paramref name="query"/>, best first, dropping non-matches:
    /// name-prefix matches ("fi" → Finder) come before word-start matches ("code" → Visual Studio Code)
    /// which come before plain substring matches. Ties keep the incoming order, which is already the
    /// program index's own (alphabetical) order, so the list never jitters between keystrokes.
    ///
    /// A linear scan of a few hundred in-memory strings — no I/O, no index rebuild — so it is safe to run
    /// on the keystroke without leaving the UI thread.
    /// </summary>
    public static List<T> Rank<T>(IEnumerable<T> items, Func<T, string?> label, string query)
    {
        var results = new List<T>();
        if (string.IsNullOrEmpty(query)) return results;

        var ranked = new List<(int Rank, int Order, T Item)>();
        var order = 0;
        foreach (var item in items)
        {
            var text = label(item) ?? "";
            var rank = RankOf(text, query);
            if (rank >= 0) ranked.Add((rank, order, item));
            order++;
        }
        ranked.Sort(static (a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Order.CompareTo(b.Order));
        foreach (var r in ranked) results.Add(r.Item);
        return results;
    }

    /// <summary>0 = name prefix, 1 = word start, 2 = substring, -1 = no match.</summary>
    private static int RankOf(string text, string query)
    {
        if (text.Length == 0) return -1;
        if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 0;
        var at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return -1;
        // A match that begins a word reads as intentional ("code" → Visual Studio *Code*); one that starts
        // mid-word is a weaker, last-resort hit.
        return IsWordBoundary(text, at) ? 1 : 2;
    }

    private static bool IsWordBoundary(string text, int at)
    {
        if (at == 0) return true;
        var prev = text[at - 1];
        return char.IsWhiteSpace(prev) || prev is '-' or '_' or '.' or '(' or '/';
    }
}
