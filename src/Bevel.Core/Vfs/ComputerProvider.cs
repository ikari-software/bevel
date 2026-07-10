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

        // Resolve a volume by its mount identity — the path segment is the ESCAPED mount path,
        // never the display name (which embeds '/' on macOS, e.g. "Macintosh HD (/)", and would
        // be mangled by VfsPath's separator handling — bevel-d3b).
        var mount = MountPathFor(path);
        var drives = DriveInfo.GetDrives();
        var drive = drives.FirstOrDefault(d => d.RootDirectory.FullName == mount);

        if (drive is null)
            throw new DirectoryNotFoundException($"Volume not found: {mount}");

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

            var childPath = VfsPath.Combine(folder, VolumeKeyFor(drive));
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
        var mount = MountPathFor(path);
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { return null; }
        return drives.FirstOrDefault(d => d.RootDirectory.FullName == mount)?.RootDirectory.FullName;
    }

    /// <summary>
    /// The path segment for a volume: its native mount path, percent-escaped so the segment
    /// contains no '/' or '\' (VfsPath splits segments on separators; bevel-d3b). This is the
    /// volume's IDENTITY — stable across label changes and collision-free — while
    /// <see cref="GetVolumeDisplayName"/> is presentation only and never appears in paths.
    /// </summary>
    private static string VolumeKeyFor(DriveInfo drive) => Uri.EscapeDataString(drive.RootDirectory.FullName);

    /// <summary>Inverse of <see cref="VolumeKeyFor"/>: the native mount path a volume path segment stands for.</summary>
    private static string MountPathFor(VfsPath path) => Uri.UnescapeDataString(path.FileName);

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
        // Win2000 presentation: "Label (mount)". Trimming the POSIX root "/" would leave an
        // empty mount, so keep the name verbatim then. On Unix, .NET reports VolumeLabel as the
        // mount path itself (no real label available) — treat that as unlabeled rather than
        // rendering "/ (/)"; real macOS labels are a PAL follow-up (bevel-d3b notes).
        var mount = drive.Name.TrimEnd(Path.DirectorySeparatorChar);
        if (mount.Length == 0) mount = drive.Name;

        if (string.IsNullOrWhiteSpace(drive.VolumeLabel) || drive.VolumeLabel == drive.Name)
            return mount;

        return $"{drive.VolumeLabel} ({mount})";
    }
}