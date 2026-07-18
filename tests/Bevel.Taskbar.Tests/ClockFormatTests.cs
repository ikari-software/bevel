using System;
using System.Globalization;
using Bevel.Taskbar;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Unit coverage for the clock format policy (bevel-cust.clock). Exercises the pure
/// <see cref="ClockWidget.TimeFormat"/> seam across the 12/24h x seconds matrix by formatting a
/// fixed afternoon time and asserting the rendered string, so a regression in the branch logic
/// (e.g. dropping the "tt" for 12-hour, or the ":ss") is caught without a visual tree.
/// </summary>
public class ClockFormatTests
{
    private static string Render(bool h24, bool seconds)
    {
        var t = new DateTime(2020, 1, 1, 14, 30, 45);
        return t.ToString(ClockWidget.TimeFormat(h24, seconds), CultureInfo.InvariantCulture);
    }

    [Fact] public void TwentyFour_hour_no_seconds() => Assert.Equal("14:30", Render(true, false));
    [Fact] public void TwentyFour_hour_with_seconds() => Assert.Equal("14:30:45", Render(true, true));
    [Fact] public void Twelve_hour_no_seconds() => Assert.Equal("2:30 PM", Render(false, false));
    [Fact] public void Twelve_hour_with_seconds() => Assert.Equal("2:30:45 PM", Render(false, true));
}
