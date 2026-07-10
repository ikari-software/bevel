using System.Collections.Concurrent;

namespace Bevel.FileManager.FileOperations;

/// <summary>
/// Runs pre-scanned jobs grouped by target volume (extracted from FileOperationService,
/// bevel-p3g). One FIFO queue — a <see cref="SemaphoreSlim"/>(1,1) — per volume key, so writes to
/// the same disk serialize while different disks run in parallel. On cancellation each volume
/// worker drains gracefully (breaks the loop) rather than throwing, so the caller still records
/// undo entries for the work that completed before the cancel (review #2).
///
/// Deliberately NOT disposable (bevel-70g): these semaphores never touch AvailableWaitHandle,
/// so they hold no kernel handle and need no disposal — while disposing them during service
/// teardown would make a still-draining worker's <c>sem.Release()</c> throw
/// ObjectDisposedException and fault that operation's Completion task.
/// </summary>
internal sealed class VolumeQueueExecutor
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _volumeQueues = new();

    /// <summary>
    /// Runs every job. <paramref name="executeJob"/> receives the job and its per-volume index;
    /// it must not throw for expected outcomes (it records them) — only truly unexpected faults.
    /// </summary>
    public async Task ExecuteAsync(
        IReadOnlyDictionary<string, IReadOnlyList<FileOpJob>> jobsByVolume,
        CancellationToken ct,
        Func<FileOpJob, int, Task> executeJob)
    {
        var volumeTasks = new List<Task>();

        foreach (var (volumeKey, volumeJobs) in jobsByVolume)
        {
            var sem = _volumeQueues.GetOrAdd(volumeKey, _ => new SemaphoreSlim(1, 1));
            var volumeTask = Task.Run(async () =>
            {
                int fileIndex = 0;
                foreach (var job in volumeJobs)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        await sem.WaitAsync(ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    try
                    {
                        await executeJob(job, fileIndex);
                    }
                    finally
                    {
                        sem.Release();
                    }
                    fileIndex++;
                }
            }, ct);

            volumeTasks.Add(volumeTask);
        }

        await Task.WhenAll(volumeTasks);
    }
}
