using Avalonia.Controls;
using Avalonia.Interactivity;
using Bevel.Pal.Abstractions;

namespace Bevel.Taskbar;

/// <summary>
/// Win2000-style Start menu (bevel-m2.11): a navy sidebar banner beside a single-column
/// cascading menu, popped up above the Start button. The item column is a vertical
/// <see cref="Menu"/> rendered with the default MenuItem theme, so selection highlight,
/// icon column, cascade arrows, and etched separators all come from the classic theme
/// (see StartMenu.axaml). Cascading groups (Programs/Documents/Settings/Search) are
/// populated here; Programs is enumerated from <see cref="IAppEnvironment"/>.
///
/// Per-item glyphs (the Win2000 folder/document/settings icons) are intentionally absent
/// until the icon set (bevel-assets) exists — the MenuItem theme already reserves the
/// 19px icon column, so wiring <c>MenuItem.Icon</c> later needs no layout change.
/// </summary>
public partial class StartMenu : UserControl
{
    private readonly IAppEnvironment? _appEnv;
    private bool _programsLoaded;

    public StartMenu() : this(null) { }

    public StartMenu(IAppEnvironment? appEnv)
    {
        InitializeComponent();
        _appEnv = appEnv;
        BuildStaticSubmenus();
    }

    public bool IsOpen => MenuPopup.IsOpen;

    /// <summary>The menu's popup, exposed for host wiring and headless render tests.</summary>
    public Avalonia.Controls.Primitives.Popup MenuPopupControl => MenuPopup;

    /// <summary>Opens the menu above the Start button and readies the Programs cascade.</summary>
    public async Task OpenAsync(Control placementTarget)
    {
        MenuPopup.PlacementTarget = placementTarget;
        MenuPopup.IsOpen = true;
        // Populate Programs now so its cascade is ready before the pointer reaches it,
        // rather than racing a hover-triggered load.
        await LoadProgramsAsync();
    }

    /// <summary>Closes the menu; the Menu's own cascade popups close with it.</summary>
    public void Close() => MenuPopup.IsOpen = false;

    // ── Cascading groups ───────────────────────────────────────────────

    private void BuildStaticSubmenus()
    {
        // Programs: placeholder so the cascade arrow shows before enumeration completes;
        // replaced by the real list on first open (LoadProgramsAsync).
        ProgramsItem.Items.Add(Disabled("(Loading…)"));

        // Documents: recent-documents list (empty for now — no MRU tracking yet).
        DocumentsItem.Items.Add(Disabled("(No recent documents)"));

        AddLeaf(SettingsItem, "Control Panel", () => { });
        AddLeaf(SettingsItem, "Network and Dial-up Connections", () => { });
        AddLeaf(SettingsItem, "Printers", () => { });
        AddLeaf(SettingsItem, "Taskbar and Start Menu…", () => { });

        AddLeaf(SearchItem, "For Files or Folders…", () => { });
        AddLeaf(SearchItem, "On the Internet…", () => { });
    }

    private async Task LoadProgramsAsync()
    {
        if (_programsLoaded || _appEnv is null) return;
        _programsLoaded = true;

        try
        {
            var apps = await _appEnv.EnumerateInstalledAppsAsync();
            ProgramsItem.Items.Clear();
            if (apps.Count == 0)
            {
                ProgramsItem.Items.Add(Disabled("(No programs found)"));
                return;
            }
            foreach (var app in apps)
            {
                var id = app.AppId; // capture per iteration
                AddLeaf(ProgramsItem, app.DisplayName, () => Launch(id));
            }
        }
        catch
        {
            ProgramsItem.Items.Clear();
            ProgramsItem.Items.Add(Disabled("(No programs found)"));
        }
    }

    private async void Launch(string appId)
    {
        if (_appEnv is not null)
        {
            try { await _appEnv.LaunchAsync(appId); }
            catch { /* best effort */ }
        }
        Close();
    }

    // ── Leaf handlers (XAML-wired) ─────────────────────────────────────

    private void OnHelpClick(object? sender, RoutedEventArgs e) => Close();
    private void OnRunClick(object? sender, RoutedEventArgs e) => Close();
    private void OnLogOffClick(object? sender, RoutedEventArgs e) => Close();
    private void OnShutDownClick(object? sender, RoutedEventArgs e) => Close();

    // ── Helpers ────────────────────────────────────────────────────────

    private void AddLeaf(MenuItem parent, string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => { action(); Close(); };
        parent.Items.Add(item);
    }

    private static MenuItem Disabled(string text) => new() { Header = text, IsEnabled = false };
}
