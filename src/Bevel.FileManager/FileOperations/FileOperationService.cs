using System.Reactive.Subjects;
using Bevel.Core.Vfs;

namespace Bevel.FileManager.FileOperations;

/// <summary>
/// Progress event emitted during file operation execution.
/// </summary>
public sealed record FileOpProgress
{
    public required string OperationId { get; init; }
    public required FileOpStatus Status { get; init; }
    public required int CurrentFileIndex { get; init; }
    public required int TotalFiles { get; init; }
    public required long BytesTransferred { get; init; }
    public required long TotalBytes { get; init; }
    public required string CurrentFileName { get; init; }
    public string? VolumeKey { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// Core file operations engine (FM-130..136) — a thin facade over focused collaborators
/// (bevel-p3g):
/// <list type="bullet">
/// <item><see cref="FileOpScanner"/> — pre-scan (enumerate + size totals + per-volume jobs).</item>
/// <item><see cref="VolumeQueueExecutor"/> — FIFO queue per target volume, parallel across volumes.</item>
/// <item><see cref="ConflictCoordinator"/> — conflict resolution via IConflictHandler.</item>
/// <item><see cref="UndoExecutor"/> — multi-level undo (depth 10).</item>
/// </list>
/// The facade owns the public API, the progress observable, the undo stack, cancellation, and the
/// low-level transfer primitives (<see cref="MoveFileAsync"/>/<see cref="CopyFileAsync"/>) that a
/// forward Move/Copy and an undo both reuse. Move and Copy share one <see cref="ExecuteTransferAsync"/>
/// (review #18). All pure managed I/O — no Avalonia dependency.
/// </summary>
public sealed class FileOperationService : IDisposable
{
    private readonly VfsRoot _vfs;
    private readonly UndoStack _undoStack;
    private readonly int _copyBufferSize;
    private readonly Subject<FileOpProgress> _progressSubject = new();
    private CancellationTokenSource? _cts;
    private readonly object _ctsGate = new();

    private readonly FileOpScanner _scanner;
    private readonly VolumeQueueExecutor _volumes = new();
    private readonly ConflictCoordinator _conflicts;
    private readonly UndoExecutor _undoExecutor;

    public IObservable<FileOpProgress> Progress => _progressSubject;
    public UndoStack Undo => _undoStack;

    public FileOperationService(
        VfsRoot vfs,
        IConflictHandler conflictHandler,
        int maxUndoDepth = 10,
        int copyBufferSize = 81920)
    {
        _vfs = vfs;
        _undoStack = new UndoStack(maxUndoDepth);
        _copyBufferSize = copyBufferSize;
        _scanner = new FileOpScanner(vfs);
        _conflicts = new ConflictCoordinator(vfs, conflictHandler);
        // Undo reuses the same reverse-move primitive and progress stream as a forward op.
        _undoExecutor = new UndoExecutor(vfs, MoveFileAsync, p => _progressSubject.OnNext(p));
    }

    /// <summary>
    /// Execute a file operation request. Returns the result when complete.
    /// </summary>
    public async Task<FileOpResult> ExecuteAsync(FileOpRequest request, CancellationToken externalCt = default)
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        lock (_ctsGate)
            _cts = linkedCts;

        try
        {
            var ct = linkedCts.Token;
            return request switch
            {
                MoveRequest r => await ExecuteTransferAsync(FileOpKind.Move, r.Sources, r.Destination, linkedCts),
                CopyRequest r => await ExecuteTransferAsync(FileOpKind.Copy, r.Sources, r.Destination, linkedCts),
                RenameRequest r => await ExecuteRenameAsync(r, ct),
                DeleteRequest r => await ExecuteDeleteAsync(r, ct),
                UndoRequest r => await ExecuteUndoAsync(r, ct),
                _ => throw new ArgumentOutOfRangeException(nameof(request))
            };
        }
        finally
        {
            // Clear the field before disposing so a late Cancel() never touches a
            // disposed CTS, and only if this operation is still the current one (an
            // overlapping ExecuteAsync may have replaced it — we still own our CTS).
            lock (_ctsGate)
            {
                if (ReferenceEquals(_cts, linkedCts))
                    _cts = null;
            }
            linkedCts.Dispose();
        }
    }

    /// <summary>
    /// Reverses the most recent undoable operation and returns the result. This is the
    /// ONLY supported way to trigger undo — callers must not pop <see cref="Undo"/>
    /// directly, which would discard the record without reverting anything (review #1/AD2).
    /// </summary>
    public Task<FileOpResult> UndoAsync(CancellationToken ct = default)
        => ExecuteAsync(
            new UndoRequest { OperationId = Guid.NewGuid().ToString("N")[..12], Timestamp = DateTimeOffset.UtcNow },
            ct);

    /// <summary>
    /// Request cancellation of the in-flight operation, if any. The current file
    /// finishes, then processing stops. Safe to call at any time, including after
    /// the operation has already completed.
    /// </summary>
    public void Cancel()
    {
        lock (_ctsGate)
        {
            try { _cts?.Cancel(); }
            catch (ObjectDisposedException) { /* operation already finished */ }
        }
    }

    // ── Transfer (Move / Copy share one path — review #18) ─────────────────

    private async Task<FileOpResult> ExecuteTransferAsync(
        FileOpKind kind, IReadOnlyList<VfsPath> sources, VfsPath destination, CancellationTokenSource opCts)
    {
        var isMove = kind == FileOpKind.Move;
        var ct = opCts.Token;
        var scan = await _scanner.ScanAsync(sources, destination, kind, ct);
        var opId = scan.Jobs.Count > 0 ? scan.Jobs[0].OperationId : Guid.NewGuid().ToString("N")[..12];
        var results = new List<FileItemResult>();
        var completed = new List<FileOpJob>();

        // Jobs on different volumes run concurrently, so every mutation of the shared
        // result/completed lists is serialized through this gate.
        var sync = new object();
        void AddResult(FileItemResult r) { lock (sync) results.Add(r); }
        static FileItemResult Outcome(FileOpJob j, FileItemResultStatus s, string? err = null)
            => new() { Source = j.Source, Destination = j.Destination, Status = s, ErrorMessage = err };

        var bytesBefore = BuildBytesBeforeMap(scan.Jobs);
        EmitProgress(opId, FileOpStatus.Scanning, 0, scan.TotalFileCount, 0, scan.TotalSize, "");

        using var sticky = _conflicts.NewResolver();

        await _volumes.ExecuteAsync(scan.JobsByVolume, ct, async (job, fileIndex) =>
        {
            if (ct.IsCancellationRequested)
            {
                // Stop gracefully: record this job as cancelled and let the batch drain so
                // items already completed still get an undo entry (review #2).
                AddResult(Outcome(job, FileItemResultStatus.Cancelled));
                return;
            }
            EmitProgress(opId, FileOpStatus.Running, fileIndex, scan.TotalFileCount,
                bytesBefore[job], scan.TotalSize, job.Source.FileName, job.VolumeKey);

            try
            {
                var resolved = await _conflicts.ResolveAsync(job, sticky, fileIndex, scan.TotalFileCount, ct);
                if (resolved == ConflictResolution.Cancel)
                {
                    job.Status = FileOpJobStatus.Cancelled;
                    AddResult(Outcome(job, FileItemResultStatus.Cancelled));
                    opCts.Cancel();
                    return;
                }
                if (resolved == ConflictResolution.No)
                {
                    job.Status = FileOpJobStatus.Skipped;
                    AddResult(Outcome(job, FileItemResultStatus.Skipped));
                    return;
                }

                if (isMove) await MoveFileAsync(job, ct);
                else await CopyFileAsync(job, ct);

                job.Status = FileOpJobStatus.Completed;
                lock (sync)
                {
                    completed.Add(job);
                    results.Add(new FileItemResult
                    {
                        Source = job.Source, Destination = job.Destination,
                        Status = FileItemResultStatus.Success,
                        UndoSource = job.Destination,
                        // A Copy's undo just deletes the copy, so it has no undo-destination;
                        // a Move's undo relocates the item back to where it came from.
                        UndoDestination = isMove ? job.Source : null,
                    });
                }
            }
            catch (OperationCanceledException)
            {
                job.Status = FileOpJobStatus.Cancelled;
                AddResult(Outcome(job, FileItemResultStatus.Cancelled));
            }
            catch (Exception ex)
            {
                job.Status = FileOpJobStatus.Failed;
                job.ErrorMessage = ex.Message;
                AddResult(Outcome(job, FileItemResultStatus.Failed, ex.Message));
            }
        });

        if (completed.Count > 0)
        {
            _undoStack.Push(isMove
                ? new MoveUndoEntry
                {
                    OperationId = opId,
                    Timestamp = DateTimeOffset.UtcNow,
                    OriginalKind = FileOpKind.Move,
                    Pairs = completed.Select(j => (j.Source, j.Destination)).ToList(),
                    Description = $"Moved {completed.Count} item(s)",
                }
                : new CopyUndoEntry
                {
                    OperationId = opId,
                    Timestamp = DateTimeOffset.UtcNow,
                    OriginalKind = FileOpKind.Copy,
                    CopiedPaths = completed.Select(j => j.Destination).ToList(),
                    Description = $"Copied {completed.Count} item(s)",
                });
        }

        var status = FileOpHelpers.BuildFinalStatus(results);
        var finalResult = new FileOpResult
        {
            OperationId = opId, Kind = kind,
            Status = status, ItemResults = results,
        };
        EmitProgress(opId, status, scan.TotalFileCount, scan.TotalFileCount,
            scan.TotalSize, scan.TotalSize, "");

        return finalResult;
    }

    private async Task MoveFileAsync(FileOpJob job, CancellationToken ct)
    {
        var sourceProvider = _vfs.GetProvider(job.Source);
        var sourceNode = await sourceProvider.ResolveAsync(job.Source, ct);

        if (sourceNode.Kind == VfsNodeKind.Folder)
        {
            var destProvider = _vfs.GetProvider(job.Destination);
            var destMutator = await destProvider.GetMutatorAsync(job.Destination.ParentValue == ""
                ? job.Destination : new VfsPath(job.Destination.Scheme, job.Destination.ParentValue), ct);

            if (destMutator is null)
                throw new InvalidOperationException($"Cannot create destination folder: {job.Destination}");

            await destMutator.CreateFolderAsync(
                new VfsPath(job.Destination.Scheme, job.Destination.ParentValue),
                job.Destination.FileName, ct);

            // The folder's children are relocated by their own jobs (scanned depth-first, so
            // they run before this folder job within the same destination volume). Remove the
            // now-empty source directory so a Move doesn't leave the original tree behind —
            // but ONLY when the provider exposes a native path AND the directory is genuinely
            // empty. A non-empty source (leftover hidden files, or a skipped/failed child) is
            // left in place rather than recursively deleted, so nothing that wasn't moved is lost.
            var srcNative = sourceProvider.ResolveEffectivePath(job.Source);
            if (srcNative is not null && Directory.Exists(srcNative)
                && !Directory.EnumerateFileSystemEntries(srcNative).Any())
            {
                Directory.Delete(srcNative, recursive: false);
            }
        }
        else
        {
            // Fast path: a native atomic rename, but only when the source provider says source
            // and destination share one volume (same disk). Everything else — cross-volume or
            // cross-scheme — falls through to the explicit copy+delete below.
            if (sourceProvider.CanFastMoveWithin(job.Source, job.Destination))
            {
                var srcNative = sourceProvider.ResolveEffectivePath(job.Source)!;
                var dstNative = _vfs.GetProvider(job.Destination).ResolveEffectivePath(job.Destination)!;

                var dstParent = Path.GetDirectoryName(dstNative);
                if (dstParent is not null && !Directory.Exists(dstParent))
                    Directory.CreateDirectory(dstParent);

                File.Move(srcNative, dstNative, overwrite: true);
                return;
            }

            // Cross-volume or cross-scheme: copy + delete
            await CopyFileContentsAsync(job.Source, job.Destination, ct);

            var mutator = await sourceProvider.GetMutatorAsync(
                new VfsPath(job.Source.Scheme, job.Source.ParentValue), ct);
            if (mutator is not null)
                await mutator.DeleteAsync(job.Source, toTrash: false, ct);
        }
    }

    private async Task CopyFileAsync(FileOpJob job, CancellationToken ct)
    {
        var sourceProvider = _vfs.GetProvider(job.Source);
        var sourceNode = await sourceProvider.ResolveAsync(job.Source, ct);

        if (sourceNode.Kind == VfsNodeKind.Folder)
        {
            var destProvider = _vfs.GetProvider(job.Destination);
            var parentPath = job.Destination.ParentValue == ""
                ? job.Destination
                : new VfsPath(job.Destination.Scheme, job.Destination.ParentValue);
            var destMutator = await destProvider.GetMutatorAsync(parentPath, ct);

            if (destMutator is null)
                throw new InvalidOperationException($"Cannot create destination folder: {job.Destination}");

            await destMutator.CreateFolderAsync(parentPath, job.Destination.FileName, ct);
        }
        else
        {
            await CopyFileContentsAsync(job.Source, job.Destination, ct);
        }
    }

    private async Task CopyFileContentsAsync(VfsPath source, VfsPath destination, CancellationToken ct)
    {
        var sourceProvider = _vfs.GetProvider(source);
        var destProvider = _vfs.GetProvider(destination);

        // Ensure parent directory exists
        var parentPath = destination.ParentValue == ""
            ? destination
            : new VfsPath(destination.Scheme, destination.ParentValue);
        var destMutator = await destProvider.GetMutatorAsync(parentPath, ct);

        // A null mutator means the destination provider is read-only. Fail with a clear
        // error rather than opening the destination for READ and writing into it (which
        // would throw NotSupportedException deep inside CopyToAsync).
        if (destMutator is null)
            throw new InvalidOperationException($"Destination is read-only; cannot copy to '{destination}'.");

        await using var readStream = await sourceProvider.OpenReadAsync(source, ct);
        await using var writeStream = await destMutator.OpenWriteAsync(destination, ct);
        await readStream.CopyToAsync(writeStream, _copyBufferSize, ct);
    }

    // ── Rename ─────────────────────────────────────────────────────────────

    private async Task<FileOpResult> ExecuteRenameAsync(RenameRequest request, CancellationToken ct)
    {
        var opId = Guid.NewGuid().ToString("N")[..12];
        var results = new List<FileItemResult>();

        EmitProgress(opId, FileOpStatus.Running, 0, 1, 0, 0, request.Path.FileName);

        try
        {
            var provider = _vfs.GetProvider(request.Path);
            var parentPath = new VfsPath(request.Path.Scheme, request.Path.ParentValue);
            var mutator = await provider.GetMutatorAsync(parentPath, ct);

            if (mutator is null)
                throw new InvalidOperationException($"Cannot rename: {request.Path}");

            var oldName = request.Path.FileName;
            await mutator.RenameAsync(request.Path, request.NewName, ct);

            var newPath = VfsPath.Combine(parentPath, request.NewName);

            _undoStack.Push(new RenameUndoEntry
            {
                OperationId = opId,
                Timestamp = DateTimeOffset.UtcNow,
                OriginalKind = FileOpKind.Rename,
                Pairs = [(newPath, request.Path)],
                Description = $"Renamed '{oldName}' to '{request.NewName}'",
            });

            results.Add(new FileItemResult
            {
                Source = request.Path, Destination = newPath,
                Status = FileItemResultStatus.Success,
                UndoSource = newPath,
                UndoDestination = request.Path,
            });

            var finalResult = new FileOpResult
            {
                OperationId = opId, Kind = FileOpKind.Rename,
                Status = FileOpStatus.Completed, ItemResults = results,
            };
            EmitProgress(opId, FileOpStatus.Completed, 1, 1, 0, 0, "");

            return finalResult;
        }
        catch (Exception ex)
        {
            results.Add(new FileItemResult
            {
                Source = request.Path, Destination = null,
                Status = FileItemResultStatus.Failed, ErrorMessage = ex.Message,
            });

            var finalResult = new FileOpResult
            {
                OperationId = opId, Kind = FileOpKind.Rename,
                Status = FileOpStatus.Failed, ItemResults = results,
                ErrorMessage = ex.Message,
            };
            EmitProgress(opId, FileOpStatus.Failed, 0, 1, 0, 0, "");

            return finalResult;
        }
    }

    // ── Delete / Trash ─────────────────────────────────────────────────────

    private async Task<FileOpResult> ExecuteDeleteAsync(DeleteRequest request, CancellationToken ct)
    {
        var opId = Guid.NewGuid().ToString("N")[..12];
        var results = new List<FileItemResult>();
        var trashPairs = new List<(VfsPath Original, VfsPath TrashPath)>();

        EmitProgress(opId, FileOpStatus.Running, 0, request.Paths.Count, 0, 0, "");

        int fileIndex = 0;
        foreach (var path in request.Paths)
        {
            ct.ThrowIfCancellationRequested();
            EmitProgress(opId, FileOpStatus.Running, fileIndex, request.Paths.Count, 0, 0, path.FileName);

            try
            {
                var provider = _vfs.GetProvider(path);
                var parentPath = new VfsPath(path.Scheme, path.ParentValue);
                var mutator = await provider.GetMutatorAsync(parentPath, ct);

                if (mutator is null)
                    throw new InvalidOperationException($"Cannot delete: {path}");

                // A trash delete returns the item's new location so it can be restored.
                var trashPath = await mutator.DeleteAsync(path, request.ToTrash, ct);
                if (trashPath is { } tp)
                    trashPairs.Add((path, tp));

                results.Add(new FileItemResult
                {
                    Source = path, Destination = null,
                    Status = FileItemResultStatus.Success,
                });
            }
            catch (OperationCanceledException)
            {
                results.Add(new FileItemResult
                {
                    Source = path, Destination = null,
                    Status = FileItemResultStatus.Cancelled,
                });
            }
            catch (Exception ex)
            {
                results.Add(new FileItemResult
                {
                    Source = path, Destination = null,
                    Status = FileItemResultStatus.Failed,
                    ErrorMessage = ex.Message,
                });
            }

            fileIndex++;
        }

        // Record a restore entry for trashed items so Delete-to-Trash can be undone.
        if (trashPairs.Count > 0)
        {
            _undoStack.Push(new TrashUndoEntry
            {
                OperationId = opId,
                Timestamp = DateTimeOffset.UtcNow,
                OriginalKind = FileOpKind.Delete,
                Pairs = trashPairs,
                Description = $"Trashed {trashPairs.Count} item(s)",
            });
        }

        var status = FileOpHelpers.BuildFinalStatus(results);
        var finalResult = new FileOpResult
        {
            OperationId = opId, Kind = FileOpKind.Delete,
            Status = status, ItemResults = results,
        };
        EmitProgress(opId, status, request.Paths.Count, request.Paths.Count, 0, 0, "");

        return finalResult;
    }

    // ── Undo ───────────────────────────────────────────────────────────────

    private async Task<FileOpResult> ExecuteUndoAsync(UndoRequest request, CancellationToken ct)
    {
        var entry = _undoStack.Pop();
        if (entry is null)
            return new FileOpResult
            {
                OperationId = request.OperationId,
                Kind = FileOpKind.Undo,
                Status = FileOpStatus.Failed,
                ItemResults = [],
                ErrorMessage = "Nothing to undo.",
            };

        return await _undoExecutor.ExecuteAsync(entry, ct);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Precomputes cumulative bytes-before for every job in a single pass (review #16).
    /// Replaces a per-file O(n) rescan of all jobs that made progress reporting O(n^2) on
    /// large batches. Reference-keyed so value-equal jobs never collide.
    /// </summary>
    private static Dictionary<FileOpJob, long> BuildBytesBeforeMap(IReadOnlyList<FileOpJob> allJobs)
    {
        var map = new Dictionary<FileOpJob, long>(ReferenceEqualityComparer.Instance);
        long running = 0;
        foreach (var j in allJobs)
        {
            map[j] = running;
            running += j.Size;
        }
        return map;
    }

    private void EmitProgress(
        string opId, FileOpStatus status, int current, int total,
        long bytesTransferred, long totalBytes, string currentFile,
        string? volumeKey = null, string? message = null)
        => _progressSubject.OnNext(FileOpHelpers.MakeProgress(
            opId, status, current, total, bytesTransferred, totalBytes, currentFile, volumeKey, message));

    public void Dispose()
    {
        _cts?.Dispose();
        _progressSubject.Dispose();
        _volumes.Dispose();
    }
}
