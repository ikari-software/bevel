using Bevel.Core.Vfs;

namespace Bevel.FileManager.FileOperations;

/// <summary>
/// Multi-level undo stack for file operations (FM-135).
/// Depth limited to 10. Each entry captures enough information to reverse
/// one completed file operation.
/// </summary>
public sealed class UndoStack
{
    private readonly LinkedList<UndoEntry> _entries = new();
    private readonly int _maxDepth;

    // The service is a DI singleton shared across windows; overlapping operations can
    // push/pop concurrently, so all access to _entries is serialized (review #9).
    private readonly object _gate = new();

    public UndoStack(int maxDepth = 10)
    {
        _maxDepth = maxDepth;
    }

    public int Count { get { lock (_gate) return _entries.Count; } }
    public bool CanUndo { get { lock (_gate) return _entries.Count > 0; } }

    /// <summary>
    /// Push an undo entry after a successful operation completes.
    /// </summary>
    public void Push(UndoEntry entry)
    {
        lock (_gate)
        {
            _entries.AddLast(entry);
            while (_entries.Count > _maxDepth)
                _entries.RemoveFirst();
        }
    }

    /// <summary>
    /// Pop the most recent undo entry. Returns null if empty.
    /// </summary>
    public UndoEntry? Pop()
    {
        lock (_gate)
        {
            if (_entries.Count == 0) return null;
            var entry = _entries.Last!.Value;
            _entries.RemoveLast();
            return entry;
        }
    }

    /// <summary>
    /// Peek at the most recent undo entry without removing it.
    /// </summary>
    public UndoEntry? Peek() { lock (_gate) return _entries.Last?.Value; }

    /// <summary>
    /// Remove all entries and return them in LIFO order (newest first)
    /// for cleanup purposes.
    /// </summary>
    public IReadOnlyList<UndoEntry> Clear()
    {
        lock (_gate)
        {
            var result = new List<UndoEntry>();
            while (_entries.Count > 0)
            {
                result.Add(_entries.Last!.Value);
                _entries.RemoveLast();
            }
            return result;
        }
    }
}

/// <summary>
/// A single undo entry. Contains enough information to reverse one operation.
/// </summary>
public abstract record UndoEntry
{
    public required string OperationId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required FileOpKind OriginalKind { get; init; }
    public string Description { get; init; } = "";
}

public sealed record MoveUndoEntry : UndoEntry
{
    /// <summary>Pairs of (moved-from, moved-to) for each file that was moved.</summary>
    public required IReadOnlyList<(VfsPath Source, VfsPath Destination)> Pairs { get; init; }
}

public sealed record CopyUndoEntry : UndoEntry
{
    /// <summary>Paths of copied files that must be deleted on undo.</summary>
    public required IReadOnlyList<VfsPath> CopiedPaths { get; init; }
}

public sealed record RenameUndoEntry : UndoEntry
{
    /// <summary>Pairs of (current-name, original-name) for each renamed file.</summary>
    public required IReadOnlyList<(VfsPath CurrentPath, VfsPath OriginalPath)> Pairs { get; init; }
}

public sealed record TrashUndoEntry : UndoEntry
{
    /// <summary>Pairs of (original-path, trash-path) for each trashed file.</summary>
    public required IReadOnlyList<(VfsPath Original, VfsPath TrashPath)> Pairs { get; init; }
}
