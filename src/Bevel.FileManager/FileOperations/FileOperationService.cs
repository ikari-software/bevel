using System.Collections.Concurrent;
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
/// Core file operations engine (FM-130..136).
/// - FIFO queue per target volume, parallel across volumes
/// - Pre-scan phase (enumerate + size totals)
/// - Conflict resolution via IConflictHandler
/// - Multi-level undo stack (depth 10)
/// - Progress observable (IObservable)
/// - Cancel stops after current file completes
/// All pure managed I/O — no Avalonia dependency.
/// </summary>
public sealed class FileOperationService : IDisposable
{
    private readonly VfsRoot _vfs;
    private readonly IConflictHandler _conflictHandler;
    private readonly UndoStack _undoStack;
    private readonly int _copyBufferSize;
    private readonly Subject<FileOpProgress> _progressSubject = new();
    private CancellationTokenSource? _cts;
    private readonly object _ctsGate = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _volumeQueues = new();

    public IObservable<FileOpProgress> Progress => _progressSubject;
    public UndoStack Undo => _undoStack;

    public FileOperationService(
        VfsRoot vfs,
        IConflictHandler conflictHandler,
        int maxUndoDepth = 10,
        int copyBufferSize = 81920)
    {
        _vfs = vfs;
        _conflictHandler = conflictHandler;
        _undoStack = new UndoStack(maxUndoDepth);
        _copyBufferSize = copyBufferSize;
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
                MoveRequest r => await ExecuteMoveAsync(r, linkedCts),
                CopyRequest r => await ExecuteCopyAsync(r, linkedCts),
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

    // ── Pre-scan ──────────────────────────────────────────────────────────

    private async Task<PreScanResult> PreScanMoveAsync(
        IReadOnlyList<VfsPath> sources, VfsPath destination, CancellationToken ct)
        => await PreScanAsync(sources, destination, FileOpKind.Move, ct);

    private async Task<PreScanResult> PreScanCopyAsync(
        IReadOnlyList<VfsPath> sources, VfsPath destination, CancellationToken ct)
        => await PreScanAsync(sources, destination, FileOpKind.Copy, ct);

    private async Task<PreScanResult> PreScanAsync(
        IReadOnlyList<VfsPath> sources,
        VfsPath destination,
        FileOpKind kind,
        CancellationToken ct)
    {
        var jobs = new List<FileOpJob>();
        var opId = Guid.NewGuid().ToString("N")[..12];
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

            var volumeKey = GetVolumeKey(destination);
            jobs.Add(new FileOpJob
            {
                OperationId = opId,
                Kind = kind,
                Source = source,
                Destination = destChild,
                VolumeKey = volumeKey,
                Size = 0,
            });
        }
        else
        {
            fileCount++;
            var size = node.Size ?? 0;
            totalSize += size;
            var destChild = VfsPath.Combine(destination, source.FileName);
            var volumeKey = GetVolumeKey(destination);

            jobs.Add(new FileOpJob
            {
                OperationId = opId,
                Kind = kind,
                Source = source,
                Destination = destChild,
                VolumeKey = volumeKey,
                Size = size,
            });
        }

        return (totalSize, fileCount, folderCount);
    }

    // ── Move ──────────────────────────────────────────────────────────────

