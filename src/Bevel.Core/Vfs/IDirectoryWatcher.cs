namespace Bevel.Core.Vfs;

/// <summary>
/// Watches a VFS folder for changes. Events are batched (100 ms window) and coalesced.
/// Null from CreateWatcher means the folder is not watchable (zip, unmounted net).
/// </summary>
public interface IDirectoryWatcher : IDisposable
{
    IObservable<FsChangeBatch> Changes { get; }
}

public sealed record FsChangeBatch(
    IReadOnlyList<VfsPath> Created,
    IReadOnlyList<VfsPath> Deleted,
    IReadOnlyList<VfsPath> Modified,
    IReadOnlyList<(VfsPath OldPath, VfsPath NewPath)> Renamed
);
