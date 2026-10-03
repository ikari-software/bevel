using Bevel.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Type-to-search in the Start menu (bevel-cezo): typing with the menu open filters the program index in
/// BOTH layouts, Enter launches the top hit, Backspace widens, Escape clears before it closes, and the
/// arrow keys are left alone. Theme-mutating (the Luna case), so it shares the taskbar theme collection.
/// </summary>
[Collection("TaskbarTheme")]
public class StartMenuSearchTests
{
    // ── Pure ranking / query logic ─────────────────────────────────────

    [Fact]
    public void Filter_accumulates_typed_text_and_ignores_control_characters()
    {
        var f = new StartMenuFilter();
        Assert.False(f.IsActive);

        Assert.True(f.Append("c"));
        Assert.True(f.Append("a"));
        Assert.Equal("ca", f.Query);
        Assert.True(f.IsActive);

        Assert.False(f.Append(""));   // Escape arrives as a key, never as search text
        Assert.False(f.Append("\r"));
        Assert.Equal("ca", f.Query);

        Assert.True(f.Backspace());
        Assert.Equal("c", f.Query);
        Assert.True(f.Backspace());
        Assert.False(f.IsActive);
        Assert.False(f.Backspace());        // nothing left to delete
    }

    [Fact]
    public void Filter_ignores_a_leading_space_but_keeps_inner_ones()
    {
        var f = new StartMenuFilter();
        Assert.False(f.Append(" "));        // a stray Space bar must not open a search for " "
        Assert.False(f.IsActive);

        f.Append("a");
        Assert.True(f.Append(" "));
        Assert.True(f.Append("b"));
        Assert.Equal("a b", f.Query);
    }

    [Fact]
    public void Ranking_puts_name_prefixes_first_then_word_starts_then_substrings()
    {
        string[] names = ["Notepad", "Calculator", "Visual Studio Code", "Xcode", "Calendar"];

        // "cal" → both prefix matches, in input order; nothing else contains it.
        Assert.Equal(["Calculator", "Calendar"], StartMenuFilter.Rank(names, static s => s, "cal"));

        // "code" → the word-start hit beats the mid-word one ("X*code*").
        Assert.Equal(["Visual Studio Code", "Xcode"], StartMenuFilter.Rank(names, static s => s, "code"));

        // Case-insensitive, and a non-match yields nothing.
        Assert.Equal(["Notepad"], StartMenuFilter.Rank(names, static s => s, "NOTE"));
        Assert.Empty(StartMenuFilter.Rank(names, static s => s, "zzz"));
        Assert.Empty(StartMenuFilter.Rank(names, static s => s, ""));
    }

    // ── Classic (Win2000) layout ───────────────────────────────────────

    [AvaloniaFact]
    public async Task Typing_filters_the_classic_programs_cascade_and_opens_it()
    {
        using var h = await Harness.OpenAsync();

        Type(h.Menu, "c");
        Type(h.Menu, "a");
        Type(h.Menu, "l");

        Assert.True(h.Menu.IsSearchActive);
        Assert.Equal("cal", h.Menu.SearchQuery);
        Assert.Equal(["Calculator", "Calendar"], h.Menu.SearchResults.Select(p => p.DisplayName));

        var programs = h.Menu.FindControl<MenuItem>("ProgramsItem")!;
        Assert.Same(h.Menu.SearchResults, programs.ItemsSource);
        Assert.True(programs.IsSubMenuOpen);   // the results are on screen without a click

        // The search strip shows the live query + match count for the classic layout only.
        Assert.True(h.Menu.FindControl<Control>("ClassicSearchStrip")!.IsVisible);
        Assert.False(h.Menu.FindControl<Control>("LunaSearchStrip")!.IsVisible);
        Assert.Equal("cal", h.Menu.FindControl<TextBlock>("ClassicSearchText")!.Text);
        Assert.Equal("2 matches", h.Menu.FindControl<TextBlock>("ClassicSearchCount")!.Text);
    }

    [AvaloniaFact]
    public async Task A_query_with_no_hits_shows_a_disabled_no_matches_row()
    {
        using var h = await Harness.OpenAsync();

        Type(h.Menu, "zzz");

        Assert.Empty(h.Menu.SearchResults);
        var programs = h.Menu.FindControl<MenuItem>("ProgramsItem")!;
        var row = Assert.Single(programs.Items.OfType<MenuItem>());
        Assert.Equal("(No matches)", row.Header);
        Assert.False(row.IsEnabled);
        Assert.Equal("no matches", h.Menu.FindControl<TextBlock>("ClassicSearchCount")!.Text);
    }

