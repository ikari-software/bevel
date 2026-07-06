using Avalonia.Input;
using Bevel.Core.Vfs;

namespace Bevel.FileManager.FileOperations;

/// <summary>
/// Data payload for file drag & drop between file manager views.
/// Also used as the internal clipboard marker to distinguish cut from copy.
/// </summary>
public sealed class FileDropPayload
{
    /// <summary>Paths being dragged/copied.</summary>
    public IReadOnlyList<VfsPath> Paths { get; init; } = Array.Empty<VfsPath>();

    /// <summary>Whether this is a cut (move) or copy operation.</summary>
    public bool IsCut { get; init; }

    /// <summary>Source window identifier for same-instance DnD optimization.</summary>
    public string? SourceWindowId { get; init; }

    public static readonly string DataFormat = "Bevel.FileDropPayload";
}

/// <summary>
/// Clipboard operations for the file manager (FM-151).
/// Publishes native file formats for interop + internal marker for cut vs copy.
/// </summary>
public static class FileClipboard
{
    private static List<VfsPath> _clipboardPaths = new();
    private static bool _isCut;

    public static IReadOnlyList<VfsPath> Paths => _clipboardPaths;
    public static bool IsCut => _isCut;
    public static bool HasContent => _clipboardPaths.Count > 0;

    public static void Cut(IEnumerable<VfsPath> paths)
    {
        _clipboardPaths = paths.ToList();
        _isCut = true;
    }

    public static void Copy(IEnumerable<VfsPath> paths)
    {
        _clipboardPaths = paths.ToList();
        _isCut = false;
    }

    public static void Clear()
    {
        _clipboardPaths.Clear();
        _isCut = false;
    }
}