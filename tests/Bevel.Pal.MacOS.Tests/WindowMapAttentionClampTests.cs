using Bevel.Ipc.V1;
using Xunit;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>
/// bevel-qb19 review: the wire→DTO mapping of <c>attention_at</c> clamps at the PAL boundary.
/// The proto field is uint64 and the DTO is long — a plain <c>(long)</c> cast wraps values above
/// Int64.MaxValue into negatives, which the VM's strictly-newer (&gt;) compare would treat as
/// ancient. Above the boundary must saturate at <see cref="long.MaxValue"/>.
/// </summary>
public sealed class WindowMapAttentionClampTests
{
    [Fact]
    public void Attention_stamp_passes_through_verbatim_in_range()
    {
        var tw = new TaskbarWindow { WindowId = "cg-1", AttentionAt = 1_763_500_000_123UL };
        Assert.Equal(1_763_500_000_123L, MacOSWindowManager.Map(tw).AttentionAt);
    }

    [Fact]
    public void Attention_stamp_zero_means_none()
    {
        var tw = new TaskbarWindow { WindowId = "cg-1", AttentionAt = 0 };
        Assert.Equal(0, MacOSWindowManager.Map(tw).AttentionAt);
    }

    [Fact]
    public void Attention_stamp_above_Int64_MaxValue_saturates_instead_of_wrapping_negative()
    {
        var tw = new TaskbarWindow { WindowId = "cg-1", AttentionAt = ulong.MaxValue };
        var mapped = MacOSWindowManager.Map(tw).AttentionAt;
        Assert.Equal(long.MaxValue, mapped);
        Assert.True(mapped > 0, "must not wrap negative — a negative stamp would read as ancient to the VM's > compare");

        var justAbove = new TaskbarWindow { WindowId = "cg-1", AttentionAt = (ulong)long.MaxValue + 1 };
        Assert.Equal(long.MaxValue, MacOSWindowManager.Map(justAbove).AttentionAt);

        var atBoundary = new TaskbarWindow { WindowId = "cg-1", AttentionAt = (ulong)long.MaxValue };
        Assert.Equal(long.MaxValue, MacOSWindowManager.Map(atBoundary).AttentionAt);
    }
}