    [AvaloniaFact]
    public async Task Backspace_widens_the_query_and_emptying_it_restores_the_full_cascade()
    {
        using var h = await Harness.OpenAsync();
        var programs = h.Menu.FindControl<MenuItem>("ProgramsItem")!;

        Type(h.Menu, "calc");
        Assert.Single(h.Menu.SearchResults);

        Press(h.Menu, Key.Back);
        Assert.Equal("cal", h.Menu.SearchQuery);
        Assert.Equal(2, h.Menu.SearchResults.Count);

        Press(h.Menu, Key.Back);
        Press(h.Menu, Key.Back);
        Press(h.Menu, Key.Back);
        Assert.False(h.Menu.IsSearchActive);
        Assert.Same(h.Model.Programs, programs.ItemsSource);            // back to the whole index
        Assert.False(h.Menu.FindControl<Control>("ClassicSearchStrip")!.IsVisible);
        Assert.True(h.Menu.IsOpen);                                     // clearing never closes the menu
    }

    [AvaloniaFact]
    public async Task Enter_launches_the_top_match_and_closes_the_menu()
    {
        using var h = await Harness.OpenAsync();
        var launched = new List<string>();
        foreach (var p in h.Model.Programs)
            p.Launched += () => launched.Add(p.DisplayName);

        Type(h.Menu, "cale");
        var top = Assert.Single(h.Menu.SearchResults).DisplayName;
        Assert.Equal("Calendar", top);

        Press(h.Menu, Key.Enter);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([top], launched);
        Assert.False(h.Menu.IsOpen);
        Assert.False(h.Menu.IsSearchActive);
    }

    [AvaloniaFact]
    public async Task Enter_on_an_arrowed_to_result_row_leaves_the_rows_own_activation_alone()
    {
        using var h = await Harness.OpenAsync();
        Type(h.Menu, "cal");
        Assert.Equal(2, h.Menu.SearchResults.Count);

        // Enter pressed with focus on the menu at large → the search launches the top hit.
        Assert.True(h.Menu.EnterShouldLaunchTopMatch(PopupRoot(h.Menu)));

        // …but with focus arrowed onto the SECOND match, the row's own activation must win, or Enter would
        // silently launch a different app than the highlighted one.
        var secondRow = new MenuItem { DataContext = h.Menu.SearchResults[1] };
        Assert.False(h.Menu.EnterShouldLaunchTopMatch(secondRow));

        // A row that is NOT part of the result set (a leaf like Run…) is no reason to skip the top hit.
        var leaf = new MenuItem { Header = "Run…" };
        Assert.True(h.Menu.EnterShouldLaunchTopMatch(leaf));
    }

    [AvaloniaFact]
    public async Task Escape_clears_the_filter_first_and_only_then_closes()
    {
        using var h = await Harness.OpenAsync();
        Type(h.Menu, "cal");

        // First Escape: the filter goes, the menu stays. (ClearSearchIfActive is the seam the taskbar's
        // own Escape handler uses, so both entry points agree on the two-step.)
        Press(h.Menu, Key.Escape);
        Assert.False(h.Menu.IsSearchActive);
        Assert.True(h.Menu.IsOpen);

        // Second Escape: nothing left to clear, so the host closes the menu.
        Assert.False(h.Menu.ClearSearchIfActive());
    }

