using System;
using System.IO;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.FileManager;
using Bevel.FileManager.FileOperations;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>bevel-hvce: SetController(controller, startDir) must navigate ONCE to the start directory.
/// The old path navigated to Home in SetController and then again to the start dir, leaving a wasted
/// Home entry in history (and a wasted cold enumeration). A single nav leaves nothing to go back to.</summary>
public class SetControllerStartDirTest : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bevel-startdir-" + Guid.NewGuid().ToString("N"));

    public SetControllerStartDirTest() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [AvaloniaFact]
    public void SetController_with_start_dir_navigates_once()
    {
        var root = new VfsRoot();
        root.Register(new LocalFsProvider());
        var controller = new FileManagerController(root, new FileOperationService(root, new DefaultConflictHandler()));
        var win = new FileManagerWindow();
        win.SetVfsRoot(root);

        var start = new VfsPath("file", _dir);
        win.SetController(controller, start);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(start, controller.CurrentDirectory);
        Assert.False(controller.CanGoBack); // single navigation — no Home entry was pushed first
    }
}
