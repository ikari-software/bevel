using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Bevel.App.Updates;
using Bevel.Core;
using Bevel.Core.Updates;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The glue between the update-check policy and the feed (bevel-rkxb). The cadence DECISIONS are pinned
/// in Bevel.Core.Tests.UpdateCheckTests; these cover the part that is not pure — whether the source is
/// actually consulted, and whether the timestamp is written.
///
/// Lives here because Bevel.App's internals are visible to this project.
/// </summary>
public class UpdateCheckServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "bevel-updsvc-" + Guid.NewGuid().ToString("N")[..10]);

    public UpdateCheckServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private async Task<SettingsService> SettingsAsync(Action<BevelSettings> configure)
    {
        var s = new SettingsService(_dir);
        await s.LoadAsync();
        await s.UpdateAsync(configure);
        return s;
    }

    [Fact]
    public async Task An_unconfigured_shell_never_touches_the_network()
    {
        // The default is an empty feed URL, and it must mean "make no requests" — not "fall back to a
        // built-in URL". There is no default feed to point at: publishing one is gated on the site.
        using var settings = await SettingsAsync(_ => { });
        var source = new CountingSource();
        var svc = new UpdateCheckService(settings, source, new UpdateCheckState(_dir));

        var wait = await svc.CheckIfDueAsync(CancellationToken.None);

        Assert.Equal(0, source.Calls);
        Assert.Null(svc.Latest);
        // It still comes back on the interval, so enabling the setting does not need a restart.
        Assert.Equal(TimeSpan.FromHours(UpdateCheckPolicy.DefaultIntervalHours), wait);
    }

    [Fact]
    public async Task A_configured_shell_asks_once_and_records_when_it_asked()
    {
        using var settings = await SettingsAsync(s => s.UpdateFeedUrl = "https://example.test/feed.json");
        var source = new CountingSource { Result = new UpdateAvailability(true, "9.9.9") };
        var state = new UpdateCheckState(_dir);
        var svc = new UpdateCheckService(settings, source, state, random: () => 0);   // no jitter delay

        await svc.CheckIfDueAsync(CancellationToken.None);
        Assert.Equal(1, source.Calls);
        Assert.True(svc.Latest?.Available);
        Assert.Equal("9.9.9", svc.Latest?.Version);
        Assert.NotNull(state.LastCheck);

        // Immediately due again? No — the recorded timestamp is what stops a relaunch loop re-asking.
        await svc.CheckIfDueAsync(CancellationToken.None);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task A_failing_feed_looks_like_no_update_and_still_backs_off()
    {
        // "We could not ask" must be indistinguishable from "nothing new" — never an error the user has
        // to act on — and it must NOT retry on every loop iteration against a server that is down.
        using var settings = await SettingsAsync(s => s.UpdateFeedUrl = "https://example.test/feed.json");
        var source = new CountingSource { Throw = true };
        var state = new UpdateCheckState(_dir);
        var svc = new UpdateCheckService(settings, source, state, random: () => 0);

        var wait = await svc.CheckIfDueAsync(CancellationToken.None);

        Assert.Equal(1, source.Calls);
        Assert.False(svc.Latest?.Available);
        Assert.NotNull(state.LastCheck);                                   // backed off
        Assert.Equal(TimeSpan.FromHours(UpdateCheckPolicy.DefaultIntervalHours), wait);
    }

    [Fact]
    public async Task The_configured_interval_is_honoured_and_clamped()
    {
        using var settings = await SettingsAsync(s =>
        {
            s.UpdateFeedUrl = "https://example.test/feed.json";
            s.UpdateCheckIntervalHours = 0;    // a mistyped 0 must not become a hot loop
        });
        var svc = new UpdateCheckService(settings, new CountingSource(), new UpdateCheckState(_dir),
            random: () => 0);

        await svc.CheckIfDueAsync(CancellationToken.None);
        var wait = await svc.CheckIfDueAsync(CancellationToken.None);

        // Close to an hour, not exactly: this path runs on the real clock, so the microseconds spent
        // between recording the check and computing the next wait legitimately come off the total.
        Assert.InRange(wait, TimeSpan.FromMinutes(59), TimeSpan.FromHours(1));
    }

    private sealed class CountingSource : IUpdateSource
    {
        public int Calls;
        public bool Throw { get; init; }
        public UpdateAvailability Result { get; init; } = UpdateAvailability.None;

        public ValueTask<UpdateAvailability> CheckAsync(string feedUrl, string currentVersion, CancellationToken ct)
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("feed unreachable");
            return ValueTask.FromResult(Result);
        }
    }
}
