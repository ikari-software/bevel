using System;
using System.Threading;
using System.Threading.Tasks;
using Bevel.Core;
using Bevel.Core.Updates;
using Bevel.App.Supervision;
using Microsoft.Extensions.Hosting;

namespace Bevel.App.Updates;

/// <summary>
/// Asks, on a schedule, whether a newer Bevel exists (bevel-rkxb).
///
/// Registered for the CORE role only. Core already owns settings.db and the helper and serves peers over
/// the shell-core IPC; if every peer polled its own feed, a five-process shell would make five times the
/// requests and five processes would each have to decide what to do about the answer.
///
/// It NEVER applies anything. UPD-01 is explicit that an update is not applied mid-session; this notices,
/// and applying rides the existing quit/restart path. It also never surfaces a failure: a feed that is
/// unreachable is indistinguishable, to the user, from being up to date.
///
/// Until Velopack is wired (bevel-ym0) the registered source is <see cref="NoUpdateSource"/>, so this
/// loop runs, persists its cadence and reports nothing — which is also exactly what it does on a shell
/// with no feed URL configured, the default.
/// </summary>
internal sealed class UpdateCheckService : BackgroundService
{
    private readonly ISettingsService _settings;
    private readonly IUpdateSource _source;
    private readonly UpdateCheckState _state;
    private readonly TimeProvider _time;
    private readonly Func<double> _random;

    /// <summary>The most recent answer, for anything that wants to surface it. Null until a check has
    /// actually run.</summary>
    public UpdateAvailability? Latest { get; private set; }

    public UpdateCheckService(
        ISettingsService settings,
        IUpdateSource source,
        UpdateCheckState state,
        TimeProvider? time = null,
        Func<double>? random = null)
    {
        _settings = settings;
        _source = source;
        _state = state;
        _time = time ?? TimeProvider.System;
        _random = random ?? Random.Shared.NextDouble;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = await CheckIfDueAsync(stoppingToken).ConfigureAwait(false);
            await Sleep(wait, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>One pass of the decision: check if a check is due, and report how long to wait before
    /// asking again. Separated from the loop so the glue is testable without controlling a timer — the
    /// cadence decisions themselves live in <see cref="UpdateCheckPolicy"/> and are tested there.</summary>
    internal async Task<TimeSpan> CheckIfDueAsync(CancellationToken ct)
    {
        var current = _settings.Current;
        var interval = UpdateCheckPolicy.ClampInterval(current.UpdateCheckIntervalHours);

        // Disabled is the default. Re-read on the interval anyway so switching it on does not require a
        // restart — that is how every other live setting behaves.
        if (!UpdateCheckPolicy.IsEnabled(current.UpdateFeedUrl)) return interval;

        var now = _time.GetUtcNow();
        var wait = UpdateCheckPolicy.DelayUntilDue(now, _state.LastCheck, interval);
        if (wait > TimeSpan.Zero) return wait;

        // Jitter only before an actually-due check, so a fleet installed from one release does not arrive
        // at the feed together.
        await Sleep(UpdateCheckPolicy.Jitter(interval, _random()), ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return interval;

        try
        {
            Latest = await _source
                .CheckAsync(current.UpdateFeedUrl, BuildStamp.VersionString(), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return interval; }
        catch (Exception)
        {
            // Never surface a check failure: "we could not ask" must look like "nothing new", not like
            // something the user has to act on.
            Latest = UpdateAvailability.None;
        }

        // Recorded even on failure, so an unreachable feed is retried on the interval rather than on
        // every loop iteration.
        _state.Record(_time.GetUtcNow());
        return interval;
    }

    private async Task Sleep(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero) return;
        try { await Task.Delay(delay, _time, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}
