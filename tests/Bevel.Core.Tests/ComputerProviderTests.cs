using Bevel.Core.Vfs;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>
/// Behavioural tests for the "My Computer" namespace over the machine's real mounted drives
/// (every machine has at least one ready volume — "/" on macOS). Volume metadata is typed
/// (bevel-972): nodes carry a <see cref="VolumeInfo"/> instead of a stringly-keyed bag.
/// </summary>
public sealed class ComputerProviderTests
{
    private readonly ComputerProvider _provider = new();

    private static async Task<List<IVfsNode>> EnumerateRootAsync(ComputerProvider provider)
    {
        var nodes = new List<IVfsNode>();
        await foreach (var n in provider.EnumerateAsync(
            VfsPath.Root("computer"), new EnumerateOptions(), CancellationToken.None))
            nodes.Add(n);
        return nodes;
    }

    [Fact]
    public async Task Enumerated_volumes_carry_typed_volume_metadata()
    {
        var volumes = await EnumerateRootAsync(_provider);

        Assert.NotEmpty(volumes);
        Assert.All(volumes, v =>
        {
            Assert.Equal(VfsNodeKind.Volume, v.Kind);
            Assert.NotNull(v.Volume);
            Assert.False(string.IsNullOrEmpty(v.Volume!.MountPath));
            // Real machines mount pseudo-volumes too (macOS autofs reports TotalSize 0),
            // so only non-negativity is invariant here.
            Assert.True(v.Volume.TotalSize >= 0);
            Assert.True(v.Volume.FreeSpace >= 0);
            Assert.NotNull(v.Volume.Format);
        });
    }

    [Fact]
    public async Task The_computer_root_itself_has_no_volume_metadata()
    {
        var root = await _provider.ResolveAsync(VfsPath.Root("computer"), CancellationToken.None);

        Assert.Equal(VfsNodeKind.VirtualRoot, root.Kind);
        Assert.Null(root.Volume);
    }

    // ── Path identity round-trip (bevel-d3b) ───────────────────────────────
    // Volume path segments are the ESCAPED mount path (identity), never the display name —
    // display names embed '/' on macOS ("Macintosh HD (/)") and would be mangled by VfsPath.

    [Fact]
    public async Task Volume_path_segments_are_separator_free_and_round_trip_through_ResolveAsync()
    {
        var volumes = await EnumerateRootAsync(_provider);

        Assert.All(volumes, v =>
        {
            Assert.False(string.IsNullOrEmpty(v.Path.FileName));
            Assert.DoesNotContain("/", v.Path.FileName);
            Assert.DoesNotContain("\\", v.Path.FileName);
        });

        foreach (var v in volumes)
        {
            var resolved = await _provider.ResolveAsync(v.Path, CancellationToken.None);
            Assert.Equal(v.Volume!.MountPath, resolved.Volume!.MountPath);
            Assert.Equal(v.DisplayName, resolved.DisplayName);
        }
    }

    [Fact]
    public async Task ResolveEffectivePath_translates_a_volume_path_to_its_native_mount()
    {
        var volumes = await EnumerateRootAsync(_provider);

        Assert.Equal(volumes[0].Volume!.MountPath, _provider.ResolveEffectivePath(volumes[0].Path));
        Assert.Null(_provider.ResolveEffectivePath(VfsPath.Root("computer"))); // root: no native path
    }

    [Fact]
    public async Task Resolving_an_unknown_volume_throws_DirectoryNotFound()
    {
        var path = VfsPath.Combine(VfsPath.Root("computer"), Uri.EscapeDataString("/no/such/mount"));

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => _provider.ResolveAsync(path, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Display_names_are_presentation_not_escaped_identity()
    {
        var volumes = await EnumerateRootAsync(_provider);

        Assert.All(volumes, v =>
        {
            Assert.False(string.IsNullOrEmpty(v.DisplayName));
            Assert.DoesNotContain("%2F", v.DisplayName, StringComparison.OrdinalIgnoreCase);
        });
    }

    // ── PAL volume labels (bevel-1cc) ──────────────────────────────────────

    private sealed class FakeLabelSource : Bevel.Pal.Abstractions.IVolumeLabelSource
    {
        public List<string> Asked { get; } = new();
        public string? Label { get; set; }

        public string? LabelFor(string mountPath)
        {
            Asked.Add(mountPath);
            return Label;
        }
    }

    private static string MountForDisplay(string mountPath)
    {
        var trimmed = mountPath.TrimEnd(Path.DirectorySeparatorChar);
        return trimmed.Length == 0 ? mountPath : trimmed;
    }

    [Fact]
    public async Task Volume_display_names_use_the_pal_label_in_win2000_form()
    {
        var labels = new FakeLabelSource { Label = "Macintosh HD" };
        var volumes = await EnumerateRootAsync(new ComputerProvider(labels));

        Assert.All(volumes, v =>
            Assert.Equal($"Macintosh HD ({MountForDisplay(v.Volume!.MountPath)})", v.DisplayName));
        // The provider asked the PAL about each volume's actual mount.
        Assert.All(volumes, v => Assert.Contains(v.Volume!.MountPath, labels.Asked));
    }

    [Fact]
    public async Task A_pal_without_a_label_leaves_the_mount_derived_name()
    {
        var labels = new FakeLabelSource { Label = null };
        var withSource = await EnumerateRootAsync(new ComputerProvider(labels));
        var withoutSource = await EnumerateRootAsync(new ComputerProvider());

        // Null from the PAL must render exactly like having no PAL at all.
        Assert.Equal(
            withoutSource.Select(v => v.DisplayName),
            withSource.Select(v => v.DisplayName));
    }
}
