using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>bevel-sdss: the pure diff behind IPermissionBroker.PermissionChanged. TCC has no change
/// notification, so the broker polls and this tracker turns each poll into the set of permissions
/// whose state actually flipped — the first observation establishes the baseline and reports nothing.</summary>
public class PermissionChangeTrackerTests
{
    [Fact]
    public void First_observation_reports_nothing()
    {
        var tracker = new PermissionChangeTracker();
        Assert.Empty(tracker.Observe(PermissionState.Denied, PermissionState.Denied));
    }

    [Fact]
    public void A_flip_reports_exactly_that_permission()
    {
        var tracker = new PermissionChangeTracker();
        tracker.Observe(PermissionState.Denied, PermissionState.Denied);

        Assert.Equal(new[] { ShellPermission.Accessibility },
            tracker.Observe(PermissionState.Granted, PermissionState.Denied));
        // Once reported, the new state is the baseline — no repeat.
        Assert.Empty(tracker.Observe(PermissionState.Granted, PermissionState.Denied));

        Assert.Equal(new[] { ShellPermission.ScreenRecording },
            tracker.Observe(PermissionState.Granted, PermissionState.Granted));
    }

    [Fact]
    public void No_change_reports_nothing()
    {
        var tracker = new PermissionChangeTracker();
        tracker.Observe(PermissionState.Granted, PermissionState.Granted);
        Assert.Empty(tracker.Observe(PermissionState.Granted, PermissionState.Granted));
    }
}
