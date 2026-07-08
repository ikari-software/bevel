using Bevel.Core.Vfs;
using Bevel.FileManager.FileOperations;
using Bevel.FileManager.Navigation;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// Tests the History dropdown data seam: NavigationStack's Back/Forward/Entries accessors and
/// GoTo repositioning, plus FileManagerController's HistoryMenu/JumpToHistory wiring. Verifies
/// GoBack/GoForward keep working and that JumpToHistory never duplicates or truncates the stack.
/// </summary>
public sealed class NavigationHistoryTests
{
    private static VfsPath P(string name) => new("file", name);

    // ── NavigationStack ────────────────────────────────────────────────

    [Fact]
    public void Entries_lists_the_full_history_oldest_first()
    {
        var nav = new NavigationStack();
        nav.Push(P("a"));
        nav.Push(P("b"));
        nav.Push(P("c"));

        Assert.Equal(new[] { P("a"), P("b"), P("c") }, nav.Entries);
        Assert.Equal(2, nav.Position);
    }

    [Fact]
    public void Back_is_most_recent_first()
    {
        var nav = new NavigationStack();
        nav.Push(P("a"));
        nav.Push(P("b"));
        nav.Push(P("c"));

        Assert.Equal(new[] { P("b"), P("a") }, nav.Back);
        Assert.Empty(nav.Forward);
    }

    [Fact]
    public void Forward_is_nearest_next_first_after_going_back()
    {
        var nav = new NavigationStack();
        nav.Push(P("a"));
        nav.Push(P("b"));
        nav.Push(P("c"));

        nav.GoBack(); // -> b, position 1
        nav.GoBack(); // -> a, position 0

        Assert.Equal(new[] { P("b"), P("c") }, nav.Forward);
        Assert.Empty(nav.Back);
    }

    [Fact]
    public void PeekAt_returns_the_entry_or_null_out_of_range()
    {
        var nav = new NavigationStack();
        nav.Push(P("a"));
        nav.Push(P("b"));

        Assert.Equal(P("a"), nav.PeekAt(0));
        Assert.Equal(P("b"), nav.PeekAt(1));
        Assert.Null(nav.PeekAt(2));
        Assert.Null(nav.PeekAt(-1));
    }

    [Fact]
    public void GoTo_repositions_without_truncating_forward_history()
    {
        var nav = new NavigationStack();
        nav.Push(P("a"));
        nav.Push(P("b"));
        nav.Push(P("c"));
        nav.GoBack(); // position 1 (b), c still reachable via forward

        var result = nav.GoTo(0); // jump straight to a

        Assert.Equal(P("a"), result);
        Assert.Equal(P("a"), nav.Current);
        Assert.Equal(0, nav.Position);
        // The stack itself is untouched — b and c are both still forward from here.
        Assert.Equal(new[] { P("a"), P("b"), P("c") }, nav.Entries);
        Assert.Equal(new[] { P("b"), P("c") }, nav.Forward);
    }

    [Fact]
    public void GoTo_out_of_range_is_a_no_op()
    {
        var nav = new NavigationStack();
        nav.Push(P("a"));
        nav.Push(P("b"));

        Assert.Null(nav.GoTo(5));
        Assert.Null(nav.GoTo(-1));
        Assert.Equal(1, nav.Position);
        Assert.Equal(P("b"), nav.Current);
    }

    [Fact]
    public void GoBack_and_GoForward_still_work_after_a_GoTo_jump()
    {
        var nav = new NavigationStack();
        nav.Push(P("a"));
        nav.Push(P("b"));
        nav.Push(P("c"));

        nav.GoTo(0); // jump to a, position 0
        Assert.False(nav.CanGoBack);
        Assert.True(nav.CanGoForward);

        var fwd = nav.GoForward();
        Assert.Equal(P("b"), fwd);
        Assert.Equal(1, nav.Position);

        var back = nav.GoBack();
        Assert.Equal(P("a"), back);
        Assert.Equal(0, nav.Position);
    }

    [Fact]
    public void Push_after_a_GoTo_jump_still_clears_forward_entries()
    {
        var nav = new NavigationStack();
        nav.Push(P("a"));
        nav.Push(P("b"));
        nav.Push(P("c"));
        nav.GoTo(0); // jump to a; b, c are forward

        nav.Push(P("d")); // new branch from a

        Assert.Equal(new[] { P("a"), P("d") }, nav.Entries);
        Assert.False(nav.CanGoForward);
    }

    // ── HistoryEntry ───────────────────────────────────────────────────

