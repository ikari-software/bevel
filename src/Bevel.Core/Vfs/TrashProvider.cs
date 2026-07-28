using System.Runtime.CompilerServices;

namespace Bevel.Core.Vfs;

/// <summary>
/// VFS provider for the user's Trash (<c>~/.Trash</c> on macOS). Scheme = "trash".
/// <para>
/// A thin virtual view over the real trash directory: the root is a virtual "Trash" folder and
/// every child maps to a native <c>~/.Trash/&lt;name&gt;</c> entry, so browsing INTO a trashed folder
/// just reads the local filesystem. Items expose only Copy / Properties / <b>permanent</b> Delete —
/// you can't rename in place, drop files INTO the Trash, or re-trash something already trashed.
/// </para>
/// <para>
/// Deletes here are PERMANENT (the item is already in the Trash). Restoring to the <i>original</i>
/// location ("Put Back") needs per-item metadata macOS does not expose for arbitrary trashed items
/// (only Finder's private <c>.DS_Store</c> holds it), so that is tracked separately; the mutator's
/// <see cref="TrashMutator.MoveAsync"/> already supports restoring to a chosen folder.
/// </para>
/// </summary>
public sealed class TrashProvider : IVfsProvider
{
    private readonly string _trashDirectory;

    /// <param name="trashDirectory">Overridable for tests; defaults to <c>~/.Trash</c>.</param>
    public TrashProvider(string? trashDirectory = null)
        => _trashDirectory = trashDirectory ?? DefaultTrashDirectory();

    private static string DefaultTrashDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash");

    public string Scheme => "trash";

    /// <summary>Maps a trash VFS path to its native <c>~/.Trash/…</c> location. Root → the trash dir itself.</summary>
    private string NativePath(VfsPath path)
        => path.IsRoot
            ? _trashDirectory
            : Path.Combine(_trashDirectory, path.Value.Replace('/', Path.DirectorySeparatorChar));

    public ValueTask<IVfsNode> ResolveAsync(VfsPath path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (path.IsRoot)
        {
            return ValueTask.FromResult<IVfsNode>(VirtualNode.VirtualRoot(
                path, "Trash", IconKey.Trash(full: HasAnyItems())));
        }

        var native = NativePath(path);
        if (!Directory.Exists(native) && !File.Exists(native))
            throw new FileNotFoundException($"Item not found in Trash: {native}");

        return ValueTask.FromResult(CreateNode(path, native));
    }

