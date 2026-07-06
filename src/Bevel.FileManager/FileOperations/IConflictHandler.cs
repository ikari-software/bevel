using Bevel.Core.Vfs;

namespace Bevel.FileManager.FileOperations;

/// <summary>
/// Callback for resolving file conflicts when a destination already exists (FM-133).
/// Implement this to show the Win2000-style "Confirm File Replace" dialog.
/// </summary>
public interface IConflictHandler
{
    /// <summary>
    /// Called when a destination file already exists during copy or move.
    /// </summary>
    ConflictResolution ResolveConflict(
        VfsPath source,
        VfsPath destination,
        long? sourceSize,
        long? destSize,
        DateTimeOffset? sourceModified,
        DateTimeOffset? destModified,
        ConflictScope scope);
}

/// <summary>
/// User choice for conflict resolution.
/// </summary>
public enum ConflictResolution
{
    /// <summary>Overwrite this single file.</summary>
    Yes,
    /// <summary>Overwrite all remaining conflicts in this operation.</summary>
    YesToAll,
    /// <summary>Skip this single file.</summary>
    No,
    /// <summary>Skip all remaining conflicts in this operation.</summary>
    NoToAll,
    /// <summary>Cancel the entire operation.</summary>
    Cancel,
    /// <summary>Rename the destination to avoid conflict.</summary>
    Rename,
}

/// <summary>
/// Contextual scope for the conflict dialog.
/// </summary>
public sealed record ConflictScope
{
    /// <summary>Index of the current file within the total operation (0-based).</summary>
    public required int FileIndex { get; init; }

    /// <summary>Total number of files in the operation.</summary>
    public required int TotalFiles { get; init; }

    /// <summary>The kind of operation triggering the conflict.</summary>
    public required FileOpKind OperationKind { get; init; }
}

/// <summary>
/// A resolver that applies sticky YesToAll / NoToAll decisions across
/// subsequent conflicts in the same operation.
/// </summary>
public sealed class StickyConflictResolver : IConflictHandler
{
    private readonly IConflictHandler _inner;
    private ConflictResolution? _sticky;

    // Called from volume-parallel workers within one operation, so the sticky state is
    // guarded against concurrent read/write (review #9).
    private readonly object _gate = new();

    public StickyConflictResolver(IConflictHandler inner)
    {
        _inner = inner;
    }

    public ConflictResolution ResolveConflict(
        VfsPath source,
        VfsPath destination,
        long? sourceSize,
        long? destSize,
        DateTimeOffset? sourceModified,
        DateTimeOffset? destModified,
        ConflictScope scope)
    {
        lock (_gate)
        {
            if (_sticky is ConflictResolution.Yes or ConflictResolution.YesToAll)
                return ConflictResolution.Yes;
            if (_sticky is ConflictResolution.No or ConflictResolution.NoToAll)
                return ConflictResolution.No;

            var result = _inner.ResolveConflict(source, destination, sourceSize, destSize,
                sourceModified, destModified, scope);

            if (result is ConflictResolution.YesToAll or ConflictResolution.NoToAll)
                _sticky = result;

            return result;
        }
    }

    public void Reset() { lock (_gate) _sticky = null; }
}
