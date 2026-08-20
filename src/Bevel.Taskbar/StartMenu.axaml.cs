using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.Core.Vfs;
using Bevel.Pal.Abstractions;
using Bevel.UI;

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
    private readonly Action<VfsPath> _openFolder;
    private readonly Action _openSearch;
    private readonly Action _toggleDesktop;
    private readonly Func<bool?>? _desktopRunning;

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
        Action? openSettings = null,
        Action<VfsPath>? openFolder = null,
        Action? openSearch = null,
        Action? toggleDesktop = null,
        Func<bool?>? desktopRunning = null)
    {
        InitializeComponent();
        _programsVm = programs;
        _quit = quit ?? RequestQuit;
        _restart = restart ?? (() => { });
        _openSettings = openSettings ?? (() => { });
        _openFolder = openFolder ?? (_ => { });
        _openSearch = openSearch ?? (() => { });
        _toggleDesktop = toggleDesktop ?? (() => { });
        _desktopRunning = desktopRunning;
        // No host wiring for the desktop toggle (parameterless test construction / all-in-one) → disable
        // the item so it never looks live-but-dead: it degrades to a visibly-disabled entry, not a no-op.
        if (toggleDesktop is null)
        {
            DesktopItem.IsEnabled = false;
            LunaDesktopButton.IsEnabled = false;
        }
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
        ApplyThemeLayout();
        RefreshDesktopLabel();
        MenuPopup.PlacementTarget = placementTarget;
        MenuPopup.IsOpen = true;
        // Move keyboard focus INTO the menu so arrow keys work immediately (bevel-vk4n). The popup is a
        // separate visual tree with its own TopLevel that only exists once open, so focusing is posted
        // (Loaded priority) to run after the popup root is realized — the avalonia-popup-needs-visual-tree
        // gotcha. The taskbar host flips itself key while the menu is open (SetKeyFocusAllowed), so this
        // focus can actually receive keystrokes.
        Dispatcher.UIThread.Post(FocusFirstItem, DispatcherPriority.Loaded);
        return Task.CompletedTask;
    }

    /// <summary>Focuses the first actionable row of the active layout so the menu is arrow-navigable the
    /// instant it opens. Classic → the first top-level MenuItem; Luna → the first pinned/place row.</summary>
    private void FocusFirstItem()
    {
        if (!MenuPopup.IsOpen) return;
        if (LunaLayout.IsVisible)
        {
            var firstRow = LunaLayout.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(b => b.Classes.Contains("lunarow") && b.IsEffectivelyVisible);
            firstRow?.Focus(NavigationMethod.Tab);
        }
        else
        {
            ItemsMenu.Items.OfType<MenuItem>().FirstOrDefault()?.Focus(NavigationMethod.Tab);
        }
    }

    private bool _lunaWired;

    /// <summary>Picks the layout for the active theme (bevel-dob). The menu is never on screen during a
    /// theme swap, so toggling the whole visual tree here is safe. The Luna two-column panel is wired
    /// once, on first show: its pinned column binds the same reconciled Programs collection the classic
    /// cascade uses, so it stays off-thread and current.</summary>
    private void ApplyThemeLayout()
    {
        var luna = Bevel.UI.ThemeService.Current == "luna";
        LunaLayout.IsVisible = luna;
        ClassicLayout.IsVisible = !luna;
        if (luna && !_lunaWired)
        {
            LunaUserName.Text = CurrentUserDisplayName();
            if (_programsVm is not null)
            {
                LunaPinned.ItemsSource = _programsVm.FrequentPrograms;   // curated: newest + most-used, capped
                // The pinned rows bind IconSource, but nothing triggered the (off-thread) icon load the way
                // the classic cascade does — so pinned icons stayed blank. The frequent list is tiny and
                // always visible, so load eagerly (EnsureIcon is async/non-blocking) rather than relying on
                // container realization, and re-load whenever the curated list is recomputed.
                EnsureFrequentIcons();
                _programsVm.FrequentPrograms.CollectionChanged += (_, _) => EnsureFrequentIcons();
                WireAllProgramsFlyout();                                 // full list lives in the flyout
            }
            _lunaWired = true;
        }
    }

    private Flyout? _allProgramsFlyout;
    private StackPanel? _allProgramsList;

    /// <summary>Attaches a plain Flyout to the Luna "All Programs" row, with content built entirely in
    /// code. A MenuFlyout's items and an inline-XAML flyout both came up 0×0 in this popup (its items
    /// don't pick up LunaLayout's row styles or the app MenuItem theme, and popups don't inherit the
    /// menu's DataContext) — so every row here carries its own explicit size, brushes and hover, with
    /// no reliance on outside styles.</summary>
    private void WireAllProgramsFlyout()
    {
        if (_programsVm is null) return;
        _allProgramsList = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = _allProgramsList,
            MaxHeight = 460,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var frame = new Border
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            MinWidth = 200,
            Child = scroll,
        };
        // Chrome from the Luna theme tokens (resolved live), not literals — see LunaTheme.axaml.
        frame[!Border.BackgroundProperty] = new DynamicResourceExtension("Luna.Brush.StartMenuPinnedColumn");
        frame[!Border.BorderBrushProperty] = new DynamicResourceExtension("Luna.Brush.StartMenuPlaceIconBorder");
        _allProgramsFlyout = new Flyout { Content = frame, Placement = PlacementMode.RightEdgeAlignedBottom };
        LunaAllProgramsButton.Flyout = _allProgramsFlyout;

        RebuildAllProgramsList();
        _programsVm.Programs.CollectionChanged += (_, _) => RebuildAllProgramsList();
    }

    private void RebuildAllProgramsList()
    {
        if (_programsVm is null || _allProgramsList is null) return;
        _allProgramsList.Children.Clear();
        foreach (var p in _programsVm.Programs)
            _allProgramsList.Children.Add(BuildAllProgramsRow(p));
    }

    private Control BuildAllProgramsRow(ProgramItemViewModel p)
    {
        var icon = new Image { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapInterpolationMode(icon, BitmapInterpolationMode.HighQuality);
        icon.Bind(Image.SourceProperty, new Binding(nameof(ProgramItemViewModel.IconSource)) { Source = p });
        p.EnsureIcon();   // every row must kick its own load — without this only pre-loaded icons showed
        var label = new TextBlock
        {
            Text = p.DisplayName,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
        };
        label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Luna.Brush.StartMenuPlaceText");
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(icon);
        content.Children.Add(label);
        var row = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(8, 5),
            CornerRadius = new CornerRadius(3),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = content,
        };
        // Full-row Luna selection on hover; colours come from the theme tokens (resolved live), matching
        // the XAML Button.lunarow hover. Re-apply the themed foreground binding on exit.
        row.PointerEntered += (_, _) => { row.Background = ThemedBrush("Bevel.Brush.Highlight", Brushes.RoyalBlue); label.Foreground = Brushes.White; };
        row.PointerExited += (_, _) => { row.Background = Brushes.Transparent; label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Luna.Brush.StartMenuPlaceText"); };
        row.PointerPressed += (_, _) =>
        {
            if (p.LaunchCommand?.CanExecute(null) == true) p.LaunchCommand.Execute(null);
            Close();
        };
        return row;
    }

    /// <summary>Resolves a themed brush from the active theme's resources — the flyout is Luna-only and is
    /// attached by the time a row can be hovered — falling back only if the key is somehow absent.</summary>
    private IBrush ThemedBrush(string key, IBrush fallback)
        => this.TryFindResource(key, out var v) && v is IBrush b ? b : fallback;

    private static string CurrentUserDisplayName()
    {
        var u = Environment.UserName;
        return string.IsNullOrWhiteSpace(u) ? "User" : char.ToUpperInvariant(u[0]) + u.Substring(1);
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

    private void WireFixedItemIcons()
    {
        SetItemIcon(ProgramsItem, "start.programs");
        SetItemIcon(DocumentsItem, "start.documents");
        SetItemIcon(SettingsItem, "start.settings");
        SetItemIcon(SearchItem, "start.search");
        SetItemIcon(HelpItem, "start.help");
        SetItemIcon(RunItem, "start.run");
        SetItemIcon(LogOffItem, "start.logoff");
        SetItemIcon(ShutDownItem, "start.shutdown");
    }

    // Fixed-item glyphs are the shared code-drawn vectors (Glyphs, Bevel.UI) — crisp at any DPI and
    // theme-token colored. Previously loaded non-embedded PNGs by URI, which silently failed and left the
    // whole icon column empty; vectors also satisfy the vector-only asset rule.
    private static void SetItemIcon(MenuItem item, string key)
        => item.Icon = Glyphs.Icon(16, new IconKey(key));

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

        // Every leaf routes to a REAL action (bevel-x6pv) — nothing here is a dead no-op or hidden:
        // Control Panel + Taskbar open Bevel Settings; Network/Printers launch the matching macOS
        // settings pane; Search opens Explorer's Find or the browser. (Log Off / Shut Down are owned by
        // bevel-4vce — the shell-action semantics decision.)
        AddLeaf(SettingsItem, "Control Panel", () => { Close(); OpenSystemSettings(); });   // the OS's own settings
        AddLeaf(SettingsItem, "Bevel Settings", () => { Close(); _openSettings(); });        // Bevel's own settings
        AddLeaf(SettingsItem, "Network and Dial-up Connections", () => { Close(); LaunchUrl("x-apple.systempreferences:com.apple.Network-Settings.extension"); });
        AddLeaf(SettingsItem, "Printers", () => { Close(); LaunchUrl("x-apple.systempreferences:com.apple.Print-Scan-Settings.extension"); });
        AddLeaf(SettingsItem, "Taskbar and Start Menu…", () => { Close(); _openSettings(); });

        AddLeaf(SearchItem, "For Files or Folders…", () => { Close(); _openSearch(); });
        AddLeaf(SearchItem, "On the Internet…", () => { Close(); LaunchUrl("https://duckduckgo.com"); });
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
        RenderOptions.SetBitmapInterpolationMode(icon, BitmapInterpolationMode.HighQuality);
        icon.Bind(Image.SourceProperty, new Binding(nameof(ProgramItemViewModel.IconSource)) { Source = vm });
        item.Icon = icon;

        vm.Launched -= Close; // idempotent: a re-realized container must not stack close handlers
        vm.Launched += Close;
        vm.EnsureIcon();      // lazy, off-thread, idempotent
    }

    // ── Leaf handlers (XAML-wired) ─────────────────────────────────────

    private void OnHelpClick(object? sender, RoutedEventArgs e) { Close(); ShowHelp(); }
    private void OnRunClick(object? sender, RoutedEventArgs e) { Close(); ShowRun(); }
    private void OnLogOffClick(object? sender, RoutedEventArgs e) => Close();   // bevel-4vce owns Log Off semantics
    private void OnShutDownClick(object? sender, RoutedEventArgs e) => Close();  // bevel-4vce owns Shut Down semantics

    // ── Real actions for the former dead items (bevel-x6pv) ────────────

    /// <summary>Opens a URL / macOS settings-pane / resource via the OS handler — same mechanism the
    /// clock and onboarding use. Backs the Network / Printers / "Search on the Internet" leaves.</summary>
    private static void LaunchUrl(string url)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "open",
                Arguments = url,
                UseShellExecute = true,
            });
        }
        catch { /* nothing to open */ }
    }

    /// <summary>Opens the OS's own settings — macOS System Settings — for the "Control Panel" entry.
    /// (Bevel's own settings are a separate "Bevel Settings" entry via <see cref="_openSettings"/>.)</summary>
    private static void OpenSystemSettings()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            psi.ArgumentList.Add("-b");
            psi.ArgumentList.Add("com.apple.systempreferences");
            System.Diagnostics.Process.Start(psi);
        }
        catch { /* System Settings unavailable */ }
    }

    private void ShowHelp() => new AboutDialog().Show();
    private void ShowRun() => new RunDialog().Show();

    // ── Luna two-column handlers ───────────────────────────────────────
    // All Programs opens a flyout bound to the full Programs collection (see StartMenu.axaml). Launching
    // a program closes the whole menu. Places/Search/Help/Run are visual stubs, matching the classic leaves.
    private void EnsureFrequentIcons()
    {
        if (_programsVm is null) return;
        foreach (var p in _programsVm.FrequentPrograms)
            p.EnsureIcon();   // off-thread, idempotent
    }

    // Launch is driven by the row's Command="{Binding LaunchCommand}"; this handler only dismisses the
    // menu. (Don't also Execute the command here — that would launch the app twice.)
    private void OnLunaProgramClick(object? sender, RoutedEventArgs e) => Close();

    private void OnLunaPlaceClick(object? sender, RoutedEventArgs e)
    {
        // Places open a Bevel Explorer window at the mapped folder (the row's Tag names it). Without this
        // the "My Documents / Pictures / Music / Computer" rows did nothing but close the menu.
        if (sender is Control { Tag: string tag })
            _openFolder(ResolvePlace(tag));
        Close();
    }

    /// <summary>Maps a place row's Tag to the folder Bevel Explorer should open. "computer" opens the
    /// filesystem root; the rest resolve to the user's known folders.</summary>
    private static VfsPath ResolvePlace(string tag) => tag switch
    {
        "computer" => new VfsPath("file", "/"),
        "pictures" => new VfsPath("file", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
        "music"    => new VfsPath("file", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
        "documents" => new VfsPath("file", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
        _ => new VfsPath("file", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
    };
    private void OnLunaSettingsClick(object? sender, RoutedEventArgs e) { Close(); OpenSystemSettings(); }   // Control Panel → OS settings
    private void OnLunaBevelSettingsClick(object? sender, RoutedEventArgs e) { Close(); _openSettings(); }   // Bevel Settings
    private void OnLunaHelpClick(object? sender, RoutedEventArgs e) { Close(); ShowHelp(); }
    private void OnLunaSearchClick(object? sender, RoutedEventArgs e) { Close(); _openSearch(); }
    private void OnLunaRunClick(object? sender, RoutedEventArgs e) { Close(); ShowRun(); }
    private void OnLunaLogOffClick(object? sender, RoutedEventArgs e) { Close(); _restart(); }
    private void OnLunaTurnOffClick(object? sender, RoutedEventArgs e) { Close(); _quit(); }
    private void OnRestartClick(object? sender, RoutedEventArgs e)
    {
        Close();
        _restart();
    }

    /// <summary>Start ▸ Show/Hide Desktop (bevel-gdie). Shared by the Win2000 leaf and the Luna row: close
    /// the menu, then flip the desktop child via the host callback (which does the launcher round-trip off
    /// the UI thread). The label refreshes on the NEXT open — a single toggle can't know the new state
    /// synchronously (the process spawn/kill is async under the launcher).</summary>
    private void OnToggleDesktopClick(object? sender, RoutedEventArgs e)
    {
        Close();
        _toggleDesktop();
    }

    /// <summary>Sets the "Show Desktop" / "Hide Desktop" label on both layouts to match the launcher's
    /// current state, computed at open-time (not cached) so a RestartAll or a desktop crash can't leave a
    /// stale label. The query is a bounded UDS round-trip, so it runs OFF the UI thread and the result is
    /// posted back; until it lands the label defaults to "Show Desktop" (the desktop is OFF by default).
    /// Unsupervised (query is null / returns null) keeps the default — the item is already disabled.</summary>
    private void RefreshDesktopLabel()
    {
        SetDesktopLabel(false); // safe default while the async probe is in flight (desktop is off by default)
        var query = _desktopRunning;
        if (query is null) return;
        Task.Run(() =>
        {
            var running = query();   // bounded off-UI-thread launcher probe; null when unreachable
            if (running is not bool r) return;
            Dispatcher.UIThread.Post(() => SetDesktopLabel(r));
        });
    }

    private void SetDesktopLabel(bool running)
    {
        var text = running ? "Hide Desktop" : "Show Desktop";
        DesktopItem.Header = running ? "Hide _Desktop" : "Show _Desktop";
        LunaDesktopText.Text = text;
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
