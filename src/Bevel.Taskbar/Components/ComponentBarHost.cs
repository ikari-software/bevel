using Avalonia.Controls;
using Avalonia.Threading;
using Bevel.Core.Components;
using Bevel.Taskbar;

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
    private readonly Func<BarGeometry> _geometry;
    private readonly TaskbarComponentsPanel _panel = new();
    private readonly List<IComponentChannel> _channels = new();
    private readonly List<ComponentSlot> _slots = new();

    // Owns surface slot assignment and the theme-push revision for every SurfacePrimitive this bar
    // hosts. Kept here (rather than threaded through the constructor) so this task does not have to
    // change the existing constructor signature that TaskbarView.axaml.cs and the conformance tests
    // already call. Exposed via <see cref="Surfaces"/> so the theme services and the bus-message
    // dispatch for FrameReady/ThemePush (both still owned by call sites outside this class) have a
    // single place to reach.
    private readonly SurfaceHost _surfaces = new(new SurfaceOwnership());

    // ApplyAsync is fired from startup AND from every settings push, each on a background thread.
    // Two overlapping runs would concurrently mutate _channels, _slots, and the plain
    // Dictionary/HashSet state inside ComponentHealth and BarGeometry — throwing
    // "Collection was modified" or silently corrupting slot order. Serialise them.
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    /// <param name="geometry">Accessor for the bar's LIVE geometry, not a snapshot (whole-branch
    /// review Fix 4). <see cref="TaskbarWindow.SetRows"/> replaces its <c>_geometry</c> field with a
    /// rebuilt instance on every row-count change; a plain <see cref="BarGeometry"/> captured once
    /// at construction would go stale the moment that happens, and this host would keep calling
    /// <see cref="BarGeometry.RemoveContribution"/> on an instance <c>TaskbarWindow</c> no longer
    /// reads from.</param>
    public ComponentBarHost(ComponentRegistry registry, ComponentHealth health, Func<BarGeometry> geometry)
    {
        _registry = registry;
        _health = health;
        _geometry = geometry;
    }

    /// <summary>The control to place on the bar. Stable across <see cref="ApplyAsync"/> calls.</summary>
    public Control View => _panel;

    public IReadOnlyList<ComponentSlot> Slots => _slots;

    /// <summary>
    /// The surface pixel path for every <c>SurfacePrimitive</c> this bar hosts. A theme or colourway
    /// change must call <see cref="SurfaceHost.PushTheme"/> on this instance — not on a private
    /// SurfaceHost elsewhere — or a surface keeps painting a stale palette (bevel-voqo). Likewise,
    /// FrameReady envelopes arriving off the component bus must resolve to <see
    /// cref="SurfaceHost.TryAcceptFrame"/> here so ownership and frame ordering are enforced against
    /// the SAME slot table the bar's own teardown (<see cref="ApplyCoreAsync"/>) releases from.
    /// </summary>
    public SurfaceHost Surfaces => _surfaces;

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

        // Whole-branch review Fix 3: these used to be computed and discarded. A hand-edited or
        // version-skewed settings.db can repair silently every apply (dropped duplicates,
        // reassigned instanceIds, demoted greedy components) with nobody able to see why the bar
        // looks different from what settings.db says — log every repair so that is diagnosable.
        foreach (var repair in normalized.Repairs)
            TaskbarLog.Info($"component list repair: {repair}");

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
                _geometry().RemoveContribution(inst.InstanceId);
                continue;
            }

            if (!_registry.TryResolve(inst.TypeId, out var type))
            {
                plans.Add(() => ComponentSlot.Inert(inst, "This component is not installed."));
                continue;
            }

            try
            {
                // CreateChannel invokes a third-party factory (whole-branch review Fix 6, spec §6:
                // "Validation rejects the component, never the bar"). It used to sit OUTSIDE this
                // try, so one throwing factory aborted the whole apply and left the region silently
                // empty instead of costing just its own slot.
                var channel = _registry.CreateChannel(inst);
                if (channel is null)
                {
                    plans.Add(() => ComponentSlot.Inert(inst, "This component could not start.", type));
                    continue;
                }

                _channels.Add(channel);
                var state = await channel.ConnectAsync(inst, ct).ConfigureAwait(false);
                if (state.Inert)
                {
                    // Whole-branch review Fix 5, spec §6: "Hangs count as failures." A timed-out
                    // component reports back via Inert: true rather than throwing, and this path
                    // used to record nothing — so a hung component was retried on every settings
                    // push forever and never reached the health budget's quarantine.
                    var hungVerdict = _health.RecordCrash(inst.InstanceId);
                    plans.Add(() => ComponentSlot.Inert(inst, hungVerdict == ComponentVerdict.Quarantine
                        ? "This component failed repeatedly and has been disabled."
                        : "This component stopped responding.", type));
                }
                else
                {
                    // EffectiveSizing, not type.Sizing (whole-branch review Fix 3): the normalizer
                    // demotes a second greedy component to content sizing (spec §4.2's one-greedy
                    // rule), and reading the manifest's declared sizing here discarded that
                    // demotion, letting two greedy components both lay out greedy.
                    var sizing = normalized.EffectiveSizing.TryGetValue(inst.InstanceId, out var s)
                        ? s : type.Sizing;
                    plans.Add(() => ComponentSlot.Live(inst, type, state, sizing));
                }
            }
            catch (Exception)
            {
                // A component that throws on start (factory or ConnectAsync) costs its own slot.
                // Quarantine decides whether it is retried; it must never reach the shell's
                // CrashLoop budget.
                var verdict = _health.RecordCrash(inst.InstanceId);
                plans.Add(() => ComponentSlot.Inert(inst, verdict == ComponentVerdict.Quarantine
                    ? "This component failed repeatedly and has been disabled."
                    : "This component failed to start.", type));
            }
        }

        // The ONE UI-thread hop: build every ComponentSlot's actual control here, then swap the
        // panel's children. Everything above this line is plain .NET state (lists, dictionaries,
        // IComponentChannel) with no UI-thread requirement of its own.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Whole-branch review Fix 6: build into a LOCAL list first, with each plan guarded by
            // its own try/catch, so one throwing plan cannot leave _slots half-built against a
            // _panel.Children that was already cleared — neither field is touched until every plan
            // has been attempted, and a single bad plan costs only its own slot.
            var built = new List<ComponentSlot>(plans.Count);
            foreach (var plan in plans)
            {
                try { built.Add(plan()); }
                catch (Exception) { /* one failing control must not blank the whole region */ }
            }
            _slots.Clear();
            _slots.AddRange(built);
            _panel.Children.Clear();
            foreach (var slot in _slots) _panel.Children.Add(slot.Content);
        });
    }
}
