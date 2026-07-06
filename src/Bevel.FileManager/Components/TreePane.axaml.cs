using Avalonia.Controls;
using Avalonia.Interactivity;
using Bevel.Core.Vfs;

namespace Bevel.FileManager.Components;

public partial class TreePane : UserControl
{
    private TreePaneViewModel? _viewModel;

    public TreePane()
    {
        InitializeComponent();
        // TreeViewItem's Expanded/Collapsed are routed events that bubble up to TreeView
        AddHandler(TreeViewItem.ExpandedEvent, OnTreeViewItemExpanded);
        AddHandler(TreeViewItem.CollapsedEvent, OnTreeViewItemCollapsed);
    }

    /// <summary>
    /// Initialize the tree with a VfsRoot. Call once after construction.
    /// </summary>
    public void Initialize(VfsRoot vfsRoot)
    {
        _viewModel = new TreePaneViewModel(vfsRoot);
        DataContext = _viewModel;
    }

    /// <summary>
    /// Set the navigation target so tree selection syncs with the item view.
    /// </summary>
    public void SetNavigationTarget(INavigationSink sink)
    {
        if (_viewModel is not null)
            _viewModel.NavigationTarget = sink;
    }

    /// <summary>
    /// Kick off the initial tree load (Desktop root + first level of children).
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_viewModel is not null)
            await _viewModel.InitializeAsync(ct);
    }

    private async void OnTreeViewItemExpanded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TreeViewItem tvi && tvi.DataContext is TreePaneNode node && _viewModel is not null)
            await _viewModel.ExpandNodeAsync(node);
    }

    private void OnTreeViewItemCollapsed(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TreeViewItem tvi && tvi.DataContext is TreePaneNode node && _viewModel is not null)
            _viewModel.CollapseNode(node);
    }
}
