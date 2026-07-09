using Bevel.Core.Vfs;

namespace Bevel.FileManager.FileOperations;

/// <summary>
/// Reverses a previously-recorded operation (extracted from FileOperationService, bevel-p3g).
/// The facade pops the <see cref="UndoEntry"/> and hands it here; this executor knows how to undo
/// each kind: Move/Trash by moving the item back (via the facade's reverse-move primitive),
/// Copy by deleting the copies, Rename by renaming back. Undo runs sequentially, not through the
/// volume queue, so reverse moves happen one at a time.
/// </summary>
internal sealed class UndoExecutor
{
    private readonly VfsRoot _vfs;
    private readonly Func<FileOpJob, CancellationToken, Task> _reverseMove;
    private readonly Action<FileOpProgress> _report;

    public UndoExecutor(
        VfsRoot vfs,
        Func<FileOpJob, CancellationToken, Task> reverseMove,
        Action<FileOpProgress> report)
    {
        _vfs = vfs;
        _reverseMove = reverseMove;
        _report = report;
    }

    public async Task<FileOpResult> ExecuteAsync(UndoEntry entry, CancellationToken ct)
        => entry switch
        {
            MoveUndoEntry e => await UndoMoveAsync(e, ct),
            CopyUndoEntry e => await UndoCopyAsync(e, ct),
            RenameUndoEntry e => await UndoRenameAsync(e, ct),
            TrashUndoEntry e => await UndoTrashAsync(e, ct),
            _ => throw new InvalidOperationException($"Unknown undo entry type: {entry.GetType().Name}")
        };

    private async Task<FileOpResult> UndoMoveAsync(MoveUndoEntry entry, CancellationToken ct)
    {
        var results = new List<FileItemResult>();
        int idx = 0;

        foreach (var (source, destination) in entry.Pairs)
        {
            ct.ThrowIfCancellationRequested();
            _report(FileOpHelpers.MakeProgress(entry.OperationId, FileOpStatus.Running, idx, entry.Pairs.Count, 0, 0, destination.FileName));

            try
            {
                var provider = _vfs.GetProvider(source);
                var parentPath = new VfsPath(source.Scheme, source.ParentValue);
                var mutator = await provider.GetMutatorAsync(parentPath, ct);

                if (mutator is null)
                    throw new InvalidOperationException($"Cannot undo move: {source}");

                // Don't silently overwrite a file recreated at the original location since
                // the move — fail the undo of this item instead of clobbering (review #11).
                if (await FileOpHelpers.TryResolveAsync(_vfs, source, ct) is not null)
                    throw new IOException(
                        $"'{source.FileName}' already exists at its original location; not overwriting on undo.");

                // Reverse: move from destination back to source
                var reverseJob = new FileOpJob
                {
                    OperationId = entry.OperationId,
                    Kind = FileOpKind.Move,
                    Source = destination,
                    Destination = source,
                    VolumeKey = _vfs.GetProvider(source).GetVolumeKey(source),
                };
                await _reverseMove(reverseJob, ct);

                results.Add(new FileItemResult
                {
                    Source = destination, Destination = source,
                    Status = FileItemResultStatus.Success,
                });
            }
            catch (Exception ex)
            {
                results.Add(new FileItemResult
                {
                    Source = destination, Destination = source,
                    Status = FileItemResultStatus.Failed,
                    ErrorMessage = ex.Message,
                });
            }

            idx++;
        }

        return new FileOpResult
        {
            OperationId = entry.OperationId, Kind = FileOpKind.Undo,
            Status = FileOpHelpers.BuildFinalStatus(results), ItemResults = results,
        };
    }

