namespace Bevel.Core;

/// <summary>Filer favorites list math (bevel-4pgk): pure string[] operations over the persisted
/// <see cref="BevelSettings.FilerFavorites"/> (values are <c>VfsPath.ToString()</c>). No duplicates,
/// order-preserving add, index-clamped move.</summary>
public static class FilerFavorites
{
    /// <summary>Append <paramref name="path"/> unless already present (idempotent, order kept).</summary>
    public static string[] Add(string[] current, string path)
        => current.Contains(path) ? current : current.Append(path).ToArray();

    public static string[] Remove(string[] current, string path)
        => current.Where(p => p != path).ToArray();

    /// <summary>Move the entry at <paramref name="from"/> to <paramref name="to"/>; out-of-range or
    /// no-op indices return the list unchanged.</summary>
    public static string[] Move(string[] current, int from, int to)
    {
        if (from == to || from < 0 || from >= current.Length || to < 0 || to >= current.Length)
            return current;
        var list = current.ToList();
        var item = list[from];
        list.RemoveAt(from);
        list.Insert(to, item);
        return list.ToArray();
    }
}
