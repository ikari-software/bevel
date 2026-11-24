using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bevel.Core.Vfs;
using Bevel.FileManager;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// bevel-ee9: tree expansion must bind to each window's OWN VfsRoot (instance state),
/// not a process-global VfsRootLocator that breaks with more than one window.
/// </summary>
public sealed class FileManagerWindowTreeTests : IDisposable
{
    private readonly string _dirA;
    private readonly string _dirB;

    public FileManagerWindowTreeTests()
    {
        _dirA = Path.Combine(Path.GetTempPath(), $"bevel-treeA-{Guid.NewGuid():N}");
        _dirB = Path.Combine(Path.GetTempPath(), $"bevel-treeB-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dirA, "subA"));
        Directory.CreateDirectory(Path.Combine(_dirB, "subB"));
    }

    public void Dispose()
    {
        foreach (var d in new[] { _dirA, _dirB })
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
    }

    private static void SetPrivate(object target, string field, object? value)
        => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    private static List<string> Expand(FileManagerWindow win, string dir)
    {
        var item = new TreeViewItem { Tag = new VfsPath("file", dir) };
        item.Items.Add(new TreeViewItem { Header = "..." });

        // The listing is read on the pool and lands in batches (bevel-so27): pump until the expansion
        // itself reports done, not until the placeholder is merely gone.
        DispatcherPump.PumpUntil(win.ExpandTreeNodeAsync(item));

        return item.Items.OfType<TreeViewItem>().Select(t => t.Header as string ?? "").ToList();
    }

    [AvaloniaFact]
    public void Selecting_the_network_node_loads_without_an_unknown_scheme()
    {
        // bevel-lkib: the "net" scheme is registered (NetworkProvider), so the tree node is browsable
        // and a network volume navigates to its file:// mount via ResolveNavigationTarget (bevel-cq7g).
        var vfs = new VfsRoot();
        vfs.Register(new LocalFsProvider());
        vfs.Register(new ComputerProvider());
        vfs.Register(new TrashProvider());
        vfs.Register(new NetworkProvider(() => new[] { ("photos", "/Volumes/photos") }));
        Assert.Contains("net", vfs.Schemes);

        var win = new FileManagerWindow();
        SetPrivate(win, "_vfsRoot", vfs);
        SetPrivate(win, "_treeCts", new CancellationTokenSource());

        var volumePath = VfsPath.Combine(VfsPath.Root("net"), Uri.EscapeDataString("/Volumes/photos"));
        Assert.Equal(new VfsPath("file", "/Volumes/photos"), win.ToNavigablePath(volumePath));
        Assert.Equal(VfsPath.Root("net"), win.ToNavigablePath(VfsPath.Root("net")));
    }

    [AvaloniaFact]
    public void Each_window_expands_against_its_own_root()
    {
        var rootA = new VfsRoot();
        rootA.Register(new LocalFsProvider());
        var rootB = new VfsRoot();
        rootB.Register(new LocalFsProvider());

        var winA = new FileManagerWindow();
        SetPrivate(winA, "_vfsRoot", rootA);
        SetPrivate(winA, "_treeCts", new CancellationTokenSource());

        var winB = new FileManagerWindow();
        SetPrivate(winB, "_vfsRoot", rootB);
        SetPrivate(winB, "_treeCts", new CancellationTokenSource());

        var childrenA = Expand(winA, _dirA);
        var childrenB = Expand(winB, _dirB);

        // Each window enumerated only its own directory tree.
        Assert.Contains("subA", childrenA);
        Assert.DoesNotContain("subB", childrenA);
        Assert.Contains("subB", childrenB);
        Assert.DoesNotContain("subA", childrenB);
    }
}
