using Avalonia;
using Avalonia.Controls;
using Bevel.Core.Components;

namespace Bevel.Taskbar.Components;

/// <summary>
/// Lays out one bar's component instances as a single ordered list. Slack goes to <see
/// cref="ComponentSizing.Greedy"/> children, split by weight — so a spacer is just a greedy child
/// with no content, and centring is an ARRANGEMENT rather than a setting (spec §4.1).
/// </summary>
public class TaskbarComponentsPanel : Panel
{
    public static readonly AttachedProperty<ComponentSizing> SizingProperty =
        AvaloniaProperty.RegisterAttached<TaskbarComponentsPanel, Control, ComponentSizing>(
            "Sizing", ComponentSizing.Content);

    public static readonly AttachedProperty<double> WeightProperty =
        AvaloniaProperty.RegisterAttached<TaskbarComponentsPanel, Control, double>("Weight", 1.0);

    public static void SetSizing(Control c, ComponentSizing v) => c.SetValue(SizingProperty, v);
    public static ComponentSizing GetSizing(Control c) => c.GetValue(SizingProperty);
    public static void SetWeight(Control c, double v) => c.SetValue(WeightProperty, v);
    public static double GetWeight(Control c) => c.GetValue(WeightProperty);

    protected override Size MeasureOverride(Size available)
    {
        var height = 0.0;
        var fixedWidth = 0.0;

        foreach (var child in Children)
        {
            if (GetSizing(child) == ComponentSizing.Greedy)
            {
                // Measured at zero width: a greedy child's width comes from slack, not from content.
                child.Measure(new Size(0, available.Height));
            }
            else
            {
                child.Measure(new Size(double.PositiveInfinity, available.Height));
                fixedWidth += child.DesiredSize.Width;
            }
            height = Math.Max(height, child.DesiredSize.Height);
        }

        var width = double.IsInfinity(available.Width) ? fixedWidth : available.Width;
        return new Size(width, double.IsInfinity(available.Height) ? height : available.Height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var greedy = new List<Control>();
        var totalWeight = 0.0;
        var fixedWidth = 0.0;

        foreach (var child in Children)
        {
            if (GetSizing(child) == ComponentSizing.Greedy)
            {
                greedy.Add(child);
                totalWeight += Math.Max(0, GetWeight(child));
            }
            else fixedWidth += child.DesiredSize.Width;
        }

        // Clamp: content wider than the bar must never produce negative slack.
        var slack = Math.Max(0, final.Width - fixedWidth);

        // All-zero weights (or a spacer-only list) split evenly rather than dividing by zero.
        var even = greedy.Count > 0 && totalWeight <= 0;

        var x = 0.0;
        foreach (var child in Children)
        {
            double w;
            if (GetSizing(child) == ComponentSizing.Greedy)
            {
                w = even
                    ? slack / greedy.Count
                    : slack * (Math.Max(0, GetWeight(child)) / totalWeight);
            }
            else w = child.DesiredSize.Width;

            child.Arrange(new Rect(x, 0, w, final.Height));
            x += w;
        }

        return final;
    }
}
