namespace Bevel.Core.Vfs;

/// <summary>
/// What operations a VFS node supports. UI uses this to enable/disable verbs.
/// </summary>
[Flags]
public enum VfsCapabilities
{
    None = 0,
    Rename = 1 << 0,
    Delete = 1 << 1,
    Trash = 1 << 2,
    CopySource = 1 << 3,
    MoveTarget = 1 << 4,
    Watchable = 1 << 5,
    Properties = 1 << 6,
    CreateChild = 1 << 7,
}
