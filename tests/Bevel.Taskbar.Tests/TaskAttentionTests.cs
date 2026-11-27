using Bevel.Core;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-qb19: the TaskItemViewModel attention state machine. The helper stamps
/// <see cref="ForeignWindow.AttentionAt"/> when a window newly appears or changes title while its
/// app is NOT foreground; the VM turns a NEW stamp into an Active animation (unless the window is
/// already focused), bounded mode settles it into a static tint after the pulse budget, and
/// activation/close stops it. All driven through a manual <see cref="IAttentionTimer"/> so no test
/// waits wall-clock time.
/// </summary>
public sealed class TaskAttentionTests
{
    private sealed class ManualAttentionTimer : IAttentionTimer
    {
        public TimeSpan? Due { get; private set; }
        public Action? Callback { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public void Start(TimeSpan dueTime, Action callback)
        {
            Due = dueTime;
            Callback = callback;
            StartCount++;
        }

        public void Stop()
        {
            StopCount++;
            Callback = null;
        }

        public void Fire()
        {
            var cb = Callback;
            Callback = null;
            cb?.Invoke();
        }
    }

    private sealed class StubWindows : IWindowManager
    {
        public Capabilities Capabilities { get; } = new(true, TrayCapability.Mirrored, []);
        public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<ForeignWindow>>([]);
        public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, PalRect? expected = null, CancellationToken ct = default)
            => Task.CompletedTask;
#pragma warning disable CS0067 // events never raised — the stub is event-silent
        public event EventHandler<ForeignWindow>? WindowOpened;
        public event EventHandler<ForeignWindow>? WindowClosed;
        public event EventHandler<ForeignWindow>? WindowChanged;
        public event EventHandler<ForeignWindow>? ForegroundChanged;
#pragma warning restore CS0067
    }

    private static ForeignWindow Win(long attentionAt = 0, bool focused = false, bool isAppPresence = false) => new(
        new ForeignWindowId(isAppPresence ? "app:com.example.app" : "w-1"),
        Title: "Doc",
        AppId: "com.example.app",
        IsMinimized: false,
        IsFocused: focused,
        Bounds: new PalRect(0, 0, 100, 100),
        IsAppPresence: isAppPresence,
        AttentionAt: attentionAt);

    private static (TaskItemViewModel vm, ManualAttentionTimer timer, TaskAttentionPolicy policy) Make(
        TaskbarAttentionAnimation animation = TaskbarAttentionAnimation.Pulse,
        TaskbarAttentionDuration duration = TaskbarAttentionDuration.Bounded,
        bool reduceMotion = false,
        ForeignWindow? w = null)
    {
        var policy = new TaskAttentionPolicy
        {
            Animation = animation,
            Duration = duration,
            ReduceMotion = reduceMotion,
        };
        var timer = new ManualAttentionTimer();
        var vm = new TaskItemViewModel(w ?? Win(), new StubWindows(), attention: policy, attentionTimer: timer);
        return (vm, timer, policy);
    }

    [Fact]
    public void A_new_attention_stamp_while_unfocused_starts_the_animation()
    {
        var (vm, timer, _) = Make();
        Assert.Equal(TaskAttentionState.Idle, vm.AttentionState);
        Assert.Equal(TaskAttentionVisual.None, vm.AttentionVisual);

        vm.Update(Win(attentionAt: 1000));

        Assert.Equal(1000, vm.AttentionAt);
        Assert.Equal(TaskAttentionState.Active, vm.AttentionState);
        Assert.Equal(TaskAttentionVisual.Pulse, vm.AttentionVisual);
        Assert.Equal(1, timer.StartCount);   // bounded pulse arms the settle timer
        Assert.Equal(TaskAttentionPolicy.BoundedSettleDelay, timer.Due);
    }

    [Fact]
    public void The_same_stamp_again_is_idempotent_no_retrigger()
    {
        var (vm, timer, _) = Make();
        vm.Update(Win(attentionAt: 1000));
        var starts = timer.StartCount;

        vm.Update(Win(attentionAt: 1000));   // the 500ms poll re-sends the sticky stamp

        Assert.Equal(starts, timer.StartCount);
        Assert.Equal(TaskAttentionState.Active, vm.AttentionState);
    }