    [AvaloniaFact]
    public async Task Arrow_keys_are_never_swallowed_by_the_search()
    {
        using var h = await Harness.OpenAsync();
        Type(h.Menu, "cal");

        foreach (var key in new[] { Key.Down, Key.Up, Key.Left, Key.Right, Key.Home, Key.End })
        {
            var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = PopupRoot(h.Menu) };
            PopupRoot(h.Menu).RaiseEvent(args);
            Assert.False(args.Handled);   // menu navigation still owns them
        }
        Assert.True(h.Menu.IsSearchActive);
    }

    [AvaloniaFact]
    public async Task Reopening_the_menu_starts_unfiltered()
    {
        using var h = await Harness.OpenAsync();
        Type(h.Menu, "cal");
        Assert.True(h.Menu.IsSearchActive);

        h.Menu.Close();
        Dispatcher.UIThread.RunJobs();
        await h.Menu.OpenAsync(h.Button);
        Dispatcher.UIThread.RunJobs();

        Assert.False(h.Menu.IsSearchActive);
        Assert.Empty(h.Menu.SearchResults);
    }

    [AvaloniaFact]
    public async Task Search_listens_on_the_popups_own_root_not_on_the_usercontrol()
    {
        using var h = await Harness.OpenAsync();

        // Keystrokes land on whichever menu row holds focus, and that row lives in the popup's separate
        // visual tree / TopLevel — a handler on the UserControl that owns the Popup would never see them.
        // This is the whole reason typing did nothing before (bevel-cezo), so pin the attachment point.
        Assert.Same(h.Menu.MenuPopupControl.Child, PopupRoot(h.Menu));

        // Typing there — where every in-menu keystroke routes — drives the filter.
        Type(h.Menu, "note");
        Assert.Equal(["Notepad"], h.Menu.SearchResults.Select(p => p.DisplayName));
    }

    // ── Luna two-column layout ─────────────────────────────────────────

    [AvaloniaFact]
    public async Task Typing_filters_the_luna_pinned_column()
    {
        try
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Blue2001);
            using var h = await Harness.OpenAsync();

            var pinned = h.Menu.FindControl<ItemsControl>("LunaPinned")!;
            Assert.Same(h.Model.FrequentPrograms, pinned.ItemsSource);   // curated list before any typing

            Type(h.Menu, "cal");

            Assert.Same(h.Menu.SearchResults, pinned.ItemsSource);       // …the ranked results while typing
            Assert.Equal(["Calculator", "Calendar"], h.Menu.SearchResults.Select(p => p.DisplayName));
            Assert.True(h.Menu.FindControl<Control>("LunaSearchStrip")!.IsVisible);
            Assert.False(h.Menu.FindControl<Control>("ClassicSearchStrip")!.IsVisible);
            Assert.Equal("cal", h.Menu.FindControl<TextBlock>("LunaSearchText")!.Text);
            Assert.Equal("2 matches", h.Menu.FindControl<TextBlock>("LunaSearchCount")!.Text);

            Press(h.Menu, Key.Escape);
            Assert.Same(h.Model.FrequentPrograms, pinned.ItemsSource);   // …and back to curated on clear
            Assert.False(h.Menu.FindControl<Control>("LunaSearchStrip")!.IsVisible);
        }
        finally { Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999); }
    }

    [AvaloniaFact]
    public async Task Enter_launches_the_top_match_in_the_luna_layout_too()
    {
        try
        {
            Bevel.UI.ThemeService.Apply(ThemeIds.Blue2001);
            using var h = await Harness.OpenAsync();
            var launched = new List<string>();
            foreach (var p in h.Model.Programs)
                p.Launched += () => launched.Add(p.DisplayName);

            Type(h.Menu, "note");
            Press(h.Menu, Key.Enter);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(["Notepad"], launched);
            Assert.False(h.Menu.IsOpen);
        }
        finally { Bevel.UI.ThemeService.Apply(ThemeIds.Industrial1999); }
    }

    // ── Plumbing ───────────────────────────────────────────────────────

    private static Control PopupRoot(StartMenu menu) => menu.FindControl<Panel>("PopupRoot")!;

    /// <summary>Feeds real text input at the popup root, exactly as the platform would once a menu row holds
    /// focus.</summary>
    private static void Type(StartMenu menu, string text)
    {
        var root = PopupRoot(menu);
        root.RaiseEvent(new TextInputEventArgs
        {
            RoutedEvent = InputElement.TextInputEvent,
            Text = text,
            Source = root,
        });
    }

    private static void Press(StartMenu menu, Key key)
    {
        var root = PopupRoot(menu);
        root.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = root });
    }

    /// <summary>An opened Start menu over a populated program index, hosted in a window so the popup has a
    /// real placement target (the popup tree only exists once open).</summary>
    private sealed class Harness : IDisposable
    {
        public required ShellModel Model { get; init; }
        public required StartMenu Menu { get; init; }
        public required Button Button { get; init; }
        public Window? Host { get; init; }

        public static async Task<Harness> OpenAsync()
        {
            var appEnv = new StubAppEnvironment(
                new InstalledApp("com.calc", "Calculator", null),
                new InstalledApp("com.cale", "Calendar", null),
                new InstalledApp("com.note", "Notepad", null),
                new InstalledApp("com.code", "Visual Studio Code", null));
            var model = new ShellModel(null, appEnv, null, usage: TestUsage.Scratch());
            model.Start();
            for (var i = 0; i < 100 && model.Programs.Count < 4; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(4, model.Programs.Count);

            var menu = new StartMenu(null, null, programs: new StartMenuViewModel(model));
            var button = new Button { Content = "Start" };
            var host = new Window { SystemDecorations = SystemDecorations.None, Content = button };
            host.Show();
            Dispatcher.UIThread.RunJobs();
            await menu.OpenAsync(button);
            Dispatcher.UIThread.RunJobs();

            return new Harness { Model = model, Menu = menu, Button = button, Host = host };
        }

        public void Dispose()
        {
            Menu.Close();
            Host?.Close();
            Model.Dispose();
        }
    }
}
