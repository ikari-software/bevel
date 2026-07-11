using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Bevel.Pal.Abstractions;

namespace Bevel.Taskbar;

/// <summary>
/// Win2000-style Start Menu with cascading Programs submenu, keyboard navigation,
/// and the full classic menu structure (Programs/Documents/Settings/Search/Help/Run/
/// Log Off/Shut Down). Backed by IAppEnvironment for installed-app enumeration.
/// </summary>
public partial class StartMenu : UserControl
{
    public bool IsOpen => MenuPopup.IsOpen;

    private readonly IAppEnvironment? _appEnv;
    private CancellationTokenSource? _programsLoadCts;
    private bool _programsLoaded;

    public StartMenu() : this(null) { }

    public StartMenu(IAppEnvironment? appEnv)
    {
        InitializeComponent();
        _appEnv = appEnv;
    }

    // ── Public accessors for named controls (tests + host wiring) ──────

    public Popup MenuPopupControl => MenuPopup;
    public Popup ProgramsPopupControl => ProgramsPopup;
    public Popup SubmenuPopupControl => SubmenuPopup;
    public Border MenuBorderControl => MenuBorder;
    public Border ProgramsBorderControl => ProgramsBorder;
    public StackPanel MenuItemsPanelControl => MenuItemsPanel;
    public StackPanel ProgramsItemsPanelControl => ProgramsItemsPanel;
    public StackPanel SubmenuItemsPanelControl => SubmenuItemsPanel;
    public Border ProgramsItemControl => ProgramsItem;
    public Border DocumentsItemControl => DocumentsItem;
    public Border SettingsItemControl => SettingsItem;
    public Border SearchItemControl => SearchItem;
    public Border HelpItemControl => HelpItem;
    public Border RunItemControl => RunItem;
    public Border LogOffItemControl => LogOffItem;
    public Border ShutDownItemControl => ShutDownItem;

    // ── Test helpers ──────────────────────────────────────────────────

    /// <summary>Opens the Programs submenu (for testing).</summary>
    public void OpenProgramsTest()
    {
        _ = LoadProgramsWithDelay();
        OpenProgramsPopup();
    }

    /// <summary>Opens the menu at the Start button's position.</summary>
    public async Task OpenAsync(Control placementTarget)
    {
        MenuPopup.PlacementTarget = placementTarget;
        MenuPopup.IsOpen = true;
        MenuBorder.Focus();
    }

    /// <summary>Closes the menu and all cascading submenus.</summary>
    public void Close()
    {
        ProgramsPopup.IsOpen = false;
        SubmenuPopup.IsOpen = false;
        MenuPopup.IsOpen = false;
    }

    // ── Main menu event handlers ──────────────────────────────────────

