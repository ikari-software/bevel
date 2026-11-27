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

    private static ForeignWindow Win(long attentionAt = 0, bool focused = false) => new(
        new ForeignWindowId("w-1"),
        Title: "Doc",
        AppId: "com.example.app",
        IsMinimized: false,
        IsFocused: focused,
        Bounds: new PalRect(0, 0, 100, 100),
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
}
