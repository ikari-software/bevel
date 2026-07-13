using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data;
using Avalonia.Interactivity;
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

    public StartMenu() : this(null, null) { }

    // appEnv/iconProvider are still accepted for call-site compatibility but are no longer used
    // here: the Programs cascade is fed by the bound StartMenuViewModel (ShellModel owns the
    // off-thread enumeration + icon loading), not enumerated in-place by the menu.
    public StartMenu(
        IAppEnvironment? appEnv,
        IIconProvider? iconProvider = null,
        Action? quit = null,
        Action? restart = null,
        StartMenuViewModel? programs = null)
    {
        InitializeComponent();
        _programsVm = programs;
        _quit = quit ?? RequestQuit;
        _restart = restart ?? (() => { });
        BuildStaticSubmenus();
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
        AddLeaf(SettingsItem, "Taskbar and Start Menu…", () => { });

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
