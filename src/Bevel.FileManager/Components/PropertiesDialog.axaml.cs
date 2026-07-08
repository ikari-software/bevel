using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.UI;

namespace Bevel.FileManager.Components;

/// <summary>
/// Win2000-style modal "Properties" sheet (General tab only, M0): shows type/location/size/dates
/// for a single VFS node, or a simplified combined summary for a multi-selection. Backs Alt+Enter,
/// File&gt;Properties and the toolbar Properties button (FileManagerWindow.ShowProperties).
/// </summary>
/// <remarks>
/// Deliberate simplifications (tracked as follow-ups by the caller):
/// <list type="bullet">
/// <item>
/// "Size on disk" is derived by rounding each file's <see cref="IVfsNode.Size"/> up to a 4 KiB
/// cluster boundary. There's no real cluster-size probe per volume; it's an approximation.
/// </item>
/// <item>
/// For a single-node selection, the name TextBox and the Read-only/Hidden checkboxes are
/// editable and commit via <see cref="IVfsMutator.RenameAsync"/> /
/// <see cref="IVfsMutator.SetAttributesAsync"/> on OK/Apply (bevel-iuh). A multi-selection or
/// empty selection stays display-only/disabled, matching the prior simplified sheet.
/// </item>
/// <item>
/// A multi-selection collapses to a simplified sheet (combined size, common type/location if they
/// agree, no per-item dates/attributes) rather than showing every item's full detail.
/// </item>
/// <item>
/// Commit failures (permission denied, name collision, node raced away, ...) are swallowed:
/// the dialog stays open rather than crashing, so the user can adjust and retry. There's no
/// surfaced error message yet — a follow-up if that's needed.
/// </item>
/// </list>
/// </remarks>
public partial class PropertiesDialog : BevelWindow
{
    /// <summary>The subset of <see cref="VfsNodeAttributes"/> this sheet's checkboxes edit.
    /// Any other bits (e.g. System) are read at populate time and preserved verbatim on commit.</summary>
    private const VfsNodeAttributes EditableAttributeMask = VfsNodeAttributes.ReadOnly | VfsNodeAttributes.Hidden;

    private readonly VfsRoot _vfsRoot;
    private readonly IReadOnlyList<IVfsNode> _items;
    private readonly CancellationTokenSource _cts = new();

    // Single-node commit state (unused/inert for multi/empty selections).
    private VfsPath _currentPath;
    private string _initialName = "";
    private VfsNodeAttributes _initialAttributes;
    private bool _dirty;
    private bool _committing;

    /// <summary>Parameterless constructor for the XAML previewer only — never used at runtime.</summary>
    public PropertiesDialog() : this(new VfsRoot(), Array.Empty<IVfsNode>())
    {
    }

    /// <summary>Convenience constructor for a single-node selection.</summary>
    public PropertiesDialog(VfsRoot vfsRoot, IVfsNode item) : this(vfsRoot, new[] { item })
    {
    }

