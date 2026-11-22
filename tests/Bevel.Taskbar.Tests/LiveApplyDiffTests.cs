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
}