    [Theory]
    [InlineData("computer", "", "My Computer")]
    [InlineData("file", "", "FILE")]
    public void LabelFor_uses_special_labels_for_scheme_roots(string scheme, string value, string expected)
        => Assert.Equal(expected, HistoryEntry.LabelFor(new VfsPath(scheme, value)));

    [Fact]
    public void LabelFor_uses_the_file_name_for_non_root_paths()
        => Assert.Equal("b", HistoryEntry.LabelFor(P("a/b")));

    // ── FileManagerController wiring ────────────────────────────────────

    private static FileManagerController MakeController()
    {
        var vfs = new VfsRoot();
        vfs.Register(new LocalFsProvider());
        var svc = new FileOperationService(vfs, new StubConflictHandler());
        return new FileManagerController(vfs, svc);
    }

    [Fact]
    public void HistoryMenu_exposes_indexed_labeled_entries_in_visit_order()
    {
        var controller = MakeController();
        controller.NavigateTo(P("a"));
        controller.NavigateTo(P("a/b"));

        var menu = controller.HistoryMenu;

        Assert.Equal(2, menu.Count);
        Assert.Equal(0, menu[0].Index);
        Assert.Equal(P("a"), menu[0].Path);
        Assert.Equal(1, menu[1].Index);
        Assert.Equal(P("a/b"), menu[1].Path);
        Assert.Equal(1, controller.HistoryPosition);
    }

    [Fact]
    public void NavigateTo_the_current_directory_does_not_refire()
    {
        var controller = MakeController();
        var fires = 0;
        controller.CurrentDirectoryChanged += _ => fires++;

        controller.NavigateTo(P("a"));
        controller.NavigateTo(P("a"));   // already here — must NOT start a second (racing) load
        Assert.Equal(1, fires);

        controller.NavigateTo(P("a/b")); // a genuine move still navigates
        Assert.Equal(2, fires);
    }

    [Fact]
    public void BackHistory_and_ForwardHistory_mirror_the_stack()
    {
        var controller = MakeController();
        controller.NavigateTo(P("a"));
        controller.NavigateTo(P("a/b"));
        controller.NavigateTo(P("a/b/c"));
        controller.GoBack();

        Assert.Equal(new[] { P("a") }, controller.BackHistory);
        Assert.Equal(new[] { P("a/b/c") }, controller.ForwardHistory);
    }

    [Fact]
    public void JumpToHistory_repositions_and_raises_arrival_events_without_corrupting_the_stack()
    {
        var controller = MakeController();
        var arrived = new List<VfsPath>();
        var navStateChanges = 0;
        controller.NavigateTo(P("a"));
        controller.NavigateTo(P("a/b"));
        controller.NavigateTo(P("a/b/c"));
        controller.CurrentDirectoryChanged += p => arrived.Add(p);
        controller.NavigationStateChanged += () => navStateChanges++;

        controller.JumpToHistory(0); // jump straight to "a"

        Assert.Equal(P("a"), controller.CurrentDirectory);
        Assert.Equal(new[] { P("a") }, arrived);
        Assert.Equal(1, navStateChanges);
        Assert.True(controller.CanGoForward);
        Assert.False(controller.CanGoBack);
        // The full history is intact — no duplication, no truncation.
        Assert.Equal(new[] { P("a"), P("a/b"), P("a/b/c") }, controller.HistoryMenu.Select(e => e.Path));
    }

    [Fact]
    public void JumpToHistory_out_of_range_does_not_raise_events()
    {
        var controller = MakeController();
        var arrived = new List<VfsPath>();
        controller.NavigateTo(P("a"));
        controller.CurrentDirectoryChanged += p => arrived.Add(p);

        controller.JumpToHistory(99);

        Assert.Empty(arrived);
        Assert.Equal(P("a"), controller.CurrentDirectory);
    }

    [Fact]
    public void JumpToHistory_then_GoBack_still_walks_correctly()
    {
        var controller = MakeController();
        controller.NavigateTo(P("a"));
        controller.NavigateTo(P("a/b"));
        controller.NavigateTo(P("a/b/c"));

        controller.JumpToHistory(1); // jump to "a/b"
        Assert.True(controller.CanGoBack);
        Assert.True(controller.CanGoForward);

        controller.GoBack();
        Assert.Equal(P("a"), controller.CurrentDirectory);

        controller.GoForward();
        Assert.Equal(P("a/b"), controller.CurrentDirectory);

        controller.GoForward();
        Assert.Equal(P("a/b/c"), controller.CurrentDirectory);
    }
}
