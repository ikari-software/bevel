using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Bevel.Core.Vfs;

namespace Bevel.FileManager.Components;

/// <summary>
/// Interface for the item view to implement — tree pane calls this when
/// the user selects a folder, so the item view navigates to match.
/// </summary>
public interface INavigationSink
{
    void NavigateTo(VfsPath path);
}

/// <summary>
/// ViewModel for the folder tree pane. Handles lazy-expand with optimistic-plus,
/// off-thread VFS enumeration, hourglass delay, and selection sync via INavigationSink.
/// </summary>
public sealed class TreePaneViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly VfsRoot _vfsRoot;
    private readonly DispatcherTimer _hourglassTimer;
    private CancellationTokenSource? _expandCts;
    private TreePaneNode? _selectedNode;

    public TreePaneViewModel(VfsRoot vfsRoot)
    {
        _vfsRoot = vfsRoot;
        _hourglassTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _hourglassTimer.Tick += (_, _) =>
        {
            _hourglassTimer.Stop();
            if (_pendingExpandNode is not null)
                _pendingExpandNode.IsLoading = true;
        };

        Children = new ObservableCollection<TreePaneNode>();
    }

    /// <summary>
    /// The item view (or any navigation target) — set after construction.
    /// </summary>
    public INavigationSink? NavigationTarget { get; set; }

    /// <summary>
    /// Root nodes of the tree (Desktop → its children).
    /// </summary>
    public ObservableCollection<TreePaneNode> Children { get; }

    /// <summary>
    /// Currently selected node in the tree. When set, fires navigation to the item view.
    /// </summary>
    public TreePaneNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (_selectedNode == value) return;
            _selectedNode = value;
            OnPropertyChanged();

            if (_selectedNode is not null)
                NavigationTarget?.NavigateTo(_selectedNode.VfsPath);
        }
    }

    private TreePaneNode? _pendingExpandNode;

    /// <summary>
    /// Initialize the tree by resolving the Desktop root and populating its children.
    /// Call from the UI thread after VfsRoot is ready.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Children.Clear();

        // The tree root is the "Desktop" — we try file:// as the scheme since
        // the desktop folder is on the local filesystem.
        var desktopPath = new VfsPath("file", GetDesktopPath());

        try
        {
            var desktopNode = await _vfsRoot.ResolveAsync(desktopPath, ct);
            var treeRoot = new TreePaneNode(desktopNode);
            treeRoot.Children.Add(new TreePaneNode()); // sentinel — optimistic-plus
            Children.Add(treeRoot);

            // Auto-expand Desktop
            treeRoot.IsExpanded = true;
            await ExpandNodeAsync(treeRoot, ct);
        }
        catch (Exception)
        {
            // Desktop path may not exist on all platforms; add a stub
            Children.Add(new TreePaneNode("Desktop", new VfsPath("file", ""), isFolder: true));
        }
    }

    /// <summary>
    /// Expand a tree node — enumerates children off-thread and shows hourglass if slow.
    /// Uses optimistic-plus: the node starts with a dummy child so the expander arrow
    /// appears before any enumeration happens.
    /// </summary>
    public async Task ExpandNodeAsync(TreePaneNode node, CancellationToken ct = default)
    {
        if (node.Children.Count > 0 && node.Children[0].IsEmptySentinel)
        {
            // Remove the sentinel
            node.Children.RemoveAt(0);
        }
        else if (node.Children.Count > 0)
        {
            return; // already expanded
        }

        node.IsLoading = false;
        _pendingExpandNode = node;
        _hourglassTimer.Start();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _expandCts?.Cancel();
        _expandCts = cts;

        try
        {
            // Off-thread enumeration
            var nodes = await Task.Run(async () =>
            {
                var result = new List<TreePaneNode>();
                try
                {
                    var options = new EnumerateOptions();
                    await foreach (var child in _vfsRoot.EnumerateAsync(node.VfsPath, options, cts.Token))
                    {
                        if (child.Kind is VfsNodeKind.File) continue; // tree shows folders only

                        var childNode = new TreePaneNode(child);
                        childNode.Children.Add(new TreePaneNode()); // optimistic-plus
                        result.Add(childNode);
                    }
                }
                catch (OperationCanceledException) { }
                catch { /* swallow enumeration errors — node just has no children */ }
                return result;
            }, cts.Token);

            // Back on UI thread — update the tree
            foreach (var child in nodes)
                node.Children.Add(child);
        }
        finally
        {
            _hourglassTimer.Stop();
            node.IsLoading = false;
            _pendingExpandNode = null;
        }
    }

    /// <summary>
    /// Collapse a tree node — clears its children and re-adds a sentinel so
    /// re-expanding triggers fresh enumeration.
    /// </summary>
    public void CollapseNode(TreePaneNode node)
    {
        node.Children.Clear();
        node.Children.Add(new TreePaneNode()); // sentinel for re-expand
    }

    public void Dispose()
    {
        _hourglassTimer.Stop();
        _expandCts?.Cancel();
        _expandCts?.Dispose();
    }

    private static string GetDesktopPath()
    {
        return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// A single node in the tree pane. Wraps an IVfsNode from the VFS layer.
/// </summary>
public sealed class TreePaneNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;
    private bool _isLoading;

    /// <summary>Create a sentinel node (empty placeholder for optimistic-plus).</summary>
    public TreePaneNode()
    {
        VfsPath = default;
        DisplayName = string.Empty;
        IsFolder = false;
        IsFile = false;
        IsEmptySentinel = true;
        Children = new ObservableCollection<TreePaneNode>();
    }

    /// <summary>Create a leaf node with no VFS backing (e.g. "Desktop" fallback).</summary>
    public TreePaneNode(string displayName, VfsPath path, bool isFolder = false)
    {
        VfsPath = path;
        DisplayName = displayName;
        IsFolder = isFolder;
        IsFile = !isFolder;
        Children = new ObservableCollection<TreePaneNode>();
    }

    /// <summary>Create a node from a resolved VFS node.</summary>
    public TreePaneNode(IVfsNode vfsNode)
    {
        VfsPath = vfsNode.Path;
        DisplayName = vfsNode.DisplayName;
        IsFolder = vfsNode.Kind == VfsNodeKind.Folder;
        IsFile = vfsNode.Kind == VfsNodeKind.File;
        IconSemanticId = vfsNode.IconKey.SemanticId;
        Children = new ObservableCollection<TreePaneNode>();
    }

    public VfsPath VfsPath { get; }
    public string DisplayName { get; }
    public bool IsFolder { get; }
    public bool IsFile { get; }
    public bool IsEmptySentinel { get; }
    public string? IconSemanticId { get; }
    public ObservableCollection<TreePaneNode> Children { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; OnPropertyChanged(); }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set { if (_isLoading == value) return; _isLoading = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
