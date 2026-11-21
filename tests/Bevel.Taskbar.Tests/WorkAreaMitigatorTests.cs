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
    // What a zoom or a macOS tile fills on this 1600x1000 screen: under a 30pt menu bar, to the bottom.
    private static readonly PalRect ZoomArea = new(0, 30, 1600, 970);
    private static readonly PalRect Zoomed = ZoomArea;
    private static readonly PalRect Corrected = new(0, 30, 1600, 940);     // bottom on the band top (970)
    private static readonly PalRect LeftHalf = new(0, 30, 800, 970);
    private static readonly PalRect RightHalf = new(800, 30, 800, 970);
    // Sequoia+ tiling can leave a margin around tiled windows.
    private static readonly PalRect ZoomedWithMargins = new(8, 38, 1584, 962);

    /// Settings rooted in a throwaway directory. The parameterless SettingsService ctor resolves the
    /// developer's REAL ~/.config/bevel and is now refused outright in a test host (BevelConfigDir);
    /// these two call sites were the ones pointing at it.
    private static SettingsService ScratchSettings()
        => new(Directory.CreateDirectory(Path.Combine(
               Path.GetTempPath(), "bevel-tests", Guid.NewGuid().ToString("n"))).FullName);

    private static ForeignWindow Win(string id, PalRect bounds, bool minimized = false,
        bool fullscreen = false, bool otherSpace = false)
        => new(new ForeignWindowId(id), "Test", "com.test", minimized, false, bounds,
               IsFullscreen: fullscreen, IsOnOtherSpace: otherSpace);

    private static WorkAreaMitigator Make(RecordingWindowManager wm, FixedClock clock)
        => new(wm, ScratchSettings(), () => Band, () => ZoomArea, clock);

    [Fact]
    public async Task Zoomed_window_is_nudged_to_sit_above_the_band()
    {
        var wm = new RecordingWindowManager(Win("w1", Zoomed));
        var m = Make(wm, new FixedClock());

        Assert.Equal(1, await m.MitigateOnceAsync());
        var (id, target) = Assert.Single(wm.Repositions);
        Assert.Equal("w1", id.Value);
        // Shrunk so its bottom sits on the band's top edge (970): height = 970 - 30.
        Assert.Equal(Corrected, target);
    }

    // bevel-u6ue: the engine only ever existed to stop ZOOMED windows going under the bar. It used to
    // shrink anything overlapping the band (a window the user dragged there on purpose, a Filer still
    // opening), which made it erratic. Now it acts on the zoom's footprint only: top just under the
    // menu bar, spanning the screen (zoom / Fill) or one half of it (left/right tile).
    [Fact]
    public async Task Half_tiles_are_nudged_too()
    {
        var wm = new RecordingWindowManager(Win("l", LeftHalf), Win("r", RightHalf));
        Assert.Equal(2, await Make(wm, new FixedClock()).MitigateOnceAsync());
        Assert.Equal(new PalRect(0, 30, 800, 940), wm.Repositions[0].Bounds);
        Assert.Equal(new PalRect(800, 30, 800, 940), wm.Repositions[1].Bounds);
    }

    [Fact]
    public async Task Tiling_margins_are_tolerated()
    {
        var wm = new RecordingWindowManager(Win("w1", ZoomedWithMargins));
        Assert.Equal(1, await Make(wm, new FixedClock()).MitigateOnceAsync());
        Assert.Equal(new PalRect(8, 38, 1584, 932), Assert.Single(wm.Repositions).Bounds);
    }

    // Review 2026-11-21 (correctness + adversarial, validated): macOS tiling also has bottom halves and
    // quarters. Their top sits at the middle of the zoom area, but they run to the screen bottom, under
    // the bar, exactly like a zoom does. The hidden Filer (top 453 of a 30..1000 area) is still far from it.
    [Fact]
    public async Task Bottom_halves_and_quarters_are_nudged_too()
    {
        var wm = new RecordingWindowManager(
            Win("bottom", new PalRect(0, 515, 1600, 485)),
            Win("bottomLeft", new PalRect(0, 515, 800, 485)),
            Win("bottomRight", new PalRect(800, 515, 800, 485)));
        Assert.Equal(3, await Make(wm, new FixedClock()).MitigateOnceAsync());
        Assert.Equal(new PalRect(0, 515, 1600, 455), wm.Repositions[0].Bounds);   // bottom on 970
        Assert.Equal(new PalRect(800, 515, 800, 455), wm.Repositions[2].Bounds);
    }

    // Review 2026-11-21 (adversarial): a margined half tile is up to ~1.5 margins narrower than half,
    // so it only passed at the exact edge of the tolerance.
    [Fact]
    public async Task Margined_half_tiles_pass_comfortably()
    {
        var wm = new RecordingWindowManager(
            Win("l", new PalRect(8, 38, 788, 954)), Win("r", new PalRect(804, 38, 788, 954)));
        Assert.Equal(2, await Make(wm, new FixedClock()).MitigateOnceAsync());
    }

    [Fact]
    public async Task A_window_dragged_to_the_middle_of_the_screen_is_still_left_alone()
    {
        // Full width, top at the vertical midpoint, but it does not reach the screen bottom.
        var wm = new RecordingWindowManager(Win("w1", new PalRect(0, 515, 1600, 300)));
        Assert.Equal(0, await Make(wm, new FixedClock()).MitigateOnceAsync());
    }

    [Theory]
    [InlineData(300, 700, 800, 400)]    // dragged partly under the bar on purpose
    [InlineData(0, 400, 1600, 600)]     // full width, but does not start under the menu bar
    [InlineData(0, 30, 1200, 970)]      // starts top-left, but neither full width nor a half
    [InlineData(0, 453, 1920, 987)]     // the pre-warmed Filer's hidden frame (bevel-tm1n)
    public async Task Windows_not_shaped_like_a_zoom_are_left_alone(int x, int y, int w, int h)
    {
        var wm = new RecordingWindowManager(Win("w1", new PalRect(x, y, w, h)));
        Assert.Equal(0, await Make(wm, new FixedClock()).MitigateOnceAsync());
        Assert.Empty(wm.Repositions);
    }

    [Fact]
    public async Task Minimized_window_is_never_nudged()
    {
        var wm = new RecordingWindowManager(Win("w1", Zoomed, minimized: true));
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
        // A bar so tall that a zoom keeps only 70pt above it (< 100 floor): shrinking it there would
        // be worse than the overlap, so it's skipped.
        var wm = new RecordingWindowManager(Win("w1", Zoomed));
        var m = new WorkAreaMitigator(wm, ScratchSettings(), () => new PalRect(0, 100, 1600, 900),
            () => ZoomArea, new FixedClock());

        await m.MitigateOnceAsync();

        Assert.Empty(wm.Repositions);
    }

    [Fact]
    public async Task Reposition_is_rate_limited_per_window()
    {
        // The fake holds the window's frame fixed after the nudge — i.e. the app re-asserted the very
        // frame we just corrected. That is the reposition fight Req 9.2's rate limit exists for.
        var wm = new RecordingWindowManager(Win("w1", Zoomed));
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
        var wm = new RecordingWindowManager(Win("w1", ZoomedWithMargins));
        var clock = new FixedClock();
        var m = Make(wm, clock);

        Assert.Equal(1, await m.MitigateOnceAsync());

        // The zoom's own final frame arrives: a DIFFERENT overlapping rect (top at the menu bar,
        // bottom at the screen edge), well inside the 2s limiter window.
        wm.Set(Win("w1", Zoomed));
        clock.Advance(TimeSpan.FromMilliseconds(400));

        Assert.Equal(1, await m.MitigateOnceAsync());
        Assert.Equal(2, wm.Repositions.Count);
        Assert.Equal(Corrected, wm.Repositions[1].Bounds);   // bottom on 970
    }

    /// <summary>A window observed clear of the band forgets its back-off, so re-zooming it moments
    /// later is corrected immediately instead of inheriting the previous correction's cooldown.</summary>
    [Fact]
    public async Task Window_seen_clear_of_the_band_forgets_its_back_off()
    {
        var zoomed = Zoomed;
        var wm = new RecordingWindowManager(Win("w1", zoomed));
        var clock = new FixedClock();
        var m = Make(wm, clock);

        await m.MitigateOnceAsync();                          // nudge #1
        wm.Set(Win("w1", Corrected));                          // correction took: clear of the band
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
        var wm = new RecordingWindowManager(Win("w1", ZoomedWithMargins));
        wm.OnReposition = count => wm.Set(count == 1
            ? Win("w1", Zoomed)                               // zoom lands over the band
            : Win("w1", Corrected));                          // second correction holds
        var m = new WorkAreaMitigator(wm, ScratchSettings(), () => Band, () => ZoomArea);
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
        // Zoom one of the Fake desktop's windows on a 900pt-tall screen with the bar along its bottom.
        var band = new PalRect(0, 870, 1600, 30);
        var zoomArea = new PalRect(0, 30, 1600, 870);
        var windows = await wm.EnumerateAsync();
        var zoomed = windows[0].Id;
        await wm.RepositionAsync(zoomed, zoomArea);
        // w2 (200,200 1024x768) also crosses this band, but it is just a window the user put there.
        var plain = windows.Single(w => w.Id.Value == "w2");
        var m = new WorkAreaMitigator(wm, ScratchSettings(), () => band, () => zoomArea, new FixedClock());

        Assert.Equal(1, await m.MitigateOnceAsync());

        var after = await wm.EnumerateAsync();
        Assert.Equal(new PalRect(0, 30, 1600, 840), after.Single(w => w.Id == zoomed).Bounds);
        Assert.Equal(plain.Bounds, after.Single(w => w.Id == plain.Id).Bounds);
        Assert.Equal(0, await m.MitigateOnceAsync());   // nothing left to correct
    }

    [Fact]
    public async Task RequestMitigation_triggers_a_pass_outside_the_window_event_stream()
    {
        if (OperatingSystem.IsWindows()) return;   // the settle DispatcherTimer never fires under Avalonia's headless Windows dispatch model (framework limit, not Bevel); passes on Linux+macOS
        // Models a display reconfiguration: no window moved (so no WindowChanged fires), yet the
        // band is now occupied by an overlapping window and must be nudged. Real clock so the
        // settle debounce actually elapses; poll with a generous deadline to stay non-flaky.
        var wm = new RecordingWindowManager(Win("w1", Zoomed));
        var m = new WorkAreaMitigator(wm, ScratchSettings(), () => Band, () => ZoomArea);
        m.Start();

        m.RequestMitigation();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (wm.Repositions.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        var (id, target) = Assert.Single(wm.Repositions);
        Assert.Equal("w1", id.Value);
        Assert.Equal(Corrected, target);
        m.Dispose();
    }

    /// <summary>
    /// bevel-ex8n: a native-fullscreen window's rect IS the display, so it overlaps the band by
    /// definition — yet it lives on its own Space where the bar is not drawn at all. Nudging it
    /// corrects nothing and mangles the user's window. Geometry alone cannot tell this apart from a
    /// zoomed window, which is why the flag has to come down from the helper.
    /// </summary>
    [Fact]
    public async Task Fullscreen_window_is_never_nudged()
    {
        // Same overlapping geometry as the nudge test, so ONLY the flag can be what spares it.
        var wm = new RecordingWindowManager(Win("w1", Zoomed, fullscreen: true));
        var m = Make(wm, new FixedClock());

        Assert.Equal(0, await m.MitigateOnceAsync());
        Assert.Empty(wm.Repositions);
    }

    /// <summary>
    /// bevel-ex8n: enumeration uses CGWindowList <c>.optionAll</c>, so a window parked on a Space no
    /// display is showing is listed with the last frame it had on screen. Its "overlap" is an
    /// artefact of where it used to be, and AX cannot address it anyway.
    /// </summary>
    [Fact]
    public async Task Window_on_another_space_is_never_nudged()
    {
        var wm = new RecordingWindowManager(Win("w1", Zoomed, otherSpace: true));
        var m = Make(wm, new FixedClock());

        Assert.Equal(0, await m.MitigateOnceAsync());
        Assert.Empty(wm.Repositions);
    }

    /// <summary>
    /// bevel-v9vr: the reposition RPC throws for a window AX cannot reach, and the throw used to
    /// escape the per-window loop and end the whole pass — so every window after the first failure
    /// silently went uncorrected, behind the caller's empty catch.
    /// </summary>
    [Fact]
    public async Task One_failing_reposition_does_not_abort_the_pass()
    {
        var wm = new RecordingWindowManager(
            Win("w1", LeftHalf),
            Win("w2", Zoomed),
            Win("w3", RightHalf));
        wm.FailFor.Add("w2");
        var m = Make(wm, new FixedClock());

        // w2 is attempted and fails; the other two succeed, so the pass reports 2.
        Assert.Equal(2, await m.MitigateOnceAsync());
        Assert.Equal(new[] { "w1", "w2", "w3" }, wm.Repositions.Select(r => r.Id.Value).ToArray());
    }

    /// <summary>The attempt is recorded even when the reposition throws, so an unreachable window
    /// costs one try per rate-limit window instead of one per pass — it used to be retried forever,
    /// because the bookkeeping line sat after the throwing await.</summary>
    [Fact]
    public async Task Failing_window_is_rate_limited_like_any_other()
    {
        var wm = new RecordingWindowManager(Win("w1", Zoomed));
        wm.FailFor.Add("w1");
        var clock = new FixedClock();
        var m = Make(wm, clock);

        Assert.Equal(0, await m.MitigateOnceAsync());   // tried, failed
        Assert.Single(wm.Repositions);

        await m.MitigateOnceAsync();                    // same frame, inside the limiter → not retried
        Assert.Single(wm.Repositions);

        clock.Advance(TimeSpan.FromSeconds(3));
        await m.MitigateOnceAsync();                    // limiter elapsed → one more try
        Assert.Equal(2, wm.Repositions.Count);
    }

    /// <summary>Records reposition calls and lets a test swap the window list between passes.</summary>
    // bevel-tm1n, measured on device 2026-11-21: a just-shown Filer (800x600, centred) was snapped to
    // x=0 y=453 w=1920 h=929. The pre-warmed Filer is built hidden at (0,453,1920,987), across the
    // band; a pass whose snapshot still held that frame "corrected" the now-visible, centred window
    // to it. Every correction therefore carries the frame it was computed from, and lands only if the
    // window is still there.
    [Fact]
    public async Task Correction_carries_the_frame_it_was_computed_from()
    {
        var at = Zoomed;
        var wm = new RecordingWindowManager(Win("w1", at));
        await Make(wm, new FixedClock()).MitigateOnceAsync();
        Assert.Equal(at, Assert.Single(wm.Expected));
    }

    [Fact]
    public async Task A_correction_planned_from_a_stale_frame_never_lands()
    {
        var fake = new Bevel.Pal.Fake.FakeWindowManager();
        var live = (await fake.EnumerateAsync())[0];
        var centred = new PalRect(400, 200, 800, 600);   // clear of the band: what the user sees
        await fake.RepositionAsync(live.Id, centred);
        // The engine's snapshot is out of date: it still says the window is zoomed, which would be
        // worth correcting. Only compare-and-set stops the stale target landing on the real window.
        var stale = new StaleSnapshot(fake, live with { Bounds = Zoomed });
        var m = new WorkAreaMitigator(stale, ScratchSettings(), () => Band, () => ZoomArea, new FixedClock());

        await m.MitigateOnceAsync();

        var after = (await fake.EnumerateAsync()).Single(w => w.Id == live.Id);
        Assert.Equal(centred, after.Bounds);
    }

    [Fact]
    public void Frame_guard_tolerates_rounding_but_not_a_different_frame()
    {
        var f = new PalRect(100, 200, 800, 600);
        Assert.True(FrameGuard.Matches(f, f with { X = 101, Height = 602 }));
        Assert.False(FrameGuard.Matches(f, f with { Width = 1920 }));
        Assert.False(FrameGuard.Matches(f, f with { Y = 453 }));
    }

    /// <summary>Delegates to a real (Fake) window manager but reports an out-of-date snapshot.</summary>
    private sealed class StaleSnapshot(IWindowManager inner, ForeignWindow snapshot) : IWindowManager
    {
        public Capabilities Capabilities => inner.Capabilities;
        public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<ForeignWindow>>(new[] { snapshot });
        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, PalRect? expected = null,
            CancellationToken ct = default) => inner.RepositionAsync(id, bounds, expected, ct);
        public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
#pragma warning disable CS0067
        public event EventHandler<ForeignWindow>? WindowOpened;
        public event EventHandler<ForeignWindow>? WindowClosed;
        public event EventHandler<ForeignWindow>? WindowChanged;
        public event EventHandler<ForeignWindow>? ForegroundChanged;
#pragma warning restore CS0067
    }

    private sealed class RecordingWindowManager : IWindowManager
    {
        private IReadOnlyList<ForeignWindow> _windows;
        public List<(ForeignWindowId Id, PalRect Bounds)> Repositions { get; } = new();
        public List<PalRect?> Expected { get; } = new();

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

        /// <summary>Window ids whose reposition throws, modelling a window AX cannot address at all
        /// (another Space, an app with its AX API off) — the core turns the helper's rejection into
        /// an InvalidOperationException. Attempts are still recorded, so a test can assert the engine
        /// TRIED and simply did not succeed.</summary>
        public HashSet<string> FailFor { get; } = new();

        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, PalRect? expected = null,
            CancellationToken ct = default)
        {
            Repositions.Add((id, bounds));
            Expected.Add(expected);
            OnReposition?.Invoke(Repositions.Count);
            if (FailFor.Contains(id.Value))
                throw new InvalidOperationException($"shell-core Reposition failed: {id.Value} unreachable");
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
