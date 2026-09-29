using System;

namespace Bevel.Core.Updates;

/// <summary>
/// When to check for updates. Pure decisions, separated from the loop that acts on them so the cadence
/// is testable without a clock, a network or a process.
///
/// Two rules come from the spec rather than from taste:
///   - a check NEVER applies anything mid-session (00-master-plan UPD-01), so this decides only when to
///     ASK; applying rides the existing restart path.
///   - the interval is wall-clock and survives restarts. A shell that is relaunched often — which is the
///     normal case during development, and after every update — must not re-check on every launch.
/// </summary>
public static class UpdateCheckPolicy
{
    /// <summary>Default cadence. Frequent enough to notice a release the day it lands, rare enough that a
    /// fleet of shells is not a load source.</summary>
    public const int DefaultIntervalHours = 8;

    /// <summary>Clamp for the configured interval: an hour floor stops a mistyped setting turning into a
    /// hot loop against someone's server, and a week ceiling keeps "enabled" meaningful.</summary>
    public static TimeSpan ClampInterval(int hours) =>
        TimeSpan.FromHours(Math.Clamp(hours, 1, 24 * 7));

    /// <summary>Checking requires somewhere to check. An empty feed URL is the disabled state, and it is
    /// the DEFAULT — a shell that has never been configured must make no network calls at all.</summary>
    public static bool IsEnabled(string? feedUrl) => !string.IsNullOrWhiteSpace(feedUrl);

    /// <summary>Whether a check is due. A missing or future-dated last-check counts as due: a clock that
    /// moved backwards should not park the checker until the clock catches up.</summary>
    public static bool IsDue(DateTimeOffset now, DateTimeOffset? lastCheck, TimeSpan interval)
    {
        if (lastCheck is not { } last) return true;
        if (last > now) return true;
        return now - last >= interval;
    }

    /// <summary>How long to wait before the next check, given when the last one happened. Never negative,
    /// and never longer than the interval itself.</summary>
    public static TimeSpan DelayUntilDue(DateTimeOffset now, DateTimeOffset? lastCheck, TimeSpan interval)
    {
        if (IsDue(now, lastCheck, interval)) return TimeSpan.Zero;
        var remaining = interval - (now - lastCheck!.Value);
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    /// <summary>Spreads the herd. Every shell installed from the same release would otherwise check at the
    /// same offset from its install, and a popular release would arrive as a spike. Up to 10% of the
    /// interval, derived from a caller-supplied random in [0,1) so the result stays testable.</summary>
    public static TimeSpan Jitter(TimeSpan interval, double unitRandom) =>
        interval * (0.1 * Math.Clamp(unitRandom, 0, 1));
}
