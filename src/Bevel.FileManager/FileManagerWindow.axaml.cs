using System.Collections.Generic;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.FileManager.Components;
using Bevel.FileManager.FileOperations;
using Bevel.FileManager.Navigation;
using Bevel.UI;

namespace Bevel.FileManager;

public partial class FileManagerWindow : BevelWindow
{
    private readonly NavigationStack _nav = new();
    private VfsRoot? _vfsRoot;
    private FileOperationService? _fileOps;
    private CancellationTokenSource? _enumerateCts;
    private CancellationTokenSource? _treeCts;
    private IDirectoryWatcher? _directoryWatcher;
    private IDisposable? _watcherSubscription;
    private CancellationTokenSource? _watcherDebounceCts;
    private Core.SettingsService? _settings;

    public FileManagerWindow()
    {
        InitializeComponent();

        // Wire up navigation events
        Toolbar.BackButton.Click += (_, _) => NavigateBack();
        Toolbar.ForwardButton.Click += (_, _) => NavigateForward();
        Toolbar.UpButton.Click += (_, _) => NavigateUp();

        // Wire up view mode switching via toolbar
        Toolbar.ViewLargeIcons.Click += (_, _) => SetView(ViewMode.LargeIcons);
        Toolbar.ViewSmallIcons.Click += (_, _) => SetView(ViewMode.SmallIcons);
        Toolbar.ViewList.Click += (_, _) => SetView(ViewMode.List);
        Toolbar.ViewDetails.Click += (_, _) => SetView(ViewMode.Details);

        // Wire up address bar
        AddressBar.AddressNavigated += (_, path) => NavigateTo(path);

        // Wire up tree selection
        TreeView.SelectionChanged += TreeView_SelectedItemChanged;

        // Wire up item activation (double-click / Enter on folder)
        ItemView.ItemActivated += OnItemActivated;

        // Keyboard shortcuts (FM-070)
        KeyDown += OnWindowKeyDown;

        // Wire up menu bar events
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
        MenuBar.Delete.Click += (_, _) => { /* stub */ };
        MenuBar.Rename.Click += (_, _) => { ItemView.Focus(); /* F2 will trigger rename */ };
        MenuBar.Properties.Click += (_, _) => { /* stub: Properties dialog */ };
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
        MenuBar.SelectAll.Click += (_, _) => { ItemView.Focus(); /* Ctrl+A handled by ItemView */ };

        // View
        MenuBar.ViewLargeIcons.Click += (_, _) => SetView(ViewMode.LargeIcons);
        MenuBar.ViewSmallIcons.Click += (_, _) => SetView(ViewMode.SmallIcons);
        MenuBar.ViewList.Click += (_, _) => SetView(ViewMode.List);
        MenuBar.ViewDetails.Click += (_, _) => SetView(ViewMode.Details);
        MenuBar.Refresh.Click += (_, _) => _ = LoadDirectory(_nav.Current);

        // Go
        MenuBar.GoBack.Click += (_, _) => NavigateBack();
        MenuBar.GoForward.Click += (_, _) => NavigateForward();
        MenuBar.GoUp.Click += (_, _) => NavigateUp();
        MenuBar.GoHome.Click += (_, _) => NavigateTo(new VfsPath("file",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        MenuBar.GoMyComputer.Click += (_, _) => NavigateTo(VfsPath.Root("computer"));
    }

    public void SetFileOperationService(FileOperationService fileOps)
    {
        _fileOps = fileOps;
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
        NavigateTo(new VfsPath("file", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
    }

    private void BuildTreeView()
    {
        if (_vfsRoot is null) return;

        TreeView.Items.Clear();

        // Root: Desktop
        var desktop = CreateTreeNode("Desktop", null);
        desktop.IsExpanded = true;

        // Home folder
        var homePath = new VfsPath("file", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
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

        await LoadDirectory(path);
    }

    private void OnItemActivated(object? sender, ItemActivatedEventArgs e)
    {
        if (e.Item.Node.Kind == VfsNodeKind.Folder)
        {
            NavigateTo(e.Item.Path);
        }
    }

    private async void NavigateTo(string pathString)
    {
        // A canonical "vfs://scheme/value" string parses via VfsPath; anything else is
        // treated as a bare local filesystem path.
        var path = VfsPath.TryParse(pathString, out var parsed)
            ? parsed
            : new VfsPath("file", pathString);
        _nav.Push(path);
        await LoadDirectory(path);
        UpdateNavigationButtons();
        UpdateTitle(path);
        AddressBar.SetAddress(path.ToString());
    }

    private async void NavigateTo(VfsPath path)
    {
        _nav.Push(path);
        await LoadDirectory(path);
        UpdateNavigationButtons();
        UpdateTitle(path);
        AddressBar.SetAddress(path.ToString());
    }

    private async void NavigateBack()
    {
        var path = _nav.GoBack();
        if (path is { } p)
        {
            await LoadDirectory(p);
            UpdateNavigationButtons();
            UpdateTitle(p);
            AddressBar.SetAddress(p.ToString());
        }
    }

    private async void NavigateForward()
    {
        var path = _nav.GoForward();
        if (path is { } p)
        {
            await LoadDirectory(p);
            UpdateNavigationButtons();
            UpdateTitle(p);
            AddressBar.SetAddress(p.ToString());
        }
    }

    private async void NavigateUp()
    {
        var path = _nav.GoUp();
        if (path is { } p)
        {
            _nav.Push(p);
            await LoadDirectory(p);
            UpdateNavigationButtons();
            UpdateTitle(p);
            AddressBar.SetAddress(p.ToString());
        }
    }

    private async System.Threading.Tasks.Task LoadDirectory(VfsPath path)
    {
        if (_vfsRoot is null) return;

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
            UpdateInfoPane(path, count);
        }
        catch (OperationCanceledException) { }
    }

    void UpdateInfoPane(VfsPath path, int count)
    {
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
        InfoPane.AddLink("My Documents", () => NavigateTo(new VfsPath("file",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))));
        InfoPane.AddLink("My Computer", () => NavigateTo(VfsPath.Root("computer")));
    }

    private void SetView(ViewMode mode)
    {
        ItemView.ViewMode = mode;
    }

    private void UpdateNavigationButtons()
    {
        Toolbar.BackButton.IsEnabled = _nav.CanGoBack;
        Toolbar.ForwardButton.IsEnabled = _nav.CanGoForward;
    }

    private void UpdateTitle(VfsPath path)
    {
        Title = $"Exploring - {path.Value}";
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
                _ = LoadDirectory(_nav.Current);
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
                NavigateUp();
                e.Handled = true;
                break;

            case Key.Left when alt && !ctrl:
                NavigateBack();
                e.Handled = true;
                break;

            case Key.Right when alt && !ctrl:
                NavigateForward();
                e.Handled = true;
                break;

            case Key.Enter when alt && !ctrl:
                // Alt+Enter = Properties (stub for now)
                e.Handled = true;
                break;

            case Key.W when mods.HasFlag(KeyModifiers.Meta):
                Close();
                e.Handled = true;
                break;

            case Key.Delete when shift:
                // Shift+Delete = permanent delete (stub)
                e.Handled = true;
                break;

            case Key.Delete when !shift:
                // Delete to trash (stub)
                e.Handled = true;
                break;

            case Key.N when ctrl && shift:
                // Ctrl+Shift+N = new folder (stub)
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
        await LoadDirectory(_nav.Current);
    }

    /// <summary>
    /// Reverses the most recent operation through the service (the ONLY correct way to
    /// undo — the previous code popped the stack without reverting anything, review #1),
    /// then refreshes the view so the change is visible.
    /// </summary>
    private async System.Threading.Tasks.Task UndoFromUiAsync()
    {
        if (_fileOps is null || !_fileOps.Undo.CanUndo)
            return;

        try
        {
            var result = await _fileOps.UndoAsync();
            await LoadDirectory(_nav.Current);
            if (!string.IsNullOrEmpty(result.ErrorMessage))
                System.Diagnostics.Debug.WriteLine($"Undo incomplete: {result.ErrorMessage}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Undo failed: {ex.Message}");
        }
    }
}