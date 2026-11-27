using Avalonia.Input;
using Bevel.Core.Vfs;

namespace Bevel.FileManager.FileOperations;

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