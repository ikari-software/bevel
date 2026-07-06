namespace Bevel.Core.Vfs;

/// <summary>
/// Options for IVfsProvider.EnumerateAsync.
/// </summary>
public sealed record EnumerateOptions
{
    /// <summary>Include hidden files (dot-files on macOS/Linux).</summary>
    public bool IncludeHidden { get; init; }

    /// <summary>Include system files.</summary>
    public bool IncludeSystem { get; init; }

    /// <summary>Optional filter predicate applied after enumeration.</summary>
    public Func<IVfsNode, bool>? Filter { get; init; }

    /// <summary>Maximum items to return (0 = unlimited).</summary>
    public int Limit { get; init; }
}
