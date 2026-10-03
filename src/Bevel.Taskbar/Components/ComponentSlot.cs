using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Bevel.Core.Components;

namespace Bevel.Taskbar.Components;

/// <summary>
/// One instance's place on the bar. An INERT slot renders a placeholder instead of vanishing, so
/// uninstalling and reinstalling a component does not silently reshuffle the bar, and a failed
/// component is visible rather than a gap.
/// </summary>
public sealed class ComponentSlot
{
    public string InstanceId { get; }
    public string TypeId { get; }
    public bool IsInert { get; }
    public Control Content { get; }

    private ComponentSlot(string instanceId, string typeId, bool inert, Control content)
    {
        InstanceId = instanceId;
        TypeId = typeId;
        IsInert = inert;
        Content = content;
    }

    /// <summary>A live slot rendering the component's label primitive value.</summary>
    public static ComponentSlot Live(ComponentInstance inst, ComponentManifest type, ComponentState state)
    {
        var text = state.Values.Count > 0 ? state.Values.First().Value : type.DisplayName;
        var c = new Button
        {
            Content = text,
            Background = null,
            BorderThickness = default,
            Padding = new Avalonia.Thickness(3, 1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(c, type.DisplayName);
        TaskbarComponentsPanel.SetSizing(c, type.Sizing);
        return new ComponentSlot(inst.InstanceId, inst.TypeId, inert: false, c);
    }

    /// <summary>
    /// A placeholder for an unresolvable, failed or quarantined component. It must be VISIBLE and
    /// keyboard-reachable, not merely present in the automation tree: a failure a sighted user
    /// cannot see and a keyboard user cannot reach is worse than a visible gap, because nobody
    /// discovers it. Hence the explicit border, the themed fill, and Focusable.
    /// </summary>
    public static ComponentSlot Inert(ComponentInstance inst, string reason)
    {
        var c = new Border
        {
            Width = 12,
            Height = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(2, 0),
            [ToolTip.TipProperty] = reason,
            // Visible, and themed — never a hardcoded colour. Bevel.Brush.ButtonShadow is the
            // existing sunken-edge token, so the placeholder reads as a recess in the bar.
            BorderThickness = new Avalonia.Thickness(1),
            [!Border.BorderBrushProperty] = new DynamicResourceExtension("Bevel.Brush.ButtonShadow"),
            [!Border.BackgroundProperty] = new DynamicResourceExtension("Bevel.Brush.TrayWell"),
            // Tab-reachable, so a keyboard user can discover the failure and read its tooltip.
            Focusable = true,
        };
        AutomationProperties.SetName(c, $"Component unavailable: {inst.TypeId}");
        TaskbarComponentsPanel.SetSizing(c, ComponentSizing.Content);
        return new ComponentSlot(inst.InstanceId, inst.TypeId, inert: true, c);
    }
}
