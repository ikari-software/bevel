namespace Bevel.Core.Vfs;

[Flags]
public enum VfsNodeAttributes
{
    None = 0,
    ReadOnly = 1 << 0,
    Hidden = 1 << 1,
    System = 1 << 2,
    Archive = 1 << 3,
}
