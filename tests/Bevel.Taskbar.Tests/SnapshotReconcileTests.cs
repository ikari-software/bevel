using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

public sealed class SnapshotReconcileTests
{
    private static TrayItem Tray(string id) => new(new TrayItemId(id), id);

    [Fact]
    public void Absent_returns_known_items_missing_from_the_snapshot()
    {
        var known = new Dictionary<string, TrayItem>
        {
            ["a"] = Tray("a"), ["b"] = Tray("b"), ["c"] = Tray("c"),
        };

        var absent = SnapshotReconcile.Absent(known, new[] { Tray("b"), Tray("z") }, t => t.Id.Value);

        Assert.Equal(new[] { "a", "c" }, absent.Select(t => t.Id.Value).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Absent_is_empty_when_nothing_is_known_or_everything_is_present()
    {
        var empty = new Dictionary<string, TrayItem>();
        Assert.Empty(SnapshotReconcile.Absent(empty, new[] { Tray("a") }, t => t.Id.Value));

        var known = new Dictionary<string, TrayItem> { ["a"] = Tray("a") };
        Assert.Empty(SnapshotReconcile.Absent(known, new[] { Tray("a") }, t => t.Id.Value));
        Assert.Single(SnapshotReconcile.Absent(known, Array.Empty<TrayItem>(), t => t.Id.Value));
    }
}
