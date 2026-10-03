using Avalonia.Controls;
using Avalonia.Threading;
using Bevel.Core.Components;

namespace Bevel.Taskbar.Components;

/// <summary>
/// Composes a region of the bar from a persisted component list. This is where the registry,
/// normalizer, layout panel, health budget and bar geometry stop being islands and become the bar
/// (spec §9). A component failing anywhere in here costs its own slot and nothing else.
/// </summary>
public sealed class ComponentBarHost
{
    private readonly ComponentRegistry _registry;
    private readonly ComponentHealth _health;
    private readonly BarGeometry _geometry;
    private readonly TaskbarComponentsPanel _panel = new();
    private readonly List<IComponentChannel> _channels = new();
    private readonly List<ComponentSlot> _slots = new();

    // ApplyAsync is fired from startup AND from every settings push, each on a background thread.
    // Two overlapping runs would concurrently mutate _channels, _slots, and the plain
    // Dictionary/HashSet state inside ComponentHealth and BarGeometry — throwing
    // "Collection was modified" or silently corrupting slot order. Serialise them.
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    public ComponentBarHost(ComponentRegistry registry, ComponentHealth health, BarGeometry geometry)
    {
        _registry = registry;
        _health = health;
        _geometry = geometry;
    }

    /// <summary>The control to place on the bar. Stable across <see cref="ApplyAsync"/> calls.</summary>
    public Control View => _panel;

    public IReadOnlyList<ComponentSlot> Slots => _slots;

    /// <summary>
    /// Rebuilds the region from <paramref name="instances"/>. Safe to call on a settings change —
    /// re-applying replaces slots rather than accumulating them, and a hidden instance releases its
    /// geometry contribution. NOTE: no Stack slot CONTRIBUTES height yet, so the shrink path is not
    /// exercised end-to-end by this task; whoever migrates Tray or Clock must not assume it is proven.
    /// </summary>
    public async Task ApplyAsync(IReadOnlyList<ComponentInstance> instances, CancellationToken ct)
    {
        await _applyGate.WaitAsync(ct).ConfigureAwait(false);
        try { await ApplyCoreAsync(instances, ct).ConfigureAwait(false); }
        finally { _applyGate.Release(); }
    }

    private async Task ApplyCoreAsync(IReadOnlyList<ComponentInstance> instances, CancellationToken ct)
    {
        foreach (var ch in _channels)
        {
            try { await ch.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { /* a component misbehaving on teardown is its problem, not the bar's */ }
        }
        _channels.Clear();

        var normalized = ComponentListNormalizer.Normalize(
            instances, id => _registry.TryResolve(id, out var m) ? m : null);

        // What each surviving instance should render as, WITHOUT constructing any Avalonia control
        // yet. ApplyAsync is invoked via Task.Run from TaskbarView (so ConnectAsync's IPC never
        // blocks the UI thread) — which means this whole method runs on a thread-pool thread, with
        // no UI dispatcher affinity at all, for EVERY call, sync channel or not. ComponentSlot.Live/
        // Inert construct real Button/Border controls, and Avalonia's AvaloniaObject constructor
        // asserts VerifyAccess(); building one here would throw "Call from invalid thread" on the
        // very first real apply. Defer construction to the UI-thread hop below instead.
        var plans = new List<Func<ComponentSlot>>();

        foreach (var inst in normalized.Instances)
        {
            if (!inst.Visible)
            {
                // Hidden, not removed: the instance and its settings stay on disk.
                _geometry.RemoveContribution(inst.InstanceId);
                continue;
            }

            if (!_registry.TryResolve(inst.TypeId, out var type))
            {
                plans.Add(() => ComponentSlot.Inert(inst, "This component is not installed."));
                continue;
            }

            var channel = _registry.CreateChannel(inst);
            if (channel is null)
            {
                plans.Add(() => ComponentSlot.Inert(inst, "This component could not start."));
                continue;
            }

            _channels.Add(channel);
            try
            {
                var state = await channel.ConnectAsync(inst, ct).ConfigureAwait(false);
                plans.Add(state.Inert
                    ? () => ComponentSlot.Inert(inst, "This component stopped responding.")
                    : () => ComponentSlot.Live(inst, type, state));
            }
            catch (Exception)
            {
                // A component that throws on start costs its own slot. Quarantine decides whether
                // it is retried; it must never reach the shell's CrashLoop budget.
                var verdict = _health.RecordCrash(inst.InstanceId);
                plans.Add(() => ComponentSlot.Inert(inst, verdict == ComponentVerdict.Quarantine
                    ? "This component failed repeatedly and has been disabled."
                    : "This component failed to start."));
            }
        }

        // The ONE UI-thread hop: build every ComponentSlot's actual control here, then swap the
        // panel's children. Everything above this line is plain .NET state (lists, dictionaries,
        // IComponentChannel) with no UI-thread requirement of its own.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _slots.Clear();
            foreach (var plan in plans) _slots.Add(plan());
            _panel.Children.Clear();
            foreach (var slot in _slots) _panel.Children.Add(slot.Content);
        });
    }
}