    private async Task<FileOpResult> ExecuteMoveAsync(MoveRequest request, CancellationTokenSource opCts)
    {
        var ct = opCts.Token;
        var scan = await PreScanMoveAsync(request.Sources, request.Destination, ct);
        var opId = scan.Jobs.Count > 0 ? scan.Jobs[0].OperationId : Guid.NewGuid().ToString("N")[..12];
        var results = new List<FileItemResult>();
        var undoPairs = new List<(VfsPath Source, VfsPath Destination)>();

        // Jobs on different volumes run concurrently (ExecuteByVolumeAsync), so every
        // mutation of the shared result/undo lists is serialized through this gate.
        var sync = new object();
        void AddResult(FileItemResult r) { lock (sync) results.Add(r); }

        var bytesBefore = BuildBytesBeforeMap(scan.Jobs);
        EmitProgress(opId, FileOpStatus.Scanning, 0, scan.TotalFileCount, 0, scan.TotalSize, "");

        using var sticky = new StickyConflictResolver(_conflictHandler);

        await ExecuteByVolumeAsync(scan.JobsByVolume, ct, async (job, volumeSem, fileIndex) =>
        {
            if (ct.IsCancellationRequested)
            {
                // Stop gracefully: record this job as cancelled and let the batch drain so
                // items already completed still get an undo entry (review #2). Throwing here
                // (the old behavior) unwound before the undo-stack push below.
                AddResult(new FileItemResult
                {
                    Source = job.Source, Destination = job.Destination,
                    Status = FileItemResultStatus.Cancelled,
                });
                return;
            }
            EmitProgress(opId, FileOpStatus.Running, fileIndex, scan.TotalFileCount,
                bytesBefore[job], scan.TotalSize, job.Source.FileName, job.VolumeKey);

            try
            {
                var resolved = await HandleConflictIfNeeded(job, sticky, fileIndex, scan.TotalFileCount, ct);
                if (resolved == ConflictResolution.Cancel)
                {
                    job.Status = FileOpJobStatus.Cancelled;
                    AddResult(new FileItemResult
                    {
                        Source = job.Source, Destination = job.Destination,
                        Status = FileItemResultStatus.Cancelled,
                    });
                    opCts.Cancel();
                    return;
                }
                if (resolved == ConflictResolution.No)
                {
                    job.Status = FileOpJobStatus.Skipped;
                    AddResult(new FileItemResult
                    {
                        Source = job.Source, Destination = job.Destination,
                        Status = FileItemResultStatus.Skipped,
                    });
                    return;
                }

                await MoveFileAsync(job, ct);

                job.Status = FileOpJobStatus.Completed;
                lock (sync)
                {
                    undoPairs.Add((job.Source, job.Destination));
                    results.Add(new FileItemResult
                    {
                        Source = job.Source, Destination = job.Destination,
                        Status = FileItemResultStatus.Success,
                        UndoSource = job.Destination,
                        UndoDestination = job.Source,
                    });
                }
            }
            catch (OperationCanceledException)
            {
                job.Status = FileOpJobStatus.Cancelled;
                AddResult(new FileItemResult
                {
                    Source = job.Source, Destination = job.Destination,
                    Status = FileItemResultStatus.Cancelled,
                });
            }
            catch (Exception ex)
            {
                job.Status = FileOpJobStatus.Failed;
                job.ErrorMessage = ex.Message;
                AddResult(new FileItemResult
                {
                    Source = job.Source, Destination = job.Destination,
                    Status = FileItemResultStatus.Failed,
                    ErrorMessage = ex.Message,
                });
            }
        });

        if (undoPairs.Count > 0)
        {
            _undoStack.Push(new MoveUndoEntry
            {
                OperationId = opId,
                Timestamp = DateTimeOffset.UtcNow,
                OriginalKind = FileOpKind.Move,
                Pairs = undoPairs,
                Description = $"Moved {undoPairs.Count} item(s)",
            });
        }

        var status = BuildFinalStatus(results);
        var finalResult = new FileOpResult
        {
            OperationId = opId, Kind = FileOpKind.Move,
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

            // The folder's children are relocated by their own jobs, which are
            // scanned depth-first (see ScanPathAsync) and therefore run before this
            // folder job within the same destination volume. Remove the now-empty
            // source directory so a Move doesn't leave the original tree behind.
            // Delete NON-recursively and only when the source is genuinely empty:
            // the move scan skips hidden entries (EnumerateOptions.IncludeHidden
            // defaults to false), so a recursive delete here could destroy files
            // that were never moved. A non-empty source (leftover hidden files, or
            // a skipped/failed child) simply leaves the folder in place.
            if (job.Source.Scheme == "file")
            {
                var srcFull = job.Source.Value;
                if (Directory.Exists(srcFull) && !Directory.EnumerateFileSystemEntries(srcFull).Any())
                    Directory.Delete(srcFull, recursive: false);
            }
        }
        else
        {
            // For local file moves within same volume, use File.Move for efficiency.
            if (job.Source.Scheme == "file" && job.Destination.Scheme == "file")
            {
                var srcPath = job.Source.Value;
                var dstPath = job.Destination.Value;

                var srcDir = Path.GetDirectoryName(srcPath) ?? "";
                var dstDir = Path.GetDirectoryName(dstPath) ?? "";

                // Same volume = rename (fast), cross-volume = copy+delete
                if (string.Equals(Path.GetPathRoot(srcPath), Path.GetPathRoot(dstPath), StringComparison.OrdinalIgnoreCase))
                {
                    var dstParent = Path.GetDirectoryName(dstPath);
                    if (dstParent is not null && !Directory.Exists(dstParent))
                        Directory.CreateDirectory(dstParent);

                    File.Move(srcPath, dstPath, overwrite: true);
                    return;
                }
            }

            // Cross-volume or cross-scheme: copy + delete
            await CopyFileContentsAsync(job.Source, job.Destination, ct);

            var mutator = await sourceProvider.GetMutatorAsync(
                new VfsPath(job.Source.Scheme, job.Source.ParentValue), ct);
            if (mutator is not null)
                await mutator.DeleteAsync(job.Source, toTrash: false, ct);
        }
    }

