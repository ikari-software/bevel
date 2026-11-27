using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Core;
using Xunit;

namespace Bevel.Taskbar.Tests;

[Collection("TaskbarTheme")]
public class LiveApplyDiffTests
{
    [AvaloniaFact]
    public void Reapplying_an_identical_snapshot_reconfigures_nothing()
    {
        // Arrange: build a minimal TaskbarView like StartMenuSettingsRaceTests
        var settings = new BevelSettings();
        TestSettings.NoStacks(settings);
        
        var view = new TaskbarView();
        view.Initialize(settings);
        
        // Get the clock from the view
        var clock = view.ClockControl;
        var initialCount = clock.ConfigureCount;

        // First apply
        var s1 = settings.Clone();
        view.ApplyLiveSettings(s1);
        var afterFirst = clock.ConfigureCount;

        // Second apply with identical settings - should be a no-op for clock
        var s2 = settings.Clone();
        view.ApplyLiveSettings(s2);
        var afterSecond = clock.ConfigureCount;

        // Before the fix: clock gets reconfigured every time (afterSecond > afterFirst)
        // After the fix: clock should not be reconfigured (afterSecond == afterFirst)
        Assert.Equal(afterFirst, afterSecond);
    }

    [AvaloniaFact]
    public void Changing_only_opacity_does_not_rebuild_clock()
    {
        // Arrange
        var settings = new BevelSettings();
        TestSettings.NoStacks(settings);
        
        var view = new TaskbarView();
        view.Initialize(settings);
        
        var clock = view.ClockControl;
        var initialCount = clock.ConfigureCount;

        // Apply initial state
        view.ApplyLiveSettings(settings);
        var afterInitial = clock.ConfigureCount;

        // Change only opacity - should NOT affect clock
        var modified = settings.Clone();
        modified.TaskbarOpacity = 80;
        view.ApplyLiveSettings(modified);
        var afterOpacityChange = clock.ConfigureCount;

        // Clock should not have been reconfigured
        Assert.Equal(afterInitial, afterOpacityChange);
    }

    [AvaloniaFact]
    public void Changing_clock_settings_does_rebuild_clock()
    {
        // Arrange
        var settings = new BevelSettings();
        TestSettings.NoStacks(settings);
        
        var view = new TaskbarView();
        view.Initialize(settings);
        
        var clock = view.ClockControl;
        
        // Apply initial state
        view.ApplyLiveSettings(settings);
        var afterInitial = clock.ConfigureCount;

        // Change clock settings - SHOULD reconfigure clock
        var modified = settings.Clone();
        modified.TaskbarClock24Hour = !settings.TaskbarClock24Hour;
        view.ApplyLiveSettings(modified);
        var afterClockChange = clock.ConfigureCount;

        // Clock should have been reconfigured exactly once
        Assert.Equal(afterInitial + 1, afterClockChange);
    }

    [AvaloniaFact]
    public void Flipping_taskbarAttentionAnimation_live_pushes_the_new_animation_into_the_shared_policy()
    {
        // bevel-qb19 review pin: the attention settings are part of the live-apply diff — flipping
        // taskbarAttentionAnimation must reach TaskAttentionPolicy.Shared (every existing button
        // resolves its overlay visual through it) without a restart.
        var previous = TaskAttentionPolicy.Shared.Animation;
        try
        {
            var settings = new BevelSettings { TaskbarAttentionAnimation = TaskbarAttentionAnimation.Pulse };
            TestSettings.NoStacks(settings);

            var view = new TaskbarView();
            view.Initialize(settings);
            Assert.Equal(TaskbarAttentionAnimation.Pulse, TaskAttentionPolicy.Shared.Animation);

            var flipped = settings.Clone();
            flipped.TaskbarAttentionAnimation = TaskbarAttentionAnimation.Sweep;
            view.ApplyLiveSettings(flipped);

            Assert.Equal(TaskbarAttentionAnimation.Sweep, TaskAttentionPolicy.Shared.Animation);

            // An identical re-apply leaves it alone (diff guard — no churn).
            view.ApplyLiveSettings(flipped.Clone());
            Assert.Equal(TaskbarAttentionAnimation.Sweep, TaskAttentionPolicy.Shared.Animation);
        }
        finally
        {
            TaskAttentionPolicy.Shared.Animation = previous;
        }
    }
}
