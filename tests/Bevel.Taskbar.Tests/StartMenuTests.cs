using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>Smoke tests for the StartMenu component.</summary>
public class StartMenuTests
{
    [AvaloniaFact]
    public void StartMenu_creates_without_errors()
    {
        var menu = new StartMenu();
        Assert.False(menu.IsOpen);
    }

    [AvaloniaFact]
    public void Formerly_dead_items_are_visible_and_wired_to_real_actions()
    {
        var opened = 0;
        var searched = 0;
        var menu = new StartMenu(null, openSettings: () => opened++, openSearch: () => searched++);

        // Nothing is hidden — the former dead leaves are real now (bevel-x6pv).
        Assert.True(menu.FindControl<MenuItem>("SearchItem")!.IsVisible);
        Assert.True(menu.FindControl<MenuItem>("HelpItem")!.IsVisible);
        Assert.True(menu.FindControl<MenuItem>("RunItem")!.IsVisible);

        // Settings offers Control Panel (OS settings) + a SEPARATE Bevel Settings, then Network/Printers/Taskbar.
        var settings = menu.FindControl<MenuItem>("SettingsItem")!;
        var labels = settings.Items.OfType<MenuItem>().Select(m => (string?)m.Header).ToArray();
        Assert.Equal(
            new[] { "Control Panel", "Bevel Settings", "Network and Dial-up Connections", "Printers", "Taskbar and Start Menu…" },
            labels);

        // "Bevel Settings" opens Bevel's own settings; "Control Panel" opens the OS's (not Bevel's).
        settings.Items.OfType<MenuItem>().First(m => (string?)m.Header == "Bevel Settings")
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(1, opened);

        // Search ▸ "For Files or Folders" drives the openSearch seam (opens Filer in Find mode).
        var search = menu.FindControl<MenuItem>("SearchItem")!;
        search.Items.OfType<MenuItem>().First(m => ((string?)m.Header)!.StartsWith("For Files"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(1, searched);
    }

    [AvaloniaFact]
    public async Task StartMenu_open_and_close_works()
    {
        var button = new Button();
        var window = new Window { Content = button, Width = 400, Height = 100 };
        window.Show();

        var menu = new StartMenu();
        var t = menu.OpenAsync(button);
        Dispatcher.UIThread.RunJobs();
        Assert.True(menu.IsOpen);

        menu.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(menu.IsOpen);
    }

    /// <summary>Raises a real PointerEntered on <paramref name="item"/>, as the mouse would live.</summary>
    private static void HoverEnter(MenuItem item) =>
        item.RaiseEvent(new PointerEventArgs(
            InputElement.PointerEnteredEvent,
            item,
            new Pointer(0, PointerType.Mouse, isPrimary: true),
            item,
            default,
            0,
            new PointerPointProperties(),
            KeyModifiers.None));

    [AvaloniaFact]
    public void Hovering_a_top_level_group_opens_its_cascade_without_a_click()
    {
        var menu = new StartMenu();
        var programs = menu.FindControl<MenuItem>("ProgramsItem")!;
        var settings = menu.FindControl<MenuItem>("SettingsItem")!;
        Assert.False(programs.IsSubMenuOpen);

        // First hover — no prior click — must cascade immediately (the menu-BAR default would not).
        HoverEnter(programs);
        Assert.True(programs.IsSubMenuOpen);

        // Moving to a sibling switches the open cascade (one at a time).
        HoverEnter(settings);
        Assert.True(settings.IsSubMenuOpen);
        Assert.False(programs.IsSubMenuOpen);
    }

    [AvaloniaFact]
    public void Hovering_a_leaf_row_collapses_an_open_cascade()
    {
        var menu = new StartMenu();
        var programs = menu.FindControl<MenuItem>("ProgramsItem")!;
        var help = menu.FindControl<MenuItem>("HelpItem")!;

        HoverEnter(programs);
        Assert.True(programs.IsSubMenuOpen);

        // A leaf has no submenu, so hovering it must close the previously-open group.
        HoverEnter(help);
        Assert.False(programs.IsSubMenuOpen);
        Assert.False(help.IsSubMenuOpen);
    }

    [AvaloniaFact]
    public void Quit_item_requests_graceful_shutdown()
    {
        var requested = false;
        var menu = new StartMenu(null, null, () => requested = true);
        var quitItem = menu.FindControl<MenuItem>("QuitItem");

        Assert.NotNull(quitItem);
        quitItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.True(requested);
        Assert.False(menu.IsOpen);
    }

    [AvaloniaFact]
    public void Restart_item_requests_clean_relaunch()
    {
        var requested = false;
        var menu = new StartMenu(null, null, restart: () => requested = true);
        var restartItem = menu.FindControl<MenuItem>("RestartItem");

        Assert.NotNull(restartItem);
        restartItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.True(requested);
        Assert.False(menu.IsOpen);
    }

    [AvaloniaFact]
    public void Bound_programs_shows_no_programs_found_when_loaded_and_empty()
    {
        // No app environment → the startup enumeration latches loaded immediately with zero apps.
        using var model = new ShellModel(null, null, null, usage: TestUsage.Scratch());
        model.Start();
        Dispatcher.UIThread.RunJobs();

        var menu = new StartMenu(null, null, programs: new StartMenuViewModel(model));
        var programsItem = menu.FindControl<MenuItem>("ProgramsItem");

        Assert.NotNull(programsItem);
        var placeholder = Assert.Single(programsItem!.Items.OfType<MenuItem>());
        Assert.Equal("(No programs found)", placeholder.Header);
        Assert.False(placeholder.IsEnabled);
    }

    [AvaloniaFact]
    public void Bound_programs_shows_loading_then_binds_when_enumeration_completes()
    {
        var appEnv = new StubAppEnvironment(new InstalledApp("com.a", "Alpha", null));
        using var model = new ShellModel(null, appEnv, null, usage: TestUsage.Scratch());
        var menu = new StartMenu(null, null, programs: new StartMenuViewModel(model));
        var programsItem = menu.FindControl<MenuItem>("ProgramsItem");
        Assert.NotNull(programsItem);

        // Before Start(): not loaded and empty → loading affordance, not "(No programs found)".
        Assert.Equal("(Loading…)", Assert.Single(programsItem!.Items.OfType<MenuItem>()).Header);

        model.Start();
        Bevel.TestSupport.Settle.Until(() => model.Programs.Count >= 1, "1 programs to load");
        Dispatcher.UIThread.RunJobs();

        // Populated → cascade binds the live reconciled collection; the placeholder is gone.
        Assert.Same(model.Programs, programsItem.ItemsSource);
        Assert.Empty(programsItem.Items.OfType<MenuItem>());
    }

    // bevel-ujx8(3): an All-Programs row that survives a Programs change must keep its Control
    // instance (no Clear+rebuild dropping scroll/hover state).
    [AvaloniaFact]
    public void A_programs_change_keeps_existing_all_programs_rows()
    {
        var appEnv = new StubAppEnvironment(new InstalledApp("com.a", "Alpha", null));
        using var model = new ShellModel(null, appEnv, null, usage: TestUsage.Scratch());
        model.Start();
        Bevel.TestSupport.Settle.Until(() => model.Programs.Count >= 1, "1 programs to load");

        var menu = new StartMenu(null, null, programs: new StartMenuViewModel(model));
        menu.WireAllProgramsFlyoutForTest();
        Dispatcher.UIThread.RunJobs();
        var list = menu.AllProgramsList!;
        Assert.Single(list.Children);
        var rowA = list.Children[0];

        appEnv.RaiseInstalledAppsChanged(new[]
        {
            new InstalledApp("com.a", "Alpha", null),
            new InstalledApp("com.b", "Beta", null),
        });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, list.Children.Count);
        Assert.Same(rowA, list.Children[0]);   // surviving row kept its Control
    }

    [Fact]
    public void Restart_command_preserves_dotnet_entry_assembly_and_arguments()
    {
        var startInfo = Bevel.App.Program.CreateRestartStartInfo(
            "/usr/local/bin/dotnet",
            "/tmp/Bevel.App.dll",
            ["--pal", "fake"]);

        Assert.Equal("/usr/local/bin/dotnet", startInfo.FileName);
        Assert.Equal(["/tmp/Bevel.App.dll", "--pal", "fake"], startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
    }

    [Fact]
    public void Restart_command_preserves_published_executable_arguments()
    {
        var startInfo = Bevel.App.Program.CreateRestartStartInfo(
            "/Applications/Bevel.app/Contents/MacOS/Bevel",
            "",
            ["--pal", "macos"]);

        Assert.Equal("/Applications/Bevel.app/Contents/MacOS/Bevel", startInfo.FileName);
        Assert.Equal(["--pal", "macos"], startInfo.ArgumentList);
    }

    // ── bevel-4vce: every shell-management item does what its label says ──

    [AvaloniaFact]
    public void Luna_log_off_calls_session_logout_not_restart()
    {
        var kinds = new List<LogoutKind>();
        var restarts = 0;
        var menu = new StartMenu(null, quit: () => { }, restart: () => restarts++,
            session: k => { kinds.Add(k); return Task.CompletedTask; });

        var logOff = menu.FindControl<Button>("Blue2001LogOffButton")!;
        Assert.True(logOff.IsEnabled);
        logOff.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(new[] { LogoutKind.LogOut }, kinds);
        Assert.Equal(0, restarts);   // the old bug: Luna "Log Off" restarted Bevel
    }

    [AvaloniaFact]
    public void Luna_turn_off_opens_the_shut_down_dialog_not_a_direct_quit()
    {
        var quits = 0;
        var menu = new StartMenu(null, quit: () => quits++, session: _ => Task.CompletedTask);
        var opened = 0;
        menu.ShutDownDialogRequested = () => opened++;

        menu.FindControl<Button>("Blue2001TurnOffButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(1, opened);
        Assert.Equal(0, quits);   // Turn Off must go through the dialog, not quit directly
    }

    [AvaloniaFact]
    public void Classic_log_off_and_shut_down_are_wired()
    {
        var kinds = new List<LogoutKind>();
        var menu = new StartMenu(null, session: k => { kinds.Add(k); return Task.CompletedTask; });
        var opened = 0;
        menu.ShutDownDialogRequested = () => opened++;

        menu.FindControl<MenuItem>("LogOffItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(new[] { LogoutKind.LogOut }, kinds);   // was a dead Close()

        menu.FindControl<MenuItem>("ShutDownItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(1, opened);                            // was a dead Close()
    }

    [AvaloniaFact]
    public void Without_a_session_log_off_is_disabled_and_os_choices_are_noops()
    {
        var restarts = 0;
        var menu = new StartMenu(null, restart: () => restarts++);   // no session

        Assert.False(menu.FindControl<MenuItem>("LogOffItem")!.IsEnabled);
        Assert.False(menu.FindControl<Button>("Blue2001LogOffButton")!.IsEnabled);

        menu.HandleShutDownChoice(ShutDownAction.RestartBevel);   // Bevel entries work without a session
        Assert.Equal(1, restarts);
        menu.HandleShutDownChoice(ShutDownAction.ShutDown);       // OS entry: no session → no-op, no throw
        menu.HandleShutDownChoice(null);
        Assert.Equal(1, restarts);
    }
}
