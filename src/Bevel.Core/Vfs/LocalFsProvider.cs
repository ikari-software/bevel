using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Reactive.Subjects;

namespace Bevel.Core.Vfs;

/// <summary>
/// VFS provider for the local filesystem. Scheme = "file".
/// Uses .NET System.IO with platform-specific attribute fetching.
/// </summary>
public sealed partial class LocalFsProvider : IVfsProvider
{
    private readonly string _trashDirectory;

    public LocalFsProvider(string? trashDirectory = null)
        => _trashDirectory = trashDirectory ?? DefaultTrashDirectory();

    /// <summary>The macOS user Trash (~/.Trash). Overridable for tests/other roots.</summary>
    private static string DefaultTrashDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash");

    public string Scheme => "file";

    public ValueTask<IVfsNode> ResolveAsync(VfsPath path, CancellationToken ct)
    {
        var fullPath = GetFullPath(path);
        ct.ThrowIfCancellationRequested();

        if (!System.IO.Directory.Exists(fullPath) && !System.IO.File.Exists(fullPath))
            throw new FileNotFoundException($"Path not found: {fullPath}");

        return ValueTask.FromResult<IVfsNode>(CreateNode(path, fullPath));
    }

    public async IAsyncEnumerable<IVfsNode> EnumerateAsync(
        VfsPath folder,
        EnumerateOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var fullPath = GetFullPath(folder);
        ct.ThrowIfCancellationRequested();

        var remaining = options.Limit;

        // Fast path: enumerate names only, defer stat to visible items
        foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(fullPath))
        {
            ct.ThrowIfCancellationRequested();

            var name = Path.GetFileName(entry);
            var childPath = VfsPath.Combine(folder, name);

            // Quick attribute check for hidden/system filtering. The entry can vanish or
            // become inaccessible between enumeration and this stat (TOCTOU) — skip that
            // single entry rather than aborting the whole listing.
            FileAttributes attrs;
            try
            {
                attrs = File.GetAttributes(entry);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // On macOS/Unix a leading-dot name is hidden by convention even without the Hidden
            // attribute (.git, .DS_Store, dotfiles) — honour that too, gated by the same flag.
            var isHidden = attrs.HasFlag(System.IO.FileAttributes.Hidden) || name.StartsWith('.');
            var isSystem = attrs.HasFlag(System.IO.FileAttributes.System);
            var isDir = attrs.HasFlag(System.IO.FileAttributes.Directory);

            if (isHidden && !options.IncludeHidden) continue;
            if (isSystem && !options.IncludeSystem) continue;

            var kind = isDir ? VfsNodeKind.Folder : VfsNodeKind.File;
            var caps = VfsCapabilities.CopySource | VfsCapabilities.Properties | VfsCapabilities.Rename | VfsCapabilities.Delete;
            if (isDir) caps |= VfsCapabilities.CreateChild | VfsCapabilities.MoveTarget | VfsCapabilities.Watchable;
            else caps |= VfsCapabilities.Trash;

            var iconKey = isDir ? IconKey.Folder() : IconKey.File(Path.GetExtension(name));

            var node = new LazyFsNode
            {
                Path = childPath, FullPath = entry,
                DisplayName = name, Kind = kind,
                MightHaveChildren = isDir,
                TypeDescription = isDir ? "File folder" : FileTypeDescription.ForExtension(Path.GetExtension(name)),
                IconKey = iconKey, Caps = caps,
            };

            if (options.Filter is not null && !options.Filter(node)) continue;

            yield return node;
            if (remaining > 0 && --remaining <= 0) yield break;
        }
    }

    public ValueTask<Stream> OpenReadAsync(VfsPath file, CancellationToken ct)
    {
        var fullPath = GetFullPath(file);
        ct.ThrowIfCancellationRequested();

        Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        return ValueTask.FromResult(stream);
    }

    public ValueTask<IVfsMutator?> GetMutatorAsync(VfsPath folder, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IVfsMutator?>(new LocalFsMutator(GetFullPath(folder), _trashDirectory));
    }

    public IDirectoryWatcher? CreateWatcher(VfsPath folder)
    {
        var fullPath = GetFullPath(folder);
        if (!System.IO.Directory.Exists(fullPath)) return null;
        return new LocalDirectoryWatcher(fullPath);
    }

