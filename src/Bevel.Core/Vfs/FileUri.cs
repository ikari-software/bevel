namespace Bevel.Core.Vfs;

/// <summary>Builds a <c>file://</c> URI from a native path, for APIs that take one (the storage
/// provider behind an OS drag, bevel-u2x6).</summary>
/// <remarks>Each segment is escaped. <see cref="UriBuilder"/> alone leaves <c>?</c>, <c>#</c> and
/// <c>%</c> in a path untouched, so a file called <c>50% off?.txt</c> would be cut at the <c>?</c> and
/// resolve to nothing. Use this instead of building the URI by hand.</remarks>
public static class FileUri
{
    public static Uri FromPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path.Replace('\\', '/');

        // A UNC path (//server/share/x) carries its server as the URI host.
        var host = "";
        if (normalized.StartsWith("//", StringComparison.Ordinal) && normalized.Length > 2 && normalized[2] != '/')
        {
            var end = normalized.IndexOf('/', 2);
            host = end < 0 ? normalized[2..] : normalized[2..end];
            normalized = end < 0 ? "" : normalized[end..];
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var escaped = new string[segments.Length];
        for (var i = 0; i < segments.Length; i++)
            // A Windows drive spec ("C:") stays literal: "C%3A" is not a drive.
            escaped[i] = i == 0 && host.Length == 0 && IsDriveSpec(segments[i])
                ? segments[i]
                : Uri.EscapeDataString(segments[i]);

        return new Uri("file://" + host + "/" + string.Join('/', escaped));
    }

    private static bool IsDriveSpec(string segment)
        => segment.Length == 2 && segment[1] == ':' && char.IsAsciiLetter(segment[0]);
}
