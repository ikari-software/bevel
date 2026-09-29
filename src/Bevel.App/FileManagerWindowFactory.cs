using Bevel.Core;
using Bevel.Core.Vfs;
using Bevel.FileManager;
using Bevel.FileManager.FileOperations;
using Bevel.Pal.Abstractions;

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
    private readonly ISettingsService _settings;
    private readonly IConflictHandler _conflictHandler;
    private readonly FileManagerWindowRegistry _registry;
    private readonly IFileOpener _fileOpener;

    public FileManagerWindowFactory(VfsRoot vfsRoot, ISettingsService settings, IConflictHandler conflictHandler,
        FileManagerWindowRegistry registry, IFileOpener fileOpener)
    {
        _vfsRoot = vfsRoot;
        _settings = settings;
        _conflictHandler = conflictHandler;
        _registry = registry;
        _fileOpener = fileOpener;
    }

    /// <summary>
    /// Constructs a new file-manager window wired to the shared VfsRoot/SettingsService with
    /// a fresh operation service + controller, navigates it to <paramref name="startDirectory"/>,
    /// shows it, and returns it. Does NOT set <c>desktop.MainWindow</c> — callers decide that
    /// (only the first window created at app startup should become the main window).
    /// <paramref name="show"/> false is the PARK variant (bevel-t48y): the window is built but
    /// neither shown nor registered — <see cref="ParkedFilerWindowHost"/> does both on the
    /// handoff, so a hidden parked window never shows up in the automation aggregation.
    /// </summary>
    public FileManagerWindow Create(VfsPath startDirectory, bool show = true)
    {
        // Fresh per window (bevel New-Window): the conflict handler is stateless so the
        // singleton is safe to share, but the operation service owns a per-instance undo
        // stack, so it — and the controller that wraps it — must be new per window.
        var fileOps = new FileOperationService(_vfsRoot, _conflictHandler);
        var controller = new FileManagerController(_vfsRoot, fileOps);

        var window = new FileManagerWindow();
        window.SetVfsRoot(_vfsRoot);
        window.SetSettingsService(_settings);
        window.SetFileOpener(_fileOpener); // double-click / Open a file -> OS default handler
        window.SetSearchService(new SearchService(_vfsRoot)); // Find/Search shares the app VfsRoot
        // Open in the user's chosen default view (Folder Options), set BEFORE the single navigation so
        // the start directory streams straight into the right view. The ViewMode enum lives in
        // Bevel.FileManager, so Bevel.Core stores it as a name.
        if (System.Enum.TryParse<Bevel.FileManager.Components.ViewMode>(_settings.Current.DefaultViewMode, out var vm))
            window.SetViewMode(vm);
        // Navigate ONCE to the requested folder (bevel-hvce): the old path navigated to Home in
        // SetController and then again here, wasting a cold Home enumeration on the fresh window.
        window.SetController(controller, startDirectory);

        if (show)
        {
            window.Show();
            // Track it so the automation surface (IShellSurface / M4-B) can address and enumerate it.
            _registry.Register(window);
        }
        return window;
    }
}
