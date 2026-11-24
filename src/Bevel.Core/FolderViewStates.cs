namespace Bevel.Core;

/// <summary>Per-folder view-state persistence (bevel-ze3o): Explorer remembers each folder's view mode
/// and sort; Filer now does too. Stored as a capped, most-recently-used-last string array on
/// <see cref="BevelSettings.FilerFolderViews"/> so the core remains the sole DB writer and every Filer
/// process agrees via the existing settings fan-out — no Filer-owned file, no write races.
/// Pure string math; malformed entries are skipped, never thrown on.</summary>
public static class FolderViewStates
{
    public const int DefaultCap = 200;

    /// <summary>One folder's persisted view: <paramref name="ViewMode"/> and <paramref name="SortColumn"/>
    /// are enum-name strings (parsed by the Filer with Enum.TryParse), so this stays serialization-simple.</summary>
    public readonly record struct FolderViewState(string ViewMode, string SortColumn, bool Ascending);

    // Entry format: "<ViewMode>|<SortColumn>|<A or D>|<path>" — path LAST because it may contain '|'.
    public static FolderViewState? Find(string[] entries, string path)
    {
        FolderViewState? found = null;
        foreach (var entry in entries)
            if (Parse(entry) is { } p && p.Path == path)
                found = p.State; // MRU is last, so the last match wins
        return found;
    }

    /// <summary>Insert/replace <paramref name="path"/>'s state, moving it to the end (MRU last), and cap the
    /// list at <paramref name="cap"/> by dropping the oldest. Malformed entries are dropped on the way.</summary>
    public static string[] Upsert(string[] entries, string path, FolderViewState state, int cap = DefaultCap)
    {
        var list = new List<string>(entries.Length + 1);
        foreach (var entry in entries)
            if (Parse(entry) is { } p)
            {
                if (p.Path != path) list.Add(entry);
            }
        list.Add(Format(state, path));
        if (list.Count > cap) list.RemoveRange(0, list.Count - cap);
        return list.ToArray();
    }

    private static string Format(FolderViewState state, string path) =>
        $"{state.ViewMode}|{state.SortColumn}|{(state.Ascending ? "A" : "D")}|{path}";

    private static (FolderViewState State, string Path)? Parse(string? entry)
    {
        if (string.IsNullOrEmpty(entry)) return null;
        // Split into at most 4 parts so a path containing '|' survives intact in the last part.
        var parts = entry.Split('|', 4);
        if (parts.Length != 4) return null;
        if (parts[0].Length == 0 || parts[1].Length == 0 || parts[3].Length == 0) return null;
        if (parts[2] is not ("A" or "D")) return null;
        return (new FolderViewState(parts[0], parts[1], parts[2] == "A"), parts[3]);
    }
}
