using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Core.Vfs;
using Bevel.FileManager.FileOperations;
using Bevel.UI;

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

    /// <summary>Select every listed path (controller/agent-driven, e.g. reveal). Paths not present
    /// are skipped; the last one present becomes the focus/anchor.</summary>
    public void SelectPaths(IEnumerable<VfsPath> paths)
    {
        ClearSel();
        ItemViewModel? last = null;
        foreach (var path in paths)
        {
            var vm = _viewModels.FirstOrDefault(v => v.Path == path);
            if (vm is not null) { AddSel(vm); last = vm; }
        }
        SelectedItem = last;
        _anchor = last;
        RaiseSelection();
    }

    /// <summary>Edit → Select All (also Ctrl+A). Clears first so the order list can't gather dupes.</summary>
    public void SelectAll()
    {
        ClearSel();
        foreach (var vm in _viewModels) AddSel(vm);
        SelectedItem = _selectedOrder.Count > 0 ? _selectedOrder[^1] : null;
        RaiseSelection();
    }

    /// <summary>Edit → Invert Selection: select everything currently unselected and vice-versa.</summary>
    public void InvertSelection()
    {
        foreach (var vm in _viewModels.ToList())
        {
            if (vm.IsSelected) RemSel(vm); else AddSel(vm);
        }
        SelectedItem = _selectedOrder.Count > 0 ? _selectedOrder[^1] : null;
        RaiseSelection();
    }

    private readonly ObservableCollection<ItemViewModel> _viewModels = new();
    // List-view column width follows the names (bevel-3fgd); fed every DisplayName that enters the list.
    private readonly NameColumnSizer _nameSizer = new();
    private readonly HashSet<ItemViewModel> _selected = new();
    private readonly List<ItemViewModel> _selectedOrder = new();
    private ItemViewModel? _anchor;
    private int _lastClickIdx = -1;

    private readonly Stopwatch _typeClock = Stopwatch.StartNew();
    private const int TypeResetMs = 700;
    private readonly Bevel.Core.Input.TypeToFind _typeToFind;

    private Point _marqueeOrigin;
    private bool _marqueeDragging;

    private Point _dragStart;
    private bool _dragArmed;
    /// <summary>The selection's IStorageItems, resolved from the PRESS so the OS file list is ready
    /// before the drag threshold is crossed (bevel-u2x6).</summary>
    private System.Threading.Tasks.Task<IStorageItem[]>? _dragStorageTask;
    /// <summary>The selection being dragged out of THIS view, for the life of the drag session; null
    /// otherwise. Each Filer window is its own process, so this identifies a within-window drag only.</summary>
    private List<VfsPath>? _ownDragPaths;

    private TextBox? _renameBox;
    private readonly FuncDataTemplate<ItemViewModel> _detailsTpl;

    private SortColumn _sortCol = SortColumn.Name;
    private bool _sortAsc = true;

    public event EventHandler<ItemActivatedEventArgs>? ItemActivated;

    /// <summary>Space on a selection asks for a lightweight preview (macOS Quick Look), distinct from
    /// activation (Enter / double-click), which opens the file or navigates the folder.</summary>
    public event EventHandler<ItemActivatedEventArgs>? PreviewRequested;
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
        TextInput += OnTextInput;   // type-ahead reads real characters (digits/accents), not Key decoding
        // Focus-visible cue: a low-vision keyboard user needs to see WHERE focus is even before they
        // move the selection. Show a subtle dotted rectangle whenever the list holds focus with nothing
        // selected; it disappears the moment a selection exists (the navy highlight then shows focus).
        ItemsScroller.ScrollChanged += OnItemsScrollChanged;
        GotFocus += (_, _) => UpdateFocusCue();
        LostFocus += (_, _) => UpdateFocusCue();
        _typeToFind = new(() => _typeClock.ElapsedMilliseconds, TypeResetMs);
        // Without AllowDrop the OS never routes drag events here — DragOver/Drop handlers below
        // would be dead code and every drop would silently do nothing (bevel-u2x6).
        DragDrop.SetAllowDrop(this, true);
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
        else if (change.Property == IsKeyboardFocusWithinProperty)
            UpdateFullNameReveal();
    }

    // ── View modes ────────────────────────────────────────────────────

    private void ApplyViewMode(ViewMode mode)
    {
        ColumnHeaders.IsVisible = mode == ViewMode.Details;
        RefreshSortIndicators();

        switch (mode)
        {
            case ViewMode.Details:
                // Vertical list at the pane's width. No horizontal scroll: the column header sits outside
                // the scroller and never scrolls, so a scrollable row could only drift out of line with
                // it, and an unconstrained width let a long name widen every row instead of trimming in
                // the star (Name) column (bevel-3fgd).
                SetScroll(horizontal: ScrollBarVisibility.Disabled, vertical: ScrollBarVisibility.Auto);
                ItemsPresenter.ItemsPanel = new FuncTemplate<Panel>(() =>
                    new VirtualizingStackPanel { Orientation = Orientation.Vertical });
                ItemsPresenter.ItemTemplate = _detailsTpl;
                break;
            case ViewMode.LargeIcons:
                GridScroll();
                ItemsPresenter.ItemsPanel = Wrap(CellSize(mode), Orientation.Horizontal);
                ItemsPresenter.ItemTemplate = LargeIconTpl;
                break;
            case ViewMode.SmallIcons:
                GridScroll();
                ItemsPresenter.ItemsPanel = Wrap(CellSize(mode), Orientation.Horizontal);
                ItemsPresenter.ItemTemplate = SmallIconTpl;
                break;
            case ViewMode.List:
                // Vertical flow: items fill a column top-to-bottom, then wrap to the next column.
                // Height is constrained (no vertical scroll) so it wraps into columns and the
                // overflow scrolls horizontally — the classic Filer "List" view.
                SetScroll(horizontal: ScrollBarVisibility.Auto, vertical: ScrollBarVisibility.Disabled);
                // Column width comes from the names, not the table (CellSize gives the row height).
                ItemsPresenter.ItemsPanel = Wrap(new Size(ListColumnWidth(), CellSize(mode).Height), Orientation.Vertical);
                ItemsPresenter.ItemTemplate = ListTpl;
                break;
            case ViewMode.Thumbnails:
                GridScroll();
                ItemsPresenter.ItemsPanel = Wrap(CellSize(mode), Orientation.Horizontal);
                ItemsPresenter.ItemTemplate = ThumbTpl;
                break;
        }
        _pinnedToReadingStart = true;
        UpdateFullNameReveal();
    }

    /// <summary>RTL List view (bevel-2ncr): the panel mirrors columns inside its extent, so the reading
    /// start (index 0) is at the far RIGHT, but a ScrollViewer opens at offset 0, the far left. Keep the
    /// reading start in view while the extent grows (folder open, streaming batches) until the user
    /// scrolls away from it: only an offset that was at the old maximum (or a list that fitted) is
    /// re-pinned, so a position the user chose is left alone. Nothing to do under LTR.</summary>
    void OnItemsScrollChanged(object? _, ScrollChangedEventArgs e)
    {
        if (ViewMode != ViewMode.List || FlowDirection != Avalonia.Media.FlowDirection.RightToLeft) return;
        var sv = ItemsScroller;
        double max = Math.Max(0, sv.Extent.Width - sv.Viewport.Width);
        if (e.ExtentDelta.X != 0 || e.ViewportDelta.X != 0)
        {
            // The list grew or the pane was resized: keep the reading start in view if we were there.
            if (_pinnedToReadingStart && sv.Offset.X != max) sv.Offset = new Vector(max, sv.Offset.Y);
        }
        else if (e.OffsetDelta.X != 0)
        {
            // Only the offset moved: the user scrolled (or a key scrolled the selection into view).
            _pinnedToReadingStart = sv.Offset.X >= max - 1;
        }
    }

    /// <summary>RTL List only: the view is showing its reading start (the far right), so growth and
    /// resizes keep it there. Set on a reset or view switch; cleared when the user scrolls away
    /// (review #2: the old handler ignored viewport-only resizes).</summary>
    bool _pinnedToReadingStart = true;

    // Horizontal-flow icon grids wrap to the viewport WIDTH, so horizontal scroll must be off
    // (otherwise the panel is measured at infinite width and never wraps); overflow scrolls down.
    private void GridScroll() => SetScroll(horizontal: ScrollBarVisibility.Disabled, vertical: ScrollBarVisibility.Auto);

    private void SetScroll(ScrollBarVisibility horizontal, ScrollBarVisibility vertical)
    {
        ItemsScroller.HorizontalScrollBarVisibility = horizontal;
        ItemsScroller.VerticalScrollBarVisibility = vertical;
    }

    private static FuncTemplate<Panel> Wrap(Size cell, Orientation flow)
        => new(() => new VirtualizingWrapPanel { ItemWidth = cell.Width, ItemHeight = cell.Height, Orientation = flow });

    /// <summary>The List column width the current names need (100 to 360 px).</summary>
    double ListColumnWidth() => _nameSizer.Width(MeasureName);

    /// <summary>The drawn width of a name in the item label's font (FontSize 11, see NameCell).</summary>
    double MeasureName(string name)
    {
        using var layout = new Avalonia.Media.TextFormatting.TextLayout(
            name, new Typeface(FontFamily, FontStyle, FontWeight), 11, null);
        return layout.WidthIncludingTrailingWhitespace;
    }

    /// <summary>After names entered the list: in List view, widen the columns to fit them. Between
    /// resets the width only grows, so columns do not jitter while a folder streams in;
    /// <paramref name="reset"/> starts again from the current names.</summary>
    void FitListColumns(bool reset)
    {
        if (ViewMode != ViewMode.List || ItemsPresenter.ItemsPanelRoot is not VirtualizingWrapPanel p) return;
        double w = ListColumnWidth();
        p.ItemWidth = reset ? w : Math.Max(p.ItemWidth, w);
    }

    /// <summary>The cell of each view mode: the ONLY place cell sizes are written (List's column width
    /// is the exception: it follows the names, see <see cref="ListColumnWidth"/>). The wrap panels are
    /// built from it and the Details row height comes from it; navigation and scroll-into-view read
    /// the panel's <see cref="WrapLayout"/>, never these numbers directly. Details is one row per item
    /// across the pane, so its width is NaN.</summary>
    internal static Size CellSize(ViewMode mode) => mode switch
    {
        ViewMode.LargeIcons => new Size(80, 72),     // 4 margin + 32 icon + 2 spacing + 30 (two text lines) + 4 margin
        ViewMode.SmallIcons => new Size(180, 20),
        ViewMode.List => new Size(180, 18),
        ViewMode.Thumbnails => new Size(120, 142),   // 2 margin + 106 frame (1 + 4 + 96 + 4 + 1) + 2 + 30 caption + 2 margin
        _ => new Size(double.NaN, 22),   // Details
    };

    // ── Templates ─────────────────────────────────────────────────────


    static TextBlock Label(string text, int maxW = 0) => new()
    {
        Text = text, FontSize = 11,
        MaxWidth = maxW > 0 ? maxW : double.MaxValue,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    // Win2000 selection: the filename label gets a Highlight-coloured bar with HighlightText text; the
    // icon is left un-highlighted, exactly like classic Filer. The colours are NOT brush objects resolved
    // here: the name cell carries the "namecell" class (plus "selected", bound to IsSelected) and the
    // styles in ItemView.axaml paint it with DynamicResource tokens, so a live theme or colour-scheme
    // switch recolours every realized row in place (bevel-5rel). No per-row event subscriptions.
    static readonly Avalonia.Data.Converters.FuncValueConverter<bool, int> RevealLinesConv =
        new(full => full ? 0 : 2);

    /// <summary>The filename label wrapped in a selection-highlight border (navy bar + white text
    /// when selected, plus a dotted focus outline). Used by every view mode's template.</summary>
    static Control NameCell(string text, TextWrapping wrap = TextWrapping.NoWrap, double maxW = 0,
        TextAlignment align = TextAlignment.Left)
    {
        var label = new NameLabel
        {
            Text = text, FontSize = 11,
            TextWrapping = wrap, TextAlignment = align,
            MaxWidth = maxW > 0 ? maxW : double.MaxValue,
            // Every cut-off name ends in an ellipsis. Wrapped names (icon views) stop at two lines of a
            // fixed 15 px, so the cell height in CellSize does not depend on the font's own metrics
            // (bevel-m9jy); NameLabel adds the ellipsis Avalonia skips when the cut falls between words.
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (wrap != TextWrapping.NoWrap)
        {
            label.LineHeight = 15;
            // Two lines, or the whole name while this is the revealed item (MaxLines 0 = no limit).
            label.Bind(TextBlock.MaxLinesProperty,
                new Avalonia.Data.Binding(nameof(ItemViewModel.ShowFullName)) { Converter = RevealLinesConv });
        }

        var highlight = new Border { Child = label, Padding = new(2, 0) };
        highlight.Classes.Add("namecell");
        highlight.BindClass("selected", new Avalonia.Data.Binding(nameof(ItemViewModel.IsSelected)), null!);
        return highlight;
    }

    /// <summary>Icon docked left, name filling the rest of the row (bevel-3fgd). A horizontal StackPanel
    /// measured the name at unlimited width, so it never trimmed and drew over the next column; the
    /// DockPanel gives it the cell's remaining width to trim to. The highlight hugs the text (Left), so
    /// the selection bar is only as wide as the visible name.</summary>
    static Control IconAndName(Control icon, Control nameCell, Thickness margin = default)
    {
        icon.Margin = new Thickness(0, 0, 4, 0);
        DockPanel.SetDock(icon, Dock.Left);
        nameCell.HorizontalAlignment = HorizontalAlignment.Left;
        var row = new DockPanel { LastChildFill = true, Margin = margin };
        row.Children.Add(icon);
        row.Children.Add(nameCell);
        return row;
    }

    /// <summary>Wrap a row template's visual root in an <see cref="ItemRow"/> so the row exposes a
    /// ListItem automation role, an accessible name (bound to DisplayName), and a SelectionItem pattern
    /// (IsSelected) to screen readers. Decorator is layout-transparent, so the template's visuals,
    /// marquee/Hit rect-testing, and rename are unaffected (bevel-6zs6).</summary>
    static Control Row(Control content)
    {
        var row = new ItemRow { Child = content };
        // DataContext is the ItemViewModel (inherited from the container), so this tracks live
        // DisplayName changes (rename / hide-extensions) without rebuilding the row.
        row.Bind(AutomationProperties.NameProperty, new Avalonia.Data.Binding(nameof(ItemViewModel.DisplayName)));
        return row;
    }

    static readonly FuncDataTemplate<ItemViewModel> LargeIconTpl = new((vm, _) =>
    {
        if (vm is null) return new TextBlock { Text = "" };
        var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = 2, Margin = new(4) };
        s.Children.Add(GlyphIcon.For(vm.IconKey, 32));
        s.Children.Add(NameCell(vm.DisplayName, TextWrapping.Wrap, 72, TextAlignment.Center));
        return Row(s);
    });

    static readonly FuncDataTemplate<ItemViewModel> SmallIconTpl = new((vm, _) =>
    {
        if (vm is null) return new TextBlock { Text = "" };
        return Row(IconAndName(GlyphIcon.For(vm.IconKey, 16), NameCell(vm.DisplayName), new Thickness(2, 0)));
    });

    static readonly FuncDataTemplate<ItemViewModel> ListTpl = new((vm, _) =>
    {
        if (vm is null) return new TextBlock { Text = "" };
        return Row(IconAndName(GlyphIcon.For(vm.IconKey, 16), NameCell(vm.DisplayName), new Thickness(0, 1)));
    });

    // Instance (not static) so each row's Size/Type/Date columns bind to the shared column
    // widths — a header resize then flows to every row live. Name column stays star-sized.
    private FuncDataTemplate<ItemViewModel> BuildDetailsTemplate() => new((vm, _) =>
    {
        if (vm is null) return new TextBlock { Text = "" };
        // Keep rows on the active window surface; classic details view does not add horizontal gridlines.
        var row = new Border { Padding = new(2, 1) };   // 1 + 1 vertical padding around the grid
        row[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(Bevel.UI.ThemeTokens.BrushWindow);
        var g = new Grid { Height = CellSize(ViewMode.Details).Height - 2 };
        g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        g.ColumnDefinitions.Add(BoundCol(SizeColWidthProperty));
        g.ColumnDefinitions.Add(BoundCol(TypeColWidthProperty));
        g.ColumnDefinitions.Add(BoundCol(DateColWidthProperty));
        g.Children.Add(IconAndName(GlyphIcon.For(vm.IconKey, 16), NameCell(vm.DisplayName)));
        g.Children.Add(new TextBlock { [Grid.ColumnProperty] = 1, Text = vm.SizeDisplay, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch, TextAlignment = TextAlignment.Right, FontSize = 11, Margin = new(4, 0) });
        g.Children.Add(new TextBlock { [Grid.ColumnProperty] = 2, Text = vm.TypeDescription, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Margin = new(4, 0) });
        g.Children.Add(new TextBlock { [Grid.ColumnProperty] = 3, Text = vm.ModifiedDisplay, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Margin = new(4, 0) });
        row.Child = g;
        return Row(row);
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
        // The frame holds only the picture; the caption sits under it, outside the box (Explorer's
        // thumbnail view). The line is the light ButtonFace grey: WindowFrame (black) and ButtonShadow
        // (#808080 in the classic scheme) both drew every tile as a hard box on the white window.
        var frame = new Border { BorderThickness = new(1), Padding = new(4), Child = GlyphIcon.For(vm.IconKey, 96) };
        frame[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(Bevel.UI.ThemeTokens.BrushWindow);
        frame[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(Bevel.UI.ThemeTokens.BrushButtonFace);
        var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = 2, Margin = new(2) };
        s.Children.Add(frame);
        s.Children.Add(NameCell(vm.DisplayName, TextWrapping.Wrap, 106, TextAlignment.Center));
        return Row(s);
    });

    // ── Rebuild ───────────────────────────────────────────────────────

    /// <summary>Clear all items and reset view. Call before streaming new items.</summary>
    public void ResetItems()
    {
        _pinnedToReadingStart = true;
        _nameSizer.Reset();
        FitListColumns(reset: true);
        _viewModels.Clear();
        _selected.Clear(); _selectedOrder.Clear(); _marqueeHits.Clear(); _anchor = null; _lastClickIdx = -1;
        ItemsPresenter.ItemsSource = _viewModels;
    }

    /// <summary>Append streamed items, each inserted at its sorted position. Keeps the bound collection
    /// continuously sorted with one incremental Insert per item, so streaming enumeration never
    /// detaches/rebuilds the whole ItemsSource — the old per-batch SortItems() made opening a folder
    /// O(n²) (a full teardown + re-add of the growing list on every 32-item chunk). A deliberate
    /// re-sort (header click) still goes through SortAsync/ApplyOrder.</summary>
    public void AddItems(IReadOnlyList<IVfsNode> nodes)
    {
        foreach (var n in nodes)
        {
            var vm = new ItemViewModel(n);
            _nameSizer.Add(vm.DisplayName);
            InsertSorted(vm);
        }
        FitListColumns(reset: false);
    }

    /// <summary>Binary-search the current sorted position for <paramref name="vm"/> and Insert it there
    /// (equal keys land after existing ones). One collection event, no teardown.</summary>
    void InsertSorted(ItemViewModel vm)
    {
        int lo = 0, hi = _viewModels.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (CompareItems(_viewModels[mid], vm, _sortCol, _sortAsc) <= 0) lo = mid + 1;
            else hi = mid;
        }
        _viewModels.Insert(lo, vm);
    }

    public bool HasItems => _viewModels.Count > 0;

    /// <summary>
    /// Differentially update the list to match <paramref name="nodes"/>, keyed by path — a React-
    /// style keyed diff. Surviving rows keep their object identity (and selection); only genuinely
    /// added or removed entries mutate the collection, and metadata is refreshed in place. When the
    /// path set is unchanged (the common watcher/refresh case) nothing rebinds, so the view does
    /// not flicker. New rows are inserted in sorted position, so no full re-sort/teardown is needed.
    /// </summary>
    public void ReconcileItems(IReadOnlyList<IVfsNode> nodes)
    {
        var existing = new Dictionary<VfsPath, ItemViewModel>(_viewModels.Count);
        foreach (var vm in _viewModels) existing[vm.Path] = vm;

        var incoming = new HashSet<VfsPath>(nodes.Count);
        foreach (var n in nodes)
        {
            incoming.Add(n.Path);
            if (existing.TryGetValue(n.Path, out var vm))
                vm.Update(n);                       // same row, fresh metadata (a rename lands here)
            else
                InsertSorted(vm = new ItemViewModel(n)); // new row placed in order, no teardown
            _nameSizer.Add(vm.DisplayName);
        }
        FitListColumns(reset: false);

        // Drop rows whose paths disappeared (in place — a Remove doesn't tear down the list).
        for (var i = _viewModels.Count - 1; i >= 0; i--)
        {
            var vm = _viewModels[i];
            if (incoming.Contains(vm.Path)) continue;
            _viewModels.RemoveAt(i);
            _selected.Remove(vm);
            _selectedOrder.Remove(vm);
        }
        // Inserts and removals both preserve sorted order, so no full re-sort is needed.
    }

    void RebuildItems()
    {
        _viewModels.Clear();
        _selected.Clear(); _selectedOrder.Clear(); _marqueeHits.Clear(); _anchor = null; _lastClickIdx = -1;
        _nameSizer.Reset();
        _pinnedToReadingStart = true;
        if (Items != null)
            foreach (var n in Items)
            {
                var vm = new ItemViewModel(n);
                _nameSizer.Add(vm.DisplayName);
                _viewModels.Add(vm);
            }
        FitListColumns(reset: true);
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

    /// <summary>Sort by a column key ("name"/"type"/"size"/"date") from the Arrange Icons context menu;
    /// toggles direction if already sorted by that column, exactly like clicking the Details header.
    /// Works in every view mode (icons/list/details), not just Details.</summary>
    public void SortBy(string key)
    {
        var col = key switch
        {
            "type" => SortColumn.Type,
            "size" => SortColumn.Size,
            "date" => SortColumn.Modified,
            _ => SortColumn.Name,
        };
        _sortAsc = _sortCol == col ? !_sortAsc : true;
        _sortCol = col;
        _ = SortAsync();
        RefreshSortIndicators();
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
        var list = items.ToList();
        list.Sort((a, b) => CompareItems(a, b, col, asc));
        return list;
    }

    /// <summary>The total order used by both the full sort and the streaming sorted-insert. Folders
    /// always precede files (Filer/Finder behaviour, independent of column and direction); within a
    /// group the chosen column decides, with a name tiebreak so the order stays deterministic
    /// (List.Sort/binary-insert aren't otherwise stable).</summary>
    internal static int CompareItems(ItemViewModel a, ItemViewModel b, SortColumn col, bool asc)
    {
        if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;   // folders first, always
        int c = col switch
        {
            SortColumn.Size     => Nullable.Compare(a.Size, b.Size),
            SortColumn.Type     => string.Compare(a.TypeDescription, b.TypeDescription, StringComparison.OrdinalIgnoreCase),
            SortColumn.Modified => Nullable.Compare(a.Modified, b.Modified),
            _                   => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase),
        };
        if (c == 0 && col != SortColumn.Name)
            c = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        return asc ? c : -c;
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

    ItemViewModel? Hit(Point pt, bool revealedFirst = true)
    {
        // pt is in ItemsPresenter coordinates (all callers use GetPosition(ItemsPresenter)).
        // Match it against realized container RECTS rather than visual hit-testing: templates
        // have transparent/empty gaps (between a row's icon and its text, or a details row's
        // empty cells) that GetVisualAt falls through, which made most clicks miss. Same
        // coordinate transform the marquee uses.
        // No _viewModels.Contains(vm) guard: a realized container's DataContext is always a current
        // ItemViewModel once ItemsSource is set, and the Contains was an O(n) scan INSIDE this O(n)
        // container loop — O(n²) per click in a large folder (ce-review: performance).
        // A revealed name draws over the cells below it, so a click on that text belongs to its item,
        // not to the one underneath (bevel-m9jy).
        if (revealedFirst && _revealed is not null && ItemsPresenter.ContainerFromItem(_revealed) is { } rc
            && rc.FindDescendantOfType<NameLabel>()?.Parent is Control nameCell
            && nameCell.TranslatePoint(default, ItemsPresenter) is { } np
            && new Rect(np, nameCell.Bounds.Size).Contains(pt))
            return _revealed;

        foreach (var c in ItemsPresenter.GetRealizedContainers())
        {
            if (c is not Control ctl || ctl.DataContext is not ItemViewModel vm)
                continue;
            var pos = ctl.TranslatePoint(default, ItemsPresenter) ?? default;
            if (new Rect(pos, ctl.Bounds.Size).Contains(pt)) return vm;
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

    /// <summary>Rows this marquee gesture has selected, so releasing the band over them again can
    /// deselect them WITHOUT disturbing a selection that existed before the gesture started (a
    /// ctrl/shift marquee adds to what was already there — the press handler only clears for a plain
    /// one). An already-selected row is never entered here, so sweeping across it leaves it alone.</summary>
    private readonly HashSet<ItemViewModel> _marqueeHits = new();

    /// <summary>
    /// Applies the band to the realized rows, flipping only the ones whose membership CHANGED
    /// (bevel-oovi).
    ///
    /// <para>This used to run once, on release, so a sweep showed no highlight at all until you let go
    /// — you could not see what you were about to select. Windows 2000 and Finder both highlight live,
    /// and "feel" is half the fidelity bar here.</para>
    ///
    /// <para>Diffing rather than clear-and-re-add matters for more than speed: ClearSel + AddSel on
    /// every pointer move would rewrite <c>IsSelected</c> on every row in view many times a second,
    /// and each write is an observable property change driving template state. It also deselected a
    /// pre-existing selection, which is why an additive marquee now behaves.</para>
    ///
    /// <para><b>Not throttled, deliberately.</b> One pass measured 0.15 ms — and that figure is the
    /// whole pointer-move dispatch, so the pass itself is less — against a 16.7 ms frame, because it
    /// walks the REALIZED containers only: 22 rows in a 2,000-entry folder. A per-frame throttle would
    /// add a timer and a coalescing rule to save nothing. <c>MarqueeSelectionTests</c> pins the budget
    /// so an implementation that became O(folder) fails instead of merely feeling bad.</para>
    /// </summary>
    void ApplyMarquee(Rect r)
    {
        foreach (var c in ItemsPresenter.GetRealizedContainers())
        {
            if (c is not Control ctl || ctl.DataContext is not ItemViewModel vm) continue;
            var pos = ctl.TranslatePoint(default, ItemsPresenter) ?? default;
            var hit = r.Intersects(new(pos.X, pos.Y, ctl.Bounds.Width, ctl.Bounds.Height));
            if (hit)
            {
                if (!vm.IsSelected) { AddSel(vm); _marqueeHits.Add(vm); }
            }
            else if (_marqueeHits.Remove(vm))
            {
                RemSel(vm);
            }
        }
    }

    /// <summary>Final pass for the release: the band's last position, then the one SelectionChanged
    /// for the whole gesture. The live passes deliberately stay silent — the info pane re-reads the
    /// whole selection on that event, which is not something to do per mouse move.</summary>
    void Marquee(Rect r)
    {
        ApplyMarquee(r);
        if (_selected.Count > 0) SelectedItem = _selectedOrder[^1];
        RaiseSelection();
    }

    void AddSel(ItemViewModel vm) { vm.IsSelected = true; _selected.Add(vm); _selectedOrder.Add(vm); }
    void RemSel(ItemViewModel vm) { vm.IsSelected = false; _selected.Remove(vm); _selectedOrder.Remove(vm); }
    void ClearSel() { foreach (var s in _selected) s.IsSelected = false; _selected.Clear(); _selectedOrder.Clear(); _marqueeHits.Clear(); }

    /// <summary>Raised whenever the selection set changes (drives the info pane).</summary>
    public event Action? SelectionChanged;
    void RaiseSelection() { UpdateFocusCue(); UpdateFullNameReveal(); SelectionChanged?.Invoke(); }

    /// <summary>The item whose label currently shows its whole name (bevel-m9jy).</summary>
    ItemViewModel? _revealed;

    /// <summary>Explorer behaviour: in Large Icons and Thumbnails the ONE selected item of a focused view
    /// shows its whole name, drawn over the rows below (its container's ZIndex is bound to the flag in
    /// <see cref="ItemList"/>); every other name stops at two lines and an ellipsis.</summary>
    void UpdateFullNameReveal()
    {
        var target = IsKeyboardFocusWithin && _selected.Count == 1 && _selectedOrder.Count == 1
                     && ViewMode is ViewMode.LargeIcons or ViewMode.Thumbnails
            ? _selectedOrder[0]
            : null;
        if (ReferenceEquals(target, _revealed)) return;
        if (_revealed is not null) _revealed.ShowFullName = false;
        _revealed = target;
        if (target is not null) target.ShowFullName = true;
    }

    /// <summary>The dotted focus rectangle is visible only while the list is focused, holds items, and
    /// has an empty selection — otherwise the selection highlight itself communicates focus.</summary>
    void UpdateFocusCue()
    {
        if (FocusCue is null) return;
        FocusCue.IsVisible = IsFocused && _selected.Count == 0 && _viewModels.Count > 0;
    }

    // ── Pointer ──────────────────────────────────────────────────────

    void OnBgPointerPressed(object? _, PointerPressedEventArgs e)
    {
        // Grab keyboard focus — handling the press (below) suppresses Avalonia's automatic
        // focus-on-click, which would otherwise leave arrows/type-ahead/Enter dead after a click.
        Focus();
        bool leftButton = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
        var pt = e.GetPosition(ItemsPresenter);
        var vm = Hit(pt);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.ClickCount == 2 && vm is not null) { ItemActivated?.Invoke(this, new(vm)); e.Handled = true; return; }

        // A non-left press (right-click for the context menu) must NEVER arm a drag or start a marquee —
        // it only moves the selection onto the item under the pointer (Filer selects what you
        // right-click). Leaving _dragArmed set here is what made closing the menu act like a drag.
        if (!leftButton)
        {
            if (vm is not null && !vm.IsSelected && !ctrl && !shift) SelectOne(vm);
            _dragArmed = false;
            return;   // let ContextRequested open the menu; no drag/marquee state touched
        }

        if (vm is null)
        {
            if (!ctrl && !shift) { ClearSel(); RaiseSelection(); }
            _marqueeOrigin = pt; _marqueeDragging = true; MarqueeRect.IsVisible = false;
            _marqueeHits.Clear();   // this gesture owns nothing yet
            e.Handled = true; return;
        }
        if (shift) RangeTo(vm);
        else if (ctrl) Toggle(vm);
        else if (!vm.IsSelected) SelectOne(vm);
        _lastClickIdx = Idx(vm);
        _dragArmed = !ctrl;                 // a plain/shift press on an item can begin a drag
        _dragStart = e.GetPosition(this);
        // START resolving the selection's IStorageItems NOW, not at drag time (bevel-u2x6). DoDragDrop
        // has to run synchronously inside the move handler — awaiting a resolve first loses the OS drag
        // gesture — so the work has to be in flight before the pointer passes the threshold. Selection is
        // already final at this point: SelectOne/RangeTo/Toggle all ran above.
        _dragStorageTask = ResolveStorageItemsAsync(TopLevel.GetTopLevel(this)?.StorageProvider,
            _selectedOrder.Select(v => (v.Path.Value, v.IsFolder)).ToList());
        e.Handled = true;
    }

    void OnBgPointerMoved(object? _, PointerEventArgs e)
    {
        // A press on a selected item becomes a drag only on a DELIBERATE gesture: the button is
        // still held and the pointer has moved well past a click's jitter. Firing on a few px
        // would hijack ordinary clicks into a modal DoDragDrop session (which can strand pointer
        // capture and break subsequent mouse input) and drop items into whatever's under release.
        if (_dragArmed && !_marqueeDragging)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _dragArmed = false; return; }
            var d = e.GetPosition(this);
            if (Math.Abs(d.X - _dragStart.X) > 10 || Math.Abs(d.Y - _dragStart.Y) > 10)
            {
                _dragArmed = false;
                _ = StartDragAsync(e);
            }
            return;
        }
        if (!_marqueeDragging) return;
        var pt = e.GetPosition(ItemsPresenter);
        var r = new Rect(Math.Min(_marqueeOrigin.X, pt.X), Math.Min(_marqueeOrigin.Y, pt.Y), Math.Abs(pt.X - _marqueeOrigin.X), Math.Abs(pt.Y - _marqueeOrigin.Y));
        if (r.Width > 4 || r.Height > 4)
        {
            Canvas.SetLeft(MarqueeRect, r.X); Canvas.SetTop(MarqueeRect, r.Y);
            MarqueeRect.Width = r.Width; MarqueeRect.Height = r.Height; MarqueeRect.IsVisible = true;
            // Highlight as the band moves, not on release (bevel-oovi). Same 4px gate as the band
            // itself, so a click's jitter never selects anything.
            ApplyMarquee(r);
        }
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

    /// <summary>Resolves selected paths to <see cref="IStorageItem"/>s for the OS drag payload.
    ///
    /// <para>Folders need <c>TryGetFolderFromPathAsync</c> and files <c>TryGetFileFromPathAsync</c> —
    /// asking for a file at a directory path yields null, which is how a mixed selection would silently
    /// drag only half of itself. Nulls are dropped rather than failing the whole drag.</para></summary>
    internal static async System.Threading.Tasks.Task<IStorageItem[]> ResolveStorageItemsAsync(
        IStorageProvider? provider, IReadOnlyList<(string Path, bool IsFolder)> items)
    {
        if (provider is null || items.Count == 0) return System.Array.Empty<IStorageItem>();

        var resolved = new List<IStorageItem>(items.Count);
        foreach (var (path, isFolder) in items)
        {
            var uri = FileUri.FromPath(path);
            IStorageItem? item = isFolder
                ? await provider.TryGetFolderFromPathAsync(uri).ConfigureAwait(true)
                : await provider.TryGetFileFromPathAsync(uri).ConfigureAwait(true);
            if (item is not null) resolved.Add(item);
        }
        return resolved.ToArray();
    }

    /// <summary>The effects a drag out of the Filer offers. Copy only: offering Move or Link hangs the
    /// native drag session on macOS (bevel-u2x6). The consequence is handled on the drop side — see
    /// <see cref="DragOverEffect"/>.</summary>
    internal const DragDropEffects DragSourceEffects = DragDropEffects.Copy;

    /// <summary>The OS payload for a drag of <paramref name="items"/>: one <see cref="DataFormat.File"/>
    /// item each (a <c>public.file-url</c> pasteboard item on macOS) and nothing else. Any further item —
    /// text, an application format — hangs the native drag session on macOS (bevel-u2x6).</summary>
    internal static DataTransfer BuildDragTransfer(IReadOnlyList<IStorageItem> items)
    {
        var transfer = new DataTransfer();
        foreach (var item in items)
        {
            var fileItem = new DataTransferItem();
            fileItem.SetFile(item);
            transfer.Add(fileItem);
        }
        return transfer;
    }

    /// <summary>Begin a drag of the current selection (bevel-u2x6).
    ///
    /// <para>The storage resolve is kicked off at pointer-press so it is ready when the drag threshold
    /// trips; awaiting it here would yield inside the pointer gesture and lose the drag. If it has not
    /// finished, failed, or resolved nothing (a Trash or Computer item has no file behind its path),
    /// no drag starts: a drag carrying no data is refused by every target and only looks broken.</para></summary>
    async System.Threading.Tasks.Task StartDragAsync(PointerEventArgs e)
    {
        var paths = _selectedOrder.Select(v => v.Path).ToList();
        if (paths.Count == 0) return;
        if (_dragStorageTask is not { IsCompletedSuccessfully: true, Result.Length: > 0 } ready) return;

        // While the session runs, a drop landing back on this view is our own drag: OnDrop then uses
        // these paths (which keep their VFS scheme) and treats a plain drop as a move.
        _ownDragPaths = paths;
        try
        {
            await DragDrop.DoDragDropAsync(e, BuildDragTransfer(ready.Result), DragSourceEffects);
        }
        catch (Exception ex)
        {
            // The platform drag session failed to start or tore down badly. Nothing to recover, but it
            // must not vanish: this is the only trace of a drag that "does nothing".
            System.Diagnostics.Trace.TraceWarning($"ItemView: drag session failed: {ex}");
        }
        finally { _ownDragPaths = null; }
    }

    // ── DnD ──────────────────────────────────────────────────────────

    /// <summary>What a drop does (bevel-u2x6 review).
    ///
    /// <list type="bullet">
    /// <item>Ctrl or Alt/Option held: copy. (Ctrl+Shift is the classic "create shortcut"; there is no
    /// link operation yet, so it copies — never moves.)</item>
    /// <item>A drag that started in this view: move, the classic within-window default.</item>
    /// <item>A drag from anywhere else (Finder, Mail, another Filer window): copy, unless Shift or Cmd
    /// asks for a move AND the source allows one. A plain drop must never remove a file from the
    /// app it came from.</item>
    /// </list></summary>
    internal static DropOperation DecideDropOperation(KeyModifiers modifiers, bool ownDrag, DragDropEffects allowed)
    {
        if (modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Alt))
            return DropOperation.Copy;
        if (ownDrag) return DropOperation.Move;
        bool wantsMove = modifiers.HasFlag(KeyModifiers.Shift) || modifiers.HasFlag(KeyModifiers.Meta);
        return wantsMove && allowed.HasFlag(DragDropEffects.Move) ? DropOperation.Move : DropOperation.Copy;
    }

    /// <summary>The effect to show for <paramref name="operation"/>, so the cursor badge matches what
    /// the drop will do. One known exception: our own drag can only offer Copy (see
    /// <see cref="DragSourceEffects"/>), and answering Move to a Copy-only source refuses the drop, so
    /// a within-window move shows the copy badge.</summary>
    internal static DragDropEffects DragOverEffect(bool hasFiles, DropOperation operation, DragDropEffects allowed)
    {
        if (!hasFiles) return DragDropEffects.None;
        return operation == DropOperation.Move && allowed.HasFlag(DragDropEffects.Move)
            ? DragDropEffects.Move
            : DragDropEffects.Copy;
    }

    void OnDragOver(object? _, DragEventArgs e)
    {
        bool own = _ownDragPaths is not null;
        var operation = DecideDropOperation(e.KeyModifiers, own, e.DragEffects);
        e.DragEffects = DragOverEffect(own || e.DataTransfer.Contains(DataFormat.File), operation, e.DragEffects);
        e.Handled = true;
    }

    /// <summary>The folder a drop at <paramref name="pt"/> (ItemsPresenter coordinates) lands in, or null
    /// for the current directory.</summary>
    /// <remarks>Drops ignore the revealed full name (review #3): it is an overlay of the selected item,
    /// which is often the thing being dragged, so the drop belongs to the cell underneath. A click on it
    /// still selects the revealed item (Hit's default).</remarks>
    internal ItemViewModel? DropTargetAt(Point pt)
        => Hit(pt, revealedFirst: false) is { IsFolder: true } over ? over : null;

    /// <summary>The paths in a drop from outside this view: the OS file list
    /// (<see cref="DataFormat.File"/>), or null when the payload carries no files. Text is never read
    /// as paths — dropping a selected sentence must not start a file operation.</summary>
    internal static List<VfsPath>? ResolveDropPaths(IDataTransfer? transfer)
    {
        var files = transfer?.TryGetFiles();
        if (files is null) return null;

        var paths = new List<VfsPath>(files.Length);
        foreach (var file in files)
            // Only real local files: a provider-backed item (content://, a relative URI) has no
            // path the file operations could act on.
            if (file.Path is { IsAbsoluteUri: true, IsFile: true } uri)
                paths.Add(new VfsPath("file", uri.LocalPath));
        return paths.Count > 0 ? paths : null;
    }

    void OnDrop(object? _, DragEventArgs e)
    {
        e.Handled = true;
        var own = _ownDragPaths;
        var paths = own ?? ResolveDropPaths(e.DataTransfer);
        if (paths is not { Count: > 0 }) return;
        var target = DropTargetAt(e.GetPosition(ItemsPresenter))?.Path;
        var operation = DecideDropOperation(e.KeyModifiers, own is not null, e.DragEffects);
        DropRequested?.Invoke(this, new(paths, operation, target));
    }

    // ── Context menu ─────────────────────────────────────────────────

    void OnContextRequested(object? _, ContextRequestedEventArgs e)
    {
        // Opening a context menu cancels any pending drag/marquee gesture, so dismissing the menu can't
        // resume one (a right-click used to leave _dragArmed set → the next move looked like a drag).
        _dragArmed = false;
        _marqueeDragging = false;
        MarqueeRect.IsVisible = false;

        // Locate the item under the pointer; a background (empty-space) request has none.
        ItemViewModel? vm = null;
        if (e.TryGetPosition(ItemsPresenter, out var pos))
            vm = Hit(pos);
        else
            pos = default;

        // Right-clicking an unselected item selects it first (Filer behaviour).
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
            case Key.Space:   // macOS Quick Look — preview, don't open
                if (_selectedOrder.Count > 0) { SelectedItem = _selectedOrder[^1]; PreviewRequested?.Invoke(this, new(SelectedItem)); }
                e.Handled = true; break;
            case Key.Enter: case Key.Down when e.KeyModifiers.HasFlag(KeyModifiers.Meta):
                if (_selectedOrder.Count > 0) { SelectedItem = _selectedOrder[^1]; ItemActivated?.Invoke(this, new(SelectedItem)); }
                e.Handled = true; break;
            case Key.A when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                SelectAll(); e.Handled = true; break;
            case Key.Down or Key.Up or Key.Left or Key.Right:
                Navigate(e.Key, e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true; break;
            case Key.Home:
                MoveTo(0, e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true; break;
            case Key.End:
                MoveTo(_viewModels.Count - 1, e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true; break;
            case Key.PageDown:
                MoveTo((_lastClickIdx < 0 ? 0 : _lastClickIdx) + CurrentLayout().PageSize(ItemsScroller.Viewport), e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true; break;
            case Key.PageUp:
                MoveTo((_lastClickIdx < 0 ? 0 : _lastClickIdx) - CurrentLayout().PageSize(ItemsScroller.Viewport), e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true; break;
            case Key.Escape:
                ClearSel(); RaiseSelection(); e.Handled = true; break;
            default:
                break;   // printable text (letters, digits, accents) is handled by OnTextInput, not Key decode
        }
    }

    /// <summary>Type-ahead: jump the selection to the item matching what the user types. Fed from real text
    /// input through the shared <see cref="Bevel.Core.Input.TypeToFind"/> (prefix accumulation, single-char
    /// cycling, forward wrap), so digits and accented characters work — the old Key-enum decode dropped both.
    /// Reuses <see cref="MoveTo"/> so the match scrolls into view and the cursor advances (enabling cycling).
    /// Text input only fires without Ctrl/Meta, so Ctrl+C/X/V never reach here.</summary>
    void OnTextInput(object? _, TextInputEventArgs e)
    {
        var text = e.Text;
        if (string.IsNullOrEmpty(text)) return;
        if (text.Length == 1 && char.IsControl(text[0])) return;   // ignore Esc/Enter/Tab and friends
        int idx = _typeToFind.Match(text, _viewModels, static vm => vm.DisplayName, _lastClickIdx);
        if (idx >= 0) { MoveTo(idx, shift: false); e.Handled = true; }
    }

    void Navigate(Key key, bool shift)
    {
        if (_viewModels.Count == 0) return;
        int cur = _lastClickIdx < 0 ? 0 : _lastClickIdx;
        // Arrow keys are VISUAL (bevel-0esz): WrapLayout.Move maps the arrow onto the view's flow, so in
        // List (items flow down, then wrap into columns) Right/Left cross columns and Up/Down move one
        // item, while in the icon views Up/Down cross rows. Under RTL the layout is mirrored (index 0
        // sits at the visual right), so Right moves toward reading-start, i.e. a LOWER index (bevel-3cd),
        // exactly like classic Filer on RTL Windows.
        bool rtl = FlowDirection == Avalonia.Media.FlowDirection.RightToLeft;
        var (dx, dy) = key switch
        {
            Key.Right => (1, 0),
            Key.Left  => (-1, 0),
            Key.Down  => (0, 1),
            Key.Up    => (0, -1),
            _         => (0, 0),
        };
        if (dx == 0 && dy == 0) return;
        MoveTo(CurrentLayout().Move(cur, dx, dy, rtl), shift);
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

    /// <summary>The geometry of what is on screen: the wrap panel's last layout, or one row per item for
    /// Details (a VirtualizingStackPanel).</summary>
    internal WrapLayout CurrentLayout()
        => ItemsPresenter.ItemsPanelRoot is VirtualizingWrapPanel p
            ? p.Layout
            : WrapLayout.SingleColumn(_viewModels.Count, ItemsScroller.Viewport.Width, CellSize(ViewMode.Details).Height);

    /// <summary>Scroll the minimum that reveals item <paramref name="index"/>, on the axis the view
    /// scrolls: down for the icon views and Details, sideways for List.</summary>
    void ScrollToIndex(int index)
    {
        var layout = CurrentLayout();
        if (layout.PerLine <= 0 || ItemsPresenter.ItemsPanelRoot is not { } panel) return;
        bool rtl = FlowDirection == Avalonia.Media.FlowDirection.RightToLeft;
        var r = layout.RectFor(index, panel.Bounds.Width, rtl);
        var vp = ItemsScroller.Viewport;
        var off = ItemsScroller.Offset;
        double x = off.X, y = off.Y;
        if (layout.Horizontal)
        {
            if (r.Y < off.Y) y = r.Y;
            else if (r.Bottom > off.Y + vp.Height) y = r.Bottom - vp.Height;
        }
        else
        {
            if (r.X < off.X) x = r.X;
            else if (r.Right > off.X + vp.Width) x = r.Right - vp.Width;
        }
        if (x != off.X || y != off.Y) ItemsScroller.Offset = new Vector(x, y);
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
        // Rename edits the REAL on-disk name, not the possibly extension-hidden DisplayName, so hiding
        // extensions never truncates a file on rename.
        vm.IsEditing = true; vm.EditName = vm.RealName;
        _renameBox = new TextBox { Text = vm.RealName, MinWidth = Math.Max(tb.Bounds.Width, 60), FontSize = tb.FontSize };
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
        // Compare against RealName — the box was seeded with RealName (line ~864), not the possibly
        // extension-hidden DisplayName. Comparing to DisplayName fired a spurious rename when the
        // name was left unchanged, and DROPPED a real "x.txt"→"x" edit (ce-review: correctness).
        if (commit && !string.IsNullOrWhiteSpace(newName)
            && !string.Equals(newName, vm.RealName, StringComparison.Ordinal))
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

/// <summary>The file operation a drop performs. Decided by <see cref="ItemView.DecideDropOperation"/>.</summary>
public enum DropOperation { Copy, Move }

public sealed class DropEventArgs(IReadOnlyList<VfsPath> paths, DropOperation operation, VfsPath? target = null) : EventArgs
{
    public IReadOnlyList<VfsPath> Paths { get; } = paths;
    public DropOperation Operation { get; } = operation;
    /// <summary>The folder the payload was dropped onto, or null for empty space (= current dir).</summary>
    public VfsPath? TargetFolder { get; } = target;
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