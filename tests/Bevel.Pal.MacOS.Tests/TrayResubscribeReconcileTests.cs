using Bevel.Pal.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using TrayChange = Bevel.Ipc.V1.TrayChange;
using TrayItem = Bevel.Pal.Abstractions.TrayItem;
using TrayItemId = Bevel.Pal.Abstractions.TrayItemId;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>
/// What the macOS tray host does when its helper stream comes back (bevel-voqo, the tray half of
/// bevel-35r9). The stream's snapshot burst only adds, so an item that quit while the stream was down
/// would stay raised for good; one list per resubscribe takes it back. No helper needed: the tests
/// drive the same two methods the stream loop does, with the list supplied by the test.
/// </summary>
public sealed class TrayResubscribeReconcileTests
{
    private static TrayItem Item(string id) => new(new TrayItemId(id), id.ToUpperInvariant());

    private static (MacOSSystemTrayHost host, List<string> log, HelperLifecycle lifecycle) NewHost()
    {
        var lifecycle = new HelperLifecycle(NullLogger<HelperLifecycle>.Instance);
        var host = new MacOSSystemTrayHost(lifecycle, NullLogger<MacOSSystemTrayHost>.Instance);
        var log = new List<string>();
        host.ItemAdded += (_, i) => log.Add("+" + i.Id.Value);
        host.ItemUpdated += (_, i) => log.Add("~" + i.Id.Value);
        host.ItemRemoved += (_, i) => log.Add("-" + i.Id.Value);
        return (host, log, lifecycle);
    }

    private static Func<CancellationToken, ValueTask<IReadOnlyList<TrayItem>?>> Lists(params string[] ids)
        => _ => ValueTask.FromResult<IReadOnlyList<TrayItem>?>(ids.Select(Item).ToList());

    [Fact]
    public async Task An_item_that_vanished_while_the_stream_was_down_is_removed_on_resubscribe()
    {
        var (host, log, lifecycle) = NewHost();
        using var _ = lifecycle; using var __ = host;
        host.Apply(TrayChange.Types.Kind.Snapshot, Item("a"));
        host.Apply(TrayChange.Types.Kind.Added, Item("b"));
        host.Apply(TrayChange.Types.Kind.Updated, Item("c"));
        log.Clear();

        await host.ReconcileAfterResubscribeAsync(Lists("b"), CancellationToken.None);

        Assert.Equal(new[] { "-a", "-c" }, log.OrderBy(x => x, StringComparer.Ordinal).ToArray());

        // Each is taken back once: the same list a second time removes nothing more.
        log.Clear();
        await host.ReconcileAfterResubscribeAsync(Lists("b"), CancellationToken.None);
        Assert.Empty(log);
    }

    [Fact]
    public async Task A_helper_that_cannot_be_asked_removes_nothing()
    {
        var (host, log, lifecycle) = NewHost();
        using var _ = lifecycle; using var __ = host;
        host.Apply(TrayChange.Types.Kind.Added, Item("a"));
        log.Clear();

        await host.ReconcileAfterResubscribeAsync(
            _ => ValueTask.FromResult<IReadOnlyList<TrayItem>?>(null), CancellationToken.None);

        Assert.Empty(log); // "no answer" is not "an empty tray"
    }

    [Fact]
    public async Task An_answered_empty_list_removes_everything_that_was_raised()
    {
        var (host, log, lifecycle) = NewHost();
        using var _ = lifecycle; using var __ = host;
        host.Apply(TrayChange.Types.Kind.Added, Item("a"));
        log.Clear();

        await host.ReconcileAfterResubscribeAsync(Lists(), CancellationToken.None);

        Assert.Equal(new[] { "-a" }, log.ToArray());
    }

    [Fact]
    public async Task The_first_subscribe_does_not_ask_for_a_list_and_a_removed_item_is_not_removed_twice()
    {
        var (host, log, lifecycle) = NewHost();
        using var _ = lifecycle; using var __ = host;
        var asked = 0;
        Func<CancellationToken, ValueTask<IReadOnlyList<TrayItem>?>> counting = _ =>
        {
            asked++;
            return ValueTask.FromResult<IReadOnlyList<TrayItem>?>(Array.Empty<TrayItem>());
        };

        await host.ReconcileAfterResubscribeAsync(counting, CancellationToken.None);
        Assert.Equal(0, asked); // nothing raised yet, so nothing to take back

        host.Apply(TrayChange.Types.Kind.Added, Item("a"));
        host.Apply(TrayChange.Types.Kind.Removed, Item("a"));
        log.Clear();
        await host.ReconcileAfterResubscribeAsync(counting, CancellationToken.None);
        Assert.Empty(log);
    }

    // The reconcile runs between opening the stream and reading it. A list that fails must not
    // throw into the stream loop (that would drop the subscription), and must not forget what was
    // raised: the next resubscribe still has to be able to take the item back.
    [Fact]
    public async Task A_list_that_fails_removes_nothing_and_keeps_what_was_raised()
    {
        var (host, log, lifecycle) = NewHost();
        using var _ = lifecycle; using var __ = host;
        host.Apply(TrayChange.Types.Kind.Added, Item("a"));
        log.Clear();

        await host.ReconcileAfterResubscribeAsync(
            _ => throw new InvalidOperationException("Helper not connected."), CancellationToken.None);
        Assert.Empty(log);

        await host.ReconcileAfterResubscribeAsync(Lists(), CancellationToken.None);
        Assert.Equal(new[] { "-a" }, log);
    }

    [Fact]
    public async Task Shutdown_during_the_list_still_cancels()
    {
        var (host, _, lifecycle) = NewHost();
        using var _1 = lifecycle; using var _2 = host;
        host.Apply(TrayChange.Types.Kind.Added, Item("a"));
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await host.ReconcileAfterResubscribeAsync(async t =>
            {
                cts.Cancel();
                await Task.Delay(Timeout.Infinite, t);
                return null;
            }, cts.Token));
    }
}
