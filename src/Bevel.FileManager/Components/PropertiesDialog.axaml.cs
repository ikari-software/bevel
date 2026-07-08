using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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
/// Deliberate simplifications for this first cut (tracked as follow-ups by the caller):
/// <list type="bullet">
/// <item>The name TextBox is display-only — committing a rename from here is not wired up.</item>
/// <item>
/// "Size on disk" is derived by rounding each file's <see cref="IVfsNode.Size"/> up to a 4 KiB
/// cluster boundary. There's no real cluster-size probe per volume; it's an approximation.
/// </item>
/// <item>
/// <see cref="IVfsNode"/> only exposes <see cref="IVfsNode.Modified"/> — Created/Accessed are not
/// available from the VFS abstraction yet, so those rows show "-".
/// </item>
/// <item>
/// Read-only/Hidden attributes aren't exposed on <see cref="IVfsNode"/> either, so the checkboxes
/// are always unchecked and disabled (display-only) rather than derived from the real file state.
/// </item>
/// <item>Apply is a no-op — there's nothing mutable on this sheet yet to apply.</item>
/// <item>
/// A multi-selection collapses to a simplified sheet (combined size, common type/location if they
/// agree, no per-item dates/attributes) rather than showing every item's full detail.
/// </item>
/// </list>
/// </remarks>
public partial class PropertiesDialog : BevelWindow
{
    private readonly VfsRoot _vfsRoot;
    private readonly IReadOnlyList<IVfsNode> _items;
    private readonly CancellationTokenSource _cts = new();

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

        OkButton.Click += (_, _) => Close();
        CancelButton.Click += (_, _) => Close();
        // Apply: no-op for now — nothing on this sheet mutates VFS state yet (see class remarks).
        ApplyButton.Click += (_, _) => { };

        Populate();
    }

    /// <summary>Shows the dialog modally over <paramref name="owner"/>.</summary>
    public Task ShowAsync(Window owner) => ShowDialog(owner);

    /// <summary>
    /// Exposed for tests: the async folder Contains/size scan started for a single-folder
    /// selection, if one was started (null for files or multi-selection). Tests await this
    /// instead of racing the background walk.
    /// </summary>
    internal Task? ContainsScanTask { get; private set; }

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

        IconHost.Child = BuildIcon(node.Kind, 32);

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

        CreatedText.Text = "-";  // Creation time isn't exposed by IVfsNode (see class remarks).
        ModifiedText.Text = node.Modified?.ToString("g") ?? "-";
        AccessedText.Text = "-"; // Access time isn't exposed by IVfsNode (see class remarks).

        // Attributes aren't exposed on IVfsNode — display-only placeholders (see class remarks).
        ReadOnlyCheckBox.IsChecked = false;
        HiddenCheckBox.IsChecked = false;
    }

    private void PopulateMulti(IReadOnlyList<IVfsNode> items)
    {
        Title = $"{items.Count} Items Properties";
        NameBox.Text = $"{items.Count} items selected";
        NameBox.IsReadOnly = true;

        IconHost.Child = BuildIcon(VfsNodeKind.File, 32);

        var allFolders = items.All(i => IsFolderKind(i.Kind));
        var allFiles = items.All(i => i.Kind == VfsNodeKind.File);
        TypeText.Text = allFolders ? "File Folders" : allFiles ? "Files" : "Multiple Types";

        var parents = items.Select(i => DescribeLocation(i.Path)).Distinct().ToList();
        LocationText.Text = parents.Count == 1 ? parents[0] : "(various locations)";

        // Multi-selection is a simplified sheet: only the top-level sizes of the selected items
        // are summed — folders in the selection are NOT recursed into. Follow-up if needed.
        var totalSize = items.Where(i => i.Size.HasValue).Sum(i => i.Size!.Value);
        SizeText.Text = FormatSizeWithBytes(totalSize);
        SizeOnDiskText.Text = FormatSizeWithBytes(RoundUpToCluster(totalSize));

        ContainsRow.IsVisible = false;
        CreatedText.Text = "-";
        ModifiedText.Text = "-";
        AccessedText.Text = "-";

        ReadOnlyCheckBox.IsChecked = false;
        HiddenCheckBox.IsChecked = false;
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
                        size += child.Size ?? 0;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            return; // Dialog closed — stop updating a window that's going away.
        }

        var sizeOnDisk = RoundUpToCluster(size);
        var suffix = truncated ? "+" : "";

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            SizeText.Text = FormatSizeWithBytes(size) + (truncated ? " (partial — large folder)" : "");
            SizeOnDiskText.Text = FormatSizeWithBytes(sizeOnDisk) + (truncated ? " (partial)" : "");
            ContainsText.Text = $"{files}{suffix} Files, {folders}{suffix} Folders";
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

    // ── Icon (clean-room: simple generic folder/document glyph, not derived from any
    //    Win2000 asset — the real semantic icon pipeline lives in ItemView and isn't
    //    reachable from here without widening its access, which is out of this lane) ──

    private static Control BuildIcon(VfsNodeKind kind, int size)
    {
        var canvas = new Canvas { Width = 32, Height = 32 };

        if (IsFolderKind(kind))
        {
            canvas.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M2,10 L2,27 L30,27 L30,12 L14,12 L11,8 L2,8 Z"),
                Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x7E)),
                Stroke = Brushes.Black,
                StrokeThickness = 1,
            });
        }
        else
        {
            canvas.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M6,2 L20,2 L26,8 L26,30 L6,30 Z"),
                Fill = Brushes.White,
                Stroke = Brushes.Black,
                StrokeThickness = 1,
            });
            canvas.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M20,2 L20,8 L26,8 Z"),
                Fill = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0)),
                Stroke = Brushes.Black,
                StrokeThickness = 0.5,
            });
            for (var i = 0; i < 3; i++)
            {
                canvas.Children.Add(new Avalonia.Controls.Shapes.Line
                {
                    StartPoint = new Point(9, 14 + i * 4),
                    EndPoint = new Point(23, 14 + i * 4),
                    Stroke = Brushes.Gray,
                    StrokeThickness = 1,
                });
            }
        }

        return new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, Child = canvas };
    }
}
