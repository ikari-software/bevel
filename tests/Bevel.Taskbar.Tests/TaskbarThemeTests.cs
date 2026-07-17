using Bevel.Core;
using Bevel.Taskbar;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Button-height tiers (bevel-m2.10.1): <see cref="TaskbarTheme.Configure"/> maps Small/Normal/Large
/// to the button height, from which the per-row and single-row bar heights derive. Normal must
/// reproduce the Win2000 classic 24/28/30 exactly. Runs serially and restores Normal so it can't
/// perturb other tests that read the shared metrics.
/// </summary>
[Collection("TaskbarTheme")]
public class TaskbarThemeTests
{
    [Fact]
    public void Tiers_map_to_button_row_and_bar_heights()
    {
        try
        {
            TaskbarTheme.Configure(TaskbarButtonSize.Normal);
            Assert.Equal(24, TaskbarTheme.ButtonHeight);
            Assert.Equal(28, TaskbarTheme.RowHeight);
            Assert.Equal(30, TaskbarTheme.TaskbarHeight);
            Assert.Equal(30, TaskbarTheme.HeightForRows(1));
            Assert.Equal(58, TaskbarTheme.HeightForRows(2));   // 30 + 28

            TaskbarTheme.Configure(TaskbarButtonSize.Small);
            Assert.Equal(18, TaskbarTheme.ButtonHeight);
            Assert.Equal(22, TaskbarTheme.RowHeight);
            Assert.Equal(24, TaskbarTheme.TaskbarHeight);

            TaskbarTheme.Configure(TaskbarButtonSize.Large);
            Assert.Equal(30, TaskbarTheme.ButtonHeight);
            Assert.Equal(34, TaskbarTheme.RowHeight);
            Assert.Equal(36, TaskbarTheme.TaskbarHeight);
        }
        finally
        {
            TaskbarTheme.Configure(TaskbarButtonSize.Normal);   // restore the shared default
        }
    }
}
