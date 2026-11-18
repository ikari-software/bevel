using System.Collections;
using System.Collections.Specialized;
using System.Reflection;
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
/// the UI thread; yields the UI thread between batches; and a newer navigation's listing can never be
/// overwritten by an older one's. The provider is synchronous (like LocalFsProvider), so if a consumer
/// iterated it directly the recorded thread would be the UI thread.
/// </summary>
public sealed class FilerEnumerationThreadTests
{
    private const BindingFlags NI = BindingFlags.NonPublic | BindingFlags.Instance;

    private static readonly VfsPath Folder = RecordingVfsProvider.Path("/top");
    private static readonly VfsPath SlowFolder = RecordingVfsProvider.Path("/slow");

    private static (FileManagerWindow win, RecordingVfsProvider provider, VfsRoot root) BuildWindow()
    {
        var provider = new RecordingVfsProvider();
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

    private static ItemView ItemViewOf(FileManagerWindow win)
        => (ItemView)typeof(FileManagerWindow).GetField("ItemView", NI)!.GetValue(win)!;

    private static string[] Rows(FileManagerWindow win)
        => (ItemViewOf(win).ItemsControl.ItemsSource as IEnumerable)?
            .Cast<ItemViewModel>().Select(v => v.DisplayName).ToArray() ?? Array.Empty<string>();

    private static INotifyCollectionChanged RowSource(FileManagerWindow win)
        => (INotifyCollectionChanged)ItemViewOf(win).ItemsControl.ItemsSource!;

    private static string StatusText(FileManagerWindow win)
    {
        var statusBar = typeof(FileManagerWindow).GetField("StatusBar", NI)!.GetValue(win)!;
        var text = (TextBlock)statusBar.GetType().GetField("ObjectCountText", NI)!.GetValue(statusBar)!;
        return text.Text ?? "";
    }

    // ── off the UI thread ─────────────────────────────────────────────────

    [AvaloniaFact]
    public void LoadDirectory_enumerates_off_the_UI_thread()
    {
        var (win, provider, _) = BuildWindow();
        var ui = Environment.CurrentManagedThreadId;

        PumpUntil(Invoke(win, "LoadDirectory", Folder));

        Assert.NotEmpty(provider.Threads);
        Assert.DoesNotContain(ui, provider.Threads);
        Assert.Equal(RecordingVfsProvider.Names("/top").Order(StringComparer.OrdinalIgnoreCase), Rows(win).Order(StringComparer.OrdinalIgnoreCase));
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
        Assert.Equal(RecordingVfsProvider.Names("/top").Length, Rows(win).Length);
    }

    [AvaloniaFact]
    public void Window_tree_expansion_enumerates_off_the_UI_thread()
    {
        var (win, provider, _) = BuildWindow();
        var ui = Environment.CurrentManagedThreadId;
        var node = new TreeViewItem { Tag = Folder };
        node.Items.Add(FolderTreeLoader.Placeholder());

        PumpUntil(win.ExpandTreeNodeAsync(node));

        Assert.NotEmpty(provider.Threads);
        Assert.DoesNotContain(ui, provider.Threads);
        var headers = node.Items.OfType<TreeViewItem>().Select(t => t.Header as string).ToList();
        Assert.Equal(RecordingVfsProvider.FolderNames("/top"), headers);   // folders only, provider order
    }

    [AvaloniaFact]
    public void Folder_picker_expansion_enumerates_off_the_UI_thread()
    {
        var provider = new RecordingVfsProvider();
        var root = new VfsRoot();
        root.Register(provider);
        var ui = Environment.CurrentManagedThreadId;
        var dialog = new FolderPickerDialog(root, Folder);
        var top = (TreeViewItem)dialog.Tree.Items.Single()!;

        PumpUntil(dialog.ExpandTreeNodeAsync(top));

        Assert.NotEmpty(provider.Threads);
        Assert.DoesNotContain(ui, provider.Threads);
        var headers = top.Items.OfType<TreeViewItem>().Select(t => t.Header as string).ToList();
        Assert.Equal(RecordingVfsProvider.FolderNames("/top"), headers);
    }

    // ── the UI thread gets a turn between batches (review #1) ──────────────
    //
    // "/many" is exactly four batches, which all fit the reader's channel: waiting for the provider to
    // finish BEFORE pumping guarantees the reader is ahead of the UI. A marker job posted at Background
    // priority while the first batch is painted must then run before the last batch is painted. Without
    // a real dispatcher yield per batch, every buffered batch is painted inside one job and the marker
    // only runs once the whole listing has landed.

    [AvaloniaFact]
    public void LoadDirectory_yields_to_the_dispatcher_between_batches_when_the_reader_is_ahead()
    {
        var (win, provider, _) = BuildWindow();
        var load = Invoke(win, "LoadDirectory", RecordingVfsProvider.Path("/many"));   // resets the list synchronously
        int? rowsWhenMarkerRan = null;
        var posted = false;
        RowSource(win).CollectionChanged += (_, e) =>
        {
            if (posted || e.Action != NotifyCollectionChangedAction.Add) return;
            posted = true;
            Dispatcher.UIThread.Post(() => rowsWhenMarkerRan = Rows(win).Length, DispatcherPriority.Background);
        };
        provider.WaitEnded("/many");

        PumpUntil(load);

        Assert.Equal(128, Rows(win).Length);
        Assert.NotNull(rowsWhenMarkerRan);
        Assert.True(rowsWhenMarkerRan < 128, $"marker ran only after {rowsWhenMarkerRan} rows: the batches were painted in one dispatcher job");
    }

    [AvaloniaFact]
    public void Tree_expansion_yields_to_the_dispatcher_between_batches_when_the_reader_is_ahead()
    {
        var (win, provider, _) = BuildWindow();
        var node = new TreeViewItem { Tag = RecordingVfsProvider.Path("/manydirs") };
        node.Items.Add(FolderTreeLoader.Placeholder());
        var load = win.ExpandTreeNodeAsync(node);   // clears the placeholder synchronously
        int? childrenWhenMarkerRan = null;
        var posted = false;
        ((INotifyCollectionChanged)node.Items).CollectionChanged += (_, e) =>
        {
            if (posted || e.Action != NotifyCollectionChangedAction.Add) return;
            posted = true;
            Dispatcher.UIThread.Post(() => childrenWhenMarkerRan = node.Items.Count, DispatcherPriority.Background);
        };
        provider.WaitEnded("/manydirs");

        PumpUntil(load);

        Assert.Equal(128, node.Items.Count);
        Assert.NotNull(childrenWhenMarkerRan);
        Assert.True(childrenWhenMarkerRan < 128, $"marker ran only after {childrenWhenMarkerRan} children: the batches were added in one dispatcher job");
    }

    // ── a superseded listing never paints (review #3, #4) ──────────────────

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
        Assert.Equal(RecordingVfsProvider.Names("/top").Length, rows.Length);
    }

