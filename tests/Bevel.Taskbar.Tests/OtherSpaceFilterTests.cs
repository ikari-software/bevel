using System.Collections.ObjectModel;
using Bevel.Core;
using Bevel.Pal.Abstractions;
using Bevel.TestSupport;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-jt30: the "list windows on other Spaces" setting filters in the taskbar PROJECTION
/// (<see cref="TaskbarItemsProjector.SetShowOtherSpaces"/>), live, without touching the helper/PAL.
/// App-presence (windowless) entries are never filtered.
/// </summary>
public class OtherSpaceFilterTests
{
    private static readonly IWindowManager Wm = new StubWm();

    private static TaskItemViewModel Win(string id, bool otherSpace = false, bool appPresence = false, string appId = "com.test")
        => new(new ForeignWindow(new ForeignWindowId(id), id, appId,
               IsMinimized: false, IsFocused: false, Bounds: default,
               IsAppPresence: appPresence, IsOnOtherSpace: otherSpace), Wm);

    private static TaskItemViewModel WithSpace(TaskItemViewModel vm, bool otherSpace)
    {
        vm.Update(new ForeignWindow(vm.Id, vm.Title, "com.test", false, false, default, IsOnOtherSpace: otherSpace));
        return vm;
    }

    [Fact]
    public void Other_space_windows_are_listed_by_default()
    {
        var src = new ObservableCollection<TaskItemViewModel> { Win("a"), Win("b", otherSpace: true) };
        using var proj = new TaskbarItemsProjector(src, mode: TaskbarGroupingMode.Never);

        Assert.Equal(2, proj.Items.Count);
    }

    [Fact]
    public void Turning_the_setting_off_drops_other_space_windows_and_keeps_current_ones()
    {
        var a = Win("a");
        var b = Win("b", otherSpace: true);
        // never filtered — and a DIFFERENT app id, since an app-presence entry is suppressed when a
        // real window of the same app exists.
        var presence = Win("app:com.other", appPresence: true, otherSpace: true, appId: "com.other");
        var src = new ObservableCollection<TaskItemViewModel> { a, b, presence };
        using var proj = new TaskbarItemsProjector(src, mode: TaskbarGroupingMode.Never);

        proj.SetShowOtherSpaces(false);

        Assert.Equal(new[] { a, presence }, proj.Items.OfType<TaskItemViewModel>().ToArray());

        proj.SetShowOtherSpaces(true);
        Assert.Equal(3, proj.Items.Count);
    }

    [Fact]
    public void A_window_that_moves_to_the_current_space_reappears()
    {
        var a = Win("a");
        var b = Win("b", otherSpace: true);
        var src = new ObservableCollection<TaskItemViewModel> { a, b };
        using var proj = new TaskbarItemsProjector(src, mode: TaskbarGroupingMode.Never);
        proj.SetShowOtherSpaces(false);
        Assert.Single(proj.Items);

        WithSpace(b, otherSpace: false);   // the Space switch lands → Update carries the flag

        Assert.Equal(2, proj.Items.Count);
        Assert.Contains(b, proj.Items);
    }

    private sealed class StubWm : TestWindowManager
    {
        public StubWm() => Capabilities = RepositionCapable;
    }
}
