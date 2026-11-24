using Bevel.Pal.Abstractions;

namespace Bevel.Pal.MacOS;

/// <summary>bevel-sdss: the pure diff that gives <see cref="IPermissionBroker.PermissionChanged"/>
/// a producer. TCC has no change notification, so the broker polls; each <see cref="Observe"/> call
/// returns exactly the permissions whose state differs from the previous call (the first call
/// establishes the baseline and returns none). Internal for tests.</summary>
internal sealed class PermissionChangeTracker
{
    private readonly object _gate = new();
    private Dictionary<ShellPermission, PermissionState>? _last;

    /// <summary>Records the current states and returns the permissions that changed since the last
    /// call. Thread-safe; intended to be called from one polling timer.</summary>
    public IReadOnlyList<ShellPermission> Observe(PermissionState accessibility, PermissionState screenRecording)
    {
        var current = new Dictionary<ShellPermission, PermissionState>
        {
            [ShellPermission.Accessibility] = accessibility,
            [ShellPermission.ScreenRecording] = screenRecording,
        };
        lock (_gate)
        {
            var last = _last;
            _last = current;
            if (last is null) return Array.Empty<ShellPermission>();
            var changed = new List<ShellPermission>();
            foreach (var (permission, state) in current)
                if (last.TryGetValue(permission, out var previous) && previous != state)
                    changed.Add(permission);
            return changed;
        }
    }
}
