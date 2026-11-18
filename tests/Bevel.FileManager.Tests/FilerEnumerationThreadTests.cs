using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.FileManager.Components;
using Xunit;
using static Bevel.FileManager.Tests.DispatcherPump;

namespace Bevel.FileManager.Tests;

/// <summary>
/// bevel-so27: every Filer consumer of a folder listing — the item list's load and differential
/// reload, the window's folder tree and the Browse-For-Folder tree — enumerates on the pool, never on
/// the UI thread, and a newer navigation's listing can never be overwritten by an older one's.
/// The provider here is synchronous (like LocalFsProvider), so if a consumer iterated it directly the
/// recorded thread would be the UI thread.
/// </summary>
public sealed class FilerEnumerationThreadTests
{
    private const BindingFlags NI = BindingFlags.NonPublic | BindingFlags.Instance;

    private static readonly VfsPath Folder = new(RecordingProvider.SchemeName, "/top");
    private static readonly VfsPath SlowFolder = new(RecordingProvider.SchemeName, "/slow");

    private static (FileManagerWindow win, RecordingProvider provider, VfsRoot root) BuildWindow()
    {
        var provider = new RecordingProvider();
        var root = new VfsRoot();
        root.Register(provider);
        var win = new FileManagerWindow();
        win.SetVfsRoot(root);
        win.Show();
        Dispatcher.UIThread.RunJobs();
        return (win, provider, root);
    }

    private static Task Invoke(FileManagerWindow win, string method, params object?[] args)
        => (Task)typeof(FileManagerWindow).GetMethod(method, NI)!.Invoke(win, args)!;

    private static string[] Rows(FileManagerWindow win)
    {
        var iv = (ItemView)typeof(FileManagerWindow).GetField("ItemView", NI)!.GetValue(win)!;
        return (iv.ItemsControl.ItemsSource as IEnumerable)?
            .Cast<ItemViewModel>().Select(v => v.DisplayName).ToArray() ?? Array.Empty<string>();
    }

