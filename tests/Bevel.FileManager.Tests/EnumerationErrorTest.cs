using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.FileManager;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>bevel-y67f: enumerating a protected or missing directory must NOT escape LoadDirectory's
/// async void caller (which crashed the process); it should be caught and surfaced in the status bar.</summary>
public class EnumerationErrorTest
{
    [AvaloniaFact]
    public async Task Missing_directory_does_not_crash_and_shows_a_message()
    {
        var root = new VfsRoot();
        root.Register(new LocalFsProvider());
        var win = new FileManagerWindow();
        win.SetVfsRoot(root);
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var missing = new VfsPath("file", Path.Combine(Path.GetTempPath(), "bevel-missing-" + Guid.NewGuid().ToString("N")));
        var load = typeof(FileManagerWindow).GetMethod("LoadDirectory", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // Must complete without throwing — previously DirectoryNotFoundException escaped to async void.
        await (Task)load.Invoke(win, new object?[] { missing })!;
        Dispatcher.UIThread.RunJobs();

        // The friendly message was surfaced in the status bar's primary panel.
        var statusBar = typeof(FileManagerWindow).GetField("StatusBar", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(win)!;
        var countText = (TextBlock)statusBar.GetType()
            .GetField("ObjectCountText", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(statusBar)!;
        Assert.Contains("no longer exists", countText.Text ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task ReloadDifferential_on_a_vanished_directory_does_not_crash()
    {
        // The F5 / watcher-refresh sibling of LoadDirectory lacked the same enumeration-error guard,
        // so refreshing after the folder's volume is ejected crashed the process (ce-review, anchor 100).
        var root = new VfsRoot();
        root.Register(new LocalFsProvider());
        var win = new FileManagerWindow();
        win.SetVfsRoot(root);
        win.Show();
        Dispatcher.UIThread.RunJobs();

        // Load a real directory with content so ReloadDifferential takes the reconcile path (HasItems).
        var dir = Path.Combine(Path.GetTempPath(), "bevel-reload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "x");
        var path = new VfsPath("file", dir);
        var load = typeof(FileManagerWindow).GetMethod("LoadDirectory", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)load.Invoke(win, new object?[] { path })!;
        Dispatcher.UIThread.RunJobs();

        // Delete the directory out from under the window, then differential-reload — must not throw.
        Directory.Delete(dir, recursive: true);
        var reload = typeof(FileManagerWindow).GetMethod("ReloadDifferential", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)reload.Invoke(win, new object?[] { path })!;
        Dispatcher.UIThread.RunJobs();
    }
}