    public PropertiesDialog(VfsRoot vfsRoot, IReadOnlyList<IVfsNode> items)
    {
        _vfsRoot = vfsRoot;
        _items = items;
        InitializeComponent();

        OkButton.Click += async (_, _) => await OnOkAsync();
        CancelButton.Click += (_, _) => Close();
        ApplyButton.Click += async (_, _) => await OnApplyAsync();

        Populate();

        // Wire dirty-tracking after the initial populate so setting Text/IsChecked from
        // Populate() above doesn't itself flip the dirty flag.
        NameBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) UpdateDirty(); };
        ReadOnlyCheckBox.PropertyChanged += (_, e) => { if (e.Property == ToggleButton.IsCheckedProperty) UpdateDirty(); };
        HiddenCheckBox.PropertyChanged += (_, e) => { if (e.Property == ToggleButton.IsCheckedProperty) UpdateDirty(); };
    }

    /// <summary>Shows the dialog modally over <paramref name="owner"/>.</summary>
    public Task ShowAsync(Window owner) => ShowDialog(owner);

    /// <summary>
    /// True once a rename and/or attribute change committed successfully via OK or Apply, so the
    /// caller (FileManagerWindow.ShowProperties) knows to refresh its listing.
    /// </summary>
    internal bool CommittedChange { get; private set; }

    /// <summary>
    /// Exposed for tests: the async folder Contains/size scan started for a single-folder
    /// selection, if one was started (null for files or multi-selection). Tests await this
    /// instead of racing the background walk.
    /// </summary>
    internal Task? ContainsScanTask { get; private set; }

    /// <summary>
    /// Exposed for tests: the async recursive-size scan started for a multi-selection that
    /// includes one or more folders (null when the selection is folder-free, since that case
    /// resolves synchronously). Mirrors <see cref="ContainsScanTask"/>.
    /// </summary>
    internal Task? MultiScanTask { get; private set; }

    // ── Exposed for tests (InternalsVisibleTo Bevel.FileManager.Tests) ─────

    internal TextBox NameField => NameBox;
    internal TextBlock TypeValue => TypeText;
    internal TextBlock LocationValue => LocationText;
    internal TextBlock SizeValue => SizeText;
    internal TextBlock SizeOnDiskValue => SizeOnDiskText;
    internal TextBlock ContainsValue => ContainsText;
    internal Grid ContainsRowGrid => ContainsRow;
    internal TextBlock CreatedValue => CreatedText;
    internal TextBlock ModifiedValue => ModifiedText;
    internal TextBlock AccessedValue => AccessedText;
    internal CheckBox ReadOnlyCheck => ReadOnlyCheckBox;
    internal CheckBox HiddenCheck => HiddenCheckBox;
    internal Button Ok => OkButton;
    internal Button Cancel => CancelButton;
    internal Button Apply => ApplyButton;

    /// <summary>Test hook: runs the same commit-then-close logic as clicking OK, awaitably
    /// (the Click handler itself is fire-and-forget, which a test can't deterministically await).</summary>
    internal Task OkAsync() => OnOkAsync();

    /// <summary>Test hook: runs the same commit logic as clicking Apply, awaitably.</summary>
    internal Task ApplyAsync() => OnApplyAsync();

    protected override void OnClosed(EventArgs e)
    {
        _cts.Cancel();
        _cts.Dispose();
        base.OnClosed(e);
    }

    // ── Population ──────────────────────────────────────────────────────

    private void Populate()
    {
        if (_items.Count == 0)
        {
            Title = "Properties";
            ContainsRow.IsVisible = false;
            ReadOnlyCheckBox.IsEnabled = false;
            HiddenCheckBox.IsEnabled = false;
            return;
        }

        if (_items.Count == 1)
            PopulateSingle(_items[0]);
        else
            PopulateMulti(_items);
    }

    private void PopulateSingle(IVfsNode node)
    {
        Title = $"{node.DisplayName} Properties";
        NameBox.Text = node.DisplayName;

        IconHost.Child = Glyphs.Icon(32, node.IconKey);   // the real per-type semantic glyph

        TypeText.Text = node.TypeDescription;
        LocationText.Text = DescribeLocation(node.Path);

        var isFolder = IsFolderKind(node.Kind);
        ContainsRow.IsVisible = isFolder;

        if (isFolder)
        {
            SizeText.Text = "Calculating...";
            SizeOnDiskText.Text = "Calculating...";
            ContainsText.Text = "Calculating...";
            ContainsScanTask = ScanFolderAsync(node.Path, _cts.Token);
        }
        else
        {
            var size = node.Size ?? 0;
            SizeText.Text = FormatSizeWithBytes(size);
            SizeOnDiskText.Text = FormatSizeWithBytes(RoundUpToCluster(size));
        }

        CreatedText.Text = node.Created?.ToString("g") ?? "-";
        ModifiedText.Text = node.Modified?.ToString("g") ?? "-";
        AccessedText.Text = node.Accessed?.ToString("g") ?? "-";

        // A single-node selection is editable: name + Read-only/Hidden commit via OK/Apply
        // (see class remarks / bevel-iuh). Track the "as loaded" state for dirty comparison.
        _currentPath = node.Path;
        _initialName = node.DisplayName;
        _initialAttributes = node.Attributes;

        NameBox.IsReadOnly = false;
        ReadOnlyCheckBox.IsEnabled = true;
        HiddenCheckBox.IsEnabled = true;
        ReadOnlyCheckBox.IsChecked = node.Attributes.HasFlag(VfsNodeAttributes.ReadOnly);
        HiddenCheckBox.IsChecked = node.Attributes.HasFlag(VfsNodeAttributes.Hidden);
    }

    private void PopulateMulti(IReadOnlyList<IVfsNode> items)
    {
        Title = $"{items.Count} Items Properties";
        NameBox.Text = $"{items.Count} items selected";
        NameBox.IsReadOnly = true;

        IconHost.Child = Glyphs.Icon(32, IconKey.Unknown);   // generic document for a mixed selection

        var allFolders = items.All(i => IsFolderKind(i.Kind));
        var allFiles = items.All(i => i.Kind == VfsNodeKind.File);
        TypeText.Text = allFolders ? "File Folders" : allFiles ? "Files" : "Multiple Types";

        var parents = items.Select(i => DescribeLocation(i.Path)).Distinct().ToList();
        LocationText.Text = parents.Count == 1 ? parents[0] : "(various locations)";

        // Multi-selection sums the selected items' sizes; folders in the selection are recursed
        // into (same capped/skip-on-error walk as ScanFolderAsync) so their contents count toward
        // the combined size. Size-on-disk sums each individual file's size rounded up to a 4 KiB
        // cluster boundary (no per-volume cluster probe — see class remarks).
        var topFiles = items.Where(i => !IsFolderKind(i.Kind)).ToList();
        var topFolders = items.Where(i => IsFolderKind(i.Kind)).ToList();

        var fileSize = topFiles.Where(i => i.Size.HasValue).Sum(i => i.Size!.Value);
        var fileSizeOnDisk = topFiles.Sum(i => RoundUpToCluster(i.Size ?? 0));

        if (topFolders.Count == 0)
        {
            SizeText.Text = FormatSizeWithBytes(fileSize);
            SizeOnDiskText.Text = FormatSizeWithBytes(fileSizeOnDisk);
        }
        else
        {
            SizeText.Text = "Calculating...";
            SizeOnDiskText.Text = "Calculating...";
            MultiScanTask = ScanMultiAsync(topFolders, fileSize, fileSizeOnDisk, _cts.Token);
        }

        ContainsRow.IsVisible = false;
        CreatedText.Text = "-";
        ModifiedText.Text = "-";
        AccessedText.Text = "-";

        // Multi-selection stays a simplified, display-only sheet — no per-item commit target.
        ReadOnlyCheckBox.IsEnabled = false;
        HiddenCheckBox.IsEnabled = false;
        ReadOnlyCheckBox.IsChecked = false;
        HiddenCheckBox.IsChecked = false;
    }

    // ── Dirty tracking + commit (single-node selection only) ────────────

    /// <summary>Recomputes <see cref="_dirty"/> from the current name/attribute controls and
    /// enables/disables Apply accordingly. No-op for multi/empty selections.</summary>
    private void UpdateDirty()
    {
        if (_items.Count != 1) return;

        var nameChanged = !string.Equals(NameBox.Text, _initialName, StringComparison.Ordinal);
        var attrsChanged = CurrentEditableAttributes() != (_initialAttributes & EditableAttributeMask);
        _dirty = nameChanged || attrsChanged;
        ApplyButton.IsEnabled = _dirty && !_committing;
    }

    private VfsNodeAttributes CurrentEditableAttributes()
    {
        var attrs = VfsNodeAttributes.None;
        if (ReadOnlyCheckBox.IsChecked == true) attrs |= VfsNodeAttributes.ReadOnly;
        if (HiddenCheckBox.IsChecked == true) attrs |= VfsNodeAttributes.Hidden;
        return attrs;
    }

    private async Task OnOkAsync()
    {
        if (_dirty && !await CommitAsync())
            return; // commit failed — keep the dialog open so the user can adjust/retry.

        Close();
    }

    private async Task OnApplyAsync()
    {
        if (!_dirty) return;
        await CommitAsync(); // stays open regardless of outcome; UpdateDirty() re-syncs Apply.
    }

    /// <summary>
    /// Commits the pending name/attribute changes for the single selected node via its parent
    /// folder's <see cref="IVfsMutator"/>. Best-effort: any failure (permission denied, name
    /// collision, node raced away, read-only folder, ...) is swallowed and reported as
    /// <c>false</c> rather than thrown, so the caller can leave the dialog open.
    /// </summary>
    private async Task<bool> CommitAsync()
    {
        if (_items.Count != 1 || !_dirty) return true;

        _committing = true;
        OkButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        ApplyButton.IsEnabled = false;

        try
        {
            var parent = _currentPath.Parent;
            var mutator = await _vfsRoot.GetProvider(parent).GetMutatorAsync(parent, _cts.Token);
            if (mutator is null) return false; // read-only folder — nothing we can commit.

            var newPath = _currentPath;
            var newName = NameBox.Text ?? "";
            if (newName.Length > 0 && !string.Equals(newName, _initialName, StringComparison.Ordinal))
            {
                await mutator.RenameAsync(_currentPath, newName, _cts.Token);
                newPath = VfsPath.Combine(parent, newName);
            }

            var newEditableAttrs = CurrentEditableAttributes();
            if (newEditableAttrs != (_initialAttributes & EditableAttributeMask))
            {
                var mergedAttrs = (_initialAttributes & ~EditableAttributeMask) | newEditableAttrs;
                await mutator.SetAttributesAsync(newPath, mergedAttrs, _cts.Token);
                _initialAttributes = mergedAttrs;
            }

            _currentPath = newPath;
            _initialName = newPath.FileName;
            CommittedChange = true;
            _dirty = false;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            _committing = false;
            OkButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            ApplyButton.IsEnabled = _dirty;
        }
    }

    private static string DescribeLocation(VfsPath path)
    {
        var parent = path.ParentValue;
        return string.IsNullOrEmpty(parent) ? path.Scheme : parent;
    }

    // ── Folder size / contents scan ─────────────────────────────────────

    /// <summary>
    /// Recursively walks <paramref name="root"/> to compute a total size and file/folder count,
    /// depth- and node-capped (mirrors SearchService's approach) so a pathologically large tree
    /// can't hang the dialog. Folders that fail to enumerate (permissions, races) are skipped
    /// rather than aborting the whole walk. Runs off the constructor so the dialog paints
    /// immediately with "Calculating..." and fills in the real numbers once done.
    /// </summary>
    private async Task ScanFolderAsync(VfsPath root, CancellationToken ct)
    {
        const int MaxNodes = 50_000;
        const int MaxDepth = 32;

        long size = 0;
        long sizeOnDisk = 0;
        var files = 0;
        var folders = 0;
        var visited = 0;
        var truncated = false;

        var stack = new Stack<(VfsPath Path, int Depth)>();
        stack.Push((root, 0));

        try
        {
            while (stack.Count > 0 && !truncated)
            {
                ct.ThrowIfCancellationRequested();
                var (path, depth) = stack.Pop();

                List<IVfsNode> children;
                try
                {
                    children = await CollectChildrenAsync(path, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    continue; // inaccessible / unsupported / raced-away folder — skip, keep walking.
                }

                foreach (var child in children)
                {
                    if (visited >= MaxNodes) { truncated = true; break; }
                    visited++;

                    if (IsFolderKind(child.Kind))
                    {
                        folders++;
                        if (depth < MaxDepth) stack.Push((child.Path, depth + 1));
                    }
                    else
                    {
                        files++;
                        var childSize = child.Size ?? 0;
                        size += childSize;
                        sizeOnDisk += RoundUpToCluster(childSize); // per-file cluster rounding, not total
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            return; // Dialog closed — stop updating a window that's going away.
        }

        var suffix = truncated ? "+" : "";

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            SizeText.Text = FormatSizeWithBytes(size) + (truncated ? " (partial — large folder)" : "");
            SizeOnDiskText.Text = FormatSizeWithBytes(sizeOnDisk) + (truncated ? " (partial)" : "");
            ContainsText.Text = $"{files}{suffix} Files, {folders}{suffix} Folders";
        });
    }

    /// <summary>
    /// Recurses the folders within a multi-selection (same capped, skip-on-error walk as
    /// <see cref="ScanFolderAsync"/>) and adds their file sizes — raw and per-file cluster-rounded
    /// — on top of the already-summed top-level file sizes from the selection.
    /// </summary>
    private async Task ScanMultiAsync(IReadOnlyList<IVfsNode> folders, long baseSize, long baseSizeOnDisk, CancellationToken ct)
    {
        const int MaxNodes = 50_000;
        const int MaxDepth = 32;

        var size = baseSize;
        var sizeOnDisk = baseSizeOnDisk;
        var visited = 0;
        var truncated = false;

        var stack = new Stack<(VfsPath Path, int Depth)>();
        foreach (var folder in folders)
            stack.Push((folder.Path, 0));

        try
        {
            while (stack.Count > 0 && !truncated)
            {
                ct.ThrowIfCancellationRequested();
                var (path, depth) = stack.Pop();

                List<IVfsNode> children;
                try
                {
                    children = await CollectChildrenAsync(path, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    continue; // inaccessible / unsupported / raced-away folder — skip, keep walking.
                }

                foreach (var child in children)
                {
                    if (visited >= MaxNodes) { truncated = true; break; }
                    visited++;

                    if (IsFolderKind(child.Kind))
                    {
                        if (depth < MaxDepth) stack.Push((child.Path, depth + 1));
                    }
                    else
                    {
                        var childSize = child.Size ?? 0;
                        size += childSize;
                        sizeOnDisk += RoundUpToCluster(childSize);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            return; // Dialog closed — stop updating a window that's going away.
        }

        var suffix = truncated ? " (partial — large folder)" : "";
        var suffixDisk = truncated ? " (partial)" : "";

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            SizeText.Text = FormatSizeWithBytes(size) + suffix;
            SizeOnDiskText.Text = FormatSizeWithBytes(sizeOnDisk) + suffixDisk;
        });
    }

    private async Task<List<IVfsNode>> CollectChildrenAsync(VfsPath folder, CancellationToken ct)
    {
        var list = new List<IVfsNode>();
        await foreach (var node in _vfsRoot.EnumerateAsync(folder, new EnumerateOptions(), ct).ConfigureAwait(false))
            list.Add(node);
        return list;
    }

    private static bool IsFolderKind(VfsNodeKind kind)
        => kind is VfsNodeKind.Folder or VfsNodeKind.Volume or VfsNodeKind.VirtualRoot;

    // ── Formatting (matches StatusBar.FormatSize) ───────────────────────

    private static long RoundUpToCluster(long bytes, long clusterSize = 4096)
        => bytes <= 0 ? 0 : (bytes + clusterSize - 1) / clusterSize * clusterSize;

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} bytes";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    private static string FormatSizeWithBytes(long bytes) => $"{FormatSize(bytes)} ({bytes:N0} bytes)";

}
