using System;
using Avalonia.Threading;

namespace Bevel.Taskbar;

/// <summary>
/// bevel-qb19: the one-shot timer the VM uses to settle a bounded attention animation. An interface
/// so tests can fire it manually instead of waiting ~4s of wall clock; the production default is
/// <see cref="DispatcherAttentionTimer"/> (UI-thread DispatcherTimer).
/// </summary>
public interface IAttentionTimer
{
    /// <summary>Arms a one-shot fire of <paramref name="callback"/> after <paramref name="dueTime"/>,
    /// replacing any pending fire.</summary>
    void Start(TimeSpan dueTime, Action callback);

    /// <summary>Cancels any pending fire. Never fires after Stop.</summary>
    void Stop();
}

/// <summary>DispatcherTimer-backed <see cref="IAttentionTimer"/>; runs the callback on the UI thread.</summary>
public sealed class DispatcherAttentionTimer : IAttentionTimer
{
    private DispatcherTimer? _timer;

    public void Start(TimeSpan dueTime, Action callback)
    {
        Stop();
        var t = new DispatcherTimer { Interval = dueTime };
        t.Tick += (_, _) => { Stop(); callback(); };
        _timer = t;
        t.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }
}
