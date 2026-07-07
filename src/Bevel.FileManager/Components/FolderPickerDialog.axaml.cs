using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Bevel.Core.Vfs;
using Bevel.UI;

namespace Bevel.FileManager.Components;

/// <summary>
/// Win2000-style modal "Browse For Folder" dialog: a lazy-loading, folders-only TreeView that
/// backs File&gt;Move To Folder / File&gt;Copy To Folder. Construct with the window's
/// <see cref="VfsRoot"/> and an optional starting location/title, then await
/// <see cref="PickAsync"/> for the chosen folder (null on Cancel).
/// </summary>
public partial class FolderPickerDialog : BevelWindow
{
    private readonly VfsRoot _vfsRoot;
    private VfsPath? _selectedPath;

    /// <summary>Parameterless constructor for the XAML previewer only — never used at runtime.</summary>
    public FolderPickerDialog() : this(new VfsRoot(), null)
    {
    }

    public FolderPickerDialog(VfsRoot vfsRoot, VfsPath? rootPath = null, string title = "Browse For Folder")
    {
        _vfsRoot = vfsRoot;
        InitializeComponent();

        Title = title;
        OkButton.Click += (_, _) => Close(ConfirmSelection());
        CancelButton.Click += (_, _) => Close(null);
        TreeView.SelectionChanged += (_, _) => SelectNode(TreeView.SelectedItem as TreeViewItem);

        var start = rootPath ?? new VfsPath("file", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        BuildTree(start);
    }

    /// <summary>Exposed for tests (InternalsVisibleTo Bevel.FileManager.Tests).</summary>
    internal TreeView Tree => TreeView;

    /// <summary>Exposed for tests.</summary>
    internal Button Ok => OkButton;

    /// <summary>Exposed for tests.</summary>
    internal Button Cancel => CancelButton;

    /// <summary>Shows the dialog modally over <paramref name="owner"/> and resolves to the
    /// chosen folder, or null if the user cancelled.</summary>
    public Task<VfsPath?> PickAsync(Window owner) => ShowDialog<VfsPath?>(owner);

    private void BuildTree(VfsPath rootPath)
    {
        TreeView.Items.Clear();
        var root = CreateTreeNode(RootLabel(rootPath), rootPath, mightHaveChildren: true);
        TreeView.Items.Add(root);
        root.IsExpanded = true;
    }

    private static string RootLabel(VfsPath path) => path.IsRoot ? path.Scheme.ToUpperInvariant() : path.FileName;

    private TreeViewItem CreateTreeNode(string header, VfsPath tag, bool mightHaveChildren)
    {
        var item = new TreeViewItem { Header = header, Tag = tag };
        if (mightHaveChildren)
        {
            item.Items.Add(Placeholder());
            item.Expanded += OnTreeNodeExpanded;
        }
        return item;
    }

    private static TreeViewItem Placeholder() => new() { Header = "..." };

    /// <summary>
    /// Lazily populates a node's folder children on first expand — only folders (and
    /// volume/virtual-root nodes) are shown, files are excluded. Internal so tests can drive
    /// expansion directly rather than relying on the visual Expanded event firing headlessly.
    /// </summary>
    internal async void OnTreeNodeExpanded(object? sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem node) return;
        if (node.Tag is not VfsPath path) return;

        if (node.Items.Count == 1 && node.Items[0] is TreeViewItem ph && ph.Header is string s && s == "...")
            node.Items.Clear();
        else if (node.Items.Count > 0)
            return; // already populated

        try
        {
            await foreach (var child in _vfsRoot.EnumerateAsync(path, new EnumerateOptions(), CancellationToken.None))
            {
                if (child.Kind is not (VfsNodeKind.Folder or VfsNodeKind.Volume or VfsNodeKind.VirtualRoot))
                    continue;

                node.Items.Add(CreateTreeNode(child.DisplayName, child.Path, child.MightHaveChildren));
            }
        }
        catch (OperationCanceledException)
        {
            // Dialog closing — stop populating.
        }
        catch
        {
            // Best-effort: skip folders we can't enumerate (permissions, races, etc.) rather
            // than leaving the tree node stuck with its placeholder.
        }
    }

    /// <summary>
    /// The selection logic behind TreeView.SelectionChanged — internal so tests can drive it
    /// directly instead of depending on headless TreeView selection event plumbing.
    /// </summary>
    internal void SelectNode(TreeViewItem? node)
    {
        _selectedPath = node?.Tag is VfsPath p ? p : (VfsPath?)null;
        OkButton.IsEnabled = _selectedPath is not null;
    }

    /// <summary>
    /// The confirm logic behind OK — returns the currently selected folder (or null). Internal
    /// so tests can invoke it directly rather than driving a real modal ShowDialog loop.
    /// </summary>
    internal VfsPath? ConfirmSelection() => _selectedPath;
}
