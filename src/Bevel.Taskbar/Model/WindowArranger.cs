using Bevel.Pal.Abstractions;

namespace Bevel.Taskbar;

/// <summary>Pure layout math for the bar's Cascade/Tile commands (bevel-apm): given a work area and a
/// window count, produce one rect per window in the same coordinate space <see cref="PalRect"/> uses
/// everywhere else. No AppKit, no I/O — unit-testable headless.</summary>
public static class WindowArranger
{
    /// <summary>Classic cascade: each window offset by a fixed step from the previous, wrapped across
    /// rows so every rect stays inside the area.</summary>
    public static IReadOnlyList<PalRect> Cascade(PalRect area, int count)
    {
        var rects = new List<PalRect>(Math.Max(0, count));
        if (count <= 0) return rects;

        const int step = 28;
        var w = Math.Min(Math.Max(320, area.Width / 2), area.Width);
        var h = Math.Min(Math.Max(240, area.Height * 2 / 3), area.Height);
        // Offset steps that fit across before wrapping to the next row.
        var columns = Math.Max(1, (area.Width - w) / step + 1);
        for (var i = 0; i < count; i++)
        {
            var x = area.X + (i % columns) * step;
            var y = area.Y + (i / columns) * step;
            if (y + h > area.Y + area.Height) y = area.Y + area.Height - h; // keep the last row inside
            rects.Add(new PalRect(x, y, w, h));
        }
        return rects;
    }

    /// <summary>"Tile Vertically": equal columns side by side, splitting the area's width with no gap
    /// or overlap.</summary>
    public static IReadOnlyList<PalRect> TileVertically(PalRect area, int count)
    {
        var rects = new List<PalRect>(Math.Max(0, count));
        if (count <= 0) return rects;
        var w = area.Width / count;
        for (var i = 0; i < count; i++)
            rects.Add(new PalRect(area.X + i * w, area.Y, w, area.Height));
        return rects;
    }

    /// <summary>"Tile Horizontally": equal rows stacked, splitting the area's height with no gap or
    /// overlap.</summary>
    public static IReadOnlyList<PalRect> TileHorizontally(PalRect area, int count)
    {
        var rects = new List<PalRect>(Math.Max(0, count));
        if (count <= 0) return rects;
        var h = area.Height / count;
        for (var i = 0; i < count; i++)
            rects.Add(new PalRect(area.X, area.Y + i * h, area.Width, h));
        return rects;
    }
}
