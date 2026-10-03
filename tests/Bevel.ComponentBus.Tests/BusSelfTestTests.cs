using Bevel.ComponentBus;
using Xunit;

namespace Bevel.ComponentBus.Tests;

/// <summary>
/// bevel-aqr7 Task 1: a real socket round-trip, so an AOT publish surfaces reflection NetMQ needs
/// at CONNECT time rather than at reference time. A bare PackageReference with no reachable call is
/// trimmed away and would make the publish check a false positive.
/// </summary>
public class BusSelfTestTests
{
    [Fact]
    public void Round_trip_carries_bytes_both_ways()
        => Assert.True(BusSelfTest.RoundTrip());
}
