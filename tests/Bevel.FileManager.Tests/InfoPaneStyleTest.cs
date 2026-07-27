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
}