    public async IAsyncEnumerable<IVfsNode> EnumerateAsync(
        VfsPath folder,
        EnumerateOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var native = NativePath(folder);
        if (!Directory.Exists(native)) yield break;

        var remaining = options.Limit;
        foreach (var entry in Directory.EnumerateFileSystemEntries(native))
        {
            ct.ThrowIfCancellationRequested();

            var name = Path.GetFileName(entry);
            // macOS drops these into every folder; they're noise in the Trash view, never real items.
            if (name is ".DS_Store" or ".localized") continue;

            // TOCTOU: the entry can vanish between enumeration and stat — skip it, don't abort.
            FileAttributes attrs;
            try { attrs = File.GetAttributes(entry); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
            { continue; }

            var isHidden = attrs.HasFlag(FileAttributes.Hidden) || name.StartsWith('.');
            if (isHidden && !options.IncludeHidden) continue;

            var childPath = VfsPath.Combine(folder, name);
            var node = CreateNode(childPath, entry, attrs);
            if (options.Filter is not null && !options.Filter(node)) continue;

            yield return node;
            if (remaining > 0 && --remaining <= 0) yield break;
        }
    }

    private static IVfsNode CreateNode(VfsPath path, string native, FileAttributes? knownAttrs = null)
    {
        var attrs = knownAttrs ?? File.GetAttributes(native);
        var isDir = attrs.HasFlag(FileAttributes.Directory);
        var name = Path.GetFileName(native);

        long? size = null;
        DateTimeOffset? modified = null;
        try
        {
            if (isDir) modified = new DirectoryInfo(native).LastWriteTimeUtc;
            else { var fi = new FileInfo(native); size = fi.Length; modified = fi.LastWriteTimeUtc; }
        }
        catch { /* stat raced or was denied — leave metadata unknown */ }

        // Trash items: browse + copy-out + properties + PERMANENT delete. Deliberately NOT Rename,
        // Trash (can't re-trash), MoveTarget or CreateChild (nothing may be dropped into the Trash).
        var caps = VfsCapabilities.CopySource | VfsCapabilities.Properties | VfsCapabilities.Delete;

        return new VirtualNode
        {
            Path = path,
            DisplayName = name,
            Kind = isDir ? VfsNodeKind.Folder : VfsNodeKind.File,
            MightHaveChildren = isDir,
            Size = size,
            Modified = modified,
            TypeDescription = isDir ? "File folder" : FileTypeDescription.ForExtension(Path.GetExtension(name)),
            IconKey = isDir ? IconKey.Folder() : IconKey.File(Path.GetExtension(name)),
            Caps = caps,
        };
    }

    /// <summary>True when the Trash holds at least one real item (ignoring macOS bookkeeping files) —
    /// drives the full-vs-empty basket icon.</summary>
    private bool HasAnyItems()
    {
        try
        {
            foreach (var e in Directory.EnumerateFileSystemEntries(_trashDirectory))
            {
                var n = Path.GetFileName(e);
                if (n is ".DS_Store" or ".localized") continue;
                return true;
            }
        }
        catch { /* no trash dir / denied → treat as empty */ }
        return false;
    }

    public ValueTask<Stream> OpenReadAsync(VfsPath file, CancellationToken ct)
    {
        var native = NativePath(file);
        ct.ThrowIfCancellationRequested();
        Stream stream = new FileStream(native, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        return ValueTask.FromResult(stream);
    }

    public ValueTask<IVfsMutator?> GetMutatorAsync(VfsPath folder, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IVfsMutator?>(new TrashMutator(_trashDirectory));
    }

    // Browse-only: the view reloads after an op completes, so no live watcher is needed (yet).
    public IDirectoryWatcher? CreateWatcher(VfsPath folder) => null;

    public NameValidationResult ValidateName(VfsPath folder, string proposedName)
        => NameValidationResult.Fail("Cannot rename items in the Trash.");

    // ── Scheme-crossing helpers ──────────────────────────────────────────────

    /// <summary>Serialize trash write-ops on the SAME volume queue as the local filesystem they
    /// physically share, so a permanent-delete or restore can't race a same-disk file op.</summary>
    public string GetVolumeKey(VfsPath path)
        => $"file:{Path.GetPathRoot(_trashDirectory) ?? "/"}";

    /// <summary>Leaf items expose their real <c>~/.Trash</c> path so preview / open / copy-out just
    /// work through the existing effective-path plumbing; the ROOT returns <c>null</c> so selecting the
    /// Trash node stays in the <c>trash://</c> namespace instead of navigating to the raw folder (which
    /// would surface <c>.DS_Store</c> and lose the trash-specific capabilities).</summary>
    public string? ResolveEffectivePath(VfsPath path)
        => path.IsRoot ? null : NativePath(path);
}

/// <summary>
/// Mutations on Trash items. Only permanent delete and restore-by-move are meaningful; everything
/// else in the <see cref="IVfsMutator"/> surface is nonsensical inside the Trash and throws.
/// </summary>
internal sealed class TrashMutator : IVfsMutator
{
    private readonly string _trashDirectory;

    public TrashMutator(string trashDirectory) => _trashDirectory = trashDirectory;

    private string NativePath(VfsPath path)
        => path.IsRoot
            ? _trashDirectory
            : Path.Combine(_trashDirectory, path.Value.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Always a PERMANENT delete — the item is already in the Trash, so <paramref name="toTrash"/>
    /// is ignored (there is nowhere further to trash it). Returns null (nothing to restore).</summary>
    public ValueTask<VfsPath?> DeleteAsync(VfsPath path, bool toTrash, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var native = NativePath(path);
        if (Directory.Exists(native)) Directory.Delete(native, recursive: true);
        else if (File.Exists(native)) File.Delete(native);
        return ValueTask.FromResult<VfsPath?>(null);
    }

    /// <summary>Restores an item OUT of the Trash by moving it into <paramref name="destinationParent"/>
    /// (a <c>file://</c> folder whose <see cref="VfsPath.Value"/> is the native path). Same-volume is an
    /// atomic rename; a cross-volume move (EXDEV) copies then deletes.</summary>
    public ValueTask<VfsPath> MoveAsync(VfsPath path, VfsPath destinationParent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var src = NativePath(path);
        var name = Path.GetFileName(src);
        var destDir = destinationParent.Value;
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, name);

        try
        {
            if (Directory.Exists(src)) Directory.Move(src, dest);
            else File.Move(src, dest);
        }
        catch (IOException)
        {
            // Cross-volume: Directory/File.Move can't rename across devices — copy then delete.
            if (Directory.Exists(src)) { CopyDirectory(src, dest); Directory.Delete(src, recursive: true); }
            else { File.Copy(src, dest, overwrite: false); File.Delete(src); }
        }

        return ValueTask.FromResult(new VfsPath("file", dest));
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(src))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.EnumerateDirectories(src))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    public ValueTask<VfsPath> CreateFolderAsync(VfsPath parent, string name, CancellationToken ct)
        => throw new NotSupportedException("Cannot create folders in the Trash.");

    public ValueTask RenameAsync(VfsPath path, string newName, CancellationToken ct)
        => throw new NotSupportedException("Cannot rename items in the Trash.");

    public ValueTask SetAttributesAsync(VfsPath path, VfsNodeAttributes attributes, CancellationToken ct)
        => throw new NotSupportedException("Cannot change attributes of items in the Trash.");

    public ValueTask<Stream> OpenWriteAsync(VfsPath file, CancellationToken ct)
        => throw new NotSupportedException("Cannot write into the Trash.");
}
