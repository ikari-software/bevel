using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.FileManager.FileOperations;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// Headless tests for the per-window / per-tab file-operation cancel wiring (bevel-c8g /
/// bevel-70g): closing a tab or the window disposes that tab's controller — cancelling its
/// running operations and tearing down its FileOperationService — without disturbing any other
/// tab's or window's work, and the ProgressDialog renders only its own operation's progress
/// events. Operations are parked deterministically via <see cref="ParkingConflictHandler"/> —
/// released only by cancellation, never by timing.
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

    // ── tab close ──────────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task Closing_a_tab_cancels_its_in_flight_operation_and_disposes_its_service()
    {
        var (win, controller, handler) = BuildWindow();

        var task = controller.CopyAsync(new[] { P("a.txt") }, P("dst"));
        WaitUntil(() => handler.Reached.Task.IsCompleted);

        win.NewTab();                                  // tab 1 becomes active; tab 0's op keeps running
        Assert.False(task.IsCompleted);

        win.CloseTabAt(0);                             // closing the op's tab disposes its controller
        WaitUntil(() => task.IsCompleted);

        var result = await task;
        Assert.Contains(result.ItemResults, r => r.Status == FileItemResultStatus.Cancelled);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_dir, "dst", "a.txt"))); // never overwritten

        // The closed tab's service is gone: further mutations on its controller throw.
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => controller.CopyAsync(new[] { P("a.txt") }, P("dst")));
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
