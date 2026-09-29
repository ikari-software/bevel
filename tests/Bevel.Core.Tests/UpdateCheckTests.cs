using System;
using System.IO;
using Bevel.Core.Updates;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>
/// The update-check cadence (bevel-rkxb). These pin the policy, which is the part Bevel owns: the feed
/// format and the apply step belong to Velopack (UPD-01, bevel-ym0).
/// </summary>
public class UpdateCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    // ── Disabled is the default, and disabled means silent ────────────────

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("https://example.test/feed.json", true)]
    public void A_feed_url_is_what_enables_checking(string? url, bool enabled)
        // A shell nobody has configured must make no network calls at all, so "no URL" is not an error
        // state or a default-URL fallback — it is simply off.
        => Assert.Equal(enabled, UpdateCheckPolicy.IsEnabled(url));

    // ── The interval is wall-clock, not per-launch ────────────────────────

    [Fact]
    public void A_never_checked_shell_is_due_immediately()
        => Assert.True(UpdateCheckPolicy.IsDue(Now, lastCheck: null, TimeSpan.FromHours(8)));

    [Fact]
    public void A_recent_check_is_not_due_again()
        => Assert.False(UpdateCheckPolicy.IsDue(Now, Now.AddHours(-1), TimeSpan.FromHours(8)));

    [Fact]
    public void A_check_older_than_the_interval_is_due()
        => Assert.True(UpdateCheckPolicy.IsDue(Now, Now.AddHours(-8), TimeSpan.FromHours(8)));

    [Fact]
    public void A_last_check_in_the_future_is_due_rather_than_parked()
    {
        // A clock that moved backwards (timezone fix, NTP correction, a restored backup) would otherwise
        // park the checker until real time caught up with the recorded future.
        Assert.True(UpdateCheckPolicy.IsDue(Now, Now.AddHours(+5), TimeSpan.FromHours(8)));
    }

    [Fact]
    public void Restarting_does_not_reset_the_interval()
    {
        // The whole reason the timestamp is persisted: a shell relaunched more often than the interval
        // must not check on every launch. That is the normal case in development and right after an
        // update, which is precisely when a fleet would hammer a freshly published feed.
        var dir = Path.Combine(Path.GetTempPath(), "bevel-upd-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            var state = new UpdateCheckState(dir);
            Assert.Null(state.LastCheck);

            state.Record(Now);
            var afterRestart = new UpdateCheckState(dir);   // a fresh process reading the same config dir
            Assert.NotNull(afterRestart.LastCheck);
            Assert.False(UpdateCheckPolicy.IsDue(Now.AddHours(1), afterRestart.LastCheck, TimeSpan.FromHours(8)));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void An_unreadable_record_counts_as_never_checked()
    {
        // Better to check again than to never check: a corrupt file must not silently disable updates.
        var dir = Path.Combine(Path.GetTempPath(), "bevel-upd-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "update-check.txt"), "not a timestamp");
            Assert.Null(new UpdateCheckState(dir).LastCheck);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // ── Guard rails ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 1)]        // a mistyped 0 would be a hot loop against someone's server
    [InlineData(-5, 1)]
    [InlineData(8, 8)]
    [InlineData(100000, 168)] // a week ceiling keeps "enabled" meaningful
    public void The_interval_is_clamped_to_something_sane(int configured, int expectedHours)
        => Assert.Equal(TimeSpan.FromHours(expectedHours), UpdateCheckPolicy.ClampInterval(configured));

    [Fact]
    public void The_default_interval_is_eight_hours()
        => Assert.Equal(8, UpdateCheckPolicy.DefaultIntervalHours);

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(1.0, 48)]     // at most 10% of an 8h interval, in minutes
    [InlineData(0.5, 24)]
    public void Jitter_spreads_the_herd_but_is_bounded(double unitRandom, int expectedMinutes)
    {
        // Every shell installed from one release would otherwise check at the same offset from install,
        // turning a popular release into a spike at the feed.
        var jitter = UpdateCheckPolicy.Jitter(TimeSpan.FromHours(8), unitRandom);
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), jitter);
    }

    [Fact]
    public void Delay_until_due_never_goes_negative_or_exceeds_the_interval()
    {
        var interval = TimeSpan.FromHours(8);
        Assert.Equal(TimeSpan.Zero, UpdateCheckPolicy.DelayUntilDue(Now, null, interval));
        Assert.Equal(TimeSpan.Zero, UpdateCheckPolicy.DelayUntilDue(Now, Now.AddHours(-9), interval));
        Assert.Equal(TimeSpan.FromHours(7), UpdateCheckPolicy.DelayUntilDue(Now, Now.AddHours(-1), interval));
    }

    // ── The source contract ──────────────────────────────────────────────

    [Fact]
    public async System.Threading.Tasks.Task The_default_source_reports_nothing_available()
    {
        // Until Velopack lands (bevel-ym0), and on any build with no feed, "nothing available" must be
        // indistinguishable from "up to date" — never an error the user has to act on.
        var result = await NoUpdateSource.Instance.CheckAsync("https://example.test/feed.json", "1.0.0", default);
        Assert.False(result.Available);
        Assert.Same(UpdateAvailability.None, UpdateAvailability.None);
    }
}
