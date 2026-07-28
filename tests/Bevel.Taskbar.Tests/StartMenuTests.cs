using System.Linq;
using Avalonia.Controls;
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
    public void Dead_items_are_hidden_and_control_panel_opens_settings()
    {
        var opened = 0;
        var menu = new StartMenu(null, openSettings: () => opened++);

        // Capability-less leaves are hidden, not shown as dead no-ops (bevel-x6pv).
        Assert.False(menu.FindControl<MenuItem>("SearchItem")!.IsVisible);
        Assert.False(menu.FindControl<MenuItem>("HelpItem")!.IsVisible);
        Assert.False(menu.FindControl<MenuItem>("RunItem")!.IsVisible);

        // Settings keeps only the two entries that map to a real surface (Network/Printers dropped).
        var settings = menu.FindControl<MenuItem>("SettingsItem")!;
        var labels = settings.Items.OfType<MenuItem>().Select(m => (string?)m.Header).ToArray();
        Assert.Equal(new[] { "Control Panel", "Taskbar and Start Menu…" }, labels);

        // Control Panel now opens Bevel Settings instead of doing nothing.
        var cp = settings.Items.OfType<MenuItem>().First(m => (string?)m.Header == "Control Panel");
        cp.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(1, opened);
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
        using var model = new ShellModel(null, null, null);
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
    public async Task Bound_programs_shows_loading_then_binds_when_enumeration_completes()
    {
        var appEnv = new StubAppEnvironment(new InstalledApp("com.a", "Alpha", null));
        using var model = new ShellModel(null, appEnv, null);
        var menu = new StartMenu(null, null, programs: new StartMenuViewModel(model));
        var programsItem = menu.FindControl<MenuItem>("ProgramsItem");
        Assert.NotNull(programsItem);

        // Before Start(): not loaded and empty → loading affordance, not "(No programs found)".
        Assert.Equal("(Loading…)", Assert.Single(programsItem!.Items.OfType<MenuItem>()).Header);

        model.Start();
        for (var i = 0; i < 50 && model.Programs.Count < 1; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();

        // Populated → cascade binds the live reconciled collection; the placeholder is gone.
        Assert.Same(model.Programs, programsItem.ItemsSource);
        Assert.Empty(programsItem.Items.OfType<MenuItem>());
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
}