    private async Task<FileOpResult> UndoCopyAsync(CopyUndoEntry entry, CancellationToken ct)
    {
        var results = new List<FileItemResult>();
        int idx = 0;

        foreach (var copiedPath in entry.CopiedPaths)
        {
            ct.ThrowIfCancellationRequested();
            _report(FileOpHelpers.MakeProgress(entry.OperationId, FileOpStatus.Running, idx, entry.CopiedPaths.Count, 0, 0, copiedPath.FileName));

            try
            {
                var provider = _vfs.GetProvider(copiedPath);
                var parentPath = new VfsPath(copiedPath.Scheme, copiedPath.ParentValue);
                var mutator = await provider.GetMutatorAsync(parentPath, ct);

                if (mutator is null)
                    throw new InvalidOperationException($"Cannot undo copy: {copiedPath}");

                await mutator.DeleteAsync(copiedPath, toTrash: false, ct);

                results.Add(new FileItemResult
                {
                    Source = copiedPath, Destination = null,
                    Status = FileItemResultStatus.Success,
                });
            }
            catch (Exception ex)
            {
                results.Add(new FileItemResult
                {
                    Source = copiedPath, Destination = null,
                    Status = FileItemResultStatus.Failed,
                    ErrorMessage = ex.Message,
                });
            }

            idx++;
        }

        return new FileOpResult
        {
            OperationId = entry.OperationId, Kind = FileOpKind.Undo,
            Status = FileOpHelpers.BuildFinalStatus(results), ItemResults = results,
        };
    }

    private async Task<FileOpResult> UndoRenameAsync(RenameUndoEntry entry, CancellationToken ct)
    {
        var results = new List<FileItemResult>();

        foreach (var (currentPath, originalPath) in entry.Pairs)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var provider = _vfs.GetProvider(currentPath);
                var parentPath = new VfsPath(currentPath.Scheme, currentPath.ParentValue);
                var mutator = await provider.GetMutatorAsync(parentPath, ct);

                if (mutator is null)
                    throw new InvalidOperationException($"Cannot undo rename: {currentPath}");

                await mutator.RenameAsync(currentPath, originalPath.FileName, ct);

                results.Add(new FileItemResult
                {
                    Source = currentPath, Destination = originalPath,
                    Status = FileItemResultStatus.Success,
                });
            }
            catch (Exception ex)
            {
                results.Add(new FileItemResult
                {
                    Source = currentPath, Destination = originalPath,
                    Status = FileItemResultStatus.Failed,
                    ErrorMessage = ex.Message,
                });
            }
        }

        return new FileOpResult
        {
            OperationId = entry.OperationId, Kind = FileOpKind.Undo,
            Status = FileOpHelpers.BuildFinalStatus(results), ItemResults = results,
        };
    }

    private async Task<FileOpResult> UndoTrashAsync(TrashUndoEntry entry, CancellationToken ct)
    {
        var results = new List<FileItemResult>();

        foreach (var (original, trashPath) in entry.Pairs)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // Don't clobber a file recreated at the original location while the item
                // sat in the trash — fail this restore instead of overwriting (review #11).
                if (await FileOpHelpers.TryResolveAsync(_vfs, original, ct) is not null)
                    throw new IOException(
                        $"'{original.FileName}' already exists at its original location; not overwriting on undo.");

                // Restore from trash: move trashPath back to original location
                var reverseJob = new FileOpJob
                {
                    OperationId = entry.OperationId,
                    Kind = FileOpKind.Move,
                    Source = trashPath,
                    Destination = original,
                    VolumeKey = _vfs.GetProvider(original).GetVolumeKey(original),
                };
                await _reverseMove(reverseJob, ct);

                results.Add(new FileItemResult
                {
                    Source = trashPath, Destination = original,
                    Status = FileItemResultStatus.Success,
                });
            }
            catch (Exception ex)
            {
                results.Add(new FileItemResult
                {
                    Source = trashPath, Destination = original,
                    Status = FileItemResultStatus.Failed,
                    ErrorMessage = ex.Message,
                });
            }
        }

        return new FileOpResult
        {
            OperationId = entry.OperationId, Kind = FileOpKind.Undo,
            Status = FileOpHelpers.BuildFinalStatus(results), ItemResults = results,
        };
    }
}
