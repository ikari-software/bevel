using System;
using Avalonia;
using Avalonia.Controls;

namespace Bevel.Taskbar;

/// <summary>
/// Lays tray icons into exactly <see cref="Rows"/> rows at their NATURAL widths (unlike a UniformGrid,
/// which sizes every cell to the widest icon — so one wide text status item would inflate them all). Items
/// keep menu-bar order and are split row-major, balancing each row's total width, so a wide icon naturally
/// occupies more of its row ("counts as more than 1× width") rather than forcing uniform squares.
/// </summary>
public sealed class TrayRowsPanel : Panel
{
    public static readonly StyledProperty<int> RowsProperty =
        AvaloniaProperty.Register<TrayRowsPanel, int>(nameof(Rows), 1);

    public int Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    static TrayRowsPanel() => AffectsMeasure<TrayRowsPanel>(RowsProperty);

    private int[] _rowOf = Array.Empty<int>();
    private int _rowsUsed = 1;
    private double _rowHeight;

    /// <summary>Row-major assignment: fill each row to its share of the total width (totalWidth / rows),
    /// advancing only while enough items remain to give every later row at least one. Pure + testable.</summary>
    internal static int[] AssignRows(double[] widths, int rows)
    {
        rows = Math.Max(1, rows);
        var rowOf = new int[widths.Length];
        if (widths.Length == 0) return rowOf;

        double total = 0;
        foreach (var w in widths) total += w;
        var target = total / rows;

        double running = 0;
        var row = 0;
        for (var i = 0; i < widths.Length; i++)
        {
            rowOf[i] = row;
            running += widths[i];
            var itemsAfter = widths.Length - 1 - i;
            var rowsAfter = rows - 1 - row;
            if (row < rows - 1 && running >= target * (row + 1) && itemsAfter >= rowsAfter && rowsAfter > 0)
                row++;
        }
        return rowOf;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var n = Children.Count;
        var widths = new double[n];
        _rowHeight = 0;
        for (var i = 0; i < n; i++)
        {
            Children[i].Measure(Size.Infinity);
            widths[i] = Children[i].DesiredSize.Width;
            _rowHeight = Math.Max(_rowHeight, Children[i].DesiredSize.Height);
        }

        var rows = Math.Max(1, Rows);
        _rowOf = AssignRows(widths, rows);

        var rowW = new double[rows];
        _rowsUsed = 1;
        for (var i = 0; i < n; i++)
        {
            rowW[_rowOf[i]] += widths[i];
            _rowsUsed = Math.Max(_rowsUsed, _rowOf[i] + 1);
        }

        double maxW = 0;
        foreach (var w in rowW) maxW = Math.Max(maxW, w);
        return new Size(maxW, _rowHeight * _rowsUsed);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rows = Math.Max(1, _rowsUsed);
        var x = new double[rows];
        for (var i = 0; i < Children.Count; i++)
        {
            var r = _rowOf.Length > i ? Math.Min(_rowOf[i], rows - 1) : 0;
            var c = Children[i];
            c.Arrange(new Rect(x[r], r * _rowHeight, c.DesiredSize.Width, _rowHeight));
            x[r] += c.DesiredSize.Width;
        }
        return finalSize;
    }
}
