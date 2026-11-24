using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Bevel.UI;

/// <summary>
/// Reflection-free property-change hook for code-built controls (bevel-gww.9, AOT): replaces
/// <c>new Binding(nameof(...))</c> in code-behind, which needs reflection at runtime and breaks
/// under trimming/AOT. <see cref="Attach"/> runs <paramref name="apply"/> on attach and on every
/// matching <see cref="INotifyPropertyChanged.PropertyChanged"/>, and unsubscribes on detach — so a
/// recycled/virtualized row never leaks a handler onto a long-lived view-model.
/// <see cref="Attach"/> is re-entrant per host: calling it again on the same host disposes the
/// previous subscription first (recycled containers stay attached in the virtualizing panel, so
/// <c>PrepareContainerForItemOverride</c> re-attaches without stacking handlers), and returns an
/// <see cref="IDisposable"/> handle that tears the subscription down explicitly.
/// </summary>
public static class InpcBind
{
    // One live subscription per host; a re-Attach on the same host replaces (disposes) the prior one.
    private static readonly ConditionalWeakTable<Control, Subscription> s_hosts = new();

    public static IDisposable Attach(Control host, INotifyPropertyChanged source, string propertyName, Action apply)
    {
        if (s_hosts.TryGetValue(host, out var previous))
            previous.Dispose();   // re-entrant: never stack two subscriptions on one host

        var subscription = new Subscription(host, source, propertyName, apply);
        s_hosts.AddOrUpdate(host, subscription);
        subscription.Start();
        return subscription;
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Control _host;
        private readonly INotifyPropertyChanged _source;
        private readonly string _propertyName;
        private readonly Action _apply;
        private bool _subscribed;   // currently hooked to source.PropertyChanged
        private bool _disposed;

        public Subscription(Control host, INotifyPropertyChanged source, string propertyName, Action apply)
        {
            _host = host;
            _source = source;
            _propertyName = propertyName;
            _apply = apply;
        }

        public void Start()
        {
            _host.AttachedToVisualTree += OnAttached;
            _host.DetachedFromVisualTree += OnDetached;
            if (_host.GetVisualRoot() is not null)
                OnAttached(_host, default!);   // already attached: subscribe + apply now
        }

        private void OnChanged(object? _, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == _propertyName)
                _apply();
        }

        private void OnAttached(object? _, VisualTreeAttachmentEventArgs e)
        {
            if (_disposed || _subscribed)
                return;
            _source.PropertyChanged += OnChanged;
            _subscribed = true;
            _apply();
        }

        private void OnDetached(object? _, VisualTreeAttachmentEventArgs e)
        {
            if (!_subscribed)
                return;
            _source.PropertyChanged -= OnChanged;
            _subscribed = false;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _host.AttachedToVisualTree -= OnAttached;
            _host.DetachedFromVisualTree -= OnDetached;
            OnDetached(_host, default!);   // unsubscribe from the source if currently attached
        }
    }
}
