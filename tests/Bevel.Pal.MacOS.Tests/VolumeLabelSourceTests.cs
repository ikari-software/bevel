using Bevel.Pal.MacOS;
using Xunit;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>
/// Tests for MacOSVolumeLabelSource (bevel-1cc): non-root labels are the mount basename
/// (macOS mounts volumes at /Volumes/&lt;label&gt;), and the root volume's label is the name
/// of the /Volumes entry that links back to "/" — exercised against a fake volumes directory
/// so the logic is deterministic, plus one real-machine check on macOS.
/// </summary>
public sealed class VolumeLabelSourceTests : IDisposable
{
    private readonly string _dir;

    public VolumeLabelSourceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"bevel-vols-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Non_root_labels_are_the_mount_basename()
    {
        var source = new MacOSVolumeLabelSource(_dir);

        Assert.Equal("extSSD", source.LabelFor("/Volumes/extSSD"));
        Assert.Equal("extSSD", source.LabelFor("/Volumes/extSSD/"));
        Assert.Equal("home", source.LabelFor("/System/Volumes/Data/home"));
        Assert.Null(source.LabelFor(""));
    }

    [Fact]
    public void Root_label_is_the_volumes_entry_linking_to_root()
    {
        if (OperatingSystem.IsWindows()) return; // symlink creation needs privilege there

        Directory.CreateDirectory(Path.Combine(_dir, "extSSD")); // a real mount dir — not it
        Directory.CreateSymbolicLink(Path.Combine(_dir, "Macintosh HD"), "/");

        var source = new MacOSVolumeLabelSource(_dir);

        Assert.Equal("Macintosh HD", source.LabelFor("/"));
    }

    [Fact]
    public void Missing_volumes_directory_yields_no_root_label()
    {
        var source = new MacOSVolumeLabelSource(Path.Combine(_dir, "does-not-exist"));

        Assert.Null(source.LabelFor("/"));
    }

    [Fact]
    public void Real_machine_resolves_a_root_label()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var label = new MacOSVolumeLabelSource().LabelFor("/");

        Assert.False(string.IsNullOrWhiteSpace(label)); // e.g. "Macintosh HD"
    }
}