    public NameValidationResult ValidateName(VfsPath folder, string proposedName)
    {
        if (string.IsNullOrWhiteSpace(proposedName))
            return NameValidationResult.Fail("Name cannot be empty.");

        if (proposedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return NameValidationResult.Fail("Name contains invalid characters.");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && proposedName.StartsWith('.'))
        {
            // Allow dot-files but warn (macOS convention)
        }

        var fullPath = Path.Combine(GetFullPath(folder), proposedName);
        if (System.IO.File.Exists(fullPath) || System.IO.Directory.Exists(fullPath))
            return NameValidationResult.Fail("An item with this name already exists.");

        return NameValidationResult.Ok;
    }

    private static string GetFullPath(VfsPath path)
    {
        if (path.IsRoot) return Path.GetPathRoot(Environment.CurrentDirectory) ?? "/";
        var value = path.Value;
        // Value should be absolute for the file scheme; resolve relative paths.
        if (!Path.IsPathRooted(value))
            value = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory) ?? "/", value);
        return value;
    }

    // ── Scheme-crossing helpers (review AD1) ──────────────────────────────────

    /// <summary>One write-queue per physical volume (path root), so moves/copies to the same
    /// disk run FIFO while different disks run in parallel.</summary>
    public string GetVolumeKey(VfsPath path)
        => $"file:{Path.GetPathRoot(GetFullPath(path)) ?? "/"}";

    /// <summary>The file scheme IS the local filesystem, so the effective path is the mapped
    /// native path.</summary>
    public string? ResolveEffectivePath(VfsPath path) => GetFullPath(path);

    /// <summary>A native <see cref="File.Move(string, string, bool)"/> is an atomic rename only
    /// within one volume; across volumes it silently copies, which the engine would rather do
    /// explicitly (with progress + undo). So claim the fast path only when the destination is
    /// also local and shares the source's path root.</summary>
    public bool CanFastMoveWithin(VfsPath source, VfsPath destination)
    {
        if (destination.Scheme != Scheme) return false;
        var srcRoot = Path.GetPathRoot(GetFullPath(source));
        var dstRoot = Path.GetPathRoot(GetFullPath(destination));
        return string.Equals(srcRoot, dstRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static IVfsNode CreateNode(VfsPath vfsPath, string fullPath, FileSystemInfo? info = null)
    {
        info ??= Directory.Exists(fullPath) ? new DirectoryInfo(fullPath) : new FileInfo(fullPath);
        var isDir = info.Attributes.HasFlag(FileAttributes.Directory);
        var kind = isDir ? VfsNodeKind.Folder : VfsNodeKind.File;
        var caps = VfsCapabilities.CopySource | VfsCapabilities.Properties | VfsCapabilities.Rename | VfsCapabilities.Delete;
        if (isDir) caps |= VfsCapabilities.CreateChild | VfsCapabilities.MoveTarget | VfsCapabilities.Watchable;
        else caps |= VfsCapabilities.Trash;
        return new LocalFsNode(vfsPath, info, kind, caps, isDir ? IconKey.Folder() : IconKey.File(info.Extension));
    }
}

/// <summary>Lazy-loading VFS node. Stat (size, modified) deferred to first access.</summary>
file sealed class LazyFsNode : IVfsNode
{
    public required VfsPath Path { get; init; }
    public required string FullPath { get; init; }
    public required string DisplayName { get; init; }
    public required VfsNodeKind Kind { get; init; }
    public required bool MightHaveChildren { get; init; }
    public required string TypeDescription { get; init; }
    public required IconKey IconKey { get; init; }
    public required VfsCapabilities Caps { get; init; }

    FileSystemInfo? _info;
    FileSystemInfo Info => _info ??= Directory.Exists(FullPath) ? new DirectoryInfo(FullPath) : new FileInfo(FullPath);

    public long? Size => Kind != VfsNodeKind.Folder && Info is FileInfo fi ? fi.Length : null;
    public DateTimeOffset? Modified => Info.LastWriteTimeUtc > DateTime.MinValue ? Info.LastWriteTimeUtc : null;
    public DateTimeOffset? Created => Info.CreationTimeUtc > DateTime.MinValue ? Info.CreationTimeUtc : null;
    public DateTimeOffset? Accessed => Info.LastAccessTimeUtc > DateTime.MinValue ? Info.LastAccessTimeUtc : null;
    public VfsNodeAttributes Attributes => FsAttr.Map(Info.Attributes);
}

/// <summary>Maps System.IO file attributes onto the VFS-level flag set.</summary>
file static class FsAttr
{
    public static VfsNodeAttributes Map(FileAttributes a)
    {
        var r = VfsNodeAttributes.None;
        if (a.HasFlag(FileAttributes.ReadOnly)) r |= VfsNodeAttributes.ReadOnly;
        if (a.HasFlag(FileAttributes.Hidden)) r |= VfsNodeAttributes.Hidden;
        if (a.HasFlag(FileAttributes.System)) r |= VfsNodeAttributes.System;
        return r;
    }
}

file sealed class LocalFsNode : IVfsNode
{
    private readonly FileSystemInfo _info;

    public LocalFsNode(VfsPath path, FileSystemInfo info, VfsNodeKind kind, VfsCapabilities caps, IconKey iconKey)
    {
        _info = info;
        Path = path;
        Kind = kind;
        Caps = caps;
        IconKey = iconKey;
        DisplayName = info.Name;
        MightHaveChildren = kind == VfsNodeKind.Folder;
        TypeDescription = kind == VfsNodeKind.Folder ? "File folder" : FileTypeDescription.ForExtension(info.Extension);
    }

    public VfsPath Path { get; }
    public string DisplayName { get; }
    public VfsNodeKind Kind { get; }
    public bool MightHaveChildren { get; }
    public IconKey IconKey { get; }
    public VfsCapabilities Caps { get; }

    public long? Size
    {
        get
        {
            if (_info is FileInfo fi)
            {
                try { return fi.Length; }
                catch (FileNotFoundException) { return null; }
            }
            return null;
        }
    }

    public DateTimeOffset? Modified => _info.LastWriteTimeUtc > DateTime.MinValue
        ? _info.LastWriteTimeUtc
        : null;

    public DateTimeOffset? Created => _info.CreationTimeUtc > DateTime.MinValue ? _info.CreationTimeUtc : null;
    public DateTimeOffset? Accessed => _info.LastAccessTimeUtc > DateTime.MinValue ? _info.LastAccessTimeUtc : null;
    public VfsNodeAttributes Attributes => FsAttr.Map(_info.Attributes);

    public string TypeDescription { get; }
}

file sealed class LocalFsMutator : IVfsMutator
{
    private readonly string _directoryPath;
    private readonly string _trashDirectory;

    public LocalFsMutator(string directoryPath, string trashDirectory)
    {
        _directoryPath = directoryPath;
        _trashDirectory = trashDirectory;
    }

    public ValueTask<VfsPath> CreateFolderAsync(VfsPath parent, string name, CancellationToken ct)
    {
        var fullPath = Path.Combine(_directoryPath, name);
        System.IO.Directory.CreateDirectory(fullPath);
        return ValueTask.FromResult(VfsPath.Combine(parent, name));
    }

    public ValueTask RenameAsync(VfsPath path, string newName, CancellationToken ct)
    {
        var fullPath = Path.Combine(_directoryPath, path.FileName);
        var newPath = Path.Combine(_directoryPath, newName);

        if (System.IO.File.Exists(fullPath))
            System.IO.File.Move(fullPath, newPath);
        else if (System.IO.Directory.Exists(fullPath))
            System.IO.Directory.Move(fullPath, newPath);

        return ValueTask.CompletedTask;
    }

    public ValueTask<VfsPath> MoveAsync(VfsPath path, VfsPath destinationParent, CancellationToken ct)
    {
        var sourceFull = Path.Combine(_directoryPath, path.FileName);
        var destFull = Path.Combine(destinationParent.Value, path.FileName);   // file-scheme value IS the real path
        LocalTrash.MoveToTrash(sourceFull, destFull);   // general relocate: file/dir + cross-volume fallback
        return ValueTask.FromResult(VfsPath.Combine(destinationParent, path.FileName));
    }

    public ValueTask<VfsPath?> DeleteAsync(VfsPath path, bool toTrash, CancellationToken ct)
    {
        var fullPath = Path.Combine(_directoryPath, path.FileName);

        if (toTrash)
        {
            // Move to the trash directory under a collision-free name and return the new
            // location so the operation can be undone (restored to its original path).
            System.IO.Directory.CreateDirectory(_trashDirectory);
            var trashPath = UniqueTrashPath(path.FileName);

            LocalTrash.MoveToTrash(fullPath, trashPath);

            return ValueTask.FromResult<VfsPath?>(new VfsPath("file", trashPath));
        }

        if (System.IO.Directory.Exists(fullPath))
            System.IO.Directory.Delete(fullPath, recursive: true);
        else
            System.IO.File.Delete(fullPath);

        return ValueTask.FromResult<VfsPath?>(null);
    }

    /// <summary>Returns a path in the trash for <paramref name="name"/> that does not collide.</summary>
    private string UniqueTrashPath(string name)
    {
        var candidate = Path.Combine(_trashDirectory, name);
        if (!System.IO.File.Exists(candidate) && !System.IO.Directory.Exists(candidate))
            return candidate;

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 1; ; i++)
        {
            candidate = Path.Combine(_trashDirectory, $"{stem} {i}{ext}");
            if (!System.IO.File.Exists(candidate) && !System.IO.Directory.Exists(candidate))
                return candidate;
        }
    }

    public ValueTask SetAttributesAsync(VfsPath path, VfsNodeAttributes attributes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var fullPath = Path.Combine(_directoryPath, path.FileName);

        // File.GetAttributes/SetAttributes work for both files and directories, so no
        // File/DirectoryInfo split is needed here.
        var current = System.IO.File.GetAttributes(fullPath);

        // Clear only the bits we manage, then re-apply from the requested set — every other
        // bit (Directory, Archive, Compressed, ReparsePoint, ...) is preserved untouched.
        var updated = current & ~(System.IO.FileAttributes.ReadOnly
            | System.IO.FileAttributes.Hidden
            | System.IO.FileAttributes.System);

        if (attributes.HasFlag(VfsNodeAttributes.ReadOnly)) updated |= System.IO.FileAttributes.ReadOnly;
        // Best-effort on macOS: FileAttributes.Hidden maps to the UF_HIDDEN flag via .NET's
        // Unix attribute shim, which Finder does respect, but it is NOT the same as a
        // dot-prefixed name — some tools/views may still surface the item. Treat this as an
        // approximation, not a guarantee, on non-Windows platforms.
        if (attributes.HasFlag(VfsNodeAttributes.Hidden)) updated |= System.IO.FileAttributes.Hidden;
        if (attributes.HasFlag(VfsNodeAttributes.System)) updated |= System.IO.FileAttributes.System;

        System.IO.File.SetAttributes(fullPath, updated);
        return ValueTask.CompletedTask;
    }

    public ValueTask<Stream> OpenWriteAsync(VfsPath file, CancellationToken ct)
    {
        var fullPath = Path.Combine(_directoryPath, file.FileName);

        // Ensure the target directory exists so a write is self-sufficient. Folder jobs
        // are scanned depth-first (children before their parent), so a nested file can be
        // written before its folder job runs; without this, that write would throw
        // DirectoryNotFoundException.
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            System.IO.Directory.CreateDirectory(dir);

        Stream stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);
        return ValueTask.FromResult(stream);
    }
}

