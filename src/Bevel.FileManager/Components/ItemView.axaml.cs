using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Core.Vfs;
using Bevel.FileManager.FileOperations;

namespace Bevel.FileManager.Components;

public partial class ItemView : UserControl
{
    public static readonly StyledProperty<ViewMode> ViewModeProperty =
        AvaloniaProperty.Register<ItemView, ViewMode>(nameof(ViewMode), ViewMode.LargeIcons);

    public static readonly StyledProperty<IReadOnlyList<IVfsNode>?> ItemsProperty =
        AvaloniaProperty.Register<ItemView, IReadOnlyList<IVfsNode>?>(nameof(Items));

    public static readonly StyledProperty<ItemViewModel?> SelectedItemProperty =
        AvaloniaProperty.Register<ItemView, ItemViewModel?>(nameof(SelectedItem));

    // Details-view column widths — shared source of truth so the header and every row
    // stay aligned, and a header drag-grip can resize both live (see OnColResize).
    public static readonly StyledProperty<GridLength> SizeColWidthProperty =
        AvaloniaProperty.Register<ItemView, GridLength>(nameof(SizeColWidth), new GridLength(80));
    public static readonly StyledProperty<GridLength> TypeColWidthProperty =
        AvaloniaProperty.Register<ItemView, GridLength>(nameof(TypeColWidth), new GridLength(120));
    public static readonly StyledProperty<GridLength> DateColWidthProperty =
        AvaloniaProperty.Register<ItemView, GridLength>(nameof(DateColWidth), new GridLength(140));

