using Bevel.Core.Vfs;

namespace Bevel.FileManager.FileOperations;

/// <summary>
/// Decides whether a Move/Copy job collides with an existing destination and, if so, asks the
/// <see cref="IConflictHandler"/> — through a per-operation <see cref="StickyConflictResolver"/> —
/// what to do. Extracted from FileOperationService (bevel-p3g).
/// </summary>
internal sealed class ConflictCoordinator
{
    private readonly VfsRoot _vfs;
    private readonly IConflictHandler _handler;

    public ConflictCoordinator(VfsRoot vfs, IConflictHandler handler)
    {
        _vfs = vfs;
        _handler = handler;
    }

    /// <summary>
    /// A fresh sticky resolver scoped to a single operation: YesToAll/NoToAll decisions persist
    /// across that operation's conflicts, and prompts are serialized. Dispose it when the
    /// operation ends (it owns a semaphore).
    /// </summary>
    public StickyConflictResolver NewResolver() => new(_handler);

    /// <summary>
    /// Returns the resolution for <paramref name="job"/>: Yes when there's no conflict (or the op
    /// isn't a Copy/Move), otherwise whatever the handler decides. Resolves the destination once
    /// and reuses that node for the dialog's size/modified fields.
    /// </summary>
    public async Task<ConflictResolution> ResolveAsync(
        FileOpJob job,
        StickyConflictResolver sticky,
        int fileIndex,
        int totalFiles,
        CancellationToken ct)
    {
        if (job.Kind is not (FileOpKind.Copy or FileOpKind.Move))
            return ConflictResolution.Yes;

        var destNode = await FileOpHelpers.TryResolveAsync(_vfs, job.Destination, ct);
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
}
