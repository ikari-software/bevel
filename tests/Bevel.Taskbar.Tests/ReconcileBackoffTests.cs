using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Bevel.Pal.Abstractions;
using Bevel.Taskbar;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The window reconcile loop must back off FAST while it has never succeeded (bevel-pa3j).
///
/// <para>The loop is the authoritative backstop for the event stream, and 2s is the right steady-state
/// cadence for that job. But the same interval also governed the retry after a FAILED tick — and at boot
/// the first tick nearly always fails, because the taskbar starts this loop while the core's Swift helper
/// is still connecting. A helper that came up 20 ms after the first attempt still left the task strip
/// empty for a full 2 s, and two near-misses cost 4 s.</para>
///
/// <para>These are real timing assertions rather than source greps, with a wide margin: the fixed-interval
/// code needs ~4 s to reach a third attempt and the backoff needs ~0.45 s, so a 1.5 s bound separates them
/// without being sensitive to scheduling noise. They run against the live loop through
/// <see cref="ShellModel.Start"/>, so a regression that reverts the pacing fails here.</para>
/// </summary>
public sealed class ReconcileBackoffTests
{
    /// <summary>An enumerate that fails a fixed number of times, recording when each attempt landed.</summary>
    private sealed class FlakyWindows : IWindowManager
    {
        private readonly int _failures;
        private int _attempts;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly TaskCompletionSource _firstSuccess =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FlakyWindows(int failures) => _failures = failures;

        public List<TimeSpan> AttemptAt { get; } = [];
        public Task FirstSuccess => _firstSuccess.Task;
        public int Attempts => Volatile.Read(ref _attempts);

        public Capabilities Capabilities { get; } = new(true, TrayCapability.Mirrored, []);

        public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
        {
            lock (AttemptAt) AttemptAt.Add(_clock.Elapsed);
            if (Interlocked.Increment(ref _attempts) <= _failures)
                throw new InvalidOperationException("Helper not connected");   // the real boot-race shape
            _firstSuccess.TrySetResult();
            return ValueTask.FromResult<IReadOnlyList<ForeignWindow>>(Array.Empty<ForeignWindow>());
        }

        public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, CancellationToken ct = default) => Task.CompletedTask;
        public Task<byte[]?> CaptureWindowAsync(ForeignWindowId id, int w, int h, CancellationToken ct = default)
            => Task.FromResult<byte[]?>(null);

        public event EventHandler<ForeignWindow>? WindowOpened;
        public event EventHandler<ForeignWindow>? WindowClosed;
        public event EventHandler<ForeignWindow>? WindowChanged;
        public event EventHandler<ForeignWindow>? ForegroundChanged;
    }

    /// <summary>Two failed ticks then a success: with the old flat interval the third attempt could not
    /// happen before ~4 s. Asserts on the elapsed time to the first SUCCESSFUL enumerate.</summary>
    [Fact]
    public async Task A_failed_first_tick_retries_in_well_under_the_steady_interval()
    {
        var windows = new FlakyWindows(failures: 2);
        using var model = new ShellModel(windows, null, null, usage: TestUsage.Scratch());

        var clock = Stopwatch.StartNew();
        model.Start();

        var completed = await Task.WhenAny(windows.FirstSuccess, Task.Delay(TimeSpan.FromSeconds(8)));
        Assert.Same(windows.FirstSuccess, completed);   // it succeeded at all

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1.5),
            $"two near-misses should cost ~450ms of backoff, not two full {2}s intervals "
            + $"(took {clock.Elapsed.TotalMilliseconds:F0}ms across {windows.Attempts} attempts)");
    }

    /// <summary>The backoff GROWS rather than hammering the helper: the gap after the second failure is
    /// longer than the gap after the first. Pins the doubling, so a flat 150ms poll would fail too — the
    /// point is a short first retry, not a permanently fast loop.</summary>
    [Fact]
    public async Task The_backoff_doubles_between_failed_ticks()
    {
        var windows = new FlakyWindows(failures: 3);
        using var model = new ShellModel(windows, null, null, usage: TestUsage.Scratch());
        model.Start();

        var completed = await Task.WhenAny(windows.FirstSuccess, Task.Delay(TimeSpan.FromSeconds(8)));
        Assert.Same(windows.FirstSuccess, completed);

        TimeSpan[] at;
        lock (windows.AttemptAt) at = windows.AttemptAt.ToArray();
        Assert.True(at.Length >= 4, $"expected 4 attempts, saw {at.Length}");

        var gap1 = at[1] - at[0];
        var gap3 = at[3] - at[2];
        Assert.True(gap3 > gap1,
            $"the backoff should grow (first gap {gap1.TotalMilliseconds:F0}ms, third {gap3.TotalMilliseconds:F0}ms)");
        Assert.True(gap1 >= ShellModel.FirstReconcileBackoff - TimeSpan.FromMilliseconds(40),
            $"the first retry should wait about {ShellModel.FirstReconcileBackoff.TotalMilliseconds}ms, "
            + $"not spin ({gap1.TotalMilliseconds:F0}ms)");
        Assert.True(gap1 < TimeSpan.FromSeconds(1),
            $"...and it must be far shorter than the steady interval ({gap1.TotalMilliseconds:F0}ms)");
    }

    /// <summary>Once a tick succeeds the loop returns to the steady backstop cadence instead of keeping a
    /// fast poll alive for the rest of the session — the loop walks the whole window list and, on macOS,
    /// the Dock's accessibility tree for badges, so a permanently fast one is not free.</summary>
    [Fact]
    public async Task After_the_first_success_the_loop_drops_back_to_the_steady_interval()
    {
        var windows = new FlakyWindows(failures: 0);
        using var model = new ShellModel(windows, null, null, usage: TestUsage.Scratch());
        model.Start();

        var completed = await Task.WhenAny(windows.FirstSuccess, Task.Delay(TimeSpan.FromSeconds(8)));
        Assert.Same(windows.FirstSuccess, completed);

        // Well past several backoff intervals, but short of the 2s steady one.
        await Task.Delay(TimeSpan.FromMilliseconds(900));
        Assert.Equal(1, windows.Attempts);
    }
}
