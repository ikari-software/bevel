using Bevel.Core.Vfs;

namespace Bevel.FileManager.Navigation;

/// <summary>
/// A single item in the navigation history, paired with a human-readable label and its absolute
/// index in <see cref="NavigationStack.Entries"/> — for building a History dropdown menu (Toolbar
/// History button / Back-chevron flyout, Go menu). Pass <see cref="Index"/> to
/// <see cref="FileManagerController.JumpToHistory(int)"/> to navigate there.
/// </summary>
public sealed record HistoryEntry(int Index, VfsPath Path, string Label)
{
    /// <summary>
    /// Builds a display label for <paramref name="path"/>, mirroring the convention already used
    /// for the info-pane title (see FileManagerWindow.UpdateInfoPane): "My Computer" for the
    /// computer-scheme root, the scheme name (uppercased) for other scheme roots, and the leaf
    /// file name otherwise.
    /// </summary>
    public static string LabelFor(VfsPath path)
        => path.Scheme == "computer" ? "My Computer"
            : path.IsRoot ? path.Scheme.ToUpperInvariant()
            : path.FileName;
}
