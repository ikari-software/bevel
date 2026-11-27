using System;
using Bevel.Core;

namespace Bevel.Taskbar;

/// <summary>bevel-qb19: what the taskbar does while a window asks for attention.</summary>
public enum TaskAttentionState
{
    /// <summary>No attention request showing (default, and after the request is acknowledged).</summary>
    Idle,

    /// <summary>Actively animating (pulsing / sweeping) — the bounded timer may still settle it.</summary>
    Active,

    /// <summary>Bounded mode after the pulse budget ran out: static tint, no animation.</summary>
    Settled,
}

/// <summary>bevel-qb19: the visual the overlay renders for a taskbar button. Pure function of
/// <see cref="TaskAttentionState"/> + the live <see cref="TaskAttentionPolicy"/>.</summary>
public enum TaskAttentionVisual
{
    None,
    Pulse,
    Sweep,
    Tint,
}

/// <summary>
/// Attention-display policy for the task strip (bevel-qb19) — the animation mode and duration the
/// settings expose, plus the OS reduced-motion flag that forces tint-only.
///
/// Like <see cref="TaskButtonClickPolicy"/>, it lives in one small mutable holder rather than on each
/// <see cref="TaskItemViewModel"/>: the setting must live-apply to buttons that already exist, so the
/// view writes it once into <see cref="Shared"/> and every button resolves its visual through it.
/// Tests construct their own instance so nothing is asserted against global state.
/// </summary>
public sealed class TaskAttentionPolicy
{
    /// <summary>The taskbar process's live policy. <c>TaskbarView</c> pushes the settings + the
    /// reduced-motion probe into it on initialize and on every settings-changed apply.</summary>
    public static TaskAttentionPolicy Shared { get; } = new();

    /// <summary>One animation cycle (pulse period / sweep whoosh duration).</summary>
    public static readonly TimeSpan PulsePeriod = TimeSpan.FromSeconds(1.2);

    /// <summary>Bounded mode settles after this many pulses (~4s) — the classic Windows
    /// bounded flash-then-tint.</summary>
    public const int MaxPulses = 3;

    /// <summary>When bounded mode gives up animating and settles into the static tint.</summary>
    public static TimeSpan BoundedSettleDelay => PulsePeriod * MaxPulses;

    /// <summary>Configured animation (setting <c>taskbarAttentionAnimation</c>). Default pulse.</summary>
    public TaskbarAttentionAnimation Animation { get; set; } = TaskbarAttentionAnimation.Pulse;

    /// <summary>Configured duration (setting <c>taskbarAttentionDuration</c>). Default bounded.</summary>
    public TaskbarAttentionDuration Duration { get; set; } = TaskbarAttentionDuration.Bounded;

    /// <summary>macOS Reduce Motion (com.apple.universalaccess reduceMotion), probed via NSWorkspace.
    /// When set, the effective animation is forced to tint-only regardless of <see cref="Animation"/>.</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>The animation actually played: reduced motion always wins (accessibility over config).</summary>
    public TaskbarAttentionAnimation EffectiveAnimation =>
        ReduceMotion ? TaskbarAttentionAnimation.TintOnly : Animation;

    /// <summary>The duration actually applied: tint-only and off are static, so they settle
    /// immediately — bounded semantics — regardless of the configured duration.</summary>
    public TaskbarAttentionDuration EffectiveDuration =>
        EffectiveAnimation is TaskbarAttentionAnimation.TintOnly or TaskbarAttentionAnimation.Off
            ? TaskbarAttentionDuration.Bounded
            : Duration;

    /// <summary>Resolves the overlay visual for a VM state under this policy.</summary>
    public static TaskAttentionVisual ResolveVisual(TaskAttentionState state, TaskAttentionPolicy policy)
    {
        var anim = policy.EffectiveAnimation;
        if (anim == TaskbarAttentionAnimation.Off || state == TaskAttentionState.Idle)
            return TaskAttentionVisual.None;
        if (state == TaskAttentionState.Settled)
            return TaskAttentionVisual.Tint;   // bounded flash-then-tint: the settle is always a static tint
        return anim switch
        {
            TaskbarAttentionAnimation.Pulse => TaskAttentionVisual.Pulse,
            TaskbarAttentionAnimation.Sweep => TaskAttentionVisual.Sweep,
            _ => TaskAttentionVisual.Tint,
        };
    }
}