/// <summary>
/// Relocates a file or directory into the trash. A same-volume move is an atomic rename; a
/// cross-volume directory rename fails (EXDEV) because <see cref="System.IO.Directory.Move"/>
/// cannot span filesystems, so we recreate the tree at the destination and delete the original
/// (bevel-bb0). <see cref="System.IO.File.Move"/> already copies across volumes internally, so a
/// single file needs no special handling.
/// </summary>
internal static class LocalTrash
{
    public static void MoveToTrash(string source, string destination)
    {
        // A same-volume Directory.Move onto an existing directory throws IOException, which the
        // cross-volume fallback below would misread as EXDEV and "recover" by copy-merge + delete of
        // the source — silently destroying it. Trash always passes a unique destination, so this only
        // guards the general-relocate reuse (MoveAsync); make a collision an explicit error either way
        // (bevel-376 review).
        if (System.IO.Directory.Exists(destination) || System.IO.File.Exists(destination))
            throw new IOException($"Cannot move to '{destination}': an item with that name already exists.");

        if (System.IO.Directory.Exists(source))
            MoveDirectory(source, destination);
        else
            System.IO.File.Move(source, destination);
    }

    private static void MoveDirectory(string source, string destination)
    {
        try
        {
            // Fast path: an atomic rename when source and trash share a volume.
            System.IO.Directory.Move(source, destination);
        }
        catch (DirectoryNotFoundException)
        {
            // A genuinely missing source — surface it rather than masking it with copy+delete.
            throw;
        }
        catch (IOException)
        {
            // Cross-volume (EXDEV) or a similar rename refusal: recreate the tree at the trash
            // location, then remove the original. The destination is a fresh, unique path, so
            // there is nothing to overwrite.
            CopyDirectory(source, destination);
            System.IO.Directory.Delete(source, recursive: true);
        }
    }

