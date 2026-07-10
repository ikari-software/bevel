namespace Bevel.Core.Vfs;

/// <summary>
/// Simple immutable VFS node for virtual providers (Computer, Trash, Network).
/// </summary>
public sealed class VirtualNode : IVfsNode
{
    public VfsPath Path { get; init; }
    public string DisplayName { get; init; } = "";
    public VfsNodeKind Kind { get; init; }
    public bool MightHaveChildren { get; init; }
    public long? Size { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public string TypeDescription { get; init; } = "";
    public IconKey IconKey { get; init; }
    public VfsCapabilities Caps { get; init; }
    public VolumeInfo? Volume { get; init; }

    public static VirtualNode Folder(VfsPath path, string name, IconKey? icon = null)
        => new()
        {
            Path = path,
            DisplayName = name,
            Kind = VfsNodeKind.Folder,
            MightHaveChildren = true,
            TypeDescription = "File Folder",
            IconKey = icon ?? IconKey.Folder(),
            Caps = VfsCapabilities.None,
        };

    public static VirtualNode VirtualRoot(VfsPath path, string name, IconKey? icon = null)
        => new()
        {
            Path = path,
            DisplayName = name,
            Kind = VfsNodeKind.VirtualRoot,
            MightHaveChildren = true,
            TypeDescription = "System Folder",
            IconKey = icon ?? IconKey.Folder(),
            Caps = VfsCapabilities.None,
        };
}