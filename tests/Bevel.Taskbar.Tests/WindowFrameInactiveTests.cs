using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Core;
using Bevel.UI;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>The side and bottom frame dims with the caption when a window loses activation: the title bar
/// swapped to its inactive ramp while the frame stayed saturated, so an inactive window looked half-dimmed.</summary>
[Collection("TaskbarTheme")]
public class WindowFrameInactiveTests
{
    private static Color FrameColor(Window w)
        => Assert.IsType<SolidColorBrush>(
            w.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_WindowFrame").BorderBrush).Color;

    private static void SetActive(Window w, bool active)
    {
        var method = typeof(Avalonia.Controls.WindowBase)
            .GetMethod(active ? "HandleActivated" : "HandleDeactivated",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        Assert.NotNull(method);
        method!.Invoke(w, null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTheory]
    [InlineData(ThemeIds.Flat, "#3567CB", "#9FB2CA")]
    [InlineData(ThemeIds.Pastel, "#B9C7D6", "#DCE3EA")]
    [InlineData(ThemeIds.Blue2001, "#0831D9", "#7FA0D2")]
    public void The_frame_follows_the_windows_activation(string theme, string active, string inactive)
    {
        ThemeService.Apply(theme);
        var a = new BevelWindow { Width = 300, Height = 200, Title = "A" };
        var b = new BevelWindow { Width = 300, Height = 200, Title = "B" };
        a.Show(); b.Show();
        Dispatcher.UIThread.RunJobs();

        // The headless platform never deactivates a window by itself; drive the same handler the native
        // backends call, which flips IsActive and so the style under test.
        SetActive(a, true);
        Assert.True(a.IsActive);
        Assert.Equal(Color.Parse(active), FrameColor(a));

        SetActive(a, false);
        Assert.False(a.IsActive);
        Assert.Equal(Color.Parse(inactive), FrameColor(a));
        Assert.Equal(Color.Parse(active), FrameColor(b));
    }
}