    /// <summary>Recursively copies every file and subdirectory of <paramref name="source"/> into
    /// <paramref name="destination"/> (which is created if needed). Symlinks are RECREATED as links,
    /// never followed: following a directory symlink would copy outside the tree — or recurse forever
    /// if it points at an ancestor (bevel-brax).</summary>
    public static void CopyDirectory(string source, string destination)
    {
        System.IO.Directory.CreateDirectory(destination);

        foreach (var file in System.IO.Directory.EnumerateFiles(source))
        {
            var dest = Path.Combine(destination, Path.GetFileName(file));
            var linkTarget = new System.IO.FileInfo(file).LinkTarget;
            if (linkTarget is not null)
                System.IO.File.CreateSymbolicLink(dest, linkTarget);   // recreate the link, don't copy its target's bytes
            else
                System.IO.File.Copy(file, dest);
        }

        foreach (var dir in System.IO.Directory.EnumerateDirectories(source))
        {
            var dest = Path.Combine(destination, Path.GetFileName(dir));
            var linkTarget = new System.IO.DirectoryInfo(dir).LinkTarget;
            if (linkTarget is not null)
                System.IO.Directory.CreateSymbolicLink(dest, linkTarget);   // recreate the dir symlink; do NOT recurse into it
            else
                CopyDirectory(dir, dest);   // a real subdirectory — recurse
        }
    }
}