    // ── Copy ──────────────────────────────────────────────────────────────

    private async Task<FileOpResult> ExecuteCopyAsync(CopyRequest request, CancellationTokenSource opCts)
    {
        var ct = opCts.Token;
        var scan = await PreScanCopyAsync(request.Sources, request.Destination, ct);
        var opId = scan.Jobs.Count > 0 ? scan.Jobs[0].OperationId : Guid.NewGuid().ToString("N")[..12];
        var results = new List<FileItemResult>();
        var copiedPaths = new List<VfsPath>();

        // Jobs on different volumes run concurrently (ExecuteByVolumeAsync), so every
        // mutation of the shared result/copied-path lists is serialized through this gate.
        var sync = new object();
        void AddResult(FileItemResult r) { lock (sync) results.Add(r); }

        var bytesBefore = BuildBytesBeforeMap(scan.Jobs);
        EmitProgress(opId, FileOpStatus.Scanning, 0, scan.TotalFileCount, 0, scan.TotalSize, "");

        using var sticky = new StickyConflictResolver(_conflictHandler);

        await ExecuteByVolumeAsync(scan.JobsByVolume, ct, async (job, volumeSem, fileIndex) =>
        {
            if (ct.IsCancellationRequested)
            {
                // Stop gracefully: record this job as cancelled and let the batch drain so
                // items already completed still get an undo entry (review #2). Throwing here
                // (the old behavior) unwound before the undo-stack push below.
                AddResult(new FileItemResult
                {
                    Source = job.Source, Destination = job.Destination,
                    Status = FileItemResultStatus.Cancelled,
                });
                return;
            }
            EmitProgress(opId, FileOpStatus.Running, fileIndex, scan.TotalFileCount,
                bytesBefore[job], scan.TotalSize, job.Source.FileName, job.VolumeKey);

            try
            {
                var resolved = await HandleConflictIfNeeded(job, sticky, fileIndex, scan.TotalFileCount, ct);
                if (resolved == ConflictResolution.Cancel)
                {
                    job.Status = FileOpJobStatus.Cancelled;
                    AddResult(new FileItemResult
                    {
                        Source = job.Source, Destination = job.Destination,
                        Status = FileItemResultStatus.Cancelled,
                    });
                    opCts.Cancel();
                    return;
                }
                if (resolved == ConflictResolution.No)
                {
                    job.Status = FileOpJobStatus.Skipped;
                    AddResult(new FileItemResult
                    {
                        Source = job.Source, Destination = job.Destination,
                        Status = FileItemResultStatus.Skipped,
                    });
                    return;
                }

                await CopyFileAsync(job, ct);

                job.Status = FileOpJobStatus.Completed;
                lock (sync)
                {
                    copiedPaths.Add(job.Destination);
                    results.Add(new FileItemResult
                    {
                        Source = job.Source, Destination = job.Destination,
                        Status = FileItemResultStatus.Success,
                        UndoSource = job.Destination,
                    });
                }
            }
            catch (OperationCanceledException)
            {
                job.Status = FileOpJobStatus.Cancelled;
                AddResult(new FileItemResult
                {
                    Source = job.Source, Destination = job.Destination,
                    Status = FileItemResultStatus.Cancelled,
                });
            }
            catch (Exception ex)
            {
                job.Status = FileOpJobStatus.Failed;
                job.ErrorMessage = ex.Message;
                AddResult(new FileItemResult
                {
                    Source = job.Source, Destination = job.Destination,
                    Status = FileItemResultStatus.Failed,
                    ErrorMessage = ex.Message,
                });
            }
        });

        if (copiedPaths.Count > 0)
        {
            _undoStack.Push(new CopyUndoEntry
            {
                OperationId = opId,
                Timestamp = DateTimeOffset.UtcNow,
                OriginalKind = FileOpKind.Copy,
                CopiedPaths = copiedPaths,
                Description = $"Copied {copiedPaths.Count} item(s)",
            });
        }

        var status = BuildFinalStatus(results);
        var finalResult = new FileOpResult
        {
            OperationId = opId, Kind = FileOpKind.Copy,
            Status = status, ItemResults = results,
        };
        EmitProgress(opId, status, scan.TotalFileCount, scan.TotalFileCount,
            scan.TotalSize, scan.TotalSize, "");

        return finalResult;
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

    // ── Rename ────────────────────────────────────────────────────────────

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

    // ── Delete / Trash ────────────────────────────────────────────────────

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

        var status = BuildFinalStatus(results);
        var finalResult = new FileOpResult
        {
            OperationId = opId, Kind = FileOpKind.Delete,
            Status = status, ItemResults = results,
        };
        EmitProgress(opId, status, request.Paths.Count, request.Paths.Count, 0, 0, "");

        return finalResult;
    }

