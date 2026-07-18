using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Bevel.Pal.Abstractions;

namespace Bevel.Taskbar;

/// <summary>
/// Win2000-style Start menu (bevel-m2.11): a navy sidebar banner beside a single-column
/// cascading menu, popped up above the Start button. The item column is a vertical
/// <see cref="Menu"/> rendered with the default MenuItem theme, so selection highlight,
/// icon column, cascade arrows, and etched separators all come from the classic theme
/// (see StartMenu.axaml).
///
/// The Programs cascade binds <see cref="StartMenuViewModel.Programs"/> — the off-thread,
/// reconciled collection owned by <see cref="ShellModel"/>; each item shapes itself and lazily
/// loads its icon on container realization (i.e. when the cascade opens), so opening the menu
/// never blocks the UI thread.
/// </summary>
public partial class StartMenu : UserControl
{
    private readonly StartMenuViewModel? _programsVm;
    private readonly Action _quit;
    private readonly Action _restart;
    private readonly Action _openSettings;

    public StartMenu() : this(null, null) { }

    // appEnv/iconProvider are still accepted for call-site compatibility but are no longer used
    // here: the Programs cascade is fed by the bound StartMenuViewModel (ShellModel owns the
    // off-thread enumeration + icon loading), not enumerated in-place by the menu.
    public StartMenu(
        IAppEnvironment? appEnv,
        IIconProvider? iconProvider = null,
        Action? quit = null,
        Action? restart = null,
        StartMenuViewModel? programs = null,
        Action? openSettings = null)
    {
        InitializeComponent();
        _programsVm = programs;
        _quit = quit ?? RequestQuit;
        _restart = restart ?? (() => { });
        _openSettings = openSettings ?? (() => { });
        BuildStaticSubmenus();
        WireFixedItemIcons();
        WireHoverToOpen();
    }

    public bool IsOpen => MenuPopup.IsOpen;

    /// <summary>The menu's popup, exposed for host wiring and headless render tests.</summary>
    public Avalonia.Controls.Primitives.Popup MenuPopupControl => MenuPopup;

    /// <summary>
    /// Opens the menu above the Start button. Programs is already live via its binding, so there
    /// is nothing to await — the Task return is kept for existing callers/tests that await it.
    /// </summary>
    public Task OpenAsync(Control placementTarget)
    {
        MenuPopup.PlacementTarget = placementTarget;
        MenuPopup.IsOpen = true;
        return Task.CompletedTask;
    }

    /// <summary>Closes the menu; the Menu's own cascade popups close with it.</summary>
    public void Close() => MenuPopup.IsOpen = false;

    // ── Hover-to-open ──────────────────────────────────────────────────

    /// <summary>
    /// Makes the top-level rows cascade on hover, the way the real Start menu does. Avalonia's
    /// <see cref="Menu"/> is modelled as a menu BAR: its top-level items stay collapsed until a
    /// CLICK puts the bar into "open mode", after which hover switches between them. That's wrong
    /// for a Start menu — the whole thing is already an open popup, so each row should behave like
    /// a submenu item and open the instant the pointer arrives, with no click first. We drive that
    /// ourselves on <see cref="InputElement.PointerEntered"/> of every top-level item.
    /// </summary>
    private void WireHoverToOpen()
    {
        foreach (var top in ItemsMenu.Items.OfType<MenuItem>())
            top.PointerEntered += OnTopLevelItemEntered;
    }

