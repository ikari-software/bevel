namespace Bevel.Core.Vfs;

/// <summary>
/// A resolved node in the virtual file system. Bind this in the UI layer.
/// </summary>
public interface IVfsNode
{
    VfsPath Path { get; }
    string DisplayName { get; }
    VfsNodeKind Kind { get; }
    bool MightHaveChildren { get; }
    long? Size { get; }
    DateTimeOffset? Modified { get; }
    string TypeDescription { get; }
    IconKey IconKey { get; }
    VfsCapabilities Caps { get; }
    IReadOnlyDictionary<string, object?> ExtraColumns { get; }

    // Optional richer metadata, surfaced by the Properties sheet. Default to "unknown" so nodes
    // that can't cheaply provide them (virtual/computer nodes, test fakes) need not implement them.
    DateTimeOffset? Created => null;
    DateTimeOffset? Accessed => null;
    VfsNodeAttributes Attributes => VfsNodeAttributes.None;
}
