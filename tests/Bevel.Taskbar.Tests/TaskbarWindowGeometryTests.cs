using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Taskbar;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-aqr7 Task 8 fix round 1, Finding 2: pins the <c>SetRows</c> rebuild path that must
/// preserve per-instance height contributions across a row-count change. Also documents (see
/// <see cref="TaskbarWindow.ContributeHeight"/>'s own doc comment) that recording a contribution
/// does NOT yet move the rendered bar — <c>ReapplyMetrics</c> still sizes the window from the
/// <c>TaskbarTheme</c> facade, not from <c>Geometry.Height</c>; migrating that is follow-on work.
/// </summary>
[Collection("TaskbarTheme")]
public class TaskbarWindowGeometryTests
{
    [AvaloniaFact]
    public void Contributions_survive_a_SetRows_rebuild()
    {
        var window = new TaskbarWindow(null, rows: 1);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.ContributeHeight("a", 40);
        Assert.Equal(40, window.Geometry.ButtonHeight);

        window.SetRows(2);

        // The rebuild inside SetRows must carry the instance's contribution forward rather than
        // dropping it — otherwise a row-count change would silently reset every component's
        // recorded height.
        Assert.Equal(40, window.Geometry.ButtonHeight);
        Assert.True(window.Geometry.Contributions.TryGetValue("a", out var h));
        Assert.Equal(40, h);
    }

    [AvaloniaFact]
    public void ContributeHeight_does_not_yet_move_the_rendered_bar()
    {
        // Pins the documented gap: recording a contribution changes Geometry but — until the
        // follow-on migrates ReapplyMetrics off the TaskbarTheme facade — not MinHeight/MaxHeight.
        var window = new TaskbarWindow(null, rows: 1);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var before = window.MinHeight;
        window.ContributeHeight("a", 40);

        Assert.Equal(40, window.Geometry.ButtonHeight);
        Assert.Equal(before, window.MinHeight);
    }

    // bevel-u6ue (review follow-up): the zoom area is cached from Screen.WorkingArea, which also moves
    // when the menu bar auto-hides or a Dock appears, events that raise neither PositionChanged,
    // ScalingChanged nor Screens.Changed. A periodic re-check must notice, and must stay silent
    // otherwise, since a change kicks a mitigation pass.
    [AvaloniaFact]
    public void Work_area_refresh_notices_a_stale_zoom_area_and_is_silent_otherwise()
    {
        var window = new TaskbarWindow(null, rows: 1);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.RefreshWorkArea());                      // nothing changed since opening
        Assert.False(window.RefreshWorkArea());

        window.StaleWorkAreaForTest();                               // the real top moved under the cache
        Assert.True(window.RefreshWorkArea());
        Assert.False(window.RefreshWorkArea());                      // refreshed: quiet again
    }
}