    [AvaloniaFact]
    public void A_batch_already_handed_to_the_list_when_a_newer_navigation_starts_is_discarded()
    {
        // By construction: both "/stale" batches are in the channel before the UI consumes any. While
        // the FIRST is painted, a Background job queues the newer navigation; the reader then hands the
        // SECOND batch over (its token is still live), and the newer navigation runs before that batch is
        // painted. Only the consumer's own post-await token check stands between it and the new list.
        var (win, provider, _) = BuildWindow();
        var stale = Invoke(win, "LoadDirectory", RecordingVfsProvider.Path("/stale"));
        Task? fresh = null;
        var posted = false;
        RowSource(win).CollectionChanged += (_, e) =>
        {
            if (posted || e.Action != NotifyCollectionChangedAction.Add) return;
            posted = true;
            Dispatcher.UIThread.Post(() => fresh = Invoke(win, "LoadDirectory", Folder), DispatcherPriority.Background);
        };
        provider.WaitEnded("/stale");

        PumpUntil(stale);
        PumpUntil(() => fresh is not null);
        PumpUntil(fresh!);

        var rows = Rows(win);
        Assert.DoesNotContain(rows, r => r.StartsWith("stale", StringComparison.Ordinal));
        Assert.Equal(RecordingVfsProvider.Names("/top").Length, rows.Length);
    }

