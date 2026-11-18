using System.Diagnostics;
using Avalonia.Threading;

namespace Bevel.FileManager.Tests;

/// <summary>
/// Drives the headless dispatcher until async UI work is done (bevel-so27). Folder enumeration now
/// runs on the pool and comes back to the UI thread one batch per dispatcher job, so a single
/// <c>Dispatcher.UIThread.RunJobs()</c> no longer means "the listing has landed". Pump until the
/// operation itself says it finished — with a deadline that FAILS the test instead of letting it
/// assert against a half-populated view.
/// </summary>
internal static class DispatcherPump
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    /// <summary>Pump until <paramref name="task"/> completes, then rethrow its failure, if any.</summary>
    public static void PumpUntil(Task task)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (task.IsCompleted) break;
            if (sw.Elapsed > Deadline) throw new TimeoutException($"UI task did not complete within {Deadline}.");
            // Returns early if the task finishes on the pool; otherwise its continuation is waiting for
            // the next RunJobs.
            try { task.Wait(TimeSpan.FromMilliseconds(5)); } catch (AggregateException) { /* rethrown below */ }
        }
        Dispatcher.UIThread.RunJobs();
        task.GetAwaiter().GetResult();
    }

    /// <summary>Pump until <paramref name="done"/> holds, for work kicked off through an async void
    /// handler that exposes no task.</summary>
    public static void PumpUntil(Func<bool> done)
    {
        var sw = Stopwatch.StartNew();
        using var tick = new ManualResetEventSlim(false);
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (done()) break;
            if (sw.Elapsed > Deadline) throw new TimeoutException($"Condition not met within {Deadline}.");
            tick.Wait(5);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
