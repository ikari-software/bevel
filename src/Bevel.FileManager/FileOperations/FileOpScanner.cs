using Bevel.Core.Vfs;

namespace Bevel.FileManager.FileOperations;

/// <summary>
/// Pre-scan phase of a Move/Copy (extracted from FileOperationService, bevel-p3g). Walks each
/// source depth-first, enumerating every child — including hidden/system entries for relocations,
/// so a Move can actually empty the source (review #12) — and produces the per-target-volume job
/// list that the executor drains.
/// </summary>
internal sealed class FileOpScanner
{
    private readonly VfsRoot _vfs;

    public FileOpScanner(VfsRoot vfs) => _vfs = vfs;

    public async Task<PreScanResult> ScanAsync(
        string opId, IReadOnlyList<VfsPath> sources, VfsPath destination, FileOpKind kind, CancellationToken ct)
    {
        var jobs = new List<FileOpJob>();
        long totalSize = 0;
        int fileCount = 0;
        int folderCount = 0;

        foreach (var source in sources)
        {
            var (size, files, folders) = await ScanPathAsync(source, destination, kind, opId, jobs, ct);
            totalSize += size;
            fileCount += files;
            folderCount += folders;
        }

        var byVolume = jobs.GroupBy(j => j.VolumeKey)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<FileOpJob>)g.ToList());

        return new PreScanResult
        {
            Jobs = jobs,
            JobsByVolume = byVolume,
            TotalSize = totalSize,
            TotalFileCount = fileCount,
            TotalFolderCount = folderCount,
        };
    }

    private async Task<(long totalSize, int fileCount, int folderCount)> ScanPathAsync(
        VfsPath source,
        VfsPath destination,
        FileOpKind kind,
        string opId,
        List<FileOpJob> jobs,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        long totalSize = 0;
        int fileCount = 0;
        int folderCount = 0;

        var provider = _vfs.GetProvider(source);
        var node = await provider.ResolveAsync(source, ct);

        if (node.Kind == VfsNodeKind.Folder)
        {
            folderCount++;
            var destChild = VfsPath.Combine(destination, source.FileName);

            // A Copy/Move must relocate EVERY child, including hidden/system entries;
            // otherwise the source folder can't be emptied and the operation silently
            // under-copies while still reporting Success (review #12).
            var childOptions = kind is FileOpKind.Copy or FileOpKind.Move
                ? new EnumerateOptions { IncludeHidden = true, IncludeSystem = true }
                : new EnumerateOptions();
            await foreach (var child in provider.EnumerateAsync(source, childOptions, ct))
            {
                ct.ThrowIfCancellationRequested();
                var (size, files, folders) = await ScanPathAsync(child.Path, destChild, kind, opId, jobs, ct);
                totalSize += size;
                fileCount += files;
                folderCount += folders;
            }

            jobs.Add(new FileOpJob
            {
                OperationId = opId,
                Kind = kind,
                Source = source,
                Destination = destChild,
                VolumeKey = _vfs.GetProvider(destination).GetVolumeKey(destination),
                Size = 0,
            });
        }
        else
        {
            fileCount++;
            var size = node.Size ?? 0;
            totalSize += size;
            var destChild = VfsPath.Combine(destination, source.FileName);

            jobs.Add(new FileOpJob
            {
                OperationId = opId,
                Kind = kind,
                Source = source,
                Destination = destChild,
                VolumeKey = _vfs.GetProvider(destination).GetVolumeKey(destination),
                Size = size,
            });
        }

        return (totalSize, fileCount, folderCount);
    }
}