    private void OnMenuKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Down:
                FocusNextItem();
                e.Handled = true;
                break;
            case Key.Up:
                FocusPrevItem();
                e.Handled = true;
                break;
            case Key.Enter when TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Control item:
                ActivateItem(item);
                e.Handled = true;
                break;
        }
    }

    private void OnProgramsPointerEntered(object? sender, PointerEventArgs e)
    {
        if (!_programsLoaded)
        {
            _ = LoadProgramsWithDelay();
        }
        else
        {
            OpenProgramsPopup();
        }
    }

    private void OnProgramsPointerExited(object? sender, PointerEventArgs e)
    {
        // Keep open — the 400ms close timer is handled by the popup's own logic.
    }

    private void OnProgramsClicked(object? sender, PointerPressedEventArgs e)
    {
        _ = LoadProgramsWithDelay();
        OpenProgramsPopup();
    }

    private void OnDocumentsClicked(object? sender, PointerPressedEventArgs e)
    {
        // Placeholder: MRU documents list.
        PopulateSubmenu("Documents", new[] { "(No recent documents)" });
        SubmenuPopup.PlacementTarget = DocumentsItem;
        SubmenuPopup.IsOpen = true;
    }

    private void OnSettingsClicked(object? sender, PointerPressedEventArgs e)
    {
        PopulateSubmenu("Settings", new[] { "Control Panel", "Taskbar & Start Menu", "Folder Options" });
        SubmenuPopup.PlacementTarget = SettingsItem;
        SubmenuPopup.IsOpen = true;
    }

    private void OnSearchClicked(object? sender, PointerPressedEventArgs e)
    {
        PopulateSubmenu("Search", new[] { "For Files or Folders...", "On the Internet..." });
        SubmenuPopup.PlacementTarget = SearchItem;
        SubmenuPopup.IsOpen = true;
    }

    private void OnHelpClicked(object? sender, PointerPressedEventArgs e)
    {
        // Placeholder: open help.
        Close();
    }

    private void OnRunClicked(object? sender, PointerPressedEventArgs e)
    {
        // Placeholder: open Run dialog.
        Close();
    }

    private void OnLogOffClicked(object? sender, PointerPressedEventArgs e)
    {
        Close();
    }

    private void OnShutDownClicked(object? sender, PointerPressedEventArgs e)
    {
        Close();
    }

    // ── Programs submenu ──────────────────────────────────────────────

    private async Task LoadProgramsWithDelay()
    {
        if (_programsLoaded || _appEnv is null) return;

        _programsLoadCts?.Cancel();
        _programsLoadCts = new CancellationTokenSource();

        // 400ms hover delay before opening.
        try
        {
            await Task.Delay(400, _programsLoadCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var apps = await _appEnv.EnumerateInstalledAppsAsync(_programsLoadCts.Token);
            if (_programsLoadCts.IsCancellationRequested) return;

            Dispatcher.UIThread.Post(() =>
            {
                ProgramsItemsPanel.Children.Clear();
                foreach (var app in apps)
                {
                    var item = CreateMenuItem(app.DisplayName, () => LaunchApp(app.AppId));
                    ProgramsItemsPanel.Children.Add(item);
                }
                _programsLoaded = true;
            });
        }
        catch
        {
            // App enumeration failed — show empty state.
            Dispatcher.UIThread.Post(() =>
            {
                ProgramsItemsPanel.Children.Clear();
                ProgramsItemsPanel.Children.Add(new TextBlock
                {
                    Text = "(No programs found)",
                    FontSize = 11,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(4, 2),
                });
                _programsLoaded = true;
            });
        }
    }

    private void OpenProgramsPopup()
    {
        ProgramsPopup.PlacementTarget = ProgramsItem;
        ProgramsPopup.IsOpen = true;
    }

    private void OnProgramsPopupKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ProgramsPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private async void LaunchApp(string appId)
    {
        if (_appEnv is null) return;
        try
        {
            await _appEnv.LaunchAsync(appId);
        }
        catch { /* best effort */ }
        Close();
    }

    // ── Submenu helpers ───────────────────────────────────────────────

    private void PopulateSubmenu(string title, string[] items)
    {
        SubmenuItemsPanel.Children.Clear();
        foreach (var item in items)
        {
            SubmenuItemsPanel.Children.Add(CreateMenuItem(item, () => { }));
        }
    }

    private void OnSubmenuPopupKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SubmenuPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    // ── Keyboard navigation ───────────────────────────────────────────

    private void FocusNextItem()
    {
        var children = MenuItemsPanel.Children;
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        var idx = focused is Control fc ? children.IndexOf(fc) : -1;
        for (int i = idx + 1; i < children.Count; i++)
        {
            if (children[i] is Control c && c.Focusable)
            {
                c.Focus();
                return;
            }
        }
    }

    private void FocusPrevItem()
    {
        var children = MenuItemsPanel.Children;
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        var idx = focused is Control fc ? children.IndexOf(fc) : 0;
        for (int i = idx - 1; i >= 0; i--)
        {
            if (children[i] is Control c && c.Focusable)
            {
                c.Focus();
                return;
            }
        }
    }

    private void ActivateItem(Control? item)
    {
        if (item is null) return;
        // Simulate a click on the item.
        if (item == ProgramsItem) { _ = LoadProgramsWithDelay(); OpenProgramsPopup(); }
        else if (item == DocumentsItem) OnDocumentsClicked(null, null!);
        else if (item == SettingsItem) OnSettingsClicked(null, null!);
        else if (item == SearchItem) OnSearchClicked(null, null!);
        else if (item == HelpItem) OnHelpClicked(null, null!);
        else if (item == RunItem) OnRunClicked(null, null!);
        else if (item == LogOffItem) OnLogOffClicked(null, null!);
        else if (item == ShutDownItem) OnShutDownClicked(null, null!);
    }

    // ── Menu item factory ─────────────────────────────────────────────

    private static Border CreateMenuItem(string text, Action onClick)
    {
        var border = new Border
        {
            Padding = new Thickness(4, 2),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
            },
        };

        border.PointerEntered += (_, _) => border.Background = Brushes.Navy;
        border.PointerExited += (_, _) => border.Background = Brushes.Transparent;
        border.PointerPressed += (_, _) => onClick();

        return border;
    }
}