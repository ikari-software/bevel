namespace Bevel.Core.Vfs;

/// <summary>
/// Mutations on a VFS folder. Null from GetMutatorAsync means the folder is read-only.
/// </summary>
public interface IVfsMutator
{
    ValueTask<VfsPath> CreateFolderAsync(VfsPath parent, string name, CancellationToken ct);

    ValueTask RenameAsync(VfsPath path, string newName, CancellationToken ct);

    /// <summary>
    /// Moves <paramref name="path"/> into <paramref name="destinationParent"/>, keeping its name, and
    /// returns the item's new path. Same-volume is an atomic rename; cross-volume copies then deletes.
    /// The mutator is obtained for the item's SOURCE folder.
    /// </summary>
    ValueTask<VfsPath> MoveAsync(VfsPath path, VfsPath destinationParent, CancellationToken ct);

    /// <summary>
    /// Deletes a node. When <paramref name="toTrash"/> is true the node is moved to the
    /// trash and its new trash location is returned (for restore/undo); a permanent delete
    /// returns null.
    /// </summary>
    ValueTask<VfsPath?> DeleteAsync(VfsPath path, bool toTrash, CancellationToken ct);

    ValueTask SetAttributesAsync(VfsPath path, VfsNodeAttributes attributes, CancellationToken ct);

    ValueTask<Stream> OpenWriteAsync(VfsPath file, CancellationToken ct);
}
