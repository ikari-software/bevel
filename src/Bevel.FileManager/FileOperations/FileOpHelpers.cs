using Bevel.Core.Vfs;

namespace Bevel.FileManager.FileOperations;

/// <summary>
/// Stateless helpers shared by the file-op collaborators (scanner, conflict coordinator, undo
/// executor) and the <see cref="FileOperationService"/> facade. Extracted during the god-class
/// decomposition (bevel-p3g) so the same resolve/status/progress logic isn't duplicated per class.
/// </summary>
internal static class FileOpHelpers
{
    /// <summary>
    /// Resolves a path, returning null ONLY when it genuinely does not exist. Any other failure
    /// (permission, IO, a locked file) propagates: treating those as "absent" would skip the
    /// conflict prompt / overwrite check and silently clobber the destination (review AD-safety).
    /// </summary>
    public static async Task<IVfsNode?> TryResolveAsync(VfsRoot vfs, VfsPath path, CancellationToken ct)
    {
        try
        {
            return await vfs.GetProvider(path).ResolveAsync(path, ct);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Rolls per-item results up into one operation status.</summary>
    public static FileOpStatus BuildFinalStatus(IReadOnlyList<FileItemResult> results)
    {
        if (results.All(r => r.Status == FileItemResultStatus.Success))
            return FileOpStatus.Completed;
        if (results.All(r => r.Status is FileItemResultStatus.Cancelled or FileItemResultStatus.Skipped))
            return FileOpStatus.Cancelled;
        if (results.Any(r => r.Status == FileItemResultStatus.Failed))
            return FileOpStatus.PartiallyCompleted;
        return FileOpStatus.Completed;
    }

    /// <summary>Single construction point for a progress event, so the facade and the undo
    /// executor emit identically-shaped records.</summary>
    public static FileOpProgress MakeProgress(
        string opId, FileOpStatus status, int current, int total,
        long bytesTransferred, long totalBytes, string currentFile,
        string? volumeKey = null, string? message = null)
        => new()
        {
            OperationId = opId,
            Status = status,
            CurrentFileIndex = current,
            TotalFiles = total,
            BytesTransferred = bytesTransferred,
            TotalBytes = totalBytes,
            CurrentFileName = currentFile,
            VolumeKey = volumeKey,
            Message = message,
        };
}
