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

    // ── Scheme-crossing helpers (review AD1) ──────────────────────────────────
    //
    // These let the file-ops engine and UI reason about volumes, native paths, and fast
    // moves WITHOUT reading VfsPath.Value or pattern-matching on scheme. A new provider
    // (Trash/Network/Zip) inherits safe defaults — one write-queue, no native path, no
    // fast move — so adding it never requires re-auditing every consumer.

    /// <summary>
    /// A stable key grouping paths that share a write-serialization queue (the same physical
    /// volume). Move/Copy jobs with the same key run FIFO; jobs with different keys run in
    /// parallel. Default: one queue for the whole scheme.
    /// </summary>
    string GetVolumeKey(VfsPath path) => Scheme;

    /// <summary>
    /// The native OS filesystem path this VFS path maps to, or <c>null</c> when this provider
    /// isn't backed by the local filesystem (callers then fall back to stream-based access).
    /// Also used to translate a virtual volume node to the mount it stands for. Default: null.
    /// </summary>
    string? ResolveEffectivePath(VfsPath path) => null;

    /// <summary>
    /// True when a native atomic rename can relocate <paramref name="source"/> to
    /// <paramref name="destination"/> without a copy+delete (same provider, same volume).
    /// Default: false (always copy+delete).
    /// </summary>
    bool CanFastMoveWithin(VfsPath source, VfsPath destination) => false;
}
