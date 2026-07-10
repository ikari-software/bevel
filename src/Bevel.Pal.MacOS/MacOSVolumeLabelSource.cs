using Bevel.Pal.Abstractions;

namespace Bevel.Pal.MacOS;

/// <summary>
/// macOS volume labels without ObjC interop or subprocesses (bevel-1cc): every mounted volume
/// appears under /Volumes, where the entry NAME is the user-facing label. Non-root volumes are
/// mounted at /Volumes/&lt;label&gt; directly (basename = label); the root volume is special —
/// its label is the name of the /Volumes symlink that points back at "/"
/// (e.g. "/Volumes/Macintosh HD" → "/"). The root label is resolved once and cached; it cannot
/// change without a remount.
/// </summary>
public sealed class MacOSVolumeLabelSource : IVolumeLabelSource
{
    private readonly Lazy<string?> _rootLabel;

    public MacOSVolumeLabelSource() : this("/Volumes")
    {
    }

    /// <summary>Test seam: resolve the root label against a fake volumes directory.</summary>
    internal MacOSVolumeLabelSource(string volumesDir)
        => _rootLabel = new Lazy<string?>(() => ResolveRootLabel(volumesDir));

    public string? LabelFor(string mountPath)
    {
        if (string.IsNullOrEmpty(mountPath))
            return null;

        if (mountPath == "/")
            return _rootLabel.Value;

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(mountPath));
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>The name of the entry in <paramref name="volumesDir"/> that links to "/", or null.</summary>
    internal static string? ResolveRootLabel(string volumesDir)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(volumesDir))
            {
                var info = new DirectoryInfo(entry);
                if (info.LinkTarget == "/" || info.ResolveLinkTarget(returnFinalTarget: true)?.FullName == "/")
                    return info.Name;
            }
        }
        catch
        {
            // Unreadable /Volumes (sandbox, exotic setup) — no label, callers fall back.
        }
        return null;
    }
}
