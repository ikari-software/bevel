namespace Bevel.Core.Vfs;

/// <summary>
/// Semantic identifier for icon resolution. Themes map SemanticId → recreated assets;
/// unmapped types fall through to native platform icons.
/// </summary>
public readonly record struct IconKey(string? SemanticId = null, string? NativeRef = null, int Size = 16)
{
    public static readonly IconKey Unknown = new(SemanticId: "doc.generic");

    public static IconKey Folder(int size = 16) => new(SemanticId: "folder", Size: size);
    public static IconKey FolderOpen(int size = 16) => new(SemanticId: "folder.open", Size: size);
    public static IconKey File(string extension, int size = 16) => new(SemanticId: $"doc.{extension.TrimStart('.').ToLowerInvariant()}", Size: size);
    public static IconKey Volume(string kind = "fixed", int size = 16) => new(SemanticId: $"drive.{kind}", Size: size);
    public static IconKey FixedDrive(int size = 16) => new(SemanticId: "drive.fixed", Size: size);
    public static IconKey CdDrive(int size = 16) => new(SemanticId: "drive.cd", Size: size);
    public static IconKey NetDrive(int size = 16) => new(SemanticId: "drive.net", Size: size);
    public static IconKey RemovableDrive(int size = 16) => new(SemanticId: "drive.removable", Size: size);
    public static IconKey Computer(int size = 16) => new(SemanticId: "computer", Size: size);
    public static IconKey Network(int size = 16) => new(SemanticId: "network", Size: size);
    public static IconKey Trash(bool full = false, int size = 16) => new(SemanticId: full ? "trash.full" : "trash.empty", Size: size);
}
