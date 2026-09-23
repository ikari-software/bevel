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

            // Big icons (bevel-c54t): the Win10/11 bar — 40px button → row 44, bar 46. The height
            // authority is unchanged (HeightForRows still derives everything), so the work-area band
            // and ResizeToRows follow automatically.
            TaskbarTheme.Configure(TaskbarButtonSize.Big);
            Assert.Equal(40, TaskbarTheme.ButtonHeight);
            Assert.Equal(44, TaskbarTheme.RowHeight);
            Assert.Equal(46, TaskbarTheme.TaskbarHeight);
            Assert.Equal(90, TaskbarTheme.HeightForRows(2));   // 46 + 44
        }
        finally
        {
            TaskbarTheme.Configure(TaskbarButtonSize.Normal);   // restore the shared default
        }
    }

    /// <summary>
    /// bevel-c54t: the taller tiers must grow the GLYPH, not just the chrome. Small/Normal keep the
    /// classic 16px icon; Large steps to 24 and Big to 32 — the sizes the helper's 64×64 source PNG can
    /// serve by downscaling, so nothing is upscaled from a 16px bitmap.
    /// </summary>
    [Fact]
    public void Tiers_grow_the_task_icon_not_just_the_chrome()
    {
        try
        {
            TaskbarTheme.Configure(TaskbarButtonSize.Small);
            Assert.Equal(16, TaskbarTheme.TaskIconSize);

            TaskbarTheme.Configure(TaskbarButtonSize.Normal);
            Assert.Equal(16, TaskbarTheme.TaskIconSize);

            TaskbarTheme.Configure(TaskbarButtonSize.Large);
            Assert.Equal(24, TaskbarTheme.TaskIconSize);

            TaskbarTheme.Configure(TaskbarButtonSize.Big);
            Assert.Equal(32, TaskbarTheme.TaskIconSize);

            // The glyph always fits inside the button with room to spare — never clipped, never
            // "taller empty chrome".
            foreach (var size in new[]
                     {
                         TaskbarButtonSize.Small, TaskbarButtonSize.Normal,
                         TaskbarButtonSize.Large, TaskbarButtonSize.Big,
                     })
            {
                TaskbarTheme.Configure(size);
                Assert.True(TaskbarTheme.TaskIconSize <= TaskbarTheme.ButtonHeight - 2,
                    $"{size}: icon {TaskbarTheme.TaskIconSize} does not fit button {TaskbarTheme.ButtonHeight}");
            }
        }
        finally
        {
            TaskbarTheme.Configure(TaskbarButtonSize.Normal);
        }
    }

    /// <summary>The tray keeps its OWN size knob (<c>TaskbarTrayIconSize</c>) — the task-button tier
    /// must not be a back door into tray sizing (bevel-xpfl boundary).</summary>
    [Fact]
    public void Task_icon_tier_is_independent_of_the_tray_icon_size_setting()
    {
        try
        {
            TaskbarTheme.Configure(TaskbarButtonSize.Big);
            var s = new BevelSettings();
            Assert.Equal(16, s.TaskbarTrayIconSize);            // tray default is untouched by the tier
            Assert.NotEqual(s.TaskbarTrayIconSize, TaskbarTheme.TaskIconSize);
        }
        finally
        {
            TaskbarTheme.Configure(TaskbarButtonSize.Normal);
        }
    }
}
