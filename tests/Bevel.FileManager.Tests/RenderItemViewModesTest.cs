using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.FileManager.Components;
using Xunit;
using static Bevel.FileManager.Tests.ItemViewLayoutTests;

namespace Bevel.FileManager.Tests;

/// <summary>
/// Owner check for the Filer view-layout set (bevel-3fgd, bevel-m9jy): renders the five view modes over
/// the same 12 items, three of them with long names, so a human can judge trimming, ellipses and column
/// widths by eye. Nothing is checked in. Output: <c>BEVEL_FILERVIEWS_DIR</c>, or
/// <c>$TMPDIR/bevel-filer-views</c>.
/// </summary>
[Collection("TaskbarTheme")]   // renders under the shared Application theme; keep it serialized with theme-mutating tests
public sealed class RenderItemViewModesTest
{
    internal static readonly string LongWithSpaces = "A really quite long file name with many words in it for wrapping.txt";
    internal static readonly string LongNoSpaces = "Supercalifragilisticexpialidocious_and_then_some_more_characters.txt";

    static IVfsNode[] Items()
    {
        var names = new[]
        {
            "b.txt", "notes.md", "Budget 2026.xlsx", LongWithSpaces, "photo.jpg", LongNoSpaces,
            "readme", ItemViewLayoutTests.LongName, "x.pdf", "Report final (2).docx", "song.mp3", "z.zip",
        };
        return names.Select(Node).ToArray();
    }

    static string OutDir()
    {
        var dir = Environment.GetEnvironmentVariable("BEVEL_FILERVIEWS_DIR")
                  ?? Path.Combine(Path.GetTempPath(), "bevel-filer-views");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [AvaloniaTheory]
    [InlineData(ViewMode.Details, "filer-details.png")]
    [InlineData(ViewMode.List, "filer-list.png")]
    [InlineData(ViewMode.SmallIcons, "filer-small.png")]
    [InlineData(ViewMode.LargeIcons, "filer-large.png")]
    [InlineData(ViewMode.Thumbnails, "filer-thumbnails.png")]
    public void Render_view_mode(ViewMode mode, string file)
    {
        var view = new ItemView { ViewMode = mode };
        var window = new Window { SystemDecorations = SystemDecorations.None, Width = 640, Height = 420, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        view.Items = Items();
        Dispatcher.UIThread.RunJobs();

        var path = Path.Combine(OutDir(), file);
        Bevel.TestSupport.SiteShot.Save(window, path, 1);
        Assert.True(new FileInfo(path).Length > 0);
        window.Close();
    }
}