    /// <summary>Opens the hovered row's cascade (if it has one) and collapses every sibling, so only
    /// one submenu shows at a time — including collapsing an open cascade when the pointer moves onto
    /// a leaf row (Help, Run, Shut Down…). Setting <see cref="MenuItem.IsSubMenuOpen"/> directly is
    /// what the built-in handler only does once the bar is already open; doing it on first hover is
    /// the whole fix.</summary>
    private void OnTopLevelItemEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not MenuItem entered) return;
        foreach (var top in ItemsMenu.Items.OfType<MenuItem>())
            top.IsSubMenuOpen = ReferenceEquals(top, entered) && top.HasSubMenu;
    }

    // ── Fixed-item icons (bevel-m2.14) ─────────────────────────────────

    // The Win2000 Start-menu glyphs are theme assets authored in Glyphs.cs and exported to PNG
    // (Bevel.IconPreview) under the Win2000 theme assembly. Bevel.Taskbar doesn't reference the
    // theme project, but avares:// resolves across any assembly loaded into the running app, so we
    // load the 16px variant by URI. The MenuItem theme already reserves a 19px icon column.
    private const string IconBase = "avares://Bevel.Themes.Win2000/Assets/Icons/";

    private void WireFixedItemIcons()
    {
        SetItemIcon(ProgramsItem, "start-programs");
        SetItemIcon(DocumentsItem, "start-documents");
        SetItemIcon(SettingsItem, "start-settings");
        SetItemIcon(SearchItem, "start-search");
        SetItemIcon(HelpItem, "start-help");
        SetItemIcon(RunItem, "start-run");
        SetItemIcon(LogOffItem, "start-logoff");
        SetItemIcon(ShutDownItem, "start-shutdown");
    }

    private static void SetItemIcon(MenuItem item, string name)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri($"{IconBase}{name}-16.png"));
            item.Icon = new Image { Width = 16, Height = 16, Source = new Bitmap(stream) };
        }
        catch
        {
            // Asset unavailable (headless test with no theme assembly loaded) — leave the column empty.
        }
    }

    // ── Cascading groups ───────────────────────────────────────────────

    private void BuildStaticSubmenus()
    {
        if (_programsVm is not null)
            WireBoundPrograms();
        else
            // No view-model (parameterless test construction) → nothing to show under Programs.
            ProgramsItem.Items.Add(Disabled("(No programs)"));

        // Documents: recent-documents list (empty for now — no MRU tracking yet).
        DocumentsItem.Items.Add(Disabled("(No recent documents)"));

        AddLeaf(SettingsItem, "Control Panel", () => { });
        AddLeaf(SettingsItem, "Network and Dial-up Connections", () => { });
        AddLeaf(SettingsItem, "Printers", () => { });
        AddLeaf(SettingsItem, "Taskbar and Start Menu…", () => { Close(); _openSettings(); });

        AddLeaf(SearchItem, "For Files or Folders…", () => { });
        AddLeaf(SearchItem, "On the Internet…", () => { });
    }

    // ── Bound Programs ─────────────────────────────────────────────────

    /// <summary>
    /// Binds the Programs cascade to the reconciled <see cref="StartMenuViewModel.Programs"/>
    /// collection. The list is kept current off the UI thread by <see cref="ShellModel"/> and
    /// diffed in place (keyed by bundle id), so a re-enumeration never rebuilds the cascade — each
    /// surviving row keeps its container, its loaded icon and the submenu's scroll position.
    ///
    /// While the collection is empty the cascade shows a disabled placeholder — "(Loading…)" until
    /// the startup enumeration latches, then "(No programs found)". The placeholder lives in
    /// <c>Items</c>, never in the reconciled collection, so the diff stays pure; it's swapped for
    /// the bound <c>ItemsSource</c> the instant the first app arrives.
    /// </summary>
    private void WireBoundPrograms()
    {
        // A menu cascade realizes its children only when the submenu opens, so shaping + lazy icon
        // kick happen exactly when Programs is hovered — opening the Start menu itself stays free.
        ProgramsItem.ContainerPrepared += OnProgramContainerPrepared;

        _programsVm!.Programs.CollectionChanged += (_, _) => UpdateProgramsPlaceholder();
        _programsVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StartMenuViewModel.ProgramsLoaded))
                UpdateProgramsPlaceholder();
        };
        UpdateProgramsPlaceholder();
    }

    /// <summary>
    /// Reconciles the Programs cascade's contents with the collection's populated/empty state:
    /// bind the live collection when it has items, else show a single disabled placeholder. Avalonia
    /// forbids holding both <c>Items</c> and <c>ItemsSource</c>, so each branch clears the other's
    /// mode before switching. Idempotent — safe to call on every collection/loaded change.
    /// </summary>
    private void UpdateProgramsPlaceholder()
    {
        var programs = _programsVm!.Programs;
        if (programs.Count > 0)
        {
            if (!ReferenceEquals(ProgramsItem.ItemsSource, programs))
            {
                ProgramsItem.Items.Clear();            // drop the placeholder before binding
                ProgramsItem.ItemsSource = programs;
            }
        }
        else
        {
            ProgramsItem.ItemsSource = null;           // detach binding before mutating Items
            ProgramsItem.Items.Clear();
            ProgramsItem.Items.Add(Disabled(
                _programsVm.ProgramsLoaded ? "(No programs found)" : "(Loading…)"));
        }
    }

    private void OnProgramContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is not MenuItem item || item.DataContext is not ProgramItemViewModel vm)
            return;

        item.Header = vm.DisplayName;
        item.Command = vm.LaunchCommand;

        var icon = new Image { Width = 16, Height = 16 };
        icon.Bind(Image.SourceProperty, new Binding(nameof(ProgramItemViewModel.IconSource)) { Source = vm });
        item.Icon = icon;

        vm.Launched -= Close; // idempotent: a re-realized container must not stack close handlers
        vm.Launched += Close;
        vm.EnsureIcon();      // lazy, off-thread, idempotent
    }

    // ── Leaf handlers (XAML-wired) ─────────────────────────────────────

    private void OnHelpClick(object? sender, RoutedEventArgs e) => Close();
    private void OnRunClick(object? sender, RoutedEventArgs e) => Close();
    private void OnLogOffClick(object? sender, RoutedEventArgs e) => Close();
    private void OnShutDownClick(object? sender, RoutedEventArgs e) => Close();
    private void OnRestartClick(object? sender, RoutedEventArgs e)
    {
        Close();
        _restart();
    }

    private void OnQuitClick(object? sender, RoutedEventArgs e)
    {
        Close();
        _quit();
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private void AddLeaf(MenuItem parent, string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => { action(); Close(); };
        parent.Items.Add(item);
    }

    private static MenuItem Disabled(string text) => new() { Header = text, IsEnabled = false };

    private static void RequestQuit()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