    public ViewMode ViewMode { get => GetValue(ViewModeProperty); set => SetValue(ViewModeProperty, value); }
    public IReadOnlyList<IVfsNode>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public ItemViewModel? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }
    public GridLength SizeColWidth { get => GetValue(SizeColWidthProperty); set => SetValue(SizeColWidthProperty, value); }
    public GridLength TypeColWidth { get => GetValue(TypeColWidthProperty); set => SetValue(TypeColWidthProperty, value); }
    public GridLength DateColWidth { get => GetValue(DateColWidthProperty); set => SetValue(DateColWidthProperty, value); }

    /// <summary>The full multi-selection in click order (last = focus). Read by the controller at command time.</summary>
    public IReadOnlyList<ItemViewModel> SelectedItems => _selectedOrder;

    /// <summary>Select a single item by path (controller/agent-driven selection; also used to
    /// preview selection in the render harness). No-op if the path isn't present.</summary>
    public void SelectPath(VfsPath path)
    {
        var vm = _viewModels.FirstOrDefault(v => v.Path == path);
        if (vm is not null) SelectOne(vm);
    }

    private readonly ObservableCollection<ItemViewModel> _viewModels = new();
    private readonly HashSet<ItemViewModel> _selected = new();
    private readonly List<ItemViewModel> _selectedOrder = new();
    private ItemViewModel? _anchor;
    private int _lastClickIdx = -1;

    private readonly Stopwatch _typeTimer = Stopwatch.StartNew();
    private string _typeBuffer = "";
    private const int TypeResetMs = 700;

    private Point _marqueeOrigin;
    private bool _marqueeDragging;

    private Point _dragStart;
    private bool _dragArmed;

    private TextBox? _renameBox;
    private readonly FuncDataTemplate<ItemViewModel> _detailsTpl;

    private SortColumn _sortCol = SortColumn.Name;
    private bool _sortAsc = true;

    public event EventHandler<ItemActivatedEventArgs>? ItemActivated;
    public event EventHandler<DropEventArgs>? DropRequested;
    public event EventHandler<RenameCommittedEventArgs>? RenameCommitted;
    public event EventHandler<FileContextRequestedEventArgs>? ItemContextRequested;

    // Expose named controls for tests
    public Avalonia.Controls.ItemsControl ItemsControl => ItemsPresenter;
    public Avalonia.Controls.Border ColumnHeaderBorder => ColumnHeaders;
    public Grid DetailsHeaderGrid => HeaderGrid;

    internal enum SortColumn { Name, Size, Type, Modified }

    public ItemView()
    {
        InitializeComponent();
        PointerPressed += OnBgPointerPressed;
        PointerMoved += OnBgPointerMoved;
        PointerReleased += OnBgPointerReleased;
        KeyDown += OnKeyDown;
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(ContextRequestedEvent, OnContextRequested);

        // Details columns: bind the header's Size/Type/Date widths to the shared properties
        // and let the header grips resize them. Rows bind the same way (BuildDetailsTemplate).
        _detailsTpl = BuildDetailsTemplate();
        HeaderGrid.ColumnDefinitions[1].Bind(ColumnDefinition.WidthProperty, this.GetObservable(SizeColWidthProperty));
        HeaderGrid.ColumnDefinitions[2].Bind(ColumnDefinition.WidthProperty, this.GetObservable(TypeColWidthProperty));
        HeaderGrid.ColumnDefinitions[3].Bind(ColumnDefinition.WidthProperty, this.GetObservable(DateColWidthProperty));
        SizeGrip.DragDelta += OnColResize;
        TypeGrip.DragDelta += OnColResize;
        DateGrip.DragDelta += OnColResize;
    }

    /// <summary>Drag a header grip to resize its column (min 28px); header and all rows follow.</summary>
    private void OnColResize(object? sender, VectorEventArgs e)
    {
        switch ((sender as Thumb)?.Name)
        {
            case "SizeGrip": SizeColWidth = ClampCol(SizeColWidth.Value + e.Vector.X); break;
            case "TypeGrip": TypeColWidth = ClampCol(TypeColWidth.Value + e.Vector.X); break;
            case "DateGrip": DateColWidth = ClampCol(DateColWidth.Value + e.Vector.X); break;
        }
    }

    private static GridLength ClampCol(double px) => new(Math.Max(28, px));

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        ApplyViewMode(ViewMode);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ViewModeProperty)
            ApplyViewMode((ViewMode)change.NewValue!);
        else if (change.Property == ItemsProperty)
            RebuildItems();
    }

    // ── View modes ────────────────────────────────────────────────────

    private void ApplyViewMode(ViewMode mode)
    {
        ColumnHeaders.IsVisible = mode == ViewMode.Details;
        RefreshSortIndicators();

        switch (mode)
        {
            case ViewMode.Details:
                ItemsPresenter.ItemsPanel = new FuncTemplate<Panel>(() =>
                    new VirtualizingStackPanel { Orientation = Orientation.Vertical });
                ItemsPresenter.ItemTemplate = _detailsTpl;
                break;
            case ViewMode.LargeIcons:
                ItemsPresenter.ItemsPanel = new FuncTemplate<Panel>(() =>
                    new VirtualizingWrapPanel { ItemWidth = 80, ItemHeight = 60, FlowDirection = Orientation.Horizontal });
                ItemsPresenter.ItemTemplate = LargeIconTpl;
                break;
            case ViewMode.SmallIcons:
                ItemsPresenter.ItemsPanel = new FuncTemplate<Panel>(() =>
                    new VirtualizingWrapPanel { ItemWidth = 180, ItemHeight = 20, FlowDirection = Orientation.Horizontal });
                ItemsPresenter.ItemTemplate = SmallIconTpl;
                break;
            case ViewMode.List:
                ItemsPresenter.ItemsPanel = new FuncTemplate<Panel>(() =>
                    new VirtualizingStackPanel { Orientation = Orientation.Vertical });
                ItemsPresenter.ItemTemplate = ListTpl;
                break;
            case ViewMode.Thumbnails:
                ItemsPresenter.ItemsPanel = new FuncTemplate<Panel>(() =>
                    new VirtualizingWrapPanel { ItemWidth = 120, ItemHeight = 120, FlowDirection = Orientation.Horizontal });
                ItemsPresenter.ItemTemplate = ThumbTpl;
                break;
        }
    }

    // ── Templates ─────────────────────────────────────────────────────

    // Vector icons — crisp at any DPI (the "vectors + gradients, not dithered bitmaps" rule).
    // Authored in a 16-unit space and scaled by a Viewbox to the requested size.
    private static LinearGradientBrush VGrad(string top, string bottom) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse(top), 0), new GradientStop(Color.Parse(bottom), 1) },
    };

    static readonly IBrush FolderBack = VGrad("#FFE49A", "#F0B03C");
    static readonly IBrush FolderFront = VGrad("#FFF3CE", "#FFD064");
    static readonly IBrush FolderEdge = new SolidColorBrush(Color.Parse("#9C6B15"));
    static readonly IBrush PaperFill = VGrad("#FFFFFF", "#ECECEC");
    static readonly IBrush PaperEdge = new SolidColorBrush(Color.Parse("#7F9DB9"));
    static readonly IBrush PaperFold = new SolidColorBrush(Color.Parse("#DCE7F2"));
    static readonly IBrush PaperLine = new SolidColorBrush(Color.Parse("#B4C6D8"));

    static Avalonia.Controls.Shapes.Path Vec(string data, IBrush? fill, IBrush? stroke = null, double sw = 0.5) =>
        new() { Data = Geometry.Parse(data), Fill = fill, Stroke = stroke, StrokeThickness = sw };

    private const string FolderBackData = "M1.5,4.3 H6 l1.4,1.4 H14 a0.7,0.7 0 0 1 0.7,0.7 V12.4 H1.5 Z";
    private const string FolderFrontData = "M1.5,6.9 H15.1 l-1.25,5.7 a0.7,0.7 0 0 1 -0.68,0.55 H2.35 a0.7,0.7 0 0 1 -0.68,-0.55 Z";
    private const string DocPageData = "M3.4,1.5 H10 L12.6,4.1 V13.9 a0.4,0.4 0 0 1 -0.4,0.4 H3.4 a0.4,0.4 0 0 1 -0.4,-0.4 V1.9 a0.4,0.4 0 0 1 0.4,-0.4 Z";

    private static SolidColorBrush SB(string hex) => new(Color.Parse(hex));
    static readonly IBrush ExeTitle = SB("#0A246A");
    static readonly IBrush ExeLine = SB("#9DB4C8");
    static readonly IBrush Sky = SB("#A9D3F5");
    static readonly IBrush Sun = SB("#FFD64A");
    static readonly IBrush Mountain = SB("#5E9E52");
    static readonly IBrush ZipLine = SB("#6B6B6B");
    static readonly IBrush ZipTeeth = SB("#9A9A9A");
    static readonly IBrush DriveBody = SB("#CBD0D6");
    static readonly IBrush DriveEdge = SB("#7A8088");
    static readonly IBrush DriveLed = SB("#62C462");
    static readonly IBrush MonScreen = SB("#4F79A8");
    static readonly IBrush MonInner = SB("#2E5B90");
    static readonly IBrush MonEdge = SB("#3A3A3A");
    static readonly IBrush MonStand = SB("#B8BCC2");

    static readonly HashSet<string> ExeExt = new(StringComparer.OrdinalIgnoreCase) { "exe", "com", "bat", "cmd", "scr", "msi", "ps1", "sh", "app", "dll" };
    static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { "png", "jpg", "jpeg", "gif", "bmp", "ico", "webp", "tif", "tiff", "svg" };
    static readonly HashSet<string> ArchiveExt = new(StringComparer.OrdinalIgnoreCase) { "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "cab" };

    static Avalonia.Controls.Shapes.Ellipse Ell(double x, double y, double d, IBrush? fill, IBrush? stroke = null, double sw = 0.4)
    {
        var e = new Avalonia.Controls.Shapes.Ellipse { Width = d, Height = d, Fill = fill, Stroke = stroke, StrokeThickness = sw };
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        return e;
    }

    /// <summary>Self-drawn vector glyph chosen from the node's semantic icon key. Crisp at any DPI.</summary>
    static Control Icon(int size, IconKey key)
    {
        var c = new Canvas { Width = 16, Height = 16 };
        BuildGlyph(c, key.SemanticId ?? "doc.generic");
        return new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, Child = c };
    }

    static void BuildGlyph(Canvas c, string id)
    {
        if (id.StartsWith("folder")) { FolderGlyph(c); return; }
        if (id == "computer") { ComputerGlyph(c); return; }
        if (id.StartsWith("drive")) { DriveGlyph(c, id); return; }
        var ext = id.StartsWith("doc.") ? id[4..] : "";
        if (ExeExt.Contains(ext)) { ExeGlyph(c); return; }
        if (ImageExt.Contains(ext)) { ImageGlyph(c); return; }
        if (ArchiveExt.Contains(ext)) { ArchiveGlyph(c); return; }
        DocGlyph(c);
    }

    static void FolderGlyph(Canvas c)
    {
        c.Children.Add(Vec(FolderBackData, FolderBack, FolderEdge));
        c.Children.Add(Vec(FolderFrontData, FolderFront, FolderEdge));
    }

    static void DocGlyph(Canvas c)
    {
        c.Children.Add(Vec(DocPageData, PaperFill, PaperEdge));
        c.Children.Add(Vec("M10,1.5 V4.1 H12.6 Z", PaperFold, PaperEdge, 0.4));
        for (var i = 0; i < 3; i++)
            c.Children.Add(Vec($"M5,{7.0 + i * 2.0} H10.6", null, PaperLine, 0.7));
    }

    static void ExeGlyph(Canvas c)
    {
        // A little program window: navy title bar over a light body.
        c.Children.Add(Vec("M2.5,3.2 H13.5 V13 H2.5 Z", PaperFill, PaperEdge, 0.5));
        c.Children.Add(Vec("M2.5,3.2 H13.5 V5.4 H2.5 Z", ExeTitle));
        c.Children.Add(Vec("M4,7.6 H12 M4,9.4 H11 M4,11.2 H9", null, ExeLine, 0.7));
    }

    static void ImageGlyph(Canvas c)
    {
        c.Children.Add(Vec(DocPageData, PaperFill, PaperEdge));
        c.Children.Add(Vec("M4.6,6.4 H11.6 V12 H4.6 Z", Sky, PaperEdge, 0.4));   // photo inset
        c.Children.Add(Ell(9.4, 7.0, 1.8, Sun));                                  // sun
        c.Children.Add(Vec("M4.6,12 L6.7,9.2 L8.3,10.8 L10,8.6 L11.6,10.6 V12 Z", Mountain));
    }

    static void ArchiveGlyph(Canvas c)
    {
        FolderGlyph(c);
        c.Children.Add(Vec("M8,6.9 V13.0", null, ZipLine, 0.9));                  // zipper
        c.Children.Add(Vec("M7.1,8.2 H8.9 M7.1,9.7 H8.9 M7.1,11.2 H8.9", null, ZipTeeth, 0.6));
        c.Children.Add(Vec("M7.3,5.8 H8.7 V7.1 H7.3 Z", ZipTeeth, ZipLine, 0.4)); // pull tab
    }

    static void DriveGlyph(Canvas c, string id)
    {
        if (id.Contains("cd"))
        {
            c.Children.Add(Ell(2.5, 2.5, 11, DriveBody, DriveEdge, 0.5));
            c.Children.Add(Ell(6.4, 6.4, 3.2, Brushes.White, DriveEdge, 0.4));
            return;
        }
        c.Children.Add(Vec("M2,5.5 H14 V11 H2 Z", DriveBody, DriveEdge, 0.5));
        c.Children.Add(Vec("M3.5,7 H10.5", null, DriveEdge, 0.5));
        c.Children.Add(Ell(11.5, 8.1, 1.4, DriveLed));
    }

    static void ComputerGlyph(Canvas c)
    {
        c.Children.Add(Vec("M2.5,3 H13.5 V10 H2.5 Z", MonEdge, MonEdge, 0.4));
        c.Children.Add(Vec("M3.3,3.8 H12.7 V9.2 H3.3 Z", MonScreen));
        c.Children.Add(Vec("M3.3,3.8 H12.7 V6.6 H3.3 Z", MonInner));
        c.Children.Add(Vec("M7,10 H9 V11.4 H7 Z", MonStand));
        c.Children.Add(Vec("M5,12.6 H11 V13.6 H5 Z", MonStand, MonEdge, 0.3));
    }

    static TextBlock Label(string text, int maxW = 0) => new()
    {
        Text = text, FontSize = 11,
        MaxWidth = maxW > 0 ? maxW : double.MaxValue,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    // Win2000 selection: the filename label gets a navy (#0A246A) highlight with white text; the
    // icon is left un-highlighted, exactly like classic Explorer. Bound to the row's IsSelected so
    // it tracks selection without rebuilding the row.
    static readonly IBrush SelectionFill = new SolidColorBrush(Color.Parse("#0A246A"));
    static readonly Avalonia.Data.Converters.FuncValueConverter<bool, IBrush?> SelBgConv =
        new(sel => sel ? SelectionFill : Brushes.Transparent);
    static readonly Avalonia.Data.Converters.FuncValueConverter<bool, IBrush> SelFgConv =
        new(sel => sel ? Brushes.White : Brushes.Black);

    /// <summary>The filename label wrapped in a selection-highlight border (navy bar + white text
    /// when selected, plus a dotted focus outline). Used by every view mode's template.</summary>
    static Control NameCell(string text, TextWrapping wrap = TextWrapping.NoWrap, double maxW = 0,
        TextAlignment align = TextAlignment.Left)
    {
        var label = new TextBlock
        {
            Text = text, FontSize = 11,
            TextWrapping = wrap, TextAlignment = align,
            MaxWidth = maxW > 0 ? maxW : double.MaxValue,
            TextTrimming = wrap == TextWrapping.NoWrap ? TextTrimming.CharacterEllipsis : TextTrimming.None,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.Bind(TextBlock.ForegroundProperty, new Avalonia.Data.Binding(nameof(ItemViewModel.IsSelected)) { Converter = SelFgConv });

        var highlight = new Border { Child = label, Padding = new(2, 0) };
        highlight.Bind(Border.BackgroundProperty, new Avalonia.Data.Binding(nameof(ItemViewModel.IsSelected)) { Converter = SelBgConv });
        return highlight;
    }

    static readonly FuncDataTemplate<ItemViewModel> LargeIconTpl = new((vm, _) =>
    {
        if (vm is null) return new TextBlock { Text = "" };
        var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = 2, Margin = new(4) };
        s.Children.Add(Icon(32, vm.IconKey));
        s.Children.Add(NameCell(vm.DisplayName, TextWrapping.Wrap, 72, TextAlignment.Center));
        return s;
    });

    static readonly FuncDataTemplate<ItemViewModel> SmallIconTpl = new((vm, _) =>
    {
        if (vm is null) return new TextBlock { Text = "" };
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new(2, 0) };
        s.Children.Add(Icon(16, vm.IconKey));
        s.Children.Add(NameCell(vm.DisplayName));
        return s;
    });

    static readonly FuncDataTemplate<ItemViewModel> ListTpl = new((vm, _) =>
    {
        if (vm is null) return new TextBlock { Text = "" };
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new(0, 1) };
        s.Children.Add(Icon(16, vm.IconKey));
        s.Children.Add(NameCell(vm.DisplayName));
        return s;
    });

    // Instance (not static) so each row's Size/Type/Date columns bind to the shared column
    // widths — a header resize then flows to every row live. Name column stays star-sized.
    private FuncDataTemplate<ItemViewModel> BuildDetailsTemplate() => new((vm, _) =>
    {
        if (vm is null) return new TextBlock { Text = "" };
        // No horizontal gridlines — classic Explorer details view has plain white rows.
        var row = new Border { Padding = new(2, 1) };
        var g = new Grid { Height = 20 };
        g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        g.ColumnDefinitions.Add(BoundCol(SizeColWidthProperty));
        g.ColumnDefinitions.Add(BoundCol(TypeColWidthProperty));
        g.ColumnDefinitions.Add(BoundCol(DateColWidthProperty));
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        name.Children.Add(Icon(16, vm.IconKey));
        name.Children.Add(NameCell(vm.DisplayName));
        g.Children.Add(name);
        g.Children.Add(new TextBlock { [Grid.ColumnProperty] = 1, Text = vm.SizeDisplay, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch, TextAlignment = TextAlignment.Right, FontSize = 11, Margin = new(4, 0) });
        g.Children.Add(new TextBlock { [Grid.ColumnProperty] = 2, Text = vm.TypeDescription, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Margin = new(4, 0) });
        g.Children.Add(new TextBlock { [Grid.ColumnProperty] = 3, Text = vm.ModifiedDisplay, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Margin = new(4, 0) });
        row.Child = g;
        return row;
    });

    /// <summary>A details-view column whose width tracks a shared ItemView property (live).</summary>
    private ColumnDefinition BoundCol(StyledProperty<GridLength> prop)
    {
        var c = new ColumnDefinition();
        c.Bind(ColumnDefinition.WidthProperty, this.GetObservable(prop));
        return c;
    }

    static readonly FuncDataTemplate<ItemViewModel> ThumbTpl = new((vm, _) =>
    {
        if (vm is null) return new TextBlock { Text = "" };
        var b = new Border { BorderBrush = new SolidColorBrush(0xFFACA899), BorderThickness = new(1), Padding = new(4), Margin = new(2), Background = Brushes.White };
        var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = 2 };
        s.Children.Add(Icon(96, vm.IconKey));
        s.Children.Add(NameCell(vm.DisplayName, TextWrapping.Wrap, 106, TextAlignment.Center));
        b.Child = s;
        return b;
    });

    // ── Rebuild ───────────────────────────────────────────────────────

    /// <summary>Clear all items and reset view. Call before streaming new items.</summary>
    public void ResetItems()
    {
        _viewModels.Clear();
        _selected.Clear(); _selectedOrder.Clear(); _anchor = null; _lastClickIdx = -1;
        ItemsPresenter.ItemsSource = _viewModels;
    }

    /// <summary>Append items incrementally (streaming enumeration). Sorts after each batch.</summary>
    public void AddItems(IReadOnlyList<IVfsNode> nodes)
    {
        foreach (var n in nodes) _viewModels.Add(new ItemViewModel(n));
        SortItems();
    }

    public bool HasItems => _viewModels.Count > 0;

    /// <summary>
    /// Differentially update the list to match <paramref name="nodes"/>, keyed by path — a React-
    /// style keyed diff. Surviving rows keep their object identity (and selection); only genuinely
    /// added or removed entries mutate the collection, and metadata is refreshed in place. When the
    /// path set is unchanged (the common watcher/refresh case) nothing rebinds, so the view does
    /// not flicker. Re-sorting (which detaches the ItemsSource) happens only when items were added.
    /// </summary>
    public void ReconcileItems(IReadOnlyList<IVfsNode> nodes)
    {
        var existing = new Dictionary<VfsPath, ItemViewModel>(_viewModels.Count);
        foreach (var vm in _viewModels) existing[vm.Path] = vm;

        var incoming = new HashSet<VfsPath>(nodes.Count);
        var added = 0;
        foreach (var n in nodes)
        {
            incoming.Add(n.Path);
            if (existing.TryGetValue(n.Path, out var vm))
                vm.Update(n);                       // same row, fresh metadata
            else
            {
                _viewModels.Add(new ItemViewModel(n));
                added++;
            }
        }

        // Drop rows whose paths disappeared (in place — a Remove doesn't tear down the list).
        for (var i = _viewModels.Count - 1; i >= 0; i--)
        {
            var vm = _viewModels[i];
            if (incoming.Contains(vm.Path)) continue;
            _viewModels.RemoveAt(i);
            _selected.Remove(vm);
            _selectedOrder.Remove(vm);
        }

        // Only a genuine insertion needs the collection re-ordered.
        if (added > 0) SortItems();
    }

    void RebuildItems()
    {
        _viewModels.Clear();
        _selected.Clear(); _selectedOrder.Clear(); _anchor = null; _lastClickIdx = -1;
        if (Items != null)
            foreach (var n in Items) _viewModels.Add(new ItemViewModel(n));
        SortItems();
        ItemsPresenter.ItemsSource = _viewModels;
    }

    // ── Sorting ──────────────────────────────────────────────────────

    void OnHeaderClick(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TextBlock tb || tb.Tag is not string ts
            || !Enum.TryParse<SortColumn>(ts, out var col)) return;
        _sortAsc = _sortCol == col ? !_sortAsc : true;
        _sortCol = col;
        _ = SortAsync(); RefreshSortIndicators();
        e.Handled = true;
    }

    /// <summary>Sort on a background thread for large lists, synchronously for small.</summary>
    async Task SortAsync()
    {
        var count = _viewModels.Count;
        if (count < 500) { SortItems(); return; }

        // Compute the order off-thread from a snapshot. Items can stream in (AddItems)
        // during the await; we must NOT drop them — append any late arrivals so nothing
        // is lost (bevel-rn9). SortAsync and AddItems both run on the UI thread, so no
        // items are added between resuming here and the rebuild below.
        var snapshot = _viewModels.ToArray();
        var col = _sortCol; var asc = _sortAsc;
        var sorted = await Task.Run(() => OrderItems(snapshot, col, asc));

        var inSorted = new HashSet<ItemViewModel>(sorted);
        var latecomers = _viewModels.Where(v => !inSorted.Contains(v)).ToList();

        ApplyOrder(sorted.Concat(latecomers));
    }

    void SortItems() => ApplyOrder(OrderItems(_viewModels, _sortCol, _sortAsc));

    /// <summary>
    /// Rebuilds the bound collection in the given order. ItemsSource is detached first to
    /// avoid the virtualization teardown crash, then re-attached.
    /// </summary>
    void ApplyOrder(IEnumerable<ItemViewModel> order)
    {
        var ordered = order.ToList();
        ItemsPresenter.ItemsSource = null;
        _viewModels.Clear();
        foreach (var v in ordered) _viewModels.Add(v);
        ItemsPresenter.ItemsSource = _viewModels;
    }

    internal static List<ItemViewModel> OrderItems(IEnumerable<ItemViewModel> items, SortColumn col, bool asc)
    {
        IEnumerable<ItemViewModel> q = col switch
        {
            SortColumn.Size     => items.OrderBy(x => x.Size ?? 0),
            SortColumn.Type     => items.OrderBy(x => x.TypeDescription, StringComparer.OrdinalIgnoreCase),
            SortColumn.Modified => items.OrderBy(x => x.Modified ?? default),
            _                   => items.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase),
        };
        if (!asc) q = q.Reverse();
        return q.ToList();
    }

    void RefreshSortIndicators()
    {
        foreach (var tb in new[] { SortName, SortSize, SortType, SortModified })
        {
            if (tb is null) continue;
            var col = tb.Tag is string s && Enum.TryParse<SortColumn>(s, out var c) ? c : SortColumn.Name;
            var baseText = col switch
            {
                SortColumn.Name => " Name", SortColumn.Size => " Size",
                SortColumn.Type => " Type", SortColumn.Modified => " Date Modified",
                _ => tb.Text ?? "",
            };
            tb.Text = col == _sortCol ? baseText + (_sortAsc ? " \u25B2" : " \u25BC") : baseText;
        }
    }

    // ── Selection ────────────────────────────────────────────────────

    ItemViewModel? Hit(Point pt)
    {
        for (var el = this.GetVisualAt(pt) as Visual; el is not null && el != this; el = el.GetVisualParent())
        {
            if (el is not Control ctl) continue;
            for (var c = ctl; c is not null && c != ItemsPresenter; c = c.Parent as Control)
                if (c.DataContext is ItemViewModel vm && _viewModels.Contains(vm)) return vm;
        }
        return null;
    }

    void SelectOne(ItemViewModel vm) { ClearSel(); AddSel(vm); SelectedItem = vm; _anchor = vm; RaiseSelection(); }
    void Toggle(ItemViewModel vm) { if (vm.IsSelected) RemSel(vm); else AddSel(vm); SelectedItem = vm; _anchor = vm; RaiseSelection(); }
    int Idx(ItemViewModel? vm) => vm is null ? -1 : _viewModels.IndexOf(vm);

    void RangeTo(ItemViewModel vm)
    {
        int a = Idx(_anchor ?? vm), b = Idx(vm);
        if (a < 0 || b < 0) return;
        ClearSel();
        for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) AddSel(_viewModels[i]);
        SelectedItem = vm;
        RaiseSelection();
    }

    void Marquee(Rect r)
    {
        ClearSel();
        foreach (var c in ItemsPresenter.GetRealizedContainers())
        {
            if (c is not Control ctl || ctl.DataContext is not ItemViewModel vm) continue;
            var pos = ctl.TranslatePoint(default, ItemsPresenter) ?? default;
            if (r.Intersects(new(pos.X, pos.Y, ctl.Bounds.Width, ctl.Bounds.Height))) AddSel(vm);
        }
        if (_selected.Count > 0) SelectedItem = _selectedOrder[^1];
        RaiseSelection();
    }

    void AddSel(ItemViewModel vm) { vm.IsSelected = true; _selected.Add(vm); _selectedOrder.Add(vm); }
    void RemSel(ItemViewModel vm) { vm.IsSelected = false; _selected.Remove(vm); _selectedOrder.Remove(vm); }
    void ClearSel() { foreach (var s in _selected) s.IsSelected = false; _selected.Clear(); _selectedOrder.Clear(); }

    /// <summary>Raised whenever the selection set changes (drives the info pane).</summary>
    public event Action? SelectionChanged;
    void RaiseSelection() => SelectionChanged?.Invoke();

    // ── Pointer ──────────────────────────────────────────────────────

    void OnBgPointerPressed(object? _, PointerPressedEventArgs e)
    {
        // Grab keyboard focus — handling the press (below) suppresses Avalonia's automatic
        // focus-on-click, which would otherwise leave arrows/type-ahead/Enter dead after a click.
        Focus();
        var pt = e.GetPosition(ItemsPresenter);
        var vm = Hit(pt);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.ClickCount == 2 && vm is not null) { ItemActivated?.Invoke(this, new(vm)); e.Handled = true; return; }
        if (vm is null)
        {
            if (!ctrl && !shift) { ClearSel(); RaiseSelection(); }
            _marqueeOrigin = pt; _marqueeDragging = true; MarqueeRect.IsVisible = false;
            e.Handled = true; return;
        }
        if (shift) RangeTo(vm);
        else if (ctrl) Toggle(vm);
        else if (!vm.IsSelected) SelectOne(vm);
        _lastClickIdx = Idx(vm);
        _dragArmed = !ctrl;                 // a plain/shift press on an item can begin a drag
        _dragStart = e.GetPosition(this);
        e.Handled = true;
    }

    void OnBgPointerMoved(object? _, PointerEventArgs e)
    {
        // Past a small threshold, a press on a selected item becomes a drag of the selection.
        if (_dragArmed && !_marqueeDragging)
        {
            var d = e.GetPosition(this);
            if (Math.Abs(d.X - _dragStart.X) > 4 || Math.Abs(d.Y - _dragStart.Y) > 4)
            {
                _dragArmed = false;
                _ = StartDragAsync(e);
            }
            return;
        }
        if (!_marqueeDragging) return;
        var pt = e.GetPosition(ItemsPresenter);
        var r = new Rect(Math.Min(_marqueeOrigin.X, pt.X), Math.Min(_marqueeOrigin.Y, pt.Y), Math.Abs(pt.X - _marqueeOrigin.X), Math.Abs(pt.Y - _marqueeOrigin.Y));
        if (r.Width > 4 || r.Height > 4) { Canvas.SetLeft(MarqueeRect, r.X); Canvas.SetTop(MarqueeRect, r.Y); MarqueeRect.Width = r.Width; MarqueeRect.Height = r.Height; MarqueeRect.IsVisible = true; }
    }

    void OnBgPointerReleased(object? _, PointerReleasedEventArgs e)
    {
        _dragArmed = false;
        if (!_marqueeDragging) return;
        _marqueeDragging = false;
        if (MarqueeRect.IsVisible)
        {
            var r = new Rect(Canvas.GetLeft(MarqueeRect), Canvas.GetTop(MarqueeRect), MarqueeRect.Width, MarqueeRect.Height);
            MarqueeRect.IsVisible = false; Marquee(r);
        }
        e.Handled = true;
    }

    /// <summary>Begin a drag of the current selection. The payload is our internal
    /// FileDropPayload, which the drop side (and other Bevel windows) already understand.</summary>
    async System.Threading.Tasks.Task StartDragAsync(PointerEventArgs e)
    {
        var paths = _selectedOrder.Select(v => v.Path).ToList();
        if (paths.Count == 0) return;
        var data = new DataObject();
        data.Set(FileDropPayload.DataFormat, new FileDropPayload { Paths = paths });
        try
        {
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch { /* drag cancelled or platform refused — nothing to clean up */ }
    }

    // ── DnD ──────────────────────────────────────────────────────────

    void OnDragOver(object? _, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) || e.Data.Contains(FileDropPayload.DataFormat)
            ? DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object? _, DragEventArgs e)
    {
        List<VfsPath>? paths = null;
        if (e.Data.Contains(FileDropPayload.DataFormat))
            paths = (e.Data.Get(FileDropPayload.DataFormat) as FileDropPayload)?.Paths.ToList();
        else if (e.Data.Contains(DataFormats.Files))
            paths = e.Data.GetFiles()?.Select(f => new VfsPath("file", f.Path.LocalPath)).ToList();
        // Dropping onto a folder targets that folder; onto empty space, the current directory.
        var over = Hit(e.GetPosition(ItemsPresenter));
        var target = over is { IsFolder: true } ? over.Path : (VfsPath?)null;
        if (paths is { Count: > 0 })
            DropRequested?.Invoke(this, new(paths, e.KeyModifiers.HasFlag(KeyModifiers.Control), e.KeyModifiers.HasFlag(KeyModifiers.Shift), target));
        e.Handled = true;
    }

    // ── Context menu ─────────────────────────────────────────────────

    void OnContextRequested(object? _, ContextRequestedEventArgs e)
    {
        // Locate the item under the pointer; a background (empty-space) request has none.
        ItemViewModel? vm = null;
        if (e.TryGetPosition(ItemsPresenter, out var pos))
            vm = Hit(pos);
        else
            pos = default;

        // Right-clicking an unselected item selects it first (Explorer behaviour).
        if (vm is not null && !vm.IsSelected) SelectOne(vm);

        ItemContextRequested?.Invoke(this, new FileContextRequestedEventArgs(vm, pos));
        e.Handled = true;
    }

    // ── Keyboard ─────────────────────────────────────────────────────

    void OnKeyDown(object? _, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F2: BeginRename(); e.Handled = true; break;
            case Key.Space: case Key.Enter: case Key.Down when e.KeyModifiers.HasFlag(KeyModifiers.Meta):
                if (_selectedOrder.Count > 0) { SelectedItem = _selectedOrder[^1]; ItemActivated?.Invoke(this, new(SelectedItem)); }
                e.Handled = true; break;
            case Key.A when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                foreach (var vm in _viewModels) AddSel(vm); e.Handled = true; break;
            case Key.Down or Key.Up or Key.Left or Key.Right:
                Navigate(e.Key, e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true; break;
            case Key.Home:
                MoveTo(0, e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true; break;
            case Key.End:
                MoveTo(_viewModels.Count - 1, e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true; break;
            case Key.PageDown:
                MoveTo((_lastClickIdx < 0 ? 0 : _lastClickIdx) + PageStep(), e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true; break;
            case Key.PageUp:
                MoveTo((_lastClickIdx < 0 ? 0 : _lastClickIdx) - PageStep(), e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true; break;
            case Key.Escape:
                ClearSel(); RaiseSelection(); e.Handled = true; break;
            default:
                // Skip type-ahead when a modifier is held so Ctrl+C/X/V (Copy/Cut/Paste,
                // handled by the window) don't jump the selection instead.
                if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Meta)
                    && e.Key is >= Key.A and <= Key.Z) TypeAhead(e.Key);
                break;
        }
    }

    void Navigate(Key key, bool shift)
    {
        if (_viewModels.Count == 0) return;
        int cols = ColsPerRow();
        int cur = _lastClickIdx < 0 ? 0 : _lastClickIdx;
        int next = key switch
        {
            Key.Down  => cur + cols,   // one row down (a single row in Details/List)
            Key.Up    => cur - cols,
            Key.Right => cur + 1,
            Key.Left  => cur - 1,
            _         => cur,
        };
        MoveTo(next, shift);
    }

    /// <summary>Move the selection cursor to <paramref name="index"/> (clamped), extending the
    /// range when Shift is held, and scroll it into view.</summary>
    void MoveTo(int index, bool shift)
    {
        if (_viewModels.Count == 0) return;
        index = Math.Clamp(index, 0, _viewModels.Count - 1);
        if (shift) RangeTo(_viewModels[index]); else SelectOne(_viewModels[index]);
        _lastClickIdx = index;   // advance the cursor — the old code never did, so arrows stuck
        ScrollToIndex(index);
    }

    /// <summary>Columns per row for the current view (1 for the vertical Details/List views).</summary>
    int ColsPerRow()
    {
        if (ViewMode is ViewMode.Details or ViewMode.List) return 1;
        double itemW = ViewMode switch
        {
            ViewMode.LargeIcons => 80,
            ViewMode.SmallIcons => 180,
            ViewMode.Thumbnails => 120,
            _ => 80,
        };
        return Math.Max(1, (int)(ItemsScroller.Bounds.Width / itemW));
    }

    int PageStep()
    {
        double itemH = ViewMode switch { ViewMode.LargeIcons => 60, ViewMode.Thumbnails => 120, _ => 20 };
        int rows = Math.Max(1, (int)(ItemsScroller.Bounds.Height / itemH));
        return Math.Max(1, rows * ColsPerRow());
    }

    void ScrollToIndex(int index)
    {
        int cols = ColsPerRow();
        double rowH = ViewMode switch { ViewMode.LargeIcons => 60, ViewMode.Thumbnails => 120, _ => 20 };
        double y = index / cols * rowH;
        double vpH = ItemsScroller.Viewport.Height;
        var off = ItemsScroller.Offset;
        if (y < off.Y) ItemsScroller.Offset = new Vector(off.X, y);
        else if (y + rowH > off.Y + vpH) ItemsScroller.Offset = new Vector(off.X, y + rowH - vpH);
    }

    // ── Type-ahead ───────────────────────────────────────────────────

    void TypeAhead(Key key)
    {
        if (_typeTimer.ElapsedMilliseconds > TypeResetMs) _typeBuffer = "";
        _typeTimer.Restart();
        _typeBuffer += key switch
        {
            Key.A => 'a', Key.B => 'b', Key.C => 'c', Key.D => 'd', Key.E => 'e', Key.F => 'f', Key.G => 'g', Key.H => 'h',
            Key.I => 'i', Key.J => 'j', Key.K => 'k', Key.L => 'l', Key.M => 'm', Key.N => 'n', Key.O => 'o', Key.P => 'p',
            Key.Q => 'q', Key.R => 'r', Key.S => 's', Key.T => 't', Key.U => 'u', Key.V => 'v', Key.W => 'w', Key.X => 'x',
            Key.Y => 'y', Key.Z => 'z',
            Key.D0 or Key.NumPad0 => '0', Key.D1 or Key.NumPad1 => '1', Key.D2 or Key.NumPad2 => '2',
            Key.D3 or Key.NumPad3 => '3', Key.D4 or Key.NumPad4 => '4', Key.D5 or Key.NumPad5 => '5',
            Key.D6 or Key.NumPad6 => '6', Key.D7 or Key.NumPad7 => '7', Key.D8 or Key.NumPad8 => '8',
            Key.D9 or Key.NumPad9 => '9', _ => (char)0,
        };
        if (_typeBuffer.Length > 0 && _typeBuffer[^1] == 0) return;
        int start = (_lastClickIdx + 1) % Math.Max(1, _viewModels.Count);
        for (int n = _viewModels.Count; n > 0; n--)
        {
            int i = (start + n) % _viewModels.Count;
            if (_viewModels[i].DisplayName.StartsWith(_typeBuffer, StringComparison.OrdinalIgnoreCase))
            { SelectOne(_viewModels[i]); return; }
        }
    }

    // ── Rename ────────────────────────────────────────────────────────

    /// <summary>Begin inline rename of the single selected item (F2, or the Rename menu item).</summary>
    public void BeginRenameSelected() => BeginRename();

    void BeginRename()
    {
        if (SelectedItem is null || _selected.Count != 1) return;
        var vm = SelectedItem;
        Control? host = null;
        foreach (var c in ItemsPresenter.GetRealizedContainers())
            if (c is Control ctl && ctl.DataContext == vm) { host = ctl; break; }
        if (host is null) return;
        var tb = host.FindDescendantOfType<TextBlock>();
        if (tb is null) return;
        vm.IsEditing = true; vm.EditName = vm.DisplayName;
        _renameBox = new TextBox { Text = vm.DisplayName, MinWidth = Math.Max(tb.Bounds.Width, 60), FontSize = tb.FontSize };
        var layer = AdornerLayer.GetAdornerLayer(host);
        if (layer is null) return;
        layer.Children.Add(_renameBox);
        _renameBox.Focus(); _renameBox.SelectAll();
        _renameBox.KeyDown += OnRenameKey; _renameBox.LostFocus += OnRenameLost;
    }

    void FinishRename(bool commit)
    {
        if (_renameBox is null || SelectedItem is null) return;
        var vm = SelectedItem;
        var box = _renameBox; _renameBox = null;
        box.KeyDown -= OnRenameKey; box.LostFocus -= OnRenameLost;

        var newName = box.Text?.Trim();
        vm.IsEditing = false;
        AdornerLayer.GetAdornerLayer(this)?.Children.Remove(box);

        // Commit through the controller (which drives FileOperationService); the optimistic
        // EditName is only a visual echo until the directory reloads with the real name.
        if (commit && !string.IsNullOrWhiteSpace(newName)
            && !string.Equals(newName, vm.DisplayName, StringComparison.Ordinal))
        {
            vm.EditName = newName;
            RenameCommitted?.Invoke(this, new RenameCommittedEventArgs(vm.Path, newName));
        }
    }

    void OnRenameKey(object? s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) FinishRename(true);
        else if (e.Key == Key.Escape) FinishRename(false);
    }

    void OnRenameLost(object? s, RoutedEventArgs e) => FinishRename(true);
}

public sealed class ItemActivatedEventArgs(ItemViewModel item) : EventArgs
{
    public ItemViewModel Item { get; } = item;
}

public sealed class DropEventArgs : EventArgs
{
    public IReadOnlyList<VfsPath> Paths { get; }
    public bool IsCopy { get; }
    public bool IsLink { get; }
    /// <summary>The folder the payload was dropped onto, or null for empty space (= current dir).</summary>
    public VfsPath? TargetFolder { get; }
    public DropEventArgs(IReadOnlyList<VfsPath> paths, bool ctrl, bool shift, VfsPath? target = null)
    {
        Paths = paths; IsCopy = ctrl && !shift; IsLink = ctrl && shift; TargetFolder = target;
    }
}

public sealed class RenameCommittedEventArgs(VfsPath path, string newName) : EventArgs
{
    public VfsPath Path { get; } = path;
    public string NewName { get; } = newName;
}

public sealed class FileContextRequestedEventArgs(ItemViewModel? item, Point position) : EventArgs
{
    /// <summary>The item under the pointer, or null for a folder-background request.</summary>
    public ItemViewModel? Item { get; } = item;
    public Point Position { get; } = position;
}