namespace Bevel.Taskbar;

/// <summary>
/// Pure add/remove/reorder logic for the taskbar's stack folder list (bevel-dw5), so the dialog's
/// handlers stay one-liners and the rules are unit-testable: paths are normalised (trailing slash
/// trimmed), duplicates rejected (ordinal), moves clamp at both ends, out-of-range removes are no-ops.
/// </summary>
public static class StackListEditor
{
    /// <summary>Appends <paramref name="path"/> unless blank or already present (ordinal). Returns the
    /// ORIGINAL array when rejected, a new one otherwise.</summary>
    public static string[] Add(string[] list, string? path)
    {
        var p = (path ?? "").Trim().TrimEnd('/');
        if (p.Length == 0) return list;
        if (list.Any(x => string.Equals(x, p, StringComparison.Ordinal))) return list;
        return list.Concat(new[] { p }).ToArray();
    }

    /// <summary>Drops the entry at <paramref name="index"/>; out-of-range is a no-op.</summary>
    public static string[] Remove(string[] list, int index)
        => index < 0 || index >= list.Length ? list : list.Where((_, i) => i != index).ToArray();

    /// <summary>Moves the entry at <paramref name="index"/> by <paramref name="delta"/>, clamped at both
    /// ends (a move that would leave the list is a no-op, so the buttons can be disabled with reason).</summary>
    public static string[] Move(string[] list, int index, int delta)
    {
        var to = index + delta;
        if (index < 0 || index >= list.Length || to < 0 || to >= list.Length) return list;
        var copy = (string[])list.Clone();
        (copy[index], copy[to]) = (copy[to], copy[index]);
        return copy;
    }
}
