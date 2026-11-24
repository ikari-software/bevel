using Bevel.Ipc.V1;
using Bevel.Pal.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>
/// What the macOS window manager does when its helper stream comes back (bevel-35r9, the window
/// half; the tray half is bevel-voqo). The stream's snapshot burst only upserts, so a window that
/// closed while the stream was down would stay in the core's projection — and on the taskbar —
/// for good; one list per resubscribe takes it back. No helper needed: the tests drive the same
/// two methods the stream loop does, with the list supplied by the test.
/// </summary>
public sealed class WindowResubscribeReconcileTests
{
    private static ForeignWindow Win(string id)
        => new(new ForeignWindowId(id), id.ToUpperInvariant(), AppId: null, IsMinimized: false,
            IsFocused: false, Bounds: default);

    private static (MacOSWindowManager wm, List<string> log, HelperLifecycle lifecycle) NewManager()
    {
        var lifecycle = new HelperLifecycle(NullLogger<HelperLifecycle>.Instance);
        var wm = new MacOSWindowManager(lifecycle, NullLogger<MacOSWindowManager>.Instance);
        var log = new List<string>();
        wm.WindowOpened += (_, w) => log.Add("+" + w.Id.Value);
        wm.WindowClosed += (_, w) => log.Add("-" + w.Id.Value);
        wm.WindowChanged += (_, w) => log.Add("~" + w.Id.Value);
        return (wm, log, lifecycle);
    }

    private static Func<CancellationToken, ValueTask<IReadOnlyList<ForeignWindow>?>> Lists(params string[] ids)
        => _ => ValueTask.FromResult<IReadOnlyList<ForeignWindow>?>(ids.Select(Win).ToList());

    [Fact]
    public async Task A_window_that_vanished_while_the_stream_was_down_is_closed_on_resubscribe()
    {
        var (wm, log, lifecycle) = NewManager();
        using var _ = lifecycle; using var __ = wm;
        wm.Apply(WindowChange.Types.Kind.Snapshot, Win("a"));
        wm.Apply(WindowChange.Types.Kind.Opened, Win("b"));
        wm.Apply(WindowChange.Types.Kind.TitleChanged, Win("c"));
        log.Clear();

        await wm.ReconcileAfterResubscribeAsync(Lists("b"), CancellationToken.None);

        Assert.Equal(new[] { "-a", "-c" }, log.OrderBy(x => x, StringComparer.Ordinal).ToArray());

        // Each is taken back once: the same list a second time closes nothing more.
        log.Clear();
        await wm.ReconcileAfterResubscribeAsync(Lists("b"), CancellationToken.None);
        Assert.Empty(log);
    }

    [Fact]
    public async Task A_helper_that_cannot_be_asked_closes_nothing()
    {
        var (wm, log, lifecycle) = NewManager();
        using var _ = lifecycle; using var __ = wm;
        wm.Apply(WindowChange.Types.Kind.Opened, Win("a"));
        log.Clear();

        await wm.ReconcileAfterResubscribeAsync(
            _ => ValueTask.FromResult<IReadOnlyList<ForeignWindow>?>(null), CancellationToken.None);

        Assert.Empty(log); // "no answer" is not "an empty window list"
    }

    [Fact]
    public async Task An_answered_empty_list_closes_everything_that_was_raised()
    {
        var (wm, log, lifecycle) = NewManager();
        using var _ = lifecycle; using var __ = wm;
        wm.Apply(WindowChange.Types.Kind.Opened, Win("a"));
        log.Clear();

        await wm.ReconcileAfterResubscribeAsync(Lists(), CancellationToken.None);

        Assert.Equal(new[] { "-a" }, log.ToArray());
    }

    [Fact]
    public async Task The_first_subscribe_never_lists()
    {
        var (wm, _, lifecycle) = NewManager();
        using var _ = lifecycle; using var __ = wm;

        var called = false;
        await wm.ReconcileAfterResubscribeAsync(_ =>
        {
            called = true;
            return ValueTask.FromResult<IReadOnlyList<ForeignWindow>?>(null);
        }, CancellationToken.None);

        Assert.False(called); // nothing raised yet, nothing to take back
    }
}