file sealed class LocalDirectoryWatcher : IDirectoryWatcher
{
    private readonly FileSystemWatcher _watcher;
    private readonly Subject<FsChangeBatch> _subject = new();

    // FileSystemWatcher raises events on threadpool threads and can deliver a queued
    // event even after EnableRaisingEvents is cleared. The gate serializes Notify against
    // Dispose so OnNext is never called on a disposed Subject.
    private readonly object _gate = new();
    private bool _disposed;

    public LocalDirectoryWatcher(string path)
    {
        _watcher = new FileSystemWatcher(path)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };

        _watcher.Created += (s, e) => Notify();
        _watcher.Deleted += (s, e) => Notify();
        _watcher.Changed += (s, e) => Notify();
        _watcher.Renamed += (s, e) => Notify();
        _watcher.Error += (s, e) => OnWatcherError();
    }

    public IObservable<FsChangeBatch> Changes => _subject;

    private void Notify()
    {
        // Batching via debounce is handled at the consumer level (FM-141).
        // For now, emit a rescan signal (empty paths = "rescan folder").
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                _subject.OnNext(new FsChangeBatch(
                    Array.Empty<VfsPath>(),
                    Array.Empty<VfsPath>(),
                    Array.Empty<VfsPath>(),
                    Array.Empty<(VfsPath, VfsPath)>()));
            }
            catch
            {
                // A subscriber throwing must not crash the FileSystemWatcher's thread-pool
                // callback and tear down watching (review R5).
            }
        }
    }

    private void OnWatcherError()
    {
        // A watcher error (typically InternalBufferOverflowException) drops events and can
        // stop the watcher raising further notifications. Emit one rescan so the consumer
        // refreshes, then try to resume watching (review #14).
        Notify();
        lock (_gate)
        {
            if (_disposed) return;
            try { _watcher.EnableRaisingEvents = true; }
            catch { /* watcher unrecoverable; the rescan above is the best-effort recovery */ }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        // Stop new events, then dispose. Any in-flight Notify has either already
        // completed under the gate or will observe _disposed and return.
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _subject.Dispose();
    }
}

internal static class FileTypeDescription
{
    /// <summary>Human-readable type label for a file extension (single source of truth).</summary>
    public static string ForExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".txt" => "Text Document",
        ".pdf" => "PDF Document",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tiff" => "Image",
        ".zip" => "Compressed (zipped) Folder",
        ".exe" or ".app" => "Application",
        "" => "File",
        _ => $"{extension.TrimStart('.').ToUpperInvariant()} File",
    };
}