    [Fact]
    public void A_stamp_arriving_while_focused_is_recorded_but_never_animates()
    {
        var (vm, timer, _) = Make(w: Win(focused: true));
        vm.SetFocused(true);

        vm.Update(Win(attentionAt: 2000, focused: true));

        Assert.Equal(2000, vm.AttentionAt);   // recorded (so a re-poll is a no-op)...
        Assert.Equal(TaskAttentionState.Idle, vm.AttentionState);   // ...but activation acknowledges it
        Assert.Equal(TaskAttentionVisual.None, vm.AttentionVisual);
        Assert.Equal(0, timer.StartCount);
    }

    [Fact]
    public void Activation_stops_a_running_animation()
    {
        var (vm, timer, _) = Make();
        vm.Update(Win(attentionAt: 1000));
        Assert.Equal(TaskAttentionState.Active, vm.AttentionState);

        vm.SetFocused(true);   // window activated / foregrounded

        Assert.Equal(TaskAttentionState.Idle, vm.AttentionState);
        Assert.Equal(TaskAttentionVisual.None, vm.AttentionVisual);
        Assert.True(timer.StopCount > 0);
    }

    [Fact]
    public void StopAttention_on_close_is_idempotent()
    {
        var (vm, timer, _) = Make();
        vm.Update(Win(attentionAt: 1000));

        vm.StopAttention();   // ShellModel's close path
        Assert.Equal(TaskAttentionState.Idle, vm.AttentionState);
        vm.StopAttention();   // idempotent
        Assert.Equal(TaskAttentionState.Idle, vm.AttentionState);
    }

    [Fact]
    public void Bounded_mode_settles_into_a_static_tint_after_the_pulse_budget()
    {
        var (vm, timer, _) = Make(duration: TaskbarAttentionDuration.Bounded);
        vm.Update(Win(attentionAt: 1000));
        Assert.Equal(TaskAttentionState.Active, vm.AttentionState);
        Assert.Equal(TaskAttentionVisual.Pulse, vm.AttentionVisual);

        timer.Fire();   // ~3 pulses elapsed

        Assert.Equal(TaskAttentionState.Settled, vm.AttentionState);
        Assert.Equal(TaskAttentionVisual.Tint, vm.AttentionVisual);   // static tint, no more animation
    }

    [Fact]
    public void Until_activated_runs_without_a_settle_timer_and_stops_on_activation()
    {
        var (vm, timer, _) = Make(duration: TaskbarAttentionDuration.UntilActivated);
        vm.Update(Win(attentionAt: 1000));

        Assert.Equal(TaskAttentionState.Active, vm.AttentionState);
        Assert.Equal(0, timer.StartCount);   // no bounded settle timer — runs until acknowledged

        vm.SetFocused(true);
        Assert.Equal(TaskAttentionState.Idle, vm.AttentionState);
    }

    [Fact]
    public void Sweep_mode_resolves_to_the_sweep_visual()
    {
        var (vm, _, _) = Make(animation: TaskbarAttentionAnimation.Sweep);
        vm.Update(Win(attentionAt: 1000));
        Assert.Equal(TaskAttentionVisual.Sweep, vm.AttentionVisual);
    }

    [Fact]
    public void Tint_only_is_static_immediately_and_ignores_the_duration_setting()
    {
        var (vm, timer, _) = Make(animation: TaskbarAttentionAnimation.TintOnly,
            duration: TaskbarAttentionDuration.UntilActivated);
        vm.Update(Win(attentionAt: 1000));

        Assert.Equal(TaskAttentionState.Active, vm.AttentionState);
        Assert.Equal(TaskAttentionVisual.Tint, vm.AttentionVisual);
        Assert.Equal(0, timer.StartCount);   // static modes never arm the settle timer
    }

