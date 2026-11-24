using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Core;
using Bevel.Core.Vfs;
using Bevel.FileManager;
using Bevel.FileManager.Components;
using Bevel.FileManager.FileOperations;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>bevel-ze3o: a folder's view mode + sort persist per folder — set Details/Size-descending in
/// folder A, visit B, navigate back to A and A is restored (B untouched), across a "restart" (fresh
/// window over the same settings DB). Isolated settings dir; real temp folders.</summary>
public sealed class FolderViewStateTests : IDisposable
{
    private readonly string _dirA;
    private readonly string _dirB;
    private readonly string _cfg;

    public FolderViewStateTests()
    {
        _dirA = Path.Combine(Path.GetTempPath(), $"bevel-vsA-{Guid.NewGuid():N}");
        _dirB = Path.Combine(Path.GetTempPath(), $"bevel-vsB-{Guid.NewGuid():N}");
        _cfg = Path.Combine(Path.GetTempPath(), $"bevel-vscfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dirA);
        Directory.CreateDirectory(_dirB);
        Directory.CreateDirectory(_cfg);
        File.WriteAllText(Path.Combine(_dirA, "a.txt"), "x");
        File.WriteAllText(Path.Combine(_dirB, "b.txt"), "x");
    }

    public void Dispose()
    {
        foreach (var d in new[] { _dirA, _dirB, _cfg })
            try { Directory.Delete(d, true); } catch { }
    }

    private static void Pump(int ms = 800)
    {
        var end = Environment.TickCount + ms;
        while (Environment.TickCount < end) { Dispatcher.UIThread.RunJobs(); System.Threading.Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
    }

    private static ItemView ViewOf(FileManagerWindow win) =>
        (ItemView)win.GetType().GetField("ItemView", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(win)!;

    private FileManagerWindow NewWindow(SettingsService settings)
    {
        var root = new VfsRoot();
        root.Register(new LocalFsProvider());
        var win = new FileManagerWindow();
        win.SetVfsRoot(root);
        win.SetSettingsService(settings);
        win.SetController(new FileManagerController(root, new FileOperationService(root, new DefaultConflictHandler())));
        win.Show();
        return win;
    }

    [AvaloniaFact]
    public async Task Navigating_back_to_a_folder_restores_its_view_and_sort()
    {
        var settings = new SettingsService(_cfg);
        await settings.LoadAsync();

        var win = NewWindow(settings);
        var view = ViewOf(win);
        var pathA = new VfsPath("file", _dirA);
        var pathB = new VfsPath("file", _dirB);

        win.ActiveController!.NavigateTo(pathA);
        Pump();
        Assert.Equal(ViewMode.LargeIcons, view.ViewMode);          // default before any change

        // User picks Details + sorts by Size descending in A.
        win.SetViewMode(ViewMode.Details);
        view.SetSort("Size", false);
        Pump();
        Assert.Equal(("Size", false), view.SortState);

        // The change was persisted for A only.
        var stored = settings.Current.FilerFolderViews;
        Assert.Single(stored);
        Assert.Equal($"Details|Size|D|{pathA}", stored[0]);

        // Visit B: gets the default view, and no entry is written for B by navigation alone.
        win.ActiveController!.NavigateTo(pathB);
        Pump();
        Assert.Equal(ViewMode.LargeIcons, view.ViewMode);
        Assert.Equal(("Name", true), view.SortState);
        Assert.Single(settings.Current.FilerFolderViews);

        // Back to A: restored without any user action.
        win.ActiveController!.NavigateTo(pathA);
        Pump();
        Assert.Equal(ViewMode.Details, view.ViewMode);
        Assert.Equal(("Size", false), view.SortState);

        // "Restart": a fresh window over the same settings DB lands on A's stored state too.
        win.ActiveController!.NavigateTo(pathB);
        Pump();
        var win2 = NewWindow(settings);
        win2.ActiveController!.NavigateTo(pathA);
        Pump();
        Assert.Equal(ViewMode.Details, ViewOf(win2).ViewMode);
        Assert.Equal(("Size", false), ViewOf(win2).SortState);
    }

    [AvaloniaFact]
    public async Task SetSort_with_the_same_state_is_a_no_op_and_raises_no_event()
    {
        var settings = new SettingsService(_cfg);
        await settings.LoadAsync();
        var win = NewWindow(settings);
        var view = ViewOf(win);
        win.ActiveController!.NavigateTo(new VfsPath("file", _dirA));
        Pump();

        var raised = 0;
        view.SortChanged += () => raised++;
        view.SetSort("Name", true);          // already the default → no event, nothing persisted
        Assert.Equal(0, raised);
        Pump();
        Assert.Empty(settings.Current.FilerFolderViews);
        view.SetSort("Size", false);         // real change → one event, one persisted entry
        Assert.Equal(1, raised);
        Pump();
        Assert.Single(settings.Current.FilerFolderViews);
    }
}
