using Bevel.Core;
using Bevel.Core.Vfs;
using Bevel.FileManager;
using Bevel.FileManager.FileOperations;

namespace Bevel.App;

/// <summary>
/// Builds fully-wired <see cref="FileManagerWindow"/> instances for File &gt; New Window
/// (Ctrl+N, bevel New-Window feature). Every window shares the process-wide
/// <see cref="VfsRoot"/> and <see cref="SettingsService"/> singletons — so the filesystem
/// view and Folder Options apply identically everywhere — but gets its own
/// <see cref="FileOperationService"/> and <see cref="FileManagerController"/>, giving each
/// window an independent navigation history, selection, clipboard, and undo stack, exactly
/// like opening a second Windows Explorer window.
/// </summary>
/// <remarks>
/// New Tab (Ctrl+T) is explicitly OUT OF SCOPE for this factory — it requires a tab-host
/// redesign of <see cref="FileManagerWindow"/> (a single window hosting multiple
/// controllers), which is a bigger change than spawning an independent top-level window.
/// </remarks>
public sealed class FileManagerWindowFactory
{
    private readonly VfsRoot _vfsRoot;
    private readonly SettingsService _settings;
    private readonly IConflictHandler _conflictHandler;

    public FileManagerWindowFactory(VfsRoot vfsRoot, SettingsService settings, IConflictHandler conflictHandler)
    {
        _vfsRoot = vfsRoot;
        _settings = settings;
        _conflictHandler = conflictHandler;
    }

    /// <summary>
    /// Constructs a new file-manager window wired to the shared VfsRoot/SettingsService with
    /// a fresh operation service + controller, navigates it to <paramref name="startDirectory"/>,
    /// shows it, and returns it. Does NOT set <c>desktop.MainWindow</c> — callers decide that
    /// (only the first window created at app startup should become the main window).
    /// </summary>
    public FileManagerWindow Create(VfsPath startDirectory)
    {
        // Fresh per window (bevel New-Window): the conflict handler is stateless so the
        // singleton is safe to share, but the operation service owns a per-instance undo
        // stack, so it — and the controller that wraps it — must be new per window.
        var fileOps = new FileOperationService(_vfsRoot, _conflictHandler);
        var controller = new FileManagerController(_vfsRoot, fileOps);

        var window = new FileManagerWindow();
        window.SetVfsRoot(_vfsRoot);
        window.SetSettingsService(_settings);
        window.SetSearchService(new SearchService(_vfsRoot)); // Find/Search shares the app VfsRoot
        window.SetController(controller); // also navigates to Home internally (fixed contract)
        controller.NavigateTo(startDirectory); // land on the requested folder; a no-op push if
                                                // startDirectory == Home (NavigationStack dedupes
                                                // identical consecutive pushes), so this preserves
                                                // observable startup behavior for the main window.

        window.Show();
        return window;
    }
}