    [Fact]
    public void Off_disables_the_visual_entirely_even_while_active()
    {
        var (vm, timer, _) = Make(animation: TaskbarAttentionAnimation.Off);
        vm.Update(Win(attentionAt: 1000));

        Assert.Equal(TaskAttentionState.Active, vm.AttentionState);   // state still tracks...
        Assert.Equal(TaskAttentionVisual.None, vm.AttentionVisual);   // ...but nothing renders
        Assert.Equal(0, timer.StartCount);
    }

    [Fact]
    public void Reduced_motion_forces_tint_only_regardless_of_the_animation_setting()
    {
        var (vm, _, _) = Make(animation: TaskbarAttentionAnimation.Pulse, reduceMotion: true);
        vm.Update(Win(attentionAt: 1000));
        Assert.Equal(TaskAttentionVisual.Tint, vm.AttentionVisual);
    }

    [Fact]
    public void A_policy_change_live_re_resolves_a_running_animation()
    {
        var (vm, _, policy) = Make(animation: TaskbarAttentionAnimation.Pulse);
        vm.Update(Win(attentionAt: 1000));
        Assert.Equal(TaskAttentionVisual.Pulse, vm.AttentionVisual);

        policy.Animation = TaskbarAttentionAnimation.Off;   // settings applied live
        vm.RefreshAttentionVisual();
        Assert.Equal(TaskAttentionVisual.None, vm.AttentionVisual);

        policy.Animation = TaskbarAttentionAnimation.Pulse;
        policy.ReduceMotion = true;
        vm.RefreshAttentionVisual();
        Assert.Equal(TaskAttentionVisual.Tint, vm.AttentionVisual);
    }

    [Fact]
    public void A_new_stamp_after_settling_reactivates_the_animation()
    {
        var (vm, timer, _) = Make();
        vm.Update(Win(attentionAt: 1000));
        timer.Fire();
        Assert.Equal(TaskAttentionState.Settled, vm.AttentionState);

        vm.Update(Win(attentionAt: 5000));   // a fresh appear/title-change while still backgrounded

        Assert.Equal(TaskAttentionState.Active, vm.AttentionState);
        Assert.Equal(TaskAttentionVisual.Pulse, vm.AttentionVisual);
    }

    // ── bevel-qb19 review regressions ────────────────────────────────────

    [Fact]
    public void MaxPulses_is_pinned_to_three_literally()
    {
        // The bounded flash-then-tint contract is "3 pulses" — pinned so a drive-by change to the
        // constant can't silently stretch the animation (BoundedSettleDelay derives from it).
        Assert.Equal(3, TaskAttentionPolicy.MaxPulses);
        Assert.Equal(TaskAttentionPolicy.PulsePeriod * 3, TaskAttentionPolicy.BoundedSettleDelay);
    }

    [Fact]
    public void A_title_chatty_window_never_re_arms_the_settle_timer_and_still_settles()
    {
        // bevel-qb19 review: each new title-change stamp used to stop+recreate the settle timer,
        // so a chatty background app pulsed forever. The deadline anchors at the FIRST stamp.
        var (vm, timer, _) = Make(duration: TaskbarAttentionDuration.Bounded);
        vm.Update(Win(attentionAt: 1000));
        Assert.Equal(TaskAttentionState.Active, vm.AttentionState);
        Assert.Equal(1, timer.StartCount);
        Assert.Equal(TaskAttentionPolicy.BoundedSettleDelay, timer.Due);

        vm.Update(Win(attentionAt: 2000));   // title changed again while Active
        vm.Update(Win(attentionAt: 3000));   // ...and again
        vm.Update(Win(attentionAt: 3400));

        Assert.Equal(3400, vm.AttentionAt);               // the stamp is recorded...
        Assert.Equal(1, timer.StartCount);                // ...but the settle timer is NOT re-armed
        Assert.Equal(TaskAttentionPolicy.BoundedSettleDelay, timer.Due);
        Assert.Equal(TaskAttentionState.Active, vm.AttentionState);

        timer.Fire();   // the original (first-stamp-anchored) timer still fires

        Assert.Equal(TaskAttentionState.Settled, vm.AttentionState);   // bounded still settles
        Assert.Equal(TaskAttentionVisual.Tint, vm.AttentionVisual);
    }

