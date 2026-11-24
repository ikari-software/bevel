using Bevel.Core.Vfs;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>bevel-lkib: the net:// provider lists mounted network shares as read-only volume nodes that
/// navigate to their file:// mount; an empty mount list is a legitimate empty folder.</summary>
public class NetworkProviderTests
{
    private static NetworkProvider Injected(params (string Name, string Mount)[] mounts)
        => new(() => mounts);

    [Fact]
    public async Task Root_lists_injected_network_mounts_as_volumes()
    {
        var provider = Injected(("photos", "/Volumes/photos"), ("backup", "/Volumes/backup"));

        var root = await provider.ResolveAsync(VfsPath.Root("net"), default);
        Assert.Equal(VfsNodeKind.VirtualRoot, root.Kind);
        Assert.Equal("Network", root.DisplayName);

        var nodes = new List<IVfsNode>();
        await foreach (var n in provider.EnumerateAsync(VfsPath.Root("net"), new EnumerateOptions(), default))
            nodes.Add(n);

        Assert.Equal(2, nodes.Count);
        Assert.All(nodes, n =>
        {
            Assert.Equal(VfsNodeKind.Volume, n.Kind);
            Assert.Equal(VfsCapabilities.Properties, n.Caps);          // read-only
            Assert.NotNull(n.Volume);
        });
        Assert.Equal("photos", nodes[0].DisplayName);
        Assert.Equal("/Volumes/photos", nodes[0].Volume!.MountPath);

        // A volume node resolves, exposes its native mount, and navigates to file:// (bevel-cq7g seam).
        var volume = nodes[0];
        var resolved = await provider.ResolveAsync(volume.Path, default);
        Assert.Equal(volume.Path, resolved.Path);
        Assert.Equal("/Volumes/photos", provider.ResolveEffectivePath(volume.Path));
        Assert.Equal(new VfsPath("file", "/Volumes/photos"), provider.ResolveNavigationTarget(volume.Path));

        // The root itself has no mount and stays put.
        Assert.Null(provider.ResolveEffectivePath(VfsPath.Root("net")));
        Assert.Null(provider.ResolveNavigationTarget(VfsPath.Root("net")));
    }

    [Fact]
    public async Task Empty_mount_list_enumerates_nothing_without_throwing()
    {
        var provider = Injected();
        var nodes = new List<IVfsNode>();
        await foreach (var n in provider.EnumerateAsync(VfsPath.Root("net"), new EnumerateOptions(), default))
            nodes.Add(n);
        Assert.Empty(nodes);

        // A sub-path enumerates nothing (contents come via the file scheme) and does not throw.
        var sub = VfsPath.Combine(VfsPath.Root("net"), Uri.EscapeDataString("/Volumes/x"));
        await foreach (var n in provider.EnumerateAsync(sub, new EnumerateOptions(), default))
            nodes.Add(n);
        Assert.Empty(nodes);
    }

    [Fact]
    public async Task Unknown_volume_throws_directory_not_found()
    {
        var provider = Injected(("photos", "/Volumes/photos"));
        var missing = VfsPath.Combine(VfsPath.Root("net"), Uri.EscapeDataString("/Volumes/gone"));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(async () => await provider.ResolveAsync(missing, default));
    }
}
