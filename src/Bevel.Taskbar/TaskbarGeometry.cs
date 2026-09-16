using Avalonia;
using Bevel.Pal.Abstractions;

namespace Bevel.Taskbar;

/// <summary>
/// Device-pixel vs DIP conversion for parking the bar on the primary display's bottom edge
/// (bevel-h0sr). Avalonia <see cref="Screen.Bounds"/> and <see cref="PixelPoint"/> are physical
/// pixels; <c>Window.Width</c>/<c>Height</c> are device-independent. Mixing them (subtracting a
/// 30 DIP height from a pixel Bounds, assigning pixel Width to DIP Width) parks the bar at the
/// wrong Y on any scale ≠ 1 and is the portable half of "sits too high / too low".
///
/// The work-area band fed to <c>WorkAreaMitigator</c> must use the SAME space as
/// <c>IWindowManager</c> bounds: physical pixels on Windows (<c>GetWindowRect</c>), points on
/// macOS (CGWindowList). Avalonia <see cref="Screen.Bounds"/> is always physical.
/// </summary>
internal static class TaskbarGeometry
{
    public static PixelPoint BottomLeft(PixelRect bounds, double scale, int heightDip)
    {
        var s = scale <= 0 ? 1.0 : scale;
        var heightPx = (int)Math.Round(heightDip * s);
        return new PixelPoint(bounds.X, bounds.Y + bounds.Height - heightPx);
    }

    public static double WidthDip(PixelRect bounds, double scale)
    {
        var s = scale <= 0 ? 1.0 : scale;
        return bounds.Width / s;
    }

    /// <param name="windowManagerUsesPhysicalPixels">
    /// True on Windows (PER_MONITOR_AWARE_V2 <c>GetWindowRect</c>). False on macOS, where the
    /// helper reports points and the band must be divided by <paramref name="scale"/>.
    /// </param>
    public static PalRect WorkAreaBand(PixelRect bounds, double scale, int heightDip, bool windowManagerUsesPhysicalPixels)
    {
        var s = scale <= 0 ? 1.0 : scale;
        if (windowManagerUsesPhysicalPixels)
        {
            var heightPx = (int)Math.Round(heightDip * s);
            return new PalRect(bounds.X, bounds.Y + bounds.Height - heightPx, bounds.Width, heightPx);
        }

        var x = bounds.X / s;
        var y = bounds.Y / s;
        var w = bounds.Width / s;
        var h = bounds.Height / s;
        return new PalRect(
            (int)Math.Round(x),
            (int)Math.Round(y + h - heightDip),
            (int)Math.Round(w),
            heightDip);
    }
}
