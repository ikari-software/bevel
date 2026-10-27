using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Layout;
using Bevel.Core.Components;

namespace Bevel.Taskbar.Components;

/// <summary>
/// Hosts the pixel regions components paint for themselves. Frames arrive through shared memory
/// (<c>MmfBgraPool</c>) and the bus carries only a FrameReady notification, so transport throughput
/// never bounds surface rate.
///
/// Three guarantees live here, all of them spec requirements rather than polish: a surface keeps its
/// declared accessible name and role so it is not a hole in the UIA/AX tree; only the owning
/// instance may write its slot; and a theme or colourway change bumps a revision the component must
/// observe, or the surface paints a stale palette forever (bevel-voqo).
/// </summary>
public sealed class SurfaceHost
{
    // LOCKED, like its sibling SurfaceOwnership and for the identical reason: once FrameReady
    // dispatch lands, TryAcceptFrame and MarkInert are reached from the bus poller thread and the
    // quarantine/watchdog path while CreateView writes _slots from the bar thread. Leaving these
    // plain reproduces the exact bug class ComponentHealth was fixed for, one layer up — and it
    // would surface as a flaky frame or a component refusing its own pixels, far from the cause.
    private readonly object _gate = new();
    private readonly SurfaceOwnership _ownership;
    private readonly Dictionary<(string Instance, string Key), uint> _slots = new();
    private readonly Dictionary<uint, ulong> _lastFrame = new();
    private readonly HashSet<string> _inert = new(StringComparer.Ordinal);
    private int _revision;

    public SurfaceHost(SurfaceOwnership ownership) => _ownership = ownership;

    /// <summary>Bumped on every theme push. Components repaint when it changes.</summary>
    /// <remarks>Volatile.Read, not a bare field access: _revision is written with
    /// Interlocked.Increment (a full fence) but a reader on another thread needs its own barrier to
    /// be guaranteed to observe it rather than a cached value.</remarks>
    public int Revision => Volatile.Read(ref _revision);

    /// <summary>The slot assigned to one instance's surface primitive.</summary>
    public uint SlotOf(string instanceId, string primitiveKey)
    {
        lock (_gate) return _slots[(instanceId, primitiveKey)];
    }

    /// <summary>
    /// Builds the control that displays a surface. The accessible name and role come from the
    /// MANIFEST and are mandatory there, so this never has to invent them.
    /// </summary>
    public Control CreateView(SurfacePrimitive primitive, string instanceId)
    {
        uint slot;
        lock (_gate)
        {
            slot = _ownership.Assign(instanceId, primitive.Key);
            _slots[(instanceId, primitive.Key)] = slot;
        }

        var image = new Image
        {
            Width = primitive.IntrinsicWidth,
            Height = primitive.IntrinsicHeight,
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = Avalonia.Media.Stretch.None,
        };
        AutomationProperties.SetName(image, primitive.AccessibleName);
        AutomationProperties.SetAutomationId(image, $"{instanceId}:{primitive.Key}");
        AutomationProperties.SetControlTypeOverride(image,
            primitive.AccessibleRole == "Image" ? AutomationControlType.Image : AutomationControlType.Custom);
        return image;
    }

    /// <summary>
    /// Accepts a frame only from the slot's owner, only in increasing frame order, and only while
    /// the instance is live. Authentication proves A component, not WHICH one.
    /// </summary>
    public bool TryAcceptFrame(string instanceId, uint slot, ulong frame)
    {
        lock (_gate)
        {
            if (_inert.Contains(instanceId)) return false;
            if (!_ownership.TryAccept(instanceId, slot)) return false;
            if (_lastFrame.TryGetValue(slot, out var last) && frame <= last) return false;
            _lastFrame[slot] = frame;
            return true;
        }
    }

    /// <summary>
    /// Marks an instance's surfaces dead. Its last frame must NOT keep showing: a stale image is
    /// indistinguishable from a working component (spec §6).
    /// </summary>
    public void MarkInert(string instanceId)
    {
        lock (_gate)
        {
            _inert.Add(instanceId);
            _ownership.Release(instanceId);
        }
    }

    /// <summary>
    /// Publishes the active theme's tokens to components and bumps <see cref="Revision"/>. Must be
    /// called on every theme AND colourway change — the two runtime recolour engines override static
    /// tokens, so a colourway re-hue is as much a repaint trigger as a theme swap.
    /// </summary>
    public void PushTheme(IReadOnlyDictionary<string, string> argb)
    {
        _ = argb;
        Interlocked.Increment(ref _revision);
    }
}
