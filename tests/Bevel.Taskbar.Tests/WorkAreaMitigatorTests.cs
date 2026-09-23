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

    /// Settings rooted in a throwaway directory. The parameterless SettingsService ctor resolves the
    /// developer's REAL ~/.config/bevel and is now refused outright in a test host (BevelConfigDir);
    /// these two call sites were the ones pointing at it.
    private static SettingsService ScratchSettings()
        => new(Directory.CreateDirectory(Path.Combine(
               Path.GetTempPath(), "bevel-tests", Guid.NewGuid().ToString("n"))).FullName);

    private static ForeignWindow Win(string id, PalRect bounds, bool minimized = false)
        => new(new ForeignWindowId(id), "Test", "com.test", minimized, false, bounds);

    private static WorkAreaMitigator Make(RecordingWindowManager wm, FixedClock clock)
        => new(wm, ScratchSettings(), () => Band, clock);

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
        // The fake holds the window's frame fixed after the nudge — i.e. the app re-asserted the very
        // frame we just corrected. That is the reposition fight Req 9.2's rate limit exists for.
        var wm = new RecordingWindowManager(Win("w1", new PalRect(0, 800, 800, 300)));
        var clock = new FixedClock();
        var m = Make(wm, clock);

        await m.MitigateOnceAsync();      // nudge #1
        Assert.Single(wm.Repositions);

        await m.MitigateOnceAsync();      // same frame, within 2s of #1 → blocked
        Assert.Single(wm.Repositions);

        clock.Advance(TimeSpan.FromSeconds(3));
        await m.MitigateOnceAsync();      // limiter elapsed → nudge #2
        Assert.Equal(2, wm.Repositions.Count);
    }

    /// <summary>
    /// bevel-yv2m: an AppKit zoom animates into NSScreen.visibleFrame — our band included — so its
    /// final frame can land AFTER our correction. That is a window that genuinely moved, not an app
    /// re-asserting the frame we corrected, so it must be corrected at once rather than sit over the
    /// bar until the cooldown expires (the old blind per-window limiter left it there for seconds).
    /// </summary>
    [Fact]
    public async Task Zoom_landing_after_a_correction_is_fixed_without_waiting_for_the_cooldown()
    {
        var wm = new RecordingWindowManager(Win("w1", new PalRect(0, 800, 800, 300)));
        var clock = new FixedClock();
        var m = Make(wm, clock);

        Assert.Equal(1, await m.MitigateOnceAsync());

        // The zoom's own final frame arrives: a DIFFERENT overlapping rect (top at the menu bar,
        // bottom at the screen edge), well inside the 2s limiter window.
        wm.Set(Win("w1", new PalRect(0, 30, 1600, 970)));
        clock.Advance(TimeSpan.FromMilliseconds(400));

        Assert.Equal(1, await m.MitigateOnceAsync());
        Assert.Equal(2, wm.Repositions.Count);
        Assert.Equal(new PalRect(0, 30, 1600, 940), wm.Repositions[1].Bounds);   // bottom on 970
    }

    /// <summary>A window observed clear of the band forgets its back-off, so re-zooming it moments
    /// later is corrected immediately instead of inheriting the previous correction's cooldown.</summary>
    [Fact]
    public async Task Window_seen_clear_of_the_band_forgets_its_back_off()
    {
        var zoomed = new PalRect(0, 800, 800, 300);
        var wm = new RecordingWindowManager(Win("w1", zoomed));
        var clock = new FixedClock();
        var m = Make(wm, clock);

        await m.MitigateOnceAsync();                          // nudge #1
        wm.Set(Win("w1", new PalRect(0, 800, 800, 170)));     // correction took: clear of the band
        await m.MitigateOnceAsync();
        Assert.Single(wm.Repositions);

        wm.Set(Win("w1", zoomed));                            // user zooms again, same target frame
        clock.Advance(TimeSpan.FromMilliseconds(300));         // still inside the 2s limiter window

        await m.MitigateOnceAsync();
        Assert.Equal(2, wm.Repositions.Count);
    }

    /// <summary>
    /// bevel-yv2m: after a correction the engine re-checks by itself a beat later, so a zoom
    /// animation whose final frame lands on top of our correction (and emits no further AX event) is
    /// fixed in well under a second instead of waiting on the 3s safety poll. Real clock: this is
    /// the verify-pass timing, not the decision logic.
    /// </summary>
    [Fact]
    public async Task Correction_is_verified_again_shortly_after_it_is_applied()
    {
        if (OperatingSystem.IsWindows()) return;   // the settle timer never fires under Avalonia's headless Windows dispatch model (framework limit, not Bevel); passes on Linux+macOS
        // The window re-enters the band ONCE right after the first correction (the animation landing)
        // and emits no event — only the engine's own verify pass can catch it.
        var wm = new RecordingWindowManager(Win("w1", new PalRect(0, 800, 800, 300)));
        wm.OnReposition = count => wm.Set(count == 1
            ? Win("w1", new PalRect(0, 30, 1600, 970))        // zoom lands over the band
            : Win("w1", new PalRect(0, 30, 1600, 940)));      // second correction holds
        var m = new WorkAreaMitigator(wm, ScratchSettings(), () => Band);
        m.Start();

        m.RequestMitigation();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);   // well inside the 3s safety poll
        while (wm.Repositions.Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        m.Dispose();

        Assert.Equal(2, wm.Repositions.Count);
    }

    /// <summary>The engine drives a real PAL implementation end to end: the Fake PAL applies the
    /// reposition, so the window is genuinely out of the band afterwards (and a second pass has
    /// nothing left to do) — no Mac required.</summary>
    [Fact]
    public async Task Fake_pal_window_ends_up_above_the_band()
    {
        var wm = new Bevel.Pal.Fake.FakeWindowManager();
        // The Fake desktop's widest window (w3: y=150 h=800 → bottom 950) clears this band, so give
        // the engine a band it does cross: the bottom 30pt of a 900pt-tall screen.
        var band = new PalRect(0, 870, 1600, 30);
        var m = new WorkAreaMitigator(wm, ScratchSettings(), () => band, new FixedClock());

        Assert.True(await m.MitigateOnceAsync() > 0);

        foreach (var w in await wm.EnumerateAsync())
            Assert.False(Crosses(w.Bounds, band), $"{w.Id.Value} still crosses the band: {w.Bounds}");
        Assert.Equal(0, await m.MitigateOnceAsync());   // nothing left to correct
    }

    [Fact]
    public async Task RequestMitigation_triggers_a_pass_outside_the_window_event_stream()
    {
        if (OperatingSystem.IsWindows()) return;   // the settle DispatcherTimer never fires under Avalonia's headless Windows dispatch model (framework limit, not Bevel); passes on Linux+macOS
        // Models a display reconfiguration: no window moved (so no WindowChanged fires), yet the
        // band is now occupied by an overlapping window and must be nudged. Real clock so the
        // settle debounce actually elapses; poll with a generous deadline to stay non-flaky.
        var wm = new RecordingWindowManager(Win("w1", new PalRect(0, 800, 800, 300)));
        var m = new WorkAreaMitigator(wm, ScratchSettings(), () => Band);
        m.Start();

        m.RequestMitigation();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (wm.Repositions.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        var (id, target) = Assert.Single(wm.Repositions);
        Assert.Equal("w1", id.Value);
        Assert.Equal(new PalRect(0, 800, 800, 170), target);
        m.Dispose();
    }

    private static bool Crosses(PalRect w, PalRect band)
        => w.Y < band.Y + band.Height && w.Y + w.Height > band.Y
        && w.X < band.X + band.Width && w.X + w.Width > band.X;

    /// <summary>Records reposition calls and lets a test swap the window list between passes.</summary>
    private sealed class RecordingWindowManager : IWindowManager
    {
        private IReadOnlyList<ForeignWindow> _windows;
        public List<(ForeignWindowId Id, PalRect Bounds)> Repositions { get; } = new();

        /// <summary>Called with the running reposition count after each one, so a test can script
        /// what the desktop looks like next (e.g. a zoom animation landing on our correction).</summary>
        public Action<int>? OnReposition { get; set; }

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
            OnReposition?.Invoke(Repositions.Count);
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