    [AvaloniaFact]
    public void LoadDirectory_enumerates_off_the_UI_thread()
    {
        var (win, provider, _) = BuildWindow();
        var ui = Environment.CurrentManagedThreadId;

        PumpUntil(Invoke(win, "LoadDirectory", Folder));

        Assert.NotEmpty(provider.Threads);
        Assert.DoesNotContain(ui, provider.Threads);
        Assert.Equal(RecordingProvider.Names("/top").OrderBy(n => n, StringComparer.OrdinalIgnoreCase), Rows(win).Order(StringComparer.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
    public void ReloadDifferential_enumerates_off_the_UI_thread()
    {
        var (win, provider, _) = BuildWindow();
        var ui = Environment.CurrentManagedThreadId;
        PumpUntil(Invoke(win, "LoadDirectory", Folder));
        provider.Threads.Clear();

        PumpUntil(Invoke(win, "ReloadDifferential", Folder));

        Assert.NotEmpty(provider.Threads);
        Assert.DoesNotContain(ui, provider.Threads);
        Assert.Equal(RecordingProvider.Names("/top").Length, Rows(win).Length);
    }

    [AvaloniaFact]
    public void Window_tree_expansion_enumerates_off_the_UI_thread()
    {
        var (win, provider, _) = BuildWindow();
        var ui = Environment.CurrentManagedThreadId;
        var node = new TreeViewItem { Tag = Folder };
        node.Items.Add(new TreeViewItem { Header = "..." });

        PumpUntil(win.ExpandTreeNodeAsync(node));

        Assert.NotEmpty(provider.Threads);
        Assert.DoesNotContain(ui, provider.Threads);
        var headers = node.Items.OfType<TreeViewItem>().Select(t => t.Header as string).ToList();
        Assert.Equal(RecordingProvider.FolderNames("/top"), headers);   // folders only, provider order
    }

    [AvaloniaFact]
    public void Folder_picker_expansion_enumerates_off_the_UI_thread()
    {
        var provider = new RecordingProvider();
        var root = new VfsRoot();
        root.Register(provider);
        var ui = Environment.CurrentManagedThreadId;
        var dialog = new FolderPickerDialog(root, Folder);
        var top = (TreeViewItem)dialog.Tree.Items.Single()!;

        PumpUntil(dialog.ExpandTreeNodeAsync(top));

        Assert.NotEmpty(provider.Threads);
        Assert.DoesNotContain(ui, provider.Threads);
        var headers = top.Items.OfType<TreeViewItem>().Select(t => t.Header as string).ToList();
        Assert.Equal(RecordingProvider.FolderNames("/top"), headers);
    }

    [AvaloniaFact]
    public void Expanding_a_node_twice_while_it_loads_does_not_duplicate_children()
    {
        var (win, provider, _) = BuildWindow();
        var node = new TreeViewItem { Tag = Folder };
        node.Items.Add(new TreeViewItem { Header = "..." });

        var first = win.ExpandTreeNodeAsync(node);
        var second = win.ExpandTreeNodeAsync(node);   // collapse + re-expand before the first landed
        PumpUntil(Task.WhenAll(first, second));

        Assert.Equal(RecordingProvider.FolderNames("/top"), node.Items.OfType<TreeViewItem>().Select(t => t.Header as string));
    }

    [AvaloniaFact]
    public void A_newer_navigation_discards_the_older_listing()
    {
        var (win, provider, _) = BuildWindow();

        var stale = Invoke(win, "LoadDirectory", SlowFolder);   // blocks on the pool until released
        var fresh = Invoke(win, "LoadDirectory", Folder);
        PumpUntil(fresh);
        provider.ReleaseSlow();
        PumpUntil(stale);

        var rows = Rows(win);
        Assert.DoesNotContain(rows, r => r.StartsWith("slow", StringComparison.Ordinal));
        Assert.Equal(RecordingProvider.Names("/top").Length, rows.Length);
    }

    /// <summary>Synchronous provider recording the thread every entry is produced on. "/slow" blocks
    /// (on whatever thread enumerates it) until <see cref="ReleaseSlow"/>.</summary>
    private sealed class RecordingProvider : IVfsProvider
    {
        public const string SchemeName = "rec";
        public string Scheme => SchemeName;
        private readonly ManualResetEventSlim _slowGate = new(false);
        public List<int> Threads { get; } = new();

        public void ReleaseSlow() => _slowGate.Set();

        public static string[] FolderNames(string folder) => Enumerable.Range(0, 40).Select(i => $"{Prefix(folder)}dir{i:D2}").ToArray();
        public static string[] Names(string folder)
            => FolderNames(folder).Concat(Enumerable.Range(0, 60).Select(i => $"{Prefix(folder)}file{i:D2}.txt")).ToArray();
        private static string Prefix(string folder) => folder == "/slow" ? "slow" : "";

        public async IAsyncEnumerable<IVfsNode> EnumerateAsync(
            VfsPath folder, EnumerateOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            if (folder.Value == "/slow") _slowGate.Wait(TimeSpan.FromSeconds(10));
            foreach (var name in Names(folder.Value))
            {
                ct.ThrowIfCancellationRequested();
                lock (Threads) Threads.Add(Environment.CurrentManagedThreadId);
                var isDir = !name.EndsWith(".txt", StringComparison.Ordinal);
                yield return isDir
                    ? VirtualNode.Folder(VfsPath.Combine(folder, name), name)
                    : new VirtualNode
                    {
                        Path = VfsPath.Combine(folder, name), DisplayName = name, Kind = VfsNodeKind.File,
                        Size = 1, TypeDescription = "Text Document", IconKey = IconKey.File(".txt"),
                    };
            }
            await Task.CompletedTask;
        }

        public ValueTask<IVfsNode> ResolveAsync(VfsPath path, CancellationToken ct)
            => ValueTask.FromResult<IVfsNode>(VirtualNode.Folder(path, path.FileName));
        public ValueTask<Stream> OpenReadAsync(VfsPath file, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<IVfsMutator?> GetMutatorAsync(VfsPath folder, CancellationToken ct) => ValueTask.FromResult<IVfsMutator?>(null);
        public IDirectoryWatcher? CreateWatcher(VfsPath folder) => null;
        public NameValidationResult ValidateName(VfsPath folder, string proposedName) => NameValidationResult.Ok;
    }
}
