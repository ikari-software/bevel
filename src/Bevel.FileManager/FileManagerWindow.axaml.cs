using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.FileManager.Components;
using Bevel.FileManager.FileOperations;
using Bevel.UI;

namespace Bevel.FileManager;

/// <summary>
/// Thin view adapter over <see cref="FileManagerController"/> (bevel-o2t): it renders the tree
/// and directory listing and forwards UI gestures (toolbar/menu/keyboard/drag-drop/rename) to
/// the controller, which owns navigation, selection, the clipboard, and all mutations. The
/// controller raises <see cref="FileManagerController.CurrentDirectoryChanged"/> when the
/// location changes and the window reloads in response.
/// </summary>
public partial class FileManagerWindow : BevelWindow
{
    private VfsRoot? _vfsRoot;
    private FileManagerController? _controller;
    private Core.SettingsService? _settings;
    private readonly ItemContextMenu _itemMenu = new();
    private readonly FolderContextMenu _folderMenu = new();

    private CancellationTokenSource? _enumerateCts;
    private VfsPath? _loadedPath;
    private VfsPath? _infoPath;
    private int _infoCount;
    private long _infoTotalSize;
    private CancellationTokenSource? _treeCts;
    private IDirectoryWatcher? _directoryWatcher;
    private IDisposable? _watcherSubscription;
    private CancellationTokenSource? _watcherDebounceCts;

