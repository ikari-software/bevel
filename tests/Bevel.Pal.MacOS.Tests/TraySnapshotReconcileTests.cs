using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>
/// Tests for <see cref="MacOSSystemTrayHost"/> snapshot reconcile (bevel-voqo).
/// </summary>
public sealed class TraySnapshotReconcileTests
{
    [Fact]
    public void SnapshotReconcile_Absent_returns_items_not_in_snapshot()
    {
        var known = new Dictionary<string, TrayItem>
        {
            ["a"] = new TrayItem(new TrayItemId("a"), "A"),
            ["b"] = new TrayItem(new TrayItemId("b"), "B"),
            ["c"] = new TrayItem(new TrayItemId("c"), "C"),
        };

        var snapshot = new[]
        {
            new TrayItem(new TrayItemId("b"), "B"),
            new TrayItem(new TrayItemId("d"), "D"),
        };

        var absent = SnapshotReconcile.Absent(known, snapshot, t => t.Id.Value);

        var absentIds = absent.Select(t => t.Id.Value).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "a", "c" }, absentIds);
    }

    [Fact]
    public void SnapshotReconcile_Absent_returns_empty_when_everything_present()
    {
        var known = new Dictionary<string, TrayItem>
        {
            ["a"] = new TrayItem(new TrayItemId("a"), "A"),
        };

        var snapshot = new[] { new TrayItem(new TrayItemId("a"), "A") };

        var absent = SnapshotReconcile.Absent(known, snapshot, t => t.Id.Value);

        Assert.Empty(absent);
    }

    [Fact]
    public void SnapshotReconcile_Absent_returns_all_when_snapshot_empty()
    {
        var known = new Dictionary<string, TrayItem>
        {
            ["a"] = new TrayItem(new TrayItemId("a"), "A"),
            ["b"] = new TrayItem(new TrayItemId("b"), "B"),
        };

        var absent = SnapshotReconcile.Absent(known, Array.Empty<TrayItem>(), t => t.Id.Value);

        var absentIds = absent.Select(t => t.Id.Value).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "a", "b" }, absentIds);
    }

    [Fact]
    public void SnapshotReconcile_Absent_returns_empty_when_known_empty()
    {
        var known = new Dictionary<string, TrayItem>();
        var snapshot = new[] { new TrayItem(new TrayItemId("a"), "A") };

        var absent = SnapshotReconcile.Absent(known, snapshot, t => t.Id.Value);

        Assert.Empty(absent);
    }
}
