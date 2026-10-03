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
    /// re-applying replaces slots rather than accumulating them, and geometry is re-derived so a
    /// smaller tier can SHRINK the bar (the stale-value failure bevel-kclq records).
    /// </summary>
    public async Task ApplyAsync(IReadOnlyList<ComponentInstance> instances, CancellationToken ct)
    {
        foreach (var ch in _channels)
        {
            try { await ch.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { /* a component misbehaving on teardown is its problem, not the bar's */ }
        }
        _channels.Clear();
        _slots.Clear();

        var normalized = ComponentListNormalizer.Normalize(
            instances, id => _registry.TryResolve(id, out var m) ? m : null);

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
                _slots.Add(ComponentSlot.Inert(inst, "This component is not installed."));
                continue;
            }

            var channel = _registry.CreateChannel(inst);
            if (channel is null)
            {
                _slots.Add(ComponentSlot.Inert(inst, "This component could not start."));
                continue;
            }

            _channels.Add(channel);
            try
            {
                var state = await channel.ConnectAsync(inst, ct).ConfigureAwait(false);
                _slots.Add(state.Inert
                    ? ComponentSlot.Inert(inst, "This component stopped responding.")
                    : ComponentSlot.Live(inst, type, state));
            }
            catch (Exception)
            {
                // A component that throws on start costs its own slot. Quarantine decides whether
                // it is retried; it must never reach the shell's CrashLoop budget.
                var verdict = _health.RecordCrash(inst.InstanceId);
                _slots.Add(ComponentSlot.Inert(inst, verdict == ComponentVerdict.Quarantine
                    ? "This component failed repeatedly and has been disabled."
                    : "This component failed to start."));
            }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _panel.Children.Clear();
            foreach (var slot in _slots) _panel.Children.Add(slot.Content);
        });
    }
}
