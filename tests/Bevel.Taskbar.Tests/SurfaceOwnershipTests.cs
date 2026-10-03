using Bevel.Taskbar.Components;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-aqr7 Task 11, Review Focus 3: the handshake proves a peer is A component, not WHICH one.
/// Without an ownership check an authenticated component could publish frames into another
/// component's surface slot — cross-component pixel injection.
/// </summary>
public class SurfaceOwnershipTests
{
    [Fact]
    public void An_owner_may_publish_to_its_own_slot()
    {
        var o = new SurfaceOwnership();
        var slot = o.Assign("inst-a", "face");
        Assert.True(o.TryAccept("inst-a", slot));
    }

    [Fact]
    public void A_different_instance_may_not_publish_to_someone_elses_slot()
    {
        var o = new SurfaceOwnership();
        var slotA = o.Assign("inst-a", "face");
        o.Assign("inst-b", "face");
        Assert.False(o.TryAccept("inst-b", slotA));
    }

    [Fact]
    public void An_unassigned_slot_is_refused()
        => Assert.False(new SurfaceOwnership().TryAccept("inst-a", 9999));

    [Fact]
    public void Each_surface_gets_a_distinct_slot()
    {
        var o = new SurfaceOwnership();
        Assert.NotEqual(o.Assign("inst-a", "one"), o.Assign("inst-a", "two"));
    }

    [Fact]
    public void Releasing_an_instance_revokes_all_of_its_slots()
    {
        var o = new SurfaceOwnership();
        var slot = o.Assign("inst-a", "face");
        o.Release("inst-a");
        Assert.False(o.TryAccept("inst-a", slot));
    }

    [Fact]
    public void A_released_slot_is_not_handed_to_the_next_instance()
    {
        var o = new SurfaceOwnership();
        var first = o.Assign("inst-a", "face");
        o.Release("inst-a");
        Assert.NotEqual(first, o.Assign("inst-b", "face"));
    }
}
