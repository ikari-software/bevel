using Avalonia.Headless.XUnit;
using Bevel.Core.Vfs;
using Bevel.FileManager.FileOperations;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// Headless tests for navigating into My Computer volumes (bevel-d3b): a computer:// volume
/// path translates to its native file:// mount for both the tree and list-activation paths
/// (shared <see cref="FileManagerWindow.ToNavigablePath"/> seam), while paths without a native
/// mount pass through unchanged.
/// </summary>
public sealed class VolumeNavigationTests
{
    [AvaloniaFact]
    public async Task A_computer_volume_path_translates_to_its_native_file_mount()
    {
        var vfs = new VfsRoot();
        vfs.Register(new LocalFsProvider());
        vfs.Register(new ComputerProvider());
        var controller = new FileManagerController(vfs, new FileOperationService(vfs, new DefaultConflictHandler()));
        var win = new FileManagerWindow();
        win.SetVfsRoot(vfs);
        win.SetController(controller);
        win.SetSearchService(new SearchService(vfs));

        IVfsNode? volume = null;
        await foreach (var n in vfs.EnumerateAsync(
            VfsPath.Root("computer"), new EnumerateOptions(), CancellationToken.None))
        {
            volume = n;
            break;
        }
        Assert.NotNull(volume); // every machine has at least one mounted volume

        var navigable = win.ToNavigablePath(volume!.Path);

        Assert.Equal(new VfsPath("file", volume.Volume!.MountPath), navigable);

        // The computer root has no native mount and passes through unchanged.
        Assert.Equal(VfsPath.Root("computer"), win.ToNavigablePath(VfsPath.Root("computer")));

        // file:// paths are never rewritten.
        var filePath = new VfsPath("file", Path.GetTempPath());
        Assert.Equal(filePath, win.ToNavigablePath(filePath));
    }

    [AvaloniaFact]
    public void A_folder_inside_the_trash_stays_in_the_trash_scheme()
    {
        // bevel-cq7g: TrashProvider.ResolveEffectivePath returns the native path for every non-root
        // item (open/preview/copy need it), but that must NOT reroute navigation — a folder inside the
        // Trash browses as trash:// with trash capabilities (no Rename), not file:// with full caps.
        var trashDir = Path.Combine(Path.GetTempPath(), $"bevel-trash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(trashDir, "sub"));
        try
        {
            var vfs = new VfsRoot();
            vfs.Register(new LocalFsProvider());
            vfs.Register(new TrashProvider(trashDir));
            var win = new FileManagerWindow();
            win.SetVfsRoot(vfs);

            var sub = new VfsPath("trash", "sub");
            Assert.Equal(sub, win.ToNavigablePath(sub));

            // Sanity: the trash provider really does have an effective native path for it — the bug was
            // that navigation used to follow it.
            Assert.NotNull(vfs.GetProvider(sub).ResolveEffectivePath(sub));
        }
        finally
        {
            try { Directory.Delete(trashDir, true); } catch { }
        }
    }
}