    [Fact]
    public void Only_a_strictly_newer_stamp_is_an_event()
    {
        // bevel-qb19 review: the compare is `>` (not `!=`) — an older/equal stamp (clock skew,
        // re-ordered delivery) must not retrigger or regress the recorded stamp.
        var (vm, timer, _) = Make();
        vm.Update(Win(attentionAt: 5000));
        Assert.Equal(1, timer.StartCount);

        vm.Update(Win(attentionAt: 3000));   // older — not an event
        Assert.Equal(5000, vm.AttentionAt);
        Assert.Equal(1, timer.StartCount);

        vm.Update(Win(attentionAt: 5000));   // equal — not an event either
        Assert.Equal(5000, vm.AttentionAt);
        Assert.Equal(1, timer.StartCount);
        Assert.Equal(TaskAttentionState.Active, vm.AttentionState);
    }

    [Fact]
    public void App_presence_entries_never_animate()
    {
        // bevel-qb19 review: an app-presence (windowless) entry must not pulse even if it carries
        // a stamp — the icon-only button animating on last-window-close was noise.
        var (vm, timer, _) = Make(w: Win(isAppPresence: true));
        Assert.True(vm.IsAppPresence);

        vm.Update(Win(attentionAt: 1000, isAppPresence: true));

        Assert.Equal(0, vm.AttentionAt);                     // not even recorded
        Assert.Equal(TaskAttentionState.Idle, vm.AttentionState);
        Assert.Equal(TaskAttentionVisual.None, vm.AttentionVisual);
        Assert.Equal(0, timer.StartCount);
    }

    [Fact]
    public void A_group_button_mirrors_a_child_s_active_attention_visual()
    {
        // One child of a grouped app goes Active → the group button flashes too.
        var policy = new TaskAttentionPolicy
        {
            Animation = TaskbarAttentionAnimation.Pulse,
            Duration = TaskbarAttentionDuration.Bounded,
        };
        var timer = new ManualAttentionTimer();
        var child = new TaskItemViewModel(Win(), new StubWindows(), attention: policy, attentionTimer: timer);
        var group = new TaskGroupViewModel("com.example.app");
        group.Windows.Add(child);

        Assert.Equal(TaskAttentionVisual.None, group.AttentionVisual);

        child.Update(Win(attentionAt: 1000));

        Assert.Equal(TaskAttentionState.Active, child.AttentionState);
        Assert.Equal(TaskAttentionVisual.Pulse, child.AttentionVisual);
        Assert.Equal(TaskAttentionVisual.Pulse, group.AttentionVisual);   // group mirrors the child

        child.SetFocused(true);   // acknowledged
        Assert.Equal(TaskAttentionVisual.None, group.AttentionVisual);
    }

    [Fact]
    public void Reduce_motion_probe_fails_closed_on_any_lookup_error()
    {
        // bevel-qb19 review: the probe used to fail OPEN (false on any lookup error). Fail CLOSED:
        // on macOS an unanswerable probe means "assume reduce motion ON" (tint-only).
        Assert.False(TaskbarNative.EvaluateReduceMotion(isMacOS: true, nsWorkspaceClassFound: true, workspaceInstanceFound: true, probeResult: 0));
        Assert.True(TaskbarNative.EvaluateReduceMotion(isMacOS: true, nsWorkspaceClassFound: true, workspaceInstanceFound: true, probeResult: 1));
        Assert.True(TaskbarNative.EvaluateReduceMotion(isMacOS: true, nsWorkspaceClassFound: false, workspaceInstanceFound: false, probeResult: 0),
            "class lookup failed → closed (assume reduced motion)");
        Assert.True(TaskbarNative.EvaluateReduceMotion(isMacOS: true, nsWorkspaceClassFound: true, workspaceInstanceFound: false, probeResult: 0),
            "shared-workspace lookup failed → closed (assume reduced motion)");
        Assert.False(TaskbarNative.EvaluateReduceMotion(isMacOS: false, nsWorkspaceClassFound: false, workspaceInstanceFound: false, probeResult: 0),
            "non-macOS has no such OS setting — not a failure, reports false");
    }
}
