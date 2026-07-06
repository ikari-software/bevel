using Bevel.Core.Vfs;

namespace Bevel.FileManager.FileOperations;

/// <summary>
/// Represents a user-initiated file operation request (FM-130..136).
/// </summary>
public abstract record FileOpRequest
{
    public required DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record MoveRequest : FileOpRequest
{
    public required IReadOnlyList<VfsPath> Sources { get; init; }
    public required VfsPath Destination { get; init; }
}

public sealed record CopyRequest : FileOpRequest
{
    public required IReadOnlyList<VfsPath> Sources { get; init; }
    public required VfsPath Destination { get; init; }
}

public sealed record RenameRequest : FileOpRequest
{
    public required VfsPath Path { get; init; }
    public required string NewName { get; init; }
}

public sealed record DeleteRequest : FileOpRequest
{
    public required IReadOnlyList<VfsPath> Paths { get; init; }

    /// <summary>When true, move to trash instead of permanent delete.</summary>
    public required bool ToTrash { get; init; }
}

public sealed record UndoRequest : FileOpRequest
{
    public required string OperationId { get; init; }
}
