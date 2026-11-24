using System.Runtime.CompilerServices;

namespace Bevel.Core.Vfs;

/// <summary>bevel-lkib: VFS provider for the "net" scheme behind the Network tree node (previously a dead
/// stub — selecting it threw KeyNotFoundException, swallowed). The root resolves to a virtual "Network"
/// node; enumerating it lists the network volumes mounted RIGHT NOW as read-only Volume nodes whose
/// navigation target is the volume's file:// mount. The mount list comes through a constructor seam so
/// tests inject it; the default filters <see cref="DriveInfo.GetDrives"/> by <see cref="DriveType.Network"/>
/// (UNVERIFIED: whether .NET reports smbfs/afpfs/nfs mounts as Network on macOS — check once on a machine
/// with a mounted share). No mounts is a legitimate empty folder ("0 object(s)").</summary>
public sealed class NetworkProvider : IVfsProvider
{
    private readonly Func<IEnumerable<(string Name, string MountPath)>> _mounts;

    public NetworkProvider(Func<IEnumerable<(string Name, string MountPath)>>? mounts = null)
        => _mounts = mounts ?? DefaultMounts;

    public string Scheme => "net";

    private static IEnumerable<(string Name, string MountPath)> DefaultMounts()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { drives = Array.Empty<DriveInfo>(); }
        foreach (var d in drives)
            if (d.DriveType == DriveType.Network && d.IsReady)
                yield return (d.Name, d.RootDirectory.FullName);
    }

    public ValueTask<IVfsNode> ResolveAsync(VfsPath path, CancellationToken ct)
    {
        if (path.IsRoot)
            return ValueTask.FromResult<IVfsNode>(VirtualNode.VirtualRoot(path, "Network", IconKey.Network()));
        var mount = MountPathFor(path);
        var found = _mounts().FirstOrDefault(m => m.MountPath == mount);
        if (found.MountPath is null)
            throw new DirectoryNotFoundException($"Network volume not found: {mount}");
        return ValueTask.FromResult<IVfsNode>(CreateVolumeNode(path, found.Name, mount));
    }

    public async IAsyncEnumerable<IVfsNode> EnumerateAsync(
        VfsPath folder,
        EnumerateOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!folder.IsRoot)
            yield break;   // a volume's contents enumerate via the file scheme, not here

        foreach (var (name, mount) in _mounts())
        {
            ct.ThrowIfCancellationRequested();
            yield return CreateVolumeNode(VfsPath.Combine(folder, VolumeKeyFor(mount)), name, mount);
        }
    }

    public ValueTask<Stream> OpenReadAsync(VfsPath file, CancellationToken ct)
        => throw new NotSupportedException("Network namespace does not support file reads.");

    public ValueTask<IVfsMutator?> GetMutatorAsync(VfsPath folder, CancellationToken ct)
        => ValueTask.FromResult<IVfsMutator?>(null);   // read-only

    public IDirectoryWatcher? CreateWatcher(VfsPath folder) => null;

    public NameValidationResult ValidateName(VfsPath folder, string proposedName)
        => NameValidationResult.Fail("Network locations are read-only.");

    // ── Scheme-crossing helpers ──────────────────────────────────────────────

    /// <summary>Serialize write-ops on the same volume queue as the mounted share's local queue.</summary>
    public string GetVolumeKey(VfsPath path)
        => path.IsRoot ? Scheme : $"file:{Path.GetPathRoot(MountPathFor(path)) ?? "/"}";

    /// <summary>The mounted share's native path for open/copy-out; the root has none.</summary>
    public string? ResolveEffectivePath(VfsPath path)
        => path.IsRoot ? null : MountPathFor(path);

    /// <summary>bevel-cq7g/lkib: a network volume navigates to its file:// mount; the root stays put.</summary>
    public VfsPath? ResolveNavigationTarget(VfsPath path)
        => path.IsRoot ? null : new VfsPath("file", MountPathFor(path));

    private static string VolumeKeyFor(string mount) => Uri.EscapeDataString(mount);
    private static string MountPathFor(VfsPath path) => Uri.UnescapeDataString(path.FileName);

    private static IVfsNode CreateVolumeNode(VfsPath path, string name, string mount) => new VirtualNode
    {
        Path = path,
        DisplayName = name,
        Kind = VfsNodeKind.Volume,
        MightHaveChildren = true,
        TypeDescription = "Network Drive",
        IconKey = IconKey.NetDrive(),
        Caps = VfsCapabilities.Properties,   // read-only: Properties only
        Volume = new VolumeInfo(MountPath: mount, TotalSize: 0, FreeSpace: 0, Format: ""),
    };
}
