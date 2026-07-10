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

    // NOTE: resolve-by-path round-trip (enumerate → ResolveAsync(node.Path)) is deliberately
    // not asserted here: macOS volume display names embed the mount path ("Macintosh HD (/)"),
    // whose '/' mangles VfsPath.FileName and breaks the round-trip — tracked as its own bug
    // (bevel: ComputerProvider path round-trip), where the fix will carry the reproducer.
}
