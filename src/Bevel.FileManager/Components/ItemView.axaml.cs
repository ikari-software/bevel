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

    public ViewMode ViewMode { get => GetValue(ViewModeProperty); set => SetValue(ViewModeProperty, value); }
    public IReadOnlyList<IVfsNode>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public ItemViewModel? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }

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

    private TextBox? _renameBox;

    private SortColumn _sortCol = SortColumn.Name;
    private bool _sortAsc = true;

    public event EventHandler<ItemActivatedEventArgs>? ItemActivated;
    public event EventHandler<DropEventArgs>? DropRequested;

    // Expose named controls for tests
    public Avalonia.Controls.ItemsControl ItemsControl => ItemsPresenter;
    public Avalonia.Controls.Border ColumnHeaderBorder => ColumnHeaders;

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
    }

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
                ItemsPresenter.ItemTemplate = DetailsTpl;
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

    static readonly IBrush FolderColor = new SolidColorBrush(0xFFFFD83D);
    static readonly IBrush FileColor = new SolidColorBrush(0xFFE0E0E0);

    static Border Icon(int size, bool folder) => new()
    {
        Width = size, Height = size,
        Background = folder ? FolderColor : FileColor,
        BorderBrush = new SolidColorBrush(0xFF999999),
        BorderThickness = new Thickness(1),
        CornerRadius = folder ? new CornerRadius(2) : new CornerRadius(0),
    };

    static TextBlock Label(string text, int maxW = 0) => new()
    {
        Text = text, FontSize = 11,
        MaxWidth = maxW > 0 ? maxW : double.MaxValue,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    static readonly FuncDataTemplate<ItemViewModel> LargeIconTpl = new((vm, _) =>
    {
        if (vm is null) return new TextBlock { Text = "" };
        var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = 2, Margin = new(4) };
        s.Children.Add(Icon(32, vm.IsFolder));
        s.Children.Add(new TextBlock { Text = vm.DisplayName, TextWrapping = TextWrapping.Wrap, MaxWidth = 72, TextAlignment = TextAlignment.Center, FontSize = 11 });
        return s;
    });

    static readonly FuncDataTemplate<ItemViewModel> SmallIconTpl = new((vm, _) =>
    {
        if (vm is null) return new TextBlock { Text = "" };
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new(2, 0) };
        s.Children.Add(Icon(16, vm.IsFolder));
        s.Children.Add(Label(vm.DisplayName));
        return s;
    });

    static readonly FuncDataTemplate<ItemViewModel> ListTpl = new((vm, _) =>
    {
        if (vm is null) return new TextBlock { Text = "" };
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new(0, 1) };
        s.Children.Add(Icon(16, vm.IsFolder));
        s.Children.Add(Label(vm.DisplayName));
        return s;
    });

    static readonly FuncDataTemplate<ItemViewModel> DetailsTpl = new((vm, _) =>
    {
        if (vm is null) return new TextBlock { Text = "" };
        var row = new Border { BorderBrush = new SolidColorBrush(0xFFE8E8E8), BorderThickness = new(0, 0, 0, 1), Padding = new(2, 1) };
        var g = new Grid { ColumnDefinitions = new("*,80,120,140"), Height = 20 };
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        name.Children.Add(Icon(16, vm.IsFolder));
        name.Children.Add(Label(vm.DisplayName));
        g.Children.Add(name);
        g.Children.Add(new TextBlock { [Grid.ColumnProperty] = 1, Text = vm.SizeDisplay, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Margin = new(4, 0) });
        g.Children.Add(new TextBlock { [Grid.ColumnProperty] = 2, Text = vm.TypeDescription, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Margin = new(4, 0) });
        g.Children.Add(new TextBlock { [Grid.ColumnProperty] = 3, Text = vm.ModifiedDisplay, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Margin = new(4, 0) });
        row.Child = g;
        return row;
    });

    static readonly FuncDataTemplate<ItemViewModel> ThumbTpl = new((vm, _) =>
    {
        if (vm is null) return new TextBlock { Text = "" };
        var b = new Border { BorderBrush = new SolidColorBrush(0xFFACA899), BorderThickness = new(1), Padding = new(4), Margin = new(2), Background = Brushes.White };
        var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = 2 };
        s.Children.Add(Icon(96, vm.IsFolder));
        s.Children.Add(new TextBlock { Text = vm.DisplayName, TextWrapping = TextWrapping.Wrap, MaxWidth = 106, TextAlignment = TextAlignment.Center, FontSize = 11 });
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

    void SelectOne(ItemViewModel vm) { ClearSel(); AddSel(vm); SelectedItem = vm; _anchor = vm; }
    void Toggle(ItemViewModel vm) { if (vm.IsSelected) RemSel(vm); else AddSel(vm); SelectedItem = vm; _anchor = vm; }
    int Idx(ItemViewModel? vm) => vm is null ? -1 : _viewModels.IndexOf(vm);

    void RangeTo(ItemViewModel vm)
    {
        int a = Idx(_anchor ?? vm), b = Idx(vm);
        if (a < 0 || b < 0) return;
        ClearSel();
        for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) AddSel(_viewModels[i]);
        SelectedItem = vm;
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
    }

    void AddSel(ItemViewModel vm) { vm.IsSelected = true; _selected.Add(vm); _selectedOrder.Add(vm); }
    void RemSel(ItemViewModel vm) { vm.IsSelected = false; _selected.Remove(vm); _selectedOrder.Remove(vm); }
    void ClearSel() { foreach (var s in _selected) s.IsSelected = false; _selected.Clear(); _selectedOrder.Clear(); }

    // ── Pointer ──────────────────────────────────────────────────────

    void OnBgPointerPressed(object? _, PointerPressedEventArgs e)
    {
        var pt = e.GetPosition(ItemsPresenter);
        var vm = Hit(pt);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.ClickCount == 2 && vm is not null) { ItemActivated?.Invoke(this, new(vm)); e.Handled = true; return; }
        if (vm is null)
        {
            if (!ctrl && !shift) ClearSel();
            _marqueeOrigin = pt; _marqueeDragging = true; MarqueeRect.IsVisible = false;
            e.Handled = true; return;
        }
        if (shift) RangeTo(vm);
        else if (ctrl) Toggle(vm);
        else if (!vm.IsSelected) SelectOne(vm);
        _lastClickIdx = Idx(vm); e.Handled = true;
    }

    void OnBgPointerMoved(object? _, PointerEventArgs e)
    {
        if (!_marqueeDragging) return;
        var pt = e.GetPosition(ItemsPresenter);
        var r = new Rect(Math.Min(_marqueeOrigin.X, pt.X), Math.Min(_marqueeOrigin.Y, pt.Y), Math.Abs(pt.X - _marqueeOrigin.X), Math.Abs(pt.Y - _marqueeOrigin.Y));
        if (r.Width > 4 || r.Height > 4) { Canvas.SetLeft(MarqueeRect, r.X); Canvas.SetTop(MarqueeRect, r.Y); MarqueeRect.Width = r.Width; MarqueeRect.Height = r.Height; MarqueeRect.IsVisible = true; }
    }

    void OnBgPointerReleased(object? _, PointerReleasedEventArgs e)
    {
        if (!_marqueeDragging) return;
        _marqueeDragging = false;
        if (MarqueeRect.IsVisible)
        {
            var r = new Rect(Canvas.GetLeft(MarqueeRect), Canvas.GetTop(MarqueeRect), MarqueeRect.Width, MarqueeRect.Height);
            MarqueeRect.IsVisible = false; Marquee(r);
        }
        e.Handled = true;
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
        if (paths is { Count: > 0 })
            DropRequested?.Invoke(this, new(paths, e.KeyModifiers.HasFlag(KeyModifiers.Control), e.KeyModifiers.HasFlag(KeyModifiers.Shift)));
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
            default: if (e.Key is >= Key.A and <= Key.Z) TypeAhead(e.Key); break;
        }
    }

    void Navigate(Key key, bool shift)
    {
        int cur = _lastClickIdx, next = cur + (key is Key.Down or Key.Right ? 1 : -1);
        if (next < 0 || next >= _viewModels.Count) return;
        if (shift) RangeTo(_viewModels[next]); else SelectOne(_viewModels[next]);
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
        var box = _renameBox; _renameBox = null;
        box.KeyDown -= OnRenameKey; box.LostFocus -= OnRenameLost;
        if (commit && !string.IsNullOrWhiteSpace(box.Text)) SelectedItem.EditName = box.Text;
        SelectedItem.IsEditing = false;
        AdornerLayer.GetAdornerLayer(this)?.Children.Remove(box);
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
    public DropEventArgs(IReadOnlyList<VfsPath> paths, bool ctrl, bool shift)
    {
        Paths = paths; IsCopy = ctrl && !shift; IsLink = ctrl && shift;
    }
}