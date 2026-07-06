namespace Bevel.Core.Vfs;

/// <summary>
/// Identifies a location in the virtual file system.
/// <para>
/// <see cref="Scheme"/> maps to an <see cref="IVfsProvider"/>; <see cref="Value"/> is the
/// provider-specific path within that scheme. The canonical separator is '/', and the
/// canonical string form is <c>vfs://{scheme}/{value}</c> (see <see cref="ToString"/> /
/// <see cref="Parse"/>).
/// </para>
/// <para>
/// Both '/' and '\' are accepted on input and normalized to '/', so native OS paths
/// (including Windows backslash paths) round-trip correctly through <see cref="Combine"/>,
/// <see cref="ParentValue"/> and <see cref="FileName"/>. All separator handling lives here
/// so no call site needs to re-implement path parsing.
/// </para>
/// </summary>
public readonly record struct VfsPath
{
    private const string UriPrefix = "vfs://";
    private static readonly char[] Separators = { '/', '\\' };

    public string Scheme { get; }

    /// <summary>Forward-slash canonical path within the scheme; empty for a scheme root.</summary>
    public string Value { get; }

    public VfsPath(string scheme, string value)
    {
        Scheme = scheme ?? throw new ArgumentNullException(nameof(scheme));
        Value = Normalize(value);
    }

    /// <summary>The root of a scheme (empty value).</summary>
    public static VfsPath Root(string scheme) => new(scheme, "");

    /// <summary>True when this path is the root of its scheme.</summary>
    public bool IsRoot => Value.Length == 0;

    /// <summary>
    /// Appends a single child segment. Leading/trailing separators on <paramref name="child"/>
    /// are ignored; an empty child returns <paramref name="parent"/> unchanged.
    /// </summary>
    public static VfsPath Combine(VfsPath parent, string child)
    {
        if (string.IsNullOrEmpty(child))
            return parent;

        var segment = child.Trim(Separators);
        if (segment.Length == 0)
            return parent;

        return parent.IsRoot
            ? new VfsPath(parent.Scheme, segment)
            : new VfsPath(parent.Scheme, parent.Value + "/" + segment);
    }

    /// <summary>The parent path's <see cref="Value"/>, or empty when this is a top-level entry.</summary>
    public string ParentValue
    {
        get
        {
            var sep = Value.LastIndexOf('/');
            return sep <= 0 ? "" : Value[..sep];
        }
    }

    /// <summary>This path's parent as a <see cref="VfsPath"/> in the same scheme.</summary>
    public VfsPath Parent => new(Scheme, ParentValue);

    /// <summary>The final segment of <see cref="Value"/> (the leaf name).</summary>
    public string FileName
    {
        get
        {
            var sep = Value.LastIndexOf('/');
            return sep < 0 ? Value : Value[(sep + 1)..];
        }
    }

    public override string ToString() => $"{UriPrefix}{Scheme}/{Value}";

    /// <summary>
    /// Parses the canonical <c>vfs://{scheme}/{value}</c> form produced by <see cref="ToString"/>.
    /// The value part is taken verbatim (no URL-decoding), so it round-trips exactly.
    /// </summary>
    public static VfsPath Parse(string s)
        => TryParse(s, out var path)
            ? path
            : throw new FormatException($"'{s}' is not a canonical VFS path (expected '{UriPrefix}<scheme>/<value>').");

    /// <summary>Non-throwing variant of <see cref="Parse"/>.</summary>
    public static bool TryParse(string? s, out VfsPath path)
    {
        path = default;
        if (string.IsNullOrEmpty(s) || !s.StartsWith(UriPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = s[UriPrefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash < 0)
        {
            if (rest.Length == 0)
                return false; // "vfs://" with no scheme
            path = new VfsPath(rest, "");
            return true;
        }

        var scheme = rest[..slash];
        if (scheme.Length == 0)
            return false; // "vfs:///value" with no scheme

        path = new VfsPath(scheme, rest[(slash + 1)..]);
        return true;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        var normalized = value.Replace('\\', '/');

        // Strip a trailing separator so "a/b/" and "a/b" compare equal, but preserve a
        // lone "/" (a rooted absolute path on POSIX) rather than collapsing it to root.
        if (normalized.Length > 1)
            normalized = normalized.TrimEnd('/');

        return normalized;
    }
}
