namespace Bevel.Interop;

/// <summary>
/// Finder's naming rules for <c>duplicate</c> and collision-free <c>make</c> (08-os-interop.md
/// §2.1.2 <c>duplicate</c>: "Finder naming rules (<c>name copy</c>)"). Pure and side-effect-free —
/// the caller supplies an <c>exists</c> predicate over the destination container, so this is fully
/// unit-testable and shared by every inbound surface.
/// </summary>
public static class FinderNaming
{
    /// <summary>Splits a leaf name into (stem, extension-including-dot). Folders and dot-files
    /// (leading dot) have no extension; the extension is only the LAST suffix, so
    /// <c>a.tar.gz</c> → (<c>a.tar</c>, <c>.gz</c>) — matching Finder's " copy" insertion point.</summary>
    public static (string Stem, string Ext) SplitExtension(string name, bool isFolder)
    {
        if (isFolder) return (name, "");
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1) return (name, "");   // dot-file or trailing dot: no ext
        return (name[..dot], name[dot..]);
    }

    /// <summary>The Finder duplicate name: "<c>stem copy.ext</c>", then "<c>stem copy 2.ext</c>",
    /// "<c>stem copy 3.ext</c>", … — the first that <paramref name="exists"/> rejects.</summary>
    public static string DuplicateName(string originalName, bool isFolder, Func<string, bool> exists)
    {
        var (stem, ext) = SplitExtension(originalName, isFolder);
        var first = $"{stem} copy{ext}";
        if (!exists(first)) return first;
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} copy {n}{ext}";
            if (!exists(candidate)) return candidate;
        }
    }

    /// <summary>The desired name if free, else Finder's numbered fallback "<c>stem 2.ext</c>",
    /// "<c>stem 3.ext</c>", … — used by <c>make</c> so a second "untitled folder" doesn't collide.</summary>
    public static string UniqueName(string desiredName, bool isFolder, Func<string, bool> exists)
    {
        if (!exists(desiredName)) return desiredName;
        var (stem, ext) = SplitExtension(desiredName, isFolder);
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} {n}{ext}";
            if (!exists(candidate)) return candidate;
        }
    }
}
