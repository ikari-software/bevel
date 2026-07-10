using System.Runtime.CompilerServices;

namespace Bevel.Core.Vfs;

/// <summary>
/// VFS provider for the "My Computer" namespace root.
/// Scheme = "computer". Roots at the Desktop/My Computer level.
/// Enumerates mounted volumes/drives as child nodes.
/// </summary>
public sealed class ComputerProvider : IVfsProvider
{
    public string Scheme => "computer";

    public ValueTask<IVfsNode> ResolveAsync(VfsPath path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (path.IsRoot)
        {
            return ValueTask.FromResult<IVfsNode>(VirtualNode.VirtualRoot(
                path, "My Computer", IconKey.Computer()));
        }

        // Resolve a specific volume by name
        var volumeName = path.FileName;
        var drives = DriveInfo.GetDrives();
        var drive = drives.FirstOrDefault(d =>
            GetVolumeDisplayName(d).Equals(volumeName, StringComparison.OrdinalIgnoreCase));

        if (drive is null)
            throw new DirectoryNotFoundException($"Volume not found: {volumeName}");

        return ValueTask.FromResult<IVfsNode>(CreateVolumeNode(path, drive));
    }

    public async IAsyncEnumerable<IVfsNode> EnumerateAsync(
        VfsPath folder,
        EnumerateOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!folder.IsRoot)
        {
            // A specific volume — enumerate its root directory via file provider
            yield break; // Volumes enumerate their root via the file scheme, not here
        }

        // My Computer root: enumerate all mounted volumes
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            yield break;
        }

        foreach (var drive in drives)
        {
            ct.ThrowIfCancellationRequested();

            if (!drive.IsReady) continue;

            var childPath = VfsPath.Combine(folder, GetVolumeDisplayName(drive));
            yield return CreateVolumeNode(childPath, drive);
        }
    }

    public ValueTask<Stream> OpenReadAsync(VfsPath file, CancellationToken ct)
        => throw new NotSupportedException("Computer namespace does not support file reads.");

    public ValueTask<IVfsMutator?> GetMutatorAsync(VfsPath folder, CancellationToken ct)
        => ValueTask.FromResult<IVfsMutator?>(null);

    public IDirectoryWatcher? CreateWatcher(VfsPath folder) => null;

    public NameValidationResult ValidateName(VfsPath folder, string proposedName)
        => NameValidationResult.Fail("Cannot rename items in Computer.");

    /// <summary>
    /// Translates a volume node to the native mount path it stands for (e.g. the
    /// "Local Disk (C:)" node → "C:\"), so the shell can navigate into it via the file provider
    /// without reaching into ExtraColumns. The root ("My Computer") and unmatched/absent volumes
    /// have no effective path.
    /// </summary>
    public string? ResolveEffectivePath(VfsPath path)
    {
        if (path.IsRoot) return null;
        var volumeName = path.FileName;
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { return null; }
        var drive = drives.FirstOrDefault(d =>
            GetVolumeDisplayName(d).Equals(volumeName, StringComparison.OrdinalIgnoreCase));
        return drive?.RootDirectory.FullName;
    }

    private static IVfsNode CreateVolumeNode(VfsPath path, DriveInfo drive)
    {
        var icon = drive.DriveType switch
        {
            DriveType.CDRom => IconKey.CdDrive(),
            DriveType.Network => IconKey.NetDrive(),
            DriveType.Removable => IconKey.RemovableDrive(),
            _ => IconKey.FixedDrive(),
        };

        return new VirtualNode
        {
            Path = path,
            DisplayName = GetVolumeDisplayName(drive),
            Kind = VfsNodeKind.Volume,
            MightHaveChildren = true,
            Size = drive.TotalSize,
            Modified = null,
            TypeDescription = drive.DriveType switch
            {
                DriveType.Fixed => "Local Disk",
                DriveType.CDRom => "CD Drive",
                DriveType.Network => "Network Drive",
                DriveType.Removable => "Removable Disk",
                _ => "Drive",
            },
            IconKey = icon,
            Caps = VfsCapabilities.Properties,
            Volume = new VolumeInfo(
                MountPath: drive.RootDirectory.FullName,
                TotalSize: drive.TotalSize,
                FreeSpace: drive.TotalFreeSpace,
                Format: drive.DriveFormat),
        };
    }

    private static string GetVolumeDisplayName(DriveInfo drive)
    {
        if (!string.IsNullOrWhiteSpace(drive.VolumeLabel))
            return $"{drive.VolumeLabel} ({drive.Name.TrimEnd(Path.DirectorySeparatorChar)})";

        return drive.Name.TrimEnd(Path.DirectorySeparatorChar);
    }
}