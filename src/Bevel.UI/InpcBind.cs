using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Bevel.UI;

/// <summary>
/// Reflection-free property-change hook for code-built controls (bevel-gww.9, AOT): replaces
/// <c>new Binding(nameof(...))</c> in code-behind, which needs reflection at runtime and breaks
/// under trimming/AOT. <see cref="Attach"/> runs <paramref name="apply"/> on attach and on every
/// matching <see cref="INotifyPropertyChanged.PropertyChanged"/>, and unsubscribes on detach — so a
/// recycled/virtualized row never leaks a handler onto a long-lived view-model. If the host is
/// already attached, it subscribes and applies immediately.
/// </summary>
public static class InpcBind
{
    public static void Attach(Control host, INotifyPropertyChanged source, string propertyName, Action apply)
    {
        void OnChanged(object? _, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == propertyName)
                apply();
        }

        void OnAttached(object? _, VisualTreeAttachmentEventArgs e)
        {
            source.PropertyChanged += OnChanged;
            apply();
        }

        void OnDetached(object? _, VisualTreeAttachmentEventArgs e)
        {
            source.PropertyChanged -= OnChanged;
        }

        host.AttachedToVisualTree += OnAttached;
        host.DetachedFromVisualTree += OnDetached;
        if (host.GetVisualRoot() is not null)
            OnAttached(host, default!);   // already attached: subscribe + apply now
    }
}
