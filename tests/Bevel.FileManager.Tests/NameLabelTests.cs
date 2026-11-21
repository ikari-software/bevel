using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Bevel.FileManager.Components;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// The one filename label (bevel-3fgd): a cut-off name always ends in an ellipsis, the label knows it
/// is cut off, and only then does it tooltip the full name.
/// </summary>
public sealed class NameLabelTests
{
    static NameLabel Hosted(string text, double slotWidth)
    {
        var label = new NameLabel { Text = text, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        var window = new Window { Width = 400, Height = 200, Content = new Border { Width = slotWidth, Child = label } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return label;
    }

    [AvaloniaFact]
    public void Single_line_name_that_fits_is_not_trimmed_and_has_no_tooltip()
    {
        var label = Hosted("b.txt", 100);
        Assert.False(label.IsTrimmed);
        Assert.Null(ToolTip.GetTip(label));
    }

    [AvaloniaFact]
    public void Single_line_name_wider_than_its_slot_is_trimmed_and_tooltips_the_full_name()
    {
        var text = "A rather long file name that cannot fit.txt";
        var label = Hosted(text, 100);
        Assert.True(label.IsTrimmed);
        Assert.Equal(text, ToolTip.GetTip(label));
        Assert.True(label.Bounds.Width <= 100);
    }

    [AvaloniaFact]
    public void Tooltip_goes_away_when_the_name_fits_again()
    {
        var label = Hosted("A rather long file name that cannot fit.txt", 100);
        label.Text = "b.txt";
        Dispatcher.UIThread.RunJobs();
        Assert.False(label.IsTrimmed);
        Assert.Null(ToolTip.GetTip(label));
    }
}
