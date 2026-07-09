using Bevel.Core.Vfs;

namespace Bevel.FileManager.FileOperations;

/// <summary>
/// Callback for resolving file conflicts when a destination already exists (FM-133).
/// Implement this to show the Win2000-style "Confirm File Replace" dialog.
/// </summary>
/// <remarks>
/// Async by contract (review AD6): the interactive dialog must marshal to the UI thread and
/// await the user's click. The engine calls this from thread-pool volume workers, so a
/// synchronous signature would force <c>Dispatcher.Invoke(...).Result</c> blocking — and several
/// volume workers blocking the UI thread at once deadlocks. Returning a <see cref="ValueTask"/>
/// lets the handler suspend the worker instead of blocking a thread.
/// </remarks>
public interface IConflictHandler
{
    /// <summary>
    /// Called when a destination file already exists during copy or move. Implementations that
    /// prompt the user must honor <paramref name="ct"/> (return/throw on cancellation) so a
    /// cancelled operation doesn't leave a dialog waiting on a click that never comes.
    /// </summary>
    ValueTask<ConflictResolution> ResolveConflictAsync(
        VfsPath source,
        VfsPath destination,
        long? sourceSize,
        long? destSize,
        DateTimeOffset? sourceModified,
        DateTimeOffset? destModified,
        ConflictScope scope,
        CancellationToken ct);
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
/// A resolver that applies sticky YesToAll / NoToAll decisions across subsequent conflicts in
/// the same operation, and serializes prompts so at most one dialog is open at a time.
/// </summary>
/// <remarks>
/// Called from volume-parallel workers within one operation. The old synchronous version held a
/// <c>lock</c> across the inner call; an async inner handler can't be awaited under a <c>lock</c>,
/// so mutual exclusion is a <see cref="SemaphoreSlim"/> instead (review #9). Serializing here is
/// also what keeps two volume workers from popping two "Confirm File Replace" dialogs at once.
/// </remarks>
public sealed class StickyConflictResolver : IConflictHandler, IDisposable
{
    private readonly IConflictHandler _inner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ConflictResolution? _sticky;

    public StickyConflictResolver(IConflictHandler inner)
    {
        _inner = inner;
    }

    public async ValueTask<ConflictResolution> ResolveConflictAsync(
        VfsPath source,
        VfsPath destination,
        long? sourceSize,
        long? destSize,
        DateTimeOffset? sourceModified,
        DateTimeOffset? destModified,
        ConflictScope scope,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_sticky is ConflictResolution.Yes or ConflictResolution.YesToAll)
                return ConflictResolution.Yes;
            if (_sticky is ConflictResolution.No or ConflictResolution.NoToAll)
                return ConflictResolution.No;

            var result = await _inner.ResolveConflictAsync(source, destination, sourceSize, destSize,
                sourceModified, destModified, scope, ct);

            if (result is ConflictResolution.YesToAll or ConflictResolution.NoToAll)
                _sticky = result;

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
