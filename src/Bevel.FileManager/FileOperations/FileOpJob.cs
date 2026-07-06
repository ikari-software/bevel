using Bevel.Core.Vfs;

namespace Bevel.FileManager.FileOperations;

/// <summary>
/// A single unit of work within a file operation. One job = one file.
/// Jobs are grouped by target volume for FIFO scheduling (FM-130).
/// </summary>
public sealed class FileOpJob
{
    public required string OperationId { get; init; }
    public required FileOpKind Kind { get; init; }
    public required VfsPath Source { get; init; }
    public required VfsPath Destination { get; init; }

    /// <summary>Volume key used for FIFO queue grouping.</summary>
    public required string VolumeKey { get; init; }

    public long Size { get; init; }
    public FileOpJobStatus Status { get; set; } = FileOpJobStatus.Pending;
    public string? ErrorMessage { get; set; }

    /// <summary>Undo info after execution: original source path for move/copy undo.</summary>
    public VfsPath? UndoSource { get; set; }

    /// <summary>Undo info after execution: created/moved/renamed destination path.</summary>
    public VfsPath? UndoDestination { get; set; }
}

public enum FileOpJobStatus
{
    Pending,
    Scanning,
    Executing,
    Completed,
    Skipped,
    Failed,
    Cancelled,
    ConflictAwaiting,
}

/// <summary>
/// Pre-scan results for an entire request before execution begins (FM-131).
/// </summary>
public sealed record PreScanResult
{
    public required IReadOnlyList<FileOpJob> Jobs { get; init; }

    /// <summary>Jobs grouped by target volume key for FIFO scheduling.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<FileOpJob>> JobsByVolume { get; init; }

    public required long TotalSize { get; init; }
    public required int TotalFileCount { get; init; }
    public required int TotalFolderCount { get; init; }
}