    private static VfsPath HomePath =>
        new("file", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private VfsPath CurrentPath => _controller?.CurrentDirectory ?? VfsPath.Root("file");

    public FileManagerWindow()
    {
        InitializeComponent();

        // Navigation (delegated to the controller)
        Toolbar.BackButton.Click += (_, _) => _controller?.GoBack();
        Toolbar.ForwardButton.Click += (_, _) => _controller?.GoForward();
        Toolbar.UpButton.Click += (_, _) => _controller?.GoUp();

        // View mode switching via toolbar
        Toolbar.ViewLargeIcons.Click += (_, _) => SetView(ViewMode.LargeIcons);
        Toolbar.ViewSmallIcons.Click += (_, _) => SetView(ViewMode.SmallIcons);
        Toolbar.ViewList.Click += (_, _) => SetView(ViewMode.List);
        Toolbar.ViewDetails.Click += (_, _) => SetView(ViewMode.Details);

        // Mutating actions (bevel-o2t): toolbar + item view -> controller
        Toolbar.Cut.Click += (_, _) => CutSelection();
        Toolbar.Copy.Click += (_, _) => CopySelection();
        Toolbar.Paste.Click += (_, _) => _ = PasteAsync();
        Toolbar.Delete.Click += (_, _) => _ = DeleteSelectionAsync(toTrash: true);
        Toolbar.Properties.Click += (_, _) => ShowProperties();
        ItemView.DropRequested += OnDropRequested;
        ItemView.RenameCommitted += OnRenameCommitted;

        // Right-click context menus (FM-080/081) share one action set wired to the controller.
        ItemView.ItemContextRequested += OnItemContextRequested;
        var contextActions = BuildContextActions();
        _itemMenu.SetActions(contextActions);
        _folderMenu.SetActions(contextActions);

        // Address bar
        AddressBar.AddressNavigated += (_, path) => NavigateTo(path);

        // Tree selection
        TreeView.SelectionChanged += TreeView_SelectedItemChanged;

        // Item activation (double-click / Enter on folder)
        ItemView.ItemActivated += OnItemActivated;
        ItemView.SelectionChanged += OnItemSelectionChanged;

        // Keyboard shortcuts (FM-070)
        KeyDown += OnWindowKeyDown;

        WireMenuBar();

        // Folders toggle: swap InfoPane <-> ExplorerPane (tree)
        Toolbar.Folders.Click += (_, _) => ToggleFolders();
        ExplorerPane.CloseClicked += (_, _) => ToggleFolders();
    }

    bool _showTree;

    void ToggleFolders()
    {
        _showTree = !_showTree;
        InfoPane.IsVisible = !_showTree;
        ExplorerPane.IsVisible = _showTree;
    }

    private void WireMenuBar()
    {
        // File
        MenuBar.Open.Click += (_, _) => { if (ItemView.SelectedItem is { } item) OnItemActivated(this, new ItemActivatedEventArgs(item)); };
        MenuBar.Delete.Click += (_, _) => _ = DeleteSelectionAsync(toTrash: true);
        MenuBar.Rename.Click += (_, _) => ItemView.BeginRenameSelected();
        MenuBar.Properties.Click += (_, _) => ShowProperties();
        MenuBar.CloseWindow.Click += (_, _) => Close();

        // Tools
        MenuBar.FolderOptions.Click += (_, _) =>
        {
            if (_settings is null) return;
            var win = new SettingsWindow(_settings);
            win.ShowDialog(this);
        };

        // Edit
        MenuBar.Undo.Click += async (_, _) => await UndoFromUiAsync();
        MenuBar.Cut.Click += (_, _) => CutSelection();
        MenuBar.Copy.Click += (_, _) => CopySelection();
        MenuBar.Paste.Click += (_, _) => _ = PasteAsync();
        MenuBar.SelectAll.Click += (_, _) => { ItemView.Focus(); ItemView.SelectAll(); };
        MenuBar.InvertSelection.Click += (_, _) => { ItemView.Focus(); ItemView.InvertSelection(); };

        // View
        MenuBar.ViewLargeIcons.Click += (_, _) => SetView(ViewMode.LargeIcons);
        MenuBar.ViewSmallIcons.Click += (_, _) => SetView(ViewMode.SmallIcons);
        MenuBar.ViewList.Click += (_, _) => SetView(ViewMode.List);
        MenuBar.ViewDetails.Click += (_, _) => SetView(ViewMode.Details);
        MenuBar.Refresh.Click += (_, _) => _controller?.Refresh();

        // Go
        MenuBar.GoBack.Click += (_, _) => _controller?.GoBack();
        MenuBar.GoForward.Click += (_, _) => _controller?.GoForward();
        MenuBar.GoUp.Click += (_, _) => _controller?.GoUp();
        MenuBar.GoHome.Click += (_, _) => NavigateTo(HomePath);
        MenuBar.GoMyComputer.Click += (_, _) => NavigateTo(VfsPath.Root("computer"));
    }

    /// <summary>
    /// Wires the controller (bevel-o2t) and kicks off the initial navigation. Replaces the
    /// former SetFileOperationService — the controller wraps FileOperationService plus the
    /// navigation/selection/clipboard state the window used to own.
    /// </summary>
    public void SetController(FileManagerController controller)
    {
        _controller = controller;
        controller.CurrentDirectoryChanged += OnCurrentDirectoryChanged;
        controller.NavigationStateChanged += OnNavigationStateChanged;
        controller.OperationRunner = RunWithProgressAsync;
        controller.NavigateTo(HomePath);
    }

    public void SetSettingsService(Core.SettingsService settings)
    {
        _settings = settings;
    }

    public void SetVfsRoot(VfsRoot root)
    {
        _vfsRoot = root;
        // Per-window token so tree expansions bind to THIS window's root and can be
        // cancelled when the root changes or the window closes (replaces the global
        // VfsRootLocator, which broke with more than one window — bevel-ee9).
        _treeCts?.Cancel();
        _treeCts?.Dispose();
        _treeCts = new CancellationTokenSource();
        BuildTreeView();
        // Initial navigation is driven by SetController so it flows through the controller.
    }

    private void BuildTreeView()
    {
        if (_vfsRoot is null) return;

        TreeView.Items.Clear();

        // Root: Desktop
        var desktop = CreateTreeNode("Desktop", null);
        desktop.IsExpanded = true;

        // Home folder
        var homePath = HomePath;
        desktop.Items.Add(CreateTreeNode("Home", homePath));

        // My Computer
        var computerPath = VfsPath.Root("computer");
        var computer = CreateTreeNode("My Computer", computerPath);
        computer.Items.Add(Placeholder());
        desktop.Items.Add(computer);

        // Network (stub)
        var networkPath = VfsPath.Root("net");
        var network = CreateTreeNode("Network", networkPath);
        desktop.Items.Add(network);

        // Trash (stub)
        var trashPath = VfsPath.Root("trash");
        var trash = CreateTreeNode("Trash", trashPath);
        desktop.Items.Add(trash);

        // Actual Desktop folder contents
        var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (Directory.Exists(desktopDir))
        {
            var desktopFsPath = new VfsPath("file", desktopDir);
            var desktopFs = CreateTreeNode("Desktop Files", desktopFsPath);
            desktopFs.Items.Add(Placeholder());
            desktop.Items.Add(desktopFs);
        }

        TreeView.Items.Add(desktop);
    }

    private TreeViewItem CreateTreeNode(string header, VfsPath? tag)
    {
        var item = new TreeViewItem { Header = header, Tag = tag };
        item.Expanded += OnTreeNodeExpanded;
        return item;
    }

    private static TreeViewItem Placeholder() => new() { Header = "..." };

    private async void OnTreeNodeExpanded(object? sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem node) return;
        if (node.Tag is not VfsPath path) return;

        // Remove placeholder
        if (node.Items.Count == 1 && node.Items[0] is TreeViewItem ph && ph.Header is string s && s == "...")
            node.Items.Clear();
        else if (node.Items.Count > 0)
            return; // already populated

        if (_vfsRoot is null) return;
        var ct = _treeCts?.Token ?? CancellationToken.None;

        try
        {
            await foreach (var child in _vfsRoot.EnumerateAsync(path, new EnumerateOptions(), ct))
            {
                if (child.Kind is not (VfsNodeKind.Folder or VfsNodeKind.Volume or VfsNodeKind.VirtualRoot))
                    continue;

                var childNode = CreateTreeNode(child.DisplayName, child.Path);
                childNode.Items.Add(Placeholder());
                node.Items.Add(childNode);
            }
        }
        catch (OperationCanceledException)
        {
            // Root changed or window closing — stop populating.
        }
        catch
        {
            // Silently ignore enumeration errors in tree
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _treeCts?.Cancel();
        _treeCts?.Dispose();
        _enumerateCts?.Cancel();
        _enumerateCts?.Dispose();
        _watcherSubscription?.Dispose();
        _watcherDebounceCts?.Cancel();
        _watcherDebounceCts?.Dispose();
        _directoryWatcher?.Dispose();
        base.OnClosed(e);
    }

    private async void TreeView_SelectedItemChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TreeView.SelectedItem is not TreeViewItem node || node.Tag is not VfsPath path)
            return;
        if (_vfsRoot is null) return;

