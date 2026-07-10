using System.Collections;
using System.Diagnostics;
using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.FileManager.FileOperations;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// Headless tests for the per-window / per-tab file-operation cancel wiring (bevel-c8g): the
/// window tracks each running operation's <see cref="IFileOpHandle"/> on the tab that started
/// it, closing that tab or the window cancels only those operations, and the ProgressDialog
/// renders only its own operation's progress events. Operations are parked deterministically
/// via <see cref="ParkingConflictHandler"/> — released only by cancellation, never by timing.
/// </summary>
public sealed class FileOpCancelWiringTests : IDisposable
{
    private readonly string _dir;

    public FileOpCancelWiringTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"bevel-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "dst"));
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "new");
        File.WriteAllText(Path.Combine(_dir, "dst", "a.txt"), "old"); // forces conflict → park
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private VfsPath P(params string[] parts) => new("file", Path.Combine(new[] { _dir }.Concat(parts).ToArray()));

    // ── helpers (mirrors TabbedBrowsingTests) ──────────────────────────────
    private const BindingFlags NI = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
    private static object? Field(object o, string name) => o.GetType().GetField(name, NI)!.GetValue(o);
    private static object? Prop(object o, string name) => o.GetType().GetProperty(name, NI)!.GetValue(o);

    private (FileManagerWindow win, FileManagerController controller, ParkingConflictHandler handler) BuildWindow()
    {
        var vfs = new VfsRoot();
        vfs.Register(new LocalFsProvider());
        var handler = new ParkingConflictHandler();
        var controller = new FileManagerController(vfs, new FileOperationService(vfs, handler));
        var win = new FileManagerWindow();
        win.SetVfsRoot(vfs);
        win.SetController(controller);
        win.SetSearchService(new SearchService(vfs));
        // Keep the modal ProgressDialog out of the test: the op stays in the pre-dialog window.
        win.ProgressDialogDelayMs = 600_000;
        controller.NavigateTo(new VfsPath("file", _dir));
        Dispatcher.UIThread.RunJobs();
        return (win, controller, handler);
    }

    private static void WaitUntil(Func<bool> cond, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms && !cond()) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The tab's tracked in-flight handles, via reflection (TabSession is private).</summary>
    private static IList InFlightOps(FileManagerWindow win, int tabIndex)
    {
        var tabs = (IList)Field(win, "_tabs")!;
        return (IList)Prop(tabs[tabIndex]!, "InFlightOps")!;
    }

    // ── tab close ──────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Closing_a_tab_cancels_that_tabs_in_flight_operation()
    {
        var (win, controller, handler) = BuildWindow();

        var task = controller.CopyAsync(new[] { P("a.txt") }, P("dst"));
        WaitUntil(() => handler.Reached.Task.IsCompleted);
        Assert.Single(InFlightOps(win, 0));           // tracked on the tab that started it

        win.NewTab();                                  // tab 1 becomes active; tab 0's op keeps running
        Assert.False(task.IsCompleted);

        win.CloseTabAt(0);                             // closing the op's tab cancels it
        WaitUntil(() => task.IsCompleted);

        var result = task.Result;
        Assert.Contains(result.ItemResults, r => r.Status == FileItemResultStatus.Cancelled);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_dir, "dst", "a.txt"))); // never overwritten
    }

    // ── window close ───────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Closing_the_window_cancels_all_of_its_in_flight_operations()
    {
        var (win, controller, handler) = BuildWindow();
        win.Show();

        var task = controller.CopyAsync(new[] { P("a.txt") }, P("dst"));
        WaitUntil(() => handler.Reached.Task.IsCompleted);
        Assert.False(task.IsCompleted);

        win.Close();
        WaitUntil(() => task.IsCompleted);

        var result = task.Result;
        Assert.Contains(result.ItemResults, r => r.Status == FileItemResultStatus.Cancelled);
    }

    // ── completion untracks ────────────────────────────────────────────────

    [AvaloniaFact]
    public void A_completed_operation_is_removed_from_the_tabs_tracking_list()
    {
        var (win, controller, handler) = BuildWindow();

        var task = controller.CopyAsync(new[] { P("a.txt") }, P("dst"));
        WaitUntil(() => handler.Reached.Task.IsCompleted);
        Assert.Single(InFlightOps(win, 0));

        ((IFileOpHandle)InFlightOps(win, 0)[0]!).Cancel(); // any cancel path completes the op
        WaitUntil(() => task.IsCompleted && InFlightOps(win, 0).Count == 0);

        Assert.Empty(InFlightOps(win, 0));                 // runner's finally untracked it
    }

    // ── ProgressDialog renders only its own operation ──────────────────────

    [AvaloniaFact]
    public void ProgressDialog_ignores_progress_events_from_other_operations()
    {
        var request = new CopyRequest
        {
            Sources = new[] { P("a.txt") },
            Destination = P("dst"),
            Timestamp = DateTimeOffset.UtcNow,
        };
        var dlg = new ProgressDialog(request, "a.txt", "dst") { OperationId = "op-mine" };

        FileOpProgress At(string opId, long bytes) => new()
        {
            OperationId = opId, Status = FileOpStatus.Running,
            CurrentFileIndex = 0, TotalFiles = 1,
            BytesTransferred = bytes, TotalBytes = 100,
            CurrentFileName = "a.txt",
        };

        dlg.OnProgress(At("op-other", 75));   // a concurrent tab/window's transfer
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, dlg.ProgressBar.Value);

        dlg.OnProgress(At("op-mine", 50));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(50, dlg.ProgressBar.Value);
    }
}
