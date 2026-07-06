using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
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

        var handler = typeof(FileManagerWindow).GetMethod("OnTreeNodeExpanded", BindingFlags.NonPublic | BindingFlags.Instance)!;
        handler.Invoke(win, new object?[] { item, new RoutedEventArgs() });

        // The enumeration streams into item.Items; pump until the placeholder is gone.
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000
               && item.Items.Count == 1
               && item.Items[0] is TreeViewItem p && (p.Header as string) == "...")
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();

        return item.Items.OfType<TreeViewItem>().Select(t => t.Header as string ?? "").ToList();
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