        // Resolve computer:// volume paths to their file:// mount path
        if (path.Scheme == "computer" && !path.IsRoot)
        {
            try
            {
                var node_ = await _vfsRoot.ResolveAsync(path, CancellationToken.None);
                if (node_.ExtraColumns.TryGetValue("MountPath", out var mountPath) && mountPath is string mp)
                {
                    path = new VfsPath("file", mp);
                }
            }
            catch { }
        }

        _controller?.NavigateTo(path);
    }

    private void OnItemActivated(object? sender, ItemActivatedEventArgs e)
    {
        if (e.Item.Node.Kind == VfsNodeKind.Folder)
        {
            NavigateTo(e.Item.Path);
        }
    }

    // ── Navigation (thin delegators to the controller) ─────────────────

    private void NavigateTo(string pathString)
    {
        // A canonical "vfs://scheme/value" string parses via VfsPath; anything else is
        // treated as a bare local filesystem path.
        var path = VfsPath.TryParse(pathString, out var parsed)
            ? parsed
            : new VfsPath("file", pathString);
        _controller?.NavigateTo(path);
    }

    private void NavigateTo(VfsPath path) => _controller?.NavigateTo(path);

    /// <summary>Select a list item by path (used by tests and the render harness).</summary>
    public void SelectInList(VfsPath path) => ItemView.SelectPath(path);

    /// <summary>The controller reached a new directory — render it, retitle, update the address.</summary>
    private async void OnCurrentDirectoryChanged(VfsPath path)
    {
        UpdateTitle(path);
        AddressBar.SetAddress(path.Value);
        // Re-landing on the directory already shown (F5, or a controller Refresh) is a
        // differential update — reconcile in place rather than clearing and rebuilding, so the
        // list doesn't flash. A genuine navigation streams a fresh listing.
        if (_loadedPath is { } loaded && loaded == path && ItemView.HasItems)
            await ReloadDifferential(path);
        else
            await LoadDirectory(path);
    }

    private void OnNavigationStateChanged() => UpdateNavigationButtons();

    private async System.Threading.Tasks.Task LoadDirectory(VfsPath path)
    {
        if (_vfsRoot is null) return;

        _loadedPath = path;
        _enumerateCts?.Cancel();
        _enumerateCts = new CancellationTokenSource();
        var ct = _enumerateCts.Token;

        ItemView.ResetItems();
        StatusBar.UpdateObjectCount(0);
        StatusBar.UpdateTotalSize(0);
        StatusBar.UpdateNamespaceZone(path.Scheme);

        try
        {
            long totalSize = 0;
            int count = 0;

            // Directory watcher
            _watcherSubscription?.Dispose();
            _directoryWatcher?.Dispose();
            try { _directoryWatcher = _vfsRoot.CreateWatcher(path); } catch { _directoryWatcher = null; }
            if (_directoryWatcher is not null)
                _watcherSubscription = _directoryWatcher.Changes.Subscribe(OnDirectoryChanged);

            // Stream items in chunks — paint as they arrive
            var chunk = new List<IVfsNode>(32);
            try
            {
                await foreach (var node in _vfsRoot.EnumerateAsync(path, new EnumerateOptions(), ct))
                {
                    chunk.Add(node);
                    count++;
                    if (node.Size.HasValue) totalSize += node.Size.Value;

                    if (chunk.Count >= 32)
                    {
                        var batch = chunk.ToArray();
                        chunk.Clear();
                        await Dispatcher.UIThread.InvokeAsync(
                            () => { ItemView.AddItems(batch); StatusBar.UpdateObjectCount(count); },
                            DispatcherPriority.Background);
                    }
                }
            }
            catch (KeyNotFoundException) { }

            // Final chunk
            if (chunk.Count > 0)
            {
                var batch = chunk.ToArray();
                await Dispatcher.UIThread.InvokeAsync(
                    () => { ItemView.AddItems(batch); StatusBar.UpdateObjectCount(count); },
                    DispatcherPriority.Background);
            }

            StatusBar.UpdateTotalSize(totalSize);
            _infoTotalSize = totalSize;
            UpdateInfoPane(path, count);
        }
        catch (OperationCanceledException) { }
    }

    void UpdateInfoPane(VfsPath path, int count)
    {
        _infoPath = path;
        _infoCount = count;
        var name = path.Scheme == "computer" ? "My Computer"
            : path.IsRoot ? path.Scheme.ToUpperInvariant()
            : path.FileName;
        InfoPane.Title = name;
        InfoPane.Description = path.Scheme switch
        {
            "computer" => "Displays the drives and folders on your computer.",
            _ => "Displays the files and folders in this location.",
        };
        InfoPane.ObjectCount = $"{count} object(s)";
        InfoPane.ClearLinks();
        InfoPane.AddLink("My Documents", () => NavigateTo(HomePath));
        InfoPane.AddLink("My Computer", () => NavigateTo(VfsPath.Root("computer")));
    }

    /// <summary>Mirror the selection into the info pane — a single item shows its type/size/date,
    /// several show a count, none restores the folder summary. Matches Win2000's Web View pane.</summary>
    void OnItemSelectionChanged()
    {
        var sel = ItemView.SelectedItems;
        if (sel.Count == 0)
        {
            if (_infoPath is { } p) UpdateInfoPane(p, _infoCount);
            StatusBar.UpdateObjectCount(_infoCount);
            StatusBar.UpdateTotalSize(_infoTotalSize);
            return;
        }

        var selectedBytes = sel.Sum(s => s.Size ?? 0);
        StatusBar.UpdateSelection(sel.Count, selectedBytes);

        if (sel.Count == 1)
        {
            var vm = sel[0];
            InfoPane.Title = vm.DisplayName;
            var lines = new List<string> { vm.IsFolder ? "File Folder" : vm.TypeDescription };
            if (!vm.IsFolder && vm.Size is not null) lines.Add($"Size: {vm.SizeDisplay}");
            if (!string.IsNullOrEmpty(vm.ModifiedDisplay)) lines.Add($"Modified: {vm.ModifiedDisplay}");
            InfoPane.Description = string.Join("\n", lines);
            return;
        }
        InfoPane.Title = $"{sel.Count} items";
        InfoPane.Description = "Multiple items selected";
    }

    private void SetView(ViewMode mode)
    {
        ItemView.ViewMode = mode;
    }

    /// <summary>Set the list view mode (used by tests and the render harness).</summary>
    public void SetViewMode(ViewMode mode) => SetView(mode);

    private void UpdateNavigationButtons()
    {
        Toolbar.BackButton.IsEnabled = _controller?.CanGoBack ?? false;
        Toolbar.ForwardButton.IsEnabled = _controller?.CanGoForward ?? false;
    }

    private void UpdateTitle(VfsPath path)
    {
        Title = $"Exploring - {path.Value}";
    }

    // ── Mutating commands (bevel-o2t) ──────────────────────────────────

    void SyncSelection() => _controller?.SetSelection(ItemView.SelectedItems.Select(i => i.Path).ToArray());

    void CopySelection() { SyncSelection(); _controller?.CopySelectionToClipboard(); }
    void CutSelection() { SyncSelection(); _controller?.CutSelectionToClipboard(); }

    async System.Threading.Tasks.Task DeleteSelectionAsync(bool toTrash)
    {
        if (_controller is null) return;
        SyncSelection();
        if (_controller.Selection.Count == 0) return;
        await RunOpAsync(() => _controller.DeleteAsync(_controller.Selection, toTrash));
    }

    async System.Threading.Tasks.Task PasteAsync()
    {
        if (_controller is null) return;
        try
        {
            var result = await _controller.PasteAsync();
            if (result is { ErrorMessage.Length: > 0 })
                System.Diagnostics.Debug.WriteLine($"Paste incomplete: {result.ErrorMessage}");
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Paste failed: {ex.Message}"); }
        finally { _controller.Refresh(); }
    }

    async System.Threading.Tasks.Task NewFolderAsync()
    {
        if (_controller is null) return;
        try { await _controller.NewFolderAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"New folder failed: {ex.Message}"); }
        finally { _controller.Refresh(); }
    }

    void OnDropRequested(object? sender, DropEventArgs e)
    {
        if (_controller is null || e.Paths.Count == 0) return;
        var dest = e.TargetFolder ?? _controller.CurrentDirectory;
        // Never drop an item onto itself (dragging a folder onto its own row).
        var paths = e.Paths.Where(p => p != dest).ToList();
        if (paths.Count == 0) return;
        _ = RunOpAsync(() => e.IsCopy ? _controller.CopyAsync(paths, dest) : _controller.MoveAsync(paths, dest));
    }

    async void OnRenameCommitted(object? sender, RenameCommittedEventArgs e)
    {
        if (_controller is null) return;
        await RunOpAsync(() => _controller.RenameAsync(e.Path, e.NewName));
    }

    void ShowProperties() { /* bevel-o2t.1: Properties dialog not yet built */ }

    // ── Context menus (FM-080/081) ─────────────────────────────────────

    void OnItemContextRequested(object? sender, FileContextRequestedEventArgs e)
    {
        SyncSelection();
        if (e.Item is not null)
            _itemMenu.Show(e.Item.Node, ItemView, e.Position);
        else
            _folderMenu.Show(ItemView, e.Position);
    }

    /// <summary>The single action set both context menus invoke — every entry routes through
    /// the controller, exactly like the toolbar/menu/keyboard paths.</summary>
    ContextMenuActions BuildContextActions() => new()
    {
        Open = () => { if (ItemView.SelectedItem is { } i) OnItemActivated(this, new ItemActivatedEventArgs(i)); },
        Cut = CutSelection,
        Copy = CopySelection,
        Paste = () => _ = PasteAsync(),
        Delete = () => _ = DeleteSelectionAsync(toTrash: true),
        Rename = () => ItemView.BeginRenameSelected(),
        Properties = ShowProperties,
        Undo = () => _ = UndoFromUiAsync(),
        Refresh = () => _controller?.Refresh(),
        CanPaste = () => _controller?.HasClipboard ?? false,
        CanUndo = () => _controller?.CanUndo ?? false,
        ViewChanged = key => SetView(ViewFromKey(key)),
        NewItem = kind => { if (kind == "folder") _ = NewFolderAsync(); },
    };

    static ViewMode ViewFromKey(string key) => key switch
    {
        "small-icons" => ViewMode.SmallIcons,
        "list" => ViewMode.List,
        "details" => ViewMode.Details,
        _ => ViewMode.LargeIcons,
    };

    // ── Progress dialog routing (FM-132) ───────────────────────────────

    /// <summary>
    /// Controller operation runner: runs Copy/Move/Delete under a ProgressDialog when they take
    /// longer than a beat, so single-file ops don't flash a modal. Everything else runs inline.
    /// </summary>
    async System.Threading.Tasks.Task<FileOpResult> RunWithProgressAsync(
        FileOpRequest request, Func<CancellationToken, System.Threading.Tasks.Task<FileOpResult>> exec)
    {
        if (_controller is null || request is not (CopyRequest or MoveRequest or DeleteRequest))
            return await exec(CancellationToken.None);

        var cts = new CancellationTokenSource();
        var opTask = exec(cts.Token);

        if (await System.Threading.Tasks.Task.WhenAny(opTask, System.Threading.Tasks.Task.Delay(400)) == opTask)
        {
            cts.Dispose();
            return await opTask; // finished fast — no dialog
        }

        var (from, to) = DescribeOp(request);
        var dlg = new ProgressDialog(request, from, to);
        try { return await dlg.AdoptAsync(_controller.Progress, opTask, cts, this); }
        finally { cts.Dispose(); }
    }

    static (string From, string To) DescribeOp(FileOpRequest request) => request switch
    {
        CopyRequest c => (DescribeSources(c.Sources), c.Destination.FileName),
        MoveRequest m => (DescribeSources(m.Sources), m.Destination.FileName),
        DeleteRequest d => (DescribeSources(d.Paths), d.ToTrash ? "Trash" : "(permanently removed)"),
        _ => ("", ""),
    };

    static string DescribeSources(IReadOnlyList<VfsPath> paths)
        => paths.Count == 1 ? paths[0].FileName : $"{paths.Count} items";

    /// <summary>Run a mutation, surface any partial-failure message, and reload the view.</summary>
    async System.Threading.Tasks.Task RunOpAsync(Func<System.Threading.Tasks.Task<FileOpResult>> op)
    {
        try
        {
            var result = await op();
            if (!string.IsNullOrEmpty(result.ErrorMessage))
                System.Diagnostics.Debug.WriteLine($"Operation incomplete: {result.ErrorMessage}");
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Operation failed: {ex.Message}"); }
        finally { _controller?.Refresh(); }
    }

    // ── Keyboard shortcuts (FM-070) ────────────────────────────────────

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        bool ctrl = mods.HasFlag(KeyModifiers.Control);
        bool alt = mods.HasFlag(KeyModifiers.Alt);
        bool shift = mods.HasFlag(KeyModifiers.Shift);

        switch (e.Key)
        {
            case Key.F5:
                _controller?.Refresh();
                e.Handled = true;
                break;

            case Key.F3:
                // Toggle search pane (stub)
                e.Handled = true;
                break;

            case Key.F4:
                if (alt)
                    Close();
                else
                    AddressBar.Focus();
                e.Handled = true;
                break;

            case Key.F6:
                CycleFocus();
                e.Handled = true;
                break;

            case Key.Tab when !ctrl:
                CycleFocus();
                e.Handled = true;
                break;

            case Key.Back when !ctrl:
                _controller?.GoUp();
                e.Handled = true;
                break;

            case Key.Left when alt && !ctrl:
                _controller?.GoBack();
                e.Handled = true;
                break;

            case Key.Right when alt && !ctrl:
                _controller?.GoForward();
                e.Handled = true;
                break;

            case Key.Enter when alt && !ctrl:
                ShowProperties();
                e.Handled = true;
                break;

            case Key.W when mods.HasFlag(KeyModifiers.Meta):
                Close();
                e.Handled = true;
                break;

            case Key.C when ctrl && !shift && !alt:
                CopySelection();
                e.Handled = true;
                break;

            case Key.X when ctrl && !shift && !alt:
                CutSelection();
                e.Handled = true;
                break;

            case Key.V when ctrl && !shift && !alt:
                _ = PasteAsync();
                e.Handled = true;
                break;

            case Key.Delete when shift:
                _ = DeleteSelectionAsync(toTrash: false); // permanent delete
                e.Handled = true;
                break;

            case Key.Delete when !shift:
                _ = DeleteSelectionAsync(toTrash: true); // delete to trash
                e.Handled = true;
                break;

            case Key.N when ctrl && shift:
                _ = NewFolderAsync();
                e.Handled = true;
                break;

            case Key.Z when ctrl && !shift:
                _ = UndoFromUiAsync();
                e.Handled = true;
                break;
        }
    }

    private void CycleFocus()
    {
        // F6/Tab: cycle TreeView → ItemView → AddressBar
        if (TreeView.IsFocused)
            ItemView.Focus();
        else if (ItemView.IsFocused)
            AddressBar.Focus();
        else
            TreeView.Focus();
    }

    private void OnDirectoryChanged(FsChangeBatch batch)
    {
        // Coalesce bursts of FileSystemWatcher events into a single reload — a git checkout
        // or bulk file operation otherwise triggers a full re-enumeration per raw event
        // (review #7). The debounce CTS is only touched on the UI thread to stay race-free.
        Dispatcher.UIThread.Post(() =>
        {
            _watcherDebounceCts?.Cancel();
            _watcherDebounceCts?.Dispose();
            var cts = new CancellationTokenSource();
            _watcherDebounceCts = cts;
            _ = DebouncedReloadAsync(cts.Token);
        });
    }

    private async System.Threading.Tasks.Task DebouncedReloadAsync(CancellationToken ct)
    {
        try { await System.Threading.Tasks.Task.Delay(250, ct); }
        catch (OperationCanceledException) { return; }
        // Watcher-driven reloads are always of the current directory, so reconcile in place — a
        // spurious event or an unrelated mtime touch leaves the path set unchanged and the list
        // does not flicker.
        await ReloadDifferential(CurrentPath);
    }

    /// <summary>
    /// Re-enumerate <paramref name="path"/> and reconcile the result into the existing list (keyed
    /// by path) instead of clearing and rebuilding. Reuses the active watcher. Buffers the full
    /// listing first because a diff needs the complete new set; the directory is already loaded, so
    /// this is bounded work. Falls back to a fresh streaming load if nothing is displayed yet.
    /// </summary>
    private async System.Threading.Tasks.Task ReloadDifferential(VfsPath path)
    {
        if (_vfsRoot is null) return;
        if (!ItemView.HasItems) { await LoadDirectory(path); return; }

        _enumerateCts?.Cancel();
        _enumerateCts?.Dispose();
        _enumerateCts = new CancellationTokenSource();
        var ct = _enumerateCts.Token;

        var nodes = new List<IVfsNode>();
        long totalSize = 0;
        try
        {
            await foreach (var node in _vfsRoot.EnumerateAsync(path, new EnumerateOptions(), ct))
            {
                nodes.Add(node);
                if (node.Size.HasValue) totalSize += node.Size.Value;
            }
        }
        catch (OperationCanceledException) { return; }
        catch (KeyNotFoundException) { }

        _loadedPath = path;
        ItemView.ReconcileItems(nodes);
        StatusBar.UpdateObjectCount(nodes.Count);
        StatusBar.UpdateTotalSize(totalSize);
        _infoTotalSize = totalSize;
        UpdateInfoPane(path, nodes.Count);
    }

    /// <summary>
    /// Reverses the most recent operation through the controller (the ONLY correct way to
    /// undo — the previous code popped the stack without reverting anything, review #1),
    /// then refreshes the view so the change is visible.
    /// </summary>
    private async System.Threading.Tasks.Task UndoFromUiAsync()
    {
        if (_controller is null || !_controller.CanUndo)
            return;

        try
        {
            var result = await _controller.UndoAsync();
            if (!string.IsNullOrEmpty(result.ErrorMessage))
                System.Diagnostics.Debug.WriteLine($"Undo incomplete: {result.ErrorMessage}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Undo failed: {ex.Message}");
        }
        finally { _controller?.Refresh(); }
    }
}
