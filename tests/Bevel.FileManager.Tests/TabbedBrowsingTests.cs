using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.FileManager;
using Bevel.FileManager.Components;
using Bevel.FileManager.FileOperations;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// Headless interaction tests for tabbed browsing (bevel-6j9): New Tab, switching tabs (rebinding
/// the single shared ItemView to the target tab's controller and directory), closing a tab, and
/// the last-tab guard. Same reflection-driven approach as FeatureInteractionTests — the window's
/// tab bookkeeping (_tabs/_activeTab/TabSession) is private, so tests drive it through the small
/// public surface (NewTab/TabCount/ActiveTabIndex/SwitchToTab/CloseTabAt) plus reflection for the
/// one piece of internal state (the active FileManagerController) that has no public getter.
/// </summary>
public sealed class TabbedBrowsingTests : IDisposable
{
    private readonly string _dir;
    private readonly string _sub;

    public TabbedBrowsingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"bevel-tabs-{Guid.NewGuid():N}");
        _sub = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(_sub);
        File.WriteAllText(Path.Combine(_dir, "apple.txt"), "a");
        File.WriteAllText(Path.Combine(_sub, "banana.txt"), "b");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ── reflection helpers (mirrors FeatureInteractionTests) ──────────────
    private const BindingFlags NI = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
    private static object? Field(object o, string name) => o.GetType().GetField(name, NI)!.GetValue(o);
    private static object? Prop(object o, string name) => o.GetType().GetProperty(name, NI)!.GetValue(o);

    private static (FileManagerWindow win, FileManagerController controller, VfsRoot vfs) BuildWindow(string dir)
    {
        var vfs = new VfsRoot();
        vfs.Register(new LocalFsProvider());
        var controller = new FileManagerController(vfs, new FileOperationService(vfs, new DefaultConflictHandler()));
        var win = new FileManagerWindow();
        win.SetVfsRoot(vfs);
        win.SetController(controller);
        win.SetSearchService(new SearchService(vfs));
        controller.NavigateTo(new VfsPath("file", dir));
        Pump();
        return (win, controller, vfs);
    }

    private static void Pump(int cycles = 10)
    {
        for (var i = 0; i < cycles; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
    }

    private static void WaitUntil(Func<bool> cond, int ms = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms && !cond()) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The window's currently-attached controller — kept in sync with the active tab.</summary>
    private static FileManagerController ActiveController(FileManagerWindow win)
        => (FileManagerController)Field(win, "_controller")!;

    private static string[] Rows(FileManagerWindow win)
    {
        var iv = (ItemView)Field(win, "ItemView")!;
        return (iv.ItemsControl.ItemsSource as IEnumerable)?
            .Cast<ItemViewModel>().Select(v => v.DisplayName).ToArray() ?? Array.Empty<string>();
    }

    private static string? TabHeader(FileManagerWindow win, int index)
    {
        var tabs = (IList)Field(win, "_tabs")!;
        var session = tabs[index]!;
        var stripId = (Guid)Prop(session, "StripId")!;
        var tabStrip = (TabStrip)Field(win, "TabStrip")!;
        return tabStrip.GetHeader(stripId);
    }

    // ── New Tab ────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public void NewTab_adds_a_tab_and_makes_it_active_at_the_current_directory()
    {
        var (win, _, _) = BuildWindow(_dir);
        Assert.Equal(1, win.TabCount);
        Assert.Equal(0, win.ActiveTabIndex);

        win.NewTab();

        Assert.Equal(2, win.TabCount);
        Assert.Equal(1, win.ActiveTabIndex);
        Assert.Equal(_dir, ActiveController(win).CurrentDirectory.Value); // opened at the prior tab's dir
    }

    [AvaloniaFact]
    public void With_one_tab_a_navigation_behaves_like_the_pre_tabs_window()
    {
        var (win, controller, _) = BuildWindow(_dir);
        WaitUntil(() => Rows(win).Contains("apple.txt"));

        controller.NavigateTo(new VfsPath("file", _sub));
        WaitUntil(() => Rows(win).Contains("banana.txt"));

        Assert.Equal(1, win.TabCount);
        Assert.Contains("banana.txt", Rows(win));
        Assert.Equal("sub", TabHeader(win, 0)); // label tracks the folder name
    }

    // ── Switching ────────────────────────────────────────────────────────
    [AvaloniaFact]
    public void Switching_tabs_rebinds_the_item_view_to_the_target_tabs_controller_and_directory()
    {
        var (win, _, _) = BuildWindow(_dir);
        WaitUntil(() => Rows(win).Contains("apple.txt"));

        win.NewTab(); // tab 1 opens at _dir too, and becomes active
        var tab1Controller = ActiveController(win);
        tab1Controller.NavigateTo(new VfsPath("file", _sub));
        WaitUntil(() => Rows(win).Contains("banana.txt"));

        win.SwitchToTab(0);
        Assert.Equal(0, win.ActiveTabIndex);
        Assert.Equal(_dir, ActiveController(win).CurrentDirectory.Value);
        WaitUntil(() => Rows(win).Contains("apple.txt") && !Rows(win).Contains("banana.txt"));

        win.SwitchToTab(1);
        Assert.Equal(1, win.ActiveTabIndex);
        Assert.Equal(_sub, ActiveController(win).CurrentDirectory.Value);
        WaitUntil(() => Rows(win).Contains("banana.txt") && !Rows(win).Contains("apple.txt"));
    }

    [AvaloniaFact]
    public void Each_tabs_navigation_history_is_independent()
    {
        // BuildWindow's controller already carries SetController's internal Home navigation
        // plus the explicit NavigateTo(_dir), so capture its position BEFORE opening tab 1 —
        // the assertion is that tab 1's navigation leaves tab 0's history untouched, not that
        // tab 0's history is empty.
        var (win, controller, _) = BuildWindow(_dir);
        var tab0PositionBefore = controller.HistoryPosition;

        win.NewTab();
        var tab1Controller = ActiveController(win);
        tab1Controller.NavigateTo(new VfsPath("file", _sub));
        Pump();

        win.SwitchToTab(0);
        var tab0Controller = ActiveController(win);

        Assert.Same(controller, tab0Controller);
        Assert.NotSame(tab0Controller, tab1Controller);
        Assert.Equal(tab0PositionBefore, tab0Controller.HistoryPosition); // untouched by tab 1's nav
        Assert.Equal(_dir, tab0Controller.CurrentDirectory.Value);
        Assert.Equal(_sub, tab1Controller.CurrentDirectory.Value);
    }

    // ── Close ────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public void CloseTab_removes_the_active_tab_and_falls_back_to_a_remaining_one()
    {
        var (win, _, _) = BuildWindow(_dir);
        win.NewTab(); // tab 1 active
        Assert.Equal(2, win.TabCount);

        win.CloseTabAt(1); // closing the active tab

        Assert.Equal(1, win.TabCount);
        Assert.Equal(0, win.ActiveTabIndex);
        Assert.Equal(_dir, ActiveController(win).CurrentDirectory.Value);
    }

    [AvaloniaFact]
    public void CloseTab_on_a_non_active_tab_does_not_disturb_the_active_one()
    {
        var (win, _, _) = BuildWindow(_dir);
        win.NewTab();
        var activeBefore = ActiveController(win);

        win.CloseTabAt(0); // tab 0 (inactive) closes; tab 1 stays active

        Assert.Equal(1, win.TabCount);
        Assert.Same(activeBefore, ActiveController(win));
    }

    // ── Last-tab guard ───────────────────────────────────────────────────
    [AvaloniaFact]
    public void CloseTab_on_the_last_remaining_tab_is_a_noop()
    {
        var (win, _, _) = BuildWindow(_dir);
        Assert.Equal(1, win.TabCount);

        win.CloseTabAt(0);

        Assert.Equal(1, win.TabCount);
        Assert.Equal(0, win.ActiveTabIndex);
    }
}
