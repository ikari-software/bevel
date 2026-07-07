using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.FileManager;
using Bevel.FileManager.Components;
using Bevel.FileManager.FileOperations;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// Headless interaction tests for the four features wired into FileManagerWindow (Search,
/// History, New Window, Move/Copy To Folder). The service/dialog logic is unit-tested elsewhere;
/// these exercise the WINDOW-level glue by driving its handlers directly (the window isn't shown
/// — same reflection-driven approach as FileManagerWindowTreeTests).
/// </summary>
public sealed class FeatureInteractionTests : IDisposable
{
    private readonly string _dir;
    private readonly string _sub;

    public FeatureInteractionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"bevel-feat-{Guid.NewGuid():N}");
        _sub = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(_sub);
        File.WriteAllText(Path.Combine(_dir, "apple.txt"), "a");
        File.WriteAllText(Path.Combine(_dir, "banana.txt"), "b");
        File.WriteAllText(Path.Combine(_dir, "cherry.png"), "c");
        File.WriteAllText(Path.Combine(_sub, "deep-apple.txt"), "d"); // recursive-match target
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ── reflection helpers (window internals are private) ────────────────
    private const BindingFlags NI = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
    private static object? Field(object o, string name)
        => o.GetType().GetField(name, NI)!.GetValue(o);
    private static object? Call(object o, string name, params object?[] args)
        => o.GetType().GetMethod(name, NI)!.Invoke(o, args);

    private static (FileManagerWindow win, FileManagerController controller, VfsRoot vfs) BuildWindow(string dir)
    {
        var vfs = new VfsRoot();
        vfs.Register(new LocalFsProvider());
        var controller = new FileManagerController(vfs, new FileOperationService(vfs, new DefaultConflictHandler()));
        var win = new FileManagerWindow();
        win.SetVfsRoot(vfs);
        win.SetController(controller);
        win.SetSearchService(new SearchService(vfs));
        controller.NavigateTo(new VfsPath("file", dir));
        Pump();
        return (win, controller, vfs);
    }

    private static void Pump(int cycles = 10)
    {
        for (var i = 0; i < cycles; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
    }

    private static void WaitUntil(Func<bool> cond, int ms = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms && !cond()) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
    }

    private static string[] Rows(FileManagerWindow win)
    {
        var iv = (ItemView)Field(win, "ItemView")!;
        return (iv.ItemsControl.ItemsSource as IEnumerable)?
            .Cast<ItemViewModel>().Select(v => v.DisplayName).ToArray() ?? Array.Empty<string>();
    }

    // ── Search ───────────────────────────────────────────────────────────
    [AvaloniaFact]
    public void Search_populates_the_list_with_recursive_name_matches()
    {
        var (win, _, _) = BuildWindow(_dir);

        Call(win, "OnSearchRequested", null, "apple");           // the SearchPane.SearchRequested path
        WaitUntil(() => Rows(win).Contains("deep-apple.txt"));

        var rows = Rows(win);
        Assert.Contains("apple.txt", rows);
        Assert.Contains("deep-apple.txt", rows);                  // found one level deep
        Assert.DoesNotContain("banana.txt", rows);
        Assert.DoesNotContain("cherry.png", rows);
    }

    [AvaloniaFact]
    public void Toggling_the_search_pane_shows_hides_and_close_restores_the_listing()
    {
        var (win, _, _) = BuildWindow(_dir);
        var pane = (Control)Field(win, "SearchPane")!;

        Assert.False(pane.IsVisible);
        Call(win, "ToggleSearchPane");
        Assert.True(pane.IsVisible);

        Call(win, "OnSearchRequested", null, "apple");
        WaitUntil(() => Rows(win).Contains("apple.txt") && !Rows(win).Contains("banana.txt"));

        Call(win, "ToggleSearchPane");                            // close -> controller.Refresh reloads the folder
        Assert.False(pane.IsVisible);
        WaitUntil(() => Rows(win).Contains("banana.txt"));
        Assert.Contains("banana.txt", Rows(win));                 // full directory listing is back
    }

    // ── History ───────────────────────────────────────────────────────────
    [AvaloniaFact]
    public void History_flyout_lists_visited_folders_current_is_bold_and_clicking_jumps()
    {
        var (win, controller, _) = BuildWindow(_dir);
        controller.NavigateTo(new VfsPath("file", _sub));         // history now includes _dir then _sub
        Pump();

        var flyout = (MenuFlyout)Call(win, "BuildHistoryFlyout")!;
        var items = flyout.Items.Cast<MenuItem>().ToList();

        Assert.True(items.Count >= 2);
        Assert.Equal(FontWeight.Bold, items[^1].FontWeight);      // current directory (sub) is bold
        Assert.All(items.Take(items.Count - 1), i => Assert.Equal(FontWeight.Normal, i.FontWeight));

        var dirLabel = new VfsPath("file", _dir).FileName;
        var dirItem = items.Single(i => (string)i.Header! == dirLabel);
        dirItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal(_dir, controller.CurrentDirectory.Value);    // jumped back to _dir
    }

    // ── New Window ─────────────────────────────────────────────────────────
    [AvaloniaFact]
    public void NewWindow_raises_NewWindowRequested_with_the_current_directory()
    {
        var (win, _, _) = BuildWindow(_dir);

        VfsPath? captured = null;
        void Handler(VfsPath p) => captured = p;
        FileManagerWindow.NewWindowRequested += Handler;
        try
        {
            win.NewWindow();
            Assert.Equal(_dir, captured?.Value);
        }
        finally { FileManagerWindow.NewWindowRequested -= Handler; }
    }

    // ── Move/Copy To Folder ─────────────────────────────────────────────────
    [AvaloniaFact]
    public async Task FolderPicker_selection_plus_controller_move_relocates_the_file()
    {
        // The modal dialog can't be driven headlessly, so exercise the two pieces the window
        // glues: pick a destination via the dialog's (internal) selection logic, then run the
        // same controller.MoveAsync the window would run.
        var vfs = new VfsRoot();
        vfs.Register(new LocalFsProvider());
        var controller = new FileManagerController(vfs, new FileOperationService(vfs, new DefaultConflictHandler()));

        var dialog = new FolderPickerDialog(vfs, new VfsPath("file", _dir), "Move Items");
        var root = (TreeViewItem)dialog.Tree.Items.Single()!;
        dialog.OnTreeNodeExpanded(root, new RoutedEventArgs());
        WaitUntil(() => root.Items.OfType<TreeViewItem>().Any(t => (t.Header as string) == "sub"));

        var subNode = root.Items.OfType<TreeViewItem>().Single(t => (t.Header as string) == "sub");
        dialog.SelectNode(subNode);
        var dest = dialog.ConfirmSelection();
        Assert.NotNull(dest);

        var apple = new VfsPath("file", Path.Combine(_dir, "apple.txt"));
        await controller.MoveAsync(new[] { apple }, dest!.Value);

        Assert.True(File.Exists(Path.Combine(_sub, "apple.txt")));
        Assert.False(File.Exists(Path.Combine(_dir, "apple.txt")));
    }

    [AvaloniaFact]
    public async Task MoveToFolder_with_no_selection_is_a_safe_noop()
    {
        var (win, controller, _) = BuildWindow(_dir);

        // Nothing selected -> the handler returns before opening any (blocking) modal.
        await (Task)Call(win, "MoveOrCopyToFolderAsync", true)!;

        Assert.Empty(controller.Selection);
        Assert.True(File.Exists(Path.Combine(_dir, "apple.txt")));  // nothing moved
    }
}
