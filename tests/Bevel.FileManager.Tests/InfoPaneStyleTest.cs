using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Core;
using Bevel.FileManager.Components;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>Every InfoPane style (Folder Options) instantiates and renders without throwing when driven
/// through the shared data API. Guards against a broken style XAML/binding. Set BEVEL_INFO_DIR to dump the
/// PNGs for eyeballing.</summary>
public class InfoPaneStyleTest
{
    [AvaloniaTheory]
    [InlineData(InfoPaneStyle.Win2000, "win2000")]
    [InlineData(InfoPaneStyle.WinXP, "winxp")]
    [InlineData(InfoPaneStyle.Win9x, "win9x")]
    public void Each_style_renders(InfoPaneStyle style, string tag)
    {
        var pane = new InfoPane { Style = style, Width = 200, Height = 380 };
        pane.Title = "Documents";
        pane.Description = "Displays the files and folders in this location.";
        pane.ObjectCount = "14 object(s)";
        pane.ClearLinks();
        pane.AddLink("My Documents", () => { });
        pane.AddLink("My Computer", () => { });

        var win = new Window { SystemDecorations = SystemDecorations.None, Width = 200, Height = 380, Content = pane };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var frame = win.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(frame!.PixelSize.Width >= 190, $"{tag} too narrow: {frame.PixelSize}");

        if (Environment.GetEnvironmentVariable("BEVEL_INFO_DIR") is { } dir)
            frame.Save(Path.Combine(dir, $"infopane-{tag}.png"));
    }

    /// <summary>The WinXP (Luna) info-pane re-hues to the active Luna colour variant (bevel-e544): its
    /// watermark + group-box headers/borders should shift HUE to the variant while staying light. Set
    /// BEVEL_INFO_DIR to dump one PNG per variant for eyeballing.</summary>
    [AvaloniaTheory]
    [InlineData("Blue")]
    [InlineData("Silver")]
    [InlineData("Black")]
    [InlineData("Purple")]
    public void WinXP_recolours_to_luna_variant(string color)
    {
        Bevel.UI.Luna.LunaVariantService.Apply(color, "Hybrid");
        try
        {
            var pane = new InfoPane { Style = InfoPaneStyle.WinXP, Width = 200, Height = 380 };
            pane.Title = "ikari";
            pane.Description = "Displays the files and folders in this location.";
            pane.ObjectCount = "265 object(s)";
            pane.ClearLinks();
            pane.AddLink("My Documents", () => { });
            pane.AddLink("My Computer", () => { });

            var win = new Window { SystemDecorations = SystemDecorations.None, Width = 200, Height = 380, Content = pane };
            win.Show();
            Dispatcher.UIThread.RunJobs();

            var frame = win.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (Environment.GetEnvironmentVariable("BEVEL_INFO_DIR") is { } dir)
                frame!.Save(Path.Combine(dir, $"infopane-luna-{color}.png"));
        }
        finally
        {
            Bevel.UI.Luna.LunaVariantService.Clear();
        }
    }
}
