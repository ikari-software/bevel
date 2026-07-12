using Bevel.Core;
using Bevel.Pal.Abstractions;
using Bevel.Taskbar;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-m2.13: the Nudge-strategy overlap engine. Drives <see cref="WorkAreaMitigator"/>'s
/// decision pass (MitigateOnceAsync) directly with a recording window manager and an injected
/// clock, free of the event/debounce timing (motion suspension lives in the settle debounce and
/// is verified live). The band is a fixed bottom-30-point strip of a 1600×1000-point screen:
/// y ∈ [970, 1000).
/// </summary>
public class WorkAreaMitigatorTests
{
    private static readonly PalRect Band = new(0, 970, 1600, 30);

    private static ForeignWindow Win(string id, PalRect bounds, bool minimized = false)
        => new(new ForeignWindowId(id), "Test", "com.test", minimized, false, bounds);

    private static WorkAreaMitigator Make(RecordingWindowManager wm, FixedClock clock)
        => new(wm, new SettingsService(), () => Band, clock);

    [Fact]
    public async Task Overlapping_window_is_nudged_to_sit_above_the_band()
    {
        // Bottom edge (1100) crosses the band; top (800) leaves 170pt of usable height.
        var wm = new RecordingWindowManager(Win("w1", new PalRect(0, 800, 800, 300)));
        var m = Make(wm, new FixedClock());

        Assert.Equal(1, await m.MitigateOnceAsync());
        var (id, target) = Assert.Single(wm.Repositions);
        Assert.Equal("w1", id.Value);
        // Shrunk so its bottom sits on the band's top edge (970): height = 970 - 800.
        Assert.Equal(new PalRect(0, 800, 800, 170), target);
    }

    [Fact]
    public async Task Minimized_window_is_never_nudged()
    {
        var wm = new RecordingWindowManager(Win("w1", new PalRect(0, 800, 800, 300), minimized: true));
        var m = Make(wm, new FixedClock());

        await m.MitigateOnceAsync();

        Assert.Empty(wm.Repositions);
    }

    [Fact]
    public async Task Window_clear_of_the_band_is_left_alone()
    {
        // Bottom edge (400) is well above the band top (970).
        var wm = new RecordingWindowManager(Win("w1", new PalRect(0, 100, 800, 300)));
        var m = Make(wm, new FixedClock());

        await m.MitigateOnceAsync();

        Assert.Empty(wm.Repositions);
    }

    [Fact]
    public async Task Window_that_would_be_squashed_below_minimum_is_left_alone()
    {
        // Top at 900 → available height above the band is only 70pt (< 100 floor):
        // shrinking it here would be worse than the overlap, so it's skipped.
        var wm = new RecordingWindowManager(Win("w1", new PalRect(0, 900, 800, 300)));
        var m = Make(wm, new FixedClock());

        await m.MitigateOnceAsync();

        Assert.Empty(wm.Repositions);
    }

    [Fact]
    public async Task Reposition_is_rate_limited_per_window()
    {
        // The fake holds the window's frame fixed after the nudge (a real window would move,
        // and the event debounce would gate re-checks) so this isolates the per-window limiter.
        var wm = new RecordingWindowManager(Win("w1", new PalRect(0, 800, 800, 300)));
        var clock = new FixedClock();
        var m = Make(wm, clock);

        await m.MitigateOnceAsync();      // nudge #1
        Assert.Single(wm.Repositions);

        await m.MitigateOnceAsync();      // within 2s of #1 → blocked
        Assert.Single(wm.Repositions);

        clock.Advance(TimeSpan.FromSeconds(3));
        await m.MitigateOnceAsync();      // limiter elapsed → nudge #2
        Assert.Equal(2, wm.Repositions.Count);
    }

    /// <summary>Records reposition calls and lets a test swap the window list between passes.</summary>
    private sealed class RecordingWindowManager : IWindowManager
    {
        private IReadOnlyList<ForeignWindow> _windows;
        public List<(ForeignWindowId Id, PalRect Bounds)> Repositions { get; } = new();

        public RecordingWindowManager(params ForeignWindow[] windows) => _windows = windows;
        public void Set(params ForeignWindow[] windows) => _windows = windows;

        public Capabilities Capabilities { get; } = new(
            Available: true, TrayMode: TrayCapability.Mirrored,
            Notes: Array.Empty<string>(), SupportsReposition: true);

        public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
            => ValueTask.FromResult(_windows);

        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, CancellationToken ct = default)
        {
            Repositions.Add((id, bounds));
            return Task.CompletedTask;
        }

        public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;

#pragma warning disable CS0067 // required by the interface; the mitigator subscribes, tests don't raise
        public event EventHandler<ForeignWindow>? WindowOpened;
        public event EventHandler<ForeignWindow>? WindowClosed;
        public event EventHandler<ForeignWindow>? WindowChanged;
        public event EventHandler<ForeignWindow>? ForegroundChanged;
#pragma warning restore CS0067
    }

    /// <summary>A hand-cranked clock so rate-limit windows are deterministic.</summary>
    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
