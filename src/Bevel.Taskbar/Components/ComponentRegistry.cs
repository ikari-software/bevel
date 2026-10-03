using Bevel.Core.Components;

namespace Bevel.Taskbar.Components;

/// <summary>
/// typeId → manifest + channel factory. Registration VALIDATES, so an invalid or unavailable type is
/// simply absent rather than a broken slot — and a third-party manifest can never take the bar down
/// by registering.
/// </summary>
public sealed class ComponentRegistry
{
    private readonly Dictionary<string, (ComponentManifest Manifest, Func<ComponentInstance, IComponentChannel> Factory)> _types
        = new(StringComparer.Ordinal);
    private readonly IReadOnlySet<string> _capabilities;

    /// <param name="availableCapabilities">PAL capability names this platform reports.</param>
    public ComponentRegistry(IReadOnlySet<string> availableCapabilities) => _capabilities = availableCapabilities;

    /// <summary>Every successfully registered manifest.</summary>
    public IReadOnlyCollection<ComponentManifest> Manifests
        => _types.Values.Select(v => v.Manifest).ToArray();

    /// <summary>
    /// Registers a type. Returns false — without throwing — when the manifest is invalid, when its
    /// required capability is unavailable, or when the id is already taken (first registration wins).
    /// </summary>
    public bool Register(ComponentManifest manifest, Func<ComponentInstance, IComponentChannel> factory)
    {
        if (!ManifestValidator.Validate(manifest).IsValid) return false;
        if (manifest.RequiresCapability is { } cap && !_capabilities.Contains(cap)) return false;
        if (_types.ContainsKey(manifest.Id)) return false;
        _types[manifest.Id] = (manifest, factory);
        return true;
    }

    public bool TryResolve(string typeId, out ComponentManifest manifest)
    {
        if (typeId is not null && _types.TryGetValue(typeId, out var e)) { manifest = e.Manifest; return true; }
        manifest = null!;
        return false;
    }

    /// <summary>Creates a channel for an instance, or null when its type is unknown (slot renders inert).</summary>
    public IComponentChannel? CreateChannel(ComponentInstance instance)
        => _types.TryGetValue(instance.TypeId, out var e) ? e.Factory(instance) : null;
}
