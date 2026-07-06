using Bevel.Core.Vfs;

namespace Bevel.FileManager.FileOperations;

/// <summary>
/// Outcome of a single file within a file operation.
/// </summary>
public sealed record FileItemResult
{
    public required VfsPath Source { get; init; }
    public required VfsPath? Destination { get; init; }
    public required FileItemResultStatus Status { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>For undo: the inverse path. Move/copy -> original source. Rename -> old name path.</summary>
    public VfsPath? UndoSource { get; init; }

    /// <summary>For undo: the path that was created/moved/renamed.</summary>
    public VfsPath? UndoDestination { get; init; }
}

public enum FileItemResultStatus
{
    Success,
    Skipped,
    Failed,
    Cancelled,
    ConflictResolved,
}

/// <summary>
/// Aggregate result of an entire file operation.
/// </summary>
public sealed record FileOpResult
{
    public required string OperationId { get; init; }
    public required FileOpKind Kind { get; init; }
    public required FileOpStatus Status { get; init; }
    public required IReadOnlyList<FileItemResult> ItemResults { get; init; }
    public string? ErrorMessage { get; init; }

    public int SucceededCount => ItemResults.Count(r => r.Status == FileItemResultStatus.Success);
    public int FailedCount => ItemResults.Count(r => r.Status == FileItemResultStatus.Failed);
    public int SkippedCount => ItemResults.Count(r => r.Status == FileItemResultStatus.Skipped);
    public int CancelledCount => ItemResults.Count(r => r.Status == FileItemResultStatus.Cancelled);
}

public enum FileOpKind
{
    Move,
    Copy,
    Rename,
    Delete,
    Undo,
}

public enum FileOpStatus
{
    Pending,
    Scanning,
    Running,
    Completed,
    PartiallyCompleted,
    Cancelled,
    Failed,
}