    // ── Undo ──────────────────────────────────────────────────────────────

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

        return entry switch
        {
            MoveUndoEntry e => await UndoMoveAsync(e, ct),
            CopyUndoEntry e => await UndoCopyAsync(e, ct),
            RenameUndoEntry e => await UndoRenameAsync(e, ct),
            TrashUndoEntry e => await UndoTrashAsync(e, ct),
            _ => throw new InvalidOperationException($"Unknown undo entry type: {entry.GetType().Name}")
        };
    }

    private async Task<FileOpResult> UndoMoveAsync(MoveUndoEntry entry, CancellationToken ct)
    {
        var results = new List<FileItemResult>();
        int idx = 0;

        foreach (var (source, destination) in entry.Pairs)
        {
            ct.ThrowIfCancellationRequested();
            EmitProgress(entry.OperationId, FileOpStatus.Running, idx, entry.Pairs.Count, 0, 0, destination.FileName);

            try
            {
                var provider = _vfs.GetProvider(source);
                var parentPath = new VfsPath(source.Scheme, source.ParentValue);
                var mutator = await provider.GetMutatorAsync(parentPath, ct);

                if (mutator is null)
                    throw new InvalidOperationException($"Cannot undo move: {source}");

                // Don't silently overwrite a file recreated at the original location since
                // the move — fail the undo of this item instead of clobbering (review #11).
                if (await TryResolveAsync(source, ct) is not null)
                    throw new IOException(
                        $"'{source.FileName}' already exists at its original location; not overwriting on undo.");

                // Reverse: move from destination back to source
                var reverseJob = new FileOpJob
                {
                    OperationId = entry.OperationId,
                    Kind = FileOpKind.Move,
                    Source = destination,
                    Destination = source,
                    VolumeKey = GetVolumeKey(source),
                };
                await MoveFileAsync(reverseJob, ct);

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
            Status = BuildFinalStatus(results), ItemResults = results,
        };
    }

    private async Task<FileOpResult> UndoCopyAsync(CopyUndoEntry entry, CancellationToken ct)
    {
        var results = new List<FileItemResult>();
        int idx = 0;

        foreach (var copiedPath in entry.CopiedPaths)
        {
            ct.ThrowIfCancellationRequested();
            EmitProgress(entry.OperationId, FileOpStatus.Running, idx, entry.CopiedPaths.Count, 0, 0, copiedPath.FileName);

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
            Status = BuildFinalStatus(results), ItemResults = results,
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
            Status = BuildFinalStatus(results), ItemResults = results,
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
                if (await TryResolveAsync(original, ct) is not null)
                    throw new IOException(
                        $"'{original.FileName}' already exists at its original location; not overwriting on undo.");

                // Restore from trash: move trashPath back to original location
                var reverseJob = new FileOpJob
                {
                    OperationId = entry.OperationId,
                    Kind = FileOpKind.Move,
                    Source = trashPath,
                    Destination = original,
                    VolumeKey = GetVolumeKey(original),
                };
                await MoveFileAsync(reverseJob, ct);

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
            Status = BuildFinalStatus(results), ItemResults = results,
        };
    }

    // ── Volume-parallel execution ─────────────────────────────────────────

    private async Task ExecuteByVolumeAsync(
        IReadOnlyDictionary<string, IReadOnlyList<FileOpJob>> jobsByVolume,
        CancellationToken ct,
        Func<FileOpJob, SemaphoreSlim, int, Task> executeJob)
    {
        var volumeTasks = new List<Task>();

        foreach (var (volumeKey, volumeJobs) in jobsByVolume)
        {
            var sem = _volumeQueues.GetOrAdd(volumeKey, _ => new SemaphoreSlim(1, 1));
            var volumeTask = Task.Run(async () =>
            {
                int fileIndex = 0;
                foreach (var job in volumeJobs)
                {
                    // Drain gracefully on cancellation rather than throwing out of the
                    // volume worker, so the caller still records undo entries for the work
                    // that completed before the cancel (review #2).
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        await sem.WaitAsync(ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    try
                    {
                        await executeJob(job, sem, fileIndex);
                    }
                    finally
                    {
                        sem.Release();
                    }
                    fileIndex++;
                }
            }, ct);

            volumeTasks.Add(volumeTask);
        }

        await Task.WhenAll(volumeTasks);
    }

    // ── Conflict handling ─────────────────────────────────────────────────

    private async Task<ConflictResolution> HandleConflictIfNeeded(
        FileOpJob job,
        StickyConflictResolver sticky,
        int fileIndex,
        int totalFiles,
        CancellationToken ct)
    {
        if (job.Kind is not (FileOpKind.Copy or FileOpKind.Move))
            return ConflictResolution.Yes;

        // Resolve the destination once: null means it doesn't exist (no conflict);
        // otherwise reuse the same node for the conflict dialog's size/modified fields
        // instead of resolving it a second time.
        var destNode = await TryResolveAsync(job.Destination, ct);
        if (destNode is null)
            return ConflictResolution.Yes;

        var sourceProvider = _vfs.GetProvider(job.Source);
        var sourceNode = await sourceProvider.ResolveAsync(job.Source, ct);

        var scope = new ConflictScope
        {
            FileIndex = fileIndex,
            TotalFiles = totalFiles,
            OperationKind = job.Kind,
        };

        return await sticky.ResolveConflictAsync(
            job.Source, job.Destination,
            sourceNode.Size, destNode.Size,
            sourceNode.Modified, destNode.Modified,
            scope, ct);
    }

    /// <summary>
    /// Resolves a path, returning null if it genuinely does not exist. Any OTHER failure
    /// (permission, IO, a locked file) propagates: treating it as "does not exist" would
    /// skip the conflict prompt and silently overwrite the destination.
    /// </summary>
    private async Task<IVfsNode?> TryResolveAsync(VfsPath path, CancellationToken ct)
    {
        try
        {
            return await _vfs.GetProvider(path).ResolveAsync(path, ct);
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

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string GetVolumeKey(VfsPath path)
    {
        if (path.Scheme == "file")
        {
            var root = Path.GetPathRoot(path.Value);
            return $"file:{root ?? "/"}";
        }
        return path.Scheme;
    }

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

    private static FileOpStatus BuildFinalStatus(List<FileItemResult> results)
    {
        if (results.All(r => r.Status == FileItemResultStatus.Success))
            return FileOpStatus.Completed;
        if (results.All(r => r.Status is FileItemResultStatus.Cancelled or FileItemResultStatus.Skipped))
            return FileOpStatus.Cancelled;
        if (results.Any(r => r.Status == FileItemResultStatus.Failed))
            return FileOpStatus.PartiallyCompleted;
        return FileOpStatus.Completed;
    }

    private void EmitProgress(
        string opId, FileOpStatus status, int current, int total,
        long bytesTransferred, long totalBytes, string currentFile,
        string? volumeKey = null, string? message = null)
    {
        _progressSubject.OnNext(new FileOpProgress
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
        });
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _progressSubject.Dispose();
        foreach (var sem in _volumeQueues.Values)
            sem.Dispose();
    }
}
