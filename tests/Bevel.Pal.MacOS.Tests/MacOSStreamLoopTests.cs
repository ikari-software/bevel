using System.Diagnostics;
using System.Runtime.CompilerServices;
using Bevel.Ipc.V1;
using Bevel.Pal.Abstractions;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using WireTrayItem = Bevel.Ipc.V1.TrayItem;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>
/// bevel-umgr: the window/tray Changes stream loops are driven by a fake <c>changesSource</c> +
/// <see cref="ManualClock"/>, so retry-after-Unavailable, retry-after-generic-error, clean
/// cancellation, and every change kind's event mapping are deterministic — no helper, no
/// <c>Task.Delay</c> in the loop.
/// </summary>
public sealed class MacOSStreamLoopTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    // ── Fakes ────────────────────────────────────────────────────────────

    private sealed class FakeWindowStream
    {
        public int Subscriptions;
        private readonly RpcException? _throwOnFirst;
        private readonly Queue<WindowChange> _changes;

        public FakeWindowStream(RpcException? throwOnFirst = null, params WindowChange[] changes)
        {
            _throwOnFirst = throwOnFirst;
            _changes = new Queue<WindowChange>(changes);
        }

        public async IAsyncEnumerable<WindowChange> Source([EnumeratorCancellation] CancellationToken ct)
        {
            Subscriptions++;
            if (Subscriptions == 1 && _throwOnFirst is { } ex)
            {
                await Task.Yield();
                throw ex;
            }
            while (_changes.Count > 0)
                yield return _changes.Dequeue();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);   // park until the test cancels
        }
    }

    private sealed class FakeTrayStream
    {
        public int Subscriptions;
        private readonly Queue<TrayChange> _changes;

        public FakeTrayStream(params TrayChange[] changes) => _changes = new Queue<TrayChange>(changes);

        public async IAsyncEnumerable<TrayChange> Source([EnumeratorCancellation] CancellationToken ct)
        {
            Subscriptions++;
            while (_changes.Count > 0)
                yield return _changes.Dequeue();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }

    private static WindowChange Change(WindowChange.Types.Kind kind, string id, bool focused = false)
        => new() { Kind = kind, Window = new TaskbarWindow { WindowId = id, Title = "t" + id, IsFocused = focused } };

    private static TrayChange TrayChangeOf(TrayChange.Types.Kind kind, string id)
        => new() { Kind = kind, Item = new WireTrayItem { ItemId = id, Tooltip = "tip" + id } };

    private static MacOSWindowManager WindowManager(ManualClock clock, FakeWindowStream fake)
        => new(new HelperLifecycle(NullLogger<HelperLifecycle>.Instance),
            NullLogger<MacOSWindowManager>.Instance, clock, Interval, fake.Source);

    private static MacOSSystemTrayHost TrayHost(ManualClock clock, FakeTrayStream fake)
        => new(new HelperLifecycle(NullLogger<HelperLifecycle>.Instance),
            NullLogger<MacOSSystemTrayHost>.Instance, clock, Interval, fake.Source);

    private static void WaitUntil(Func<bool> cond, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException($"Timed out waiting for {what}.");
            Thread.Sleep(5);
        }
    }

    // ── Window stream loop ───────────────────────────────────────────────

    [Fact]
    public async Task Window_Unavailable_delays_one_retry_interval_then_resubscribes()
    {
        var clock = new ManualClock();
        var fake = new FakeWindowStream(
            new RpcException(new Status(StatusCode.Unavailable, "helper down")),
            Change(WindowChange.Types.Kind.Opened, "1"));
        using var wm = WindowManager(clock, fake);
        int opened = 0;
        wm.WindowOpened += (_, _) => Interlocked.Increment(ref opened);

        using var cts = new CancellationTokenSource();
        _ = wm.StartPollAsync(cts.Token);

        await clock.NextDelayArrived();                       // the loop parked on the retry delay
        Assert.Equal(new[] { Interval }, clock.Requested);
        Assert.Equal(1, fake.Subscriptions);

        clock.Tick();                                          // release the delay → resubscribe
        WaitUntil(() => fake.Subscriptions == 2, "second subscription");
        WaitUntil(() => Volatile.Read(ref opened) == 1, "WindowOpened from the second stream");
        cts.Cancel();
    }

    [Fact]
    public async Task Window_generic_exception_delays_then_resubscribes()
    {
        var clock = new ManualClock();
        var fake = new FakeWindowStream(new RpcException(new Status(StatusCode.Internal, "boom")));
        using var wm = WindowManager(clock, fake);

        using var cts = new CancellationTokenSource();
        _ = wm.StartPollAsync(cts.Token);

        await clock.NextDelayArrived();
        Assert.Equal(new[] { Interval }, clock.Requested);   // generic error also waits one interval
        clock.Tick();
        WaitUntil(() => fake.Subscriptions == 2, "resubscription after generic error");
        cts.Cancel();
    }

    [Fact]
    public void Window_cancellation_exits_without_a_delay()
    {
        var clock = new ManualClock();
        var fake = new FakeWindowStream();                     // no throw, no changes: parks on cancel
        using var wm = WindowManager(clock, fake);

        using var cts = new CancellationTokenSource();
        _ = wm.StartPollAsync(cts.Token);
        WaitUntil(() => fake.Subscriptions == 1, "first subscription");
        cts.Cancel();
        WaitUntil(() => clock.PendingCount == 0 && clock.Requested.Count == 0, "loop exited");
        Assert.Empty(clock.Requested);                        // cancel never parks on the clock
    }

    [Theory]
    [InlineData(WindowChange.Types.Kind.Snapshot, "changed", false)]
    [InlineData(WindowChange.Types.Kind.Opened, "opened", false)]
    [InlineData(WindowChange.Types.Kind.Closed, "closed", false)]
    [InlineData(WindowChange.Types.Kind.Focused, "changed+foreground", true)]
    [InlineData(WindowChange.Types.Kind.TitleChanged, "changed", false)]
    [InlineData(WindowChange.Types.Kind.Moved, "changed", false)]
    [InlineData(WindowChange.Types.Kind.Minimized, "changed", false)]
    [InlineData(WindowChange.Types.Kind.Deminimized, "changed", false)]
    public void Window_each_change_kind_raises_its_event(WindowChange.Types.Kind kind, string expected, bool focused)
    {
        var clock = new ManualClock();
        var fake = new FakeWindowStream(null, Change(kind, "7", focused));
        using var wm = WindowManager(clock, fake);
        int changed = 0, opened = 0, closed = 0, foreground = 0;
        wm.WindowChanged += (_, _) => Interlocked.Increment(ref changed);
        wm.WindowOpened += (_, _) => Interlocked.Increment(ref opened);
        wm.WindowClosed += (_, _) => Interlocked.Increment(ref closed);
        wm.ForegroundChanged += (_, _) => Interlocked.Increment(ref foreground);

        using var cts = new CancellationTokenSource();
        _ = wm.StartPollAsync(cts.Token);
        WaitUntil(() =>
            Volatile.Read(ref changed) + Volatile.Read(ref opened) + Volatile.Read(ref closed) >= 1,
            $"{kind} to raise its event");
        cts.Cancel();

        switch (expected)
        {
            case "changed": Assert.Equal(1, Volatile.Read(ref changed)); Assert.Equal(0, Volatile.Read(ref foreground)); break;
            case "opened": Assert.Equal(1, Volatile.Read(ref opened)); break;
            case "closed": Assert.Equal(1, Volatile.Read(ref closed)); break;
            case "changed+foreground": Assert.Equal(1, Volatile.Read(ref changed)); Assert.Equal(1, Volatile.Read(ref foreground)); break;
        }
    }

    // ── Tray stream loop ─────────────────────────────────────────────────

    [Theory]
    [InlineData(TrayChange.Types.Kind.Snapshot, "added")]
    [InlineData(TrayChange.Types.Kind.Added, "added")]
    [InlineData(TrayChange.Types.Kind.Updated, "updated")]
    [InlineData(TrayChange.Types.Kind.Removed, "removed")]
    public void Tray_each_change_kind_raises_its_event(TrayChange.Types.Kind kind, string expected)
    {
        var clock = new ManualClock();
        var fake = new FakeTrayStream(TrayChangeOf(kind, "9:9"));
        using var host = TrayHost(clock, fake);
        int added = 0, updated = 0, removed = 0;
        host.ItemAdded += (_, _) => Interlocked.Increment(ref added);
        host.ItemUpdated += (_, _) => Interlocked.Increment(ref updated);
        host.ItemRemoved += (_, _) => Interlocked.Increment(ref removed);

        using var cts = new CancellationTokenSource();
        _ = host.StartPollAsync(cts.Token);
        WaitUntil(() =>
            Volatile.Read(ref added) + Volatile.Read(ref updated) + Volatile.Read(ref removed) >= 1,
            $"{kind} to raise its event");
        cts.Cancel();

        switch (expected)
        {
            case "added": Assert.Equal(1, Volatile.Read(ref added)); break;
            case "updated": Assert.Equal(1, Volatile.Read(ref updated)); break;
            case "removed": Assert.Equal(1, Volatile.Read(ref removed)); break;
        }
    }

    [Fact]
    public async Task Tray_Unavailable_delays_one_retry_interval_then_resubscribes()
    {
        var clock = new ManualClock();
        int subscriptions = 0;
        RpcException? first = new RpcException(new Status(StatusCode.Unavailable, "down"));
        async IAsyncEnumerable<TrayChange> Source([EnumeratorCancellation] CancellationToken ct)
        {
            subscriptions++;
            if (subscriptions == 1 && first is { } ex) { await Task.Yield(); throw ex; }
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            yield break;
        }

        using var host = new MacOSSystemTrayHost(new HelperLifecycle(NullLogger<HelperLifecycle>.Instance),
            NullLogger<MacOSSystemTrayHost>.Instance, clock, Interval, Source);
        using var cts = new CancellationTokenSource();
        _ = host.StartPollAsync(cts.Token);

        await clock.NextDelayArrived();
        Assert.Equal(new[] { Interval }, clock.Requested);
        clock.Tick();
        WaitUntil(() => Volatile.Read(ref subscriptions) == 2, "tray resubscription");
        cts.Cancel();
    }
}