    [AvaloniaFact]
    public void A_reload_overtaken_by_a_newer_load_does_not_reconcile_its_listing()
    {
        var (win, provider, _) = BuildWindow();
        PumpUntil(Invoke(win, "LoadDirectory", Folder));   // HasItems, so the reload takes the reconcile path

        var stale = Invoke(win, "ReloadDifferential", SlowFolder);   // held at the provider's gate
        var fresh = Invoke(win, "LoadDirectory", Folder);
        PumpUntil(fresh);
        provider.ReleaseSlow();
        PumpUntil(stale);

        var rows = Rows(win);
        Assert.DoesNotContain(rows, r => r.StartsWith("slow", StringComparison.Ordinal));
        Assert.Equal(RecordingVfsProvider.Names("/top").Length, rows.Length);
    }

    [AvaloniaFact]
    public void A_superseded_reload_whose_folder_fails_shows_no_error()
    {
        var (win, provider, _) = BuildWindow();
        PumpUntil(Invoke(win, "LoadDirectory", Folder));

        var stale = Invoke(win, "ReloadDifferential", RecordingVfsProvider.Path("/failslow"));
        var fresh = Invoke(win, "LoadDirectory", Folder);
        PumpUntil(fresh);
        provider.ReleaseSlow();   // the superseded read now fails
        PumpUntil(stale);

        Assert.DoesNotContain("no longer exists", StatusText(win), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RecordingVfsProvider.Names("/top").Length, Rows(win).Length);
    }

    [AvaloniaFact]
    public void A_superseded_load_whose_folder_fails_shows_no_error()
    {
        var (win, provider, _) = BuildWindow();

        var stale = Invoke(win, "LoadDirectory", RecordingVfsProvider.Path("/failslow"));
        var fresh = Invoke(win, "LoadDirectory", Folder);
        PumpUntil(fresh);
        provider.ReleaseSlow();
        PumpUntil(stale);

        Assert.DoesNotContain("no longer exists", StatusText(win), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RecordingVfsProvider.Names("/top").Length, Rows(win).Length);
    }

    // ── reveal survives a reload that supersedes the load (review #2) ──────

    [AvaloniaFact]
    public void A_reload_that_supersedes_a_load_still_applies_the_pending_reveal()
    {
        // F5 / a watcher refresh landing while the first listing is still streaming cancels that load
        // before its tail applies the reveal; the reload that replaces it must apply it instead.
        var (win, provider, _) = BuildWindow();
        var gated = RecordingVfsProvider.Path("/gated");
        var target = VfsPath.Combine(gated, "gated-bfile010.txt");
        win.SelectAfterLoad(new[] { target });

        var load = Invoke(win, "LoadDirectory", gated);
        PumpUntil(() => Rows(win).Length >= 32);   // first batch painted; the read is now held at the gate
        var reload = Invoke(win, "ReloadDifferential", gated);   // cancels the load
        provider.ReleaseGate();
        PumpUntil(reload);
        PumpUntil(load);

        Assert.Equal(64, Rows(win).Length);
        Assert.Equal(new[] { target }, ItemViewOf(win).SelectedItems.Select(v => v.Path));
    }

    [AvaloniaFact]
    public void Expanding_a_node_twice_while_it_loads_does_not_duplicate_children()
    {
        var (win, _, _) = BuildWindow();
        var node = new TreeViewItem { Tag = Folder };
        node.Items.Add(FolderTreeLoader.Placeholder());

        var first = win.ExpandTreeNodeAsync(node);
        var second = win.ExpandTreeNodeAsync(node);   // collapse + re-expand before the first landed
        PumpUntil(Task.WhenAll(first, second));

        Assert.Equal(RecordingVfsProvider.FolderNames("/top"), node.Items.OfType<TreeViewItem>().Select(t => t.Header as string));
    }
}
