namespace Bevel.Core.Vfs;

/// <summary>
/// Discriminates VFS node types for the UI layer.
/// </summary>
public enum VfsNodeKind
{
    File,
    Folder,
    Volume,
    VirtualRoot,
    Link,
}
