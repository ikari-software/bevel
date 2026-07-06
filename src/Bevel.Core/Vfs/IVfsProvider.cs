namespace Bevel.Core.Vfs;

/// <summary>
/// Provides VFS access for a specific scheme (file, computer, trash, net, zip).
/// Each provider handles its own namespace.
/// </summary>
public interface IVfsProvider
{
    string Scheme { get; }

    ValueTask<IVfsNode> ResolveAsync(VfsPath path, CancellationToken ct);

    IAsyncEnumerable<IVfsNode> EnumerateAsync(VfsPath folder, EnumerateOptions options, CancellationToken ct);

    ValueTask<Stream> OpenReadAsync(VfsPath file, CancellationToken ct);

    ValueTask<IVfsMutator?> GetMutatorAsync(VfsPath folder, CancellationToken ct);

    IDirectoryWatcher? CreateWatcher(VfsPath folder);

    NameValidationResult ValidateName(VfsPath folder, string proposedName);
}
