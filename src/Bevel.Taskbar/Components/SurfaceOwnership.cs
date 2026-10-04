namespace Bevel.Taskbar.Components;

/// <summary>
/// Maps shared-memory surface slots to the instance allowed to write them. The bus handshake proves a
/// peer is A component; this proves it is the one that owns the slot it is publishing to. Slot numbers
/// are never reused after release, so a late frame from a dead component cannot land in a live slot.
///
/// Locked: nothing in this task calls it from two threads, but Task 14 fires <see cref="Release"/>
/// from the bus POLLER thread on quarantine while the bar reads <see cref="TryAccept"/> from its own
/// thread. An unsynchronised <c>Dictionary</c> torn between those two is a corruption bug that would
/// surface as a wrong pixel or a crash, far from the change that caused it.
/// </summary>
public sealed class SurfaceOwnership
{
    private readonly object _gate = new();
    private readonly Dictionary<uint, string> _owner = new();
    private uint _next = 1;

    /// <summary>Assigns a fresh slot for one surface primitive of one instance.</summary>
    public uint Assign(string instanceId, string primitiveKey)
    {
        lock (_gate)
        {
            var slot = _next++;
            _owner[slot] = instanceId;
            return slot;
        }
    }

    /// <summary>True only when <paramref name="instanceId"/> owns <paramref name="slot"/>.</summary>
    public bool TryAccept(string instanceId, uint slot)
    {
        lock (_gate)
            return _owner.TryGetValue(slot, out var owner) && string.Equals(owner, instanceId, StringComparison.Ordinal);
    }

    /// <summary>Revokes every slot held by an instance, on quarantine or teardown.</summary>
    public void Release(string instanceId)
    {
        lock (_gate)
        {
            foreach (var slot in _owner.Where(kv => kv.Value == instanceId).Select(kv => kv.Key).ToArray())
                _owner.Remove(slot);
        }
    }
}
