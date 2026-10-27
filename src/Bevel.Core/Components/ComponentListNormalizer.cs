namespace Bevel.Core.Components;

/// <summary>
/// A repaired list, the sizing each surviving instance should actually be laid out with, and a
/// human-readable note per repair for logging. <paramref name="EffectiveSizing"/> is keyed by
/// instanceId and may differ from the manifest's declared sizing where a rule forced a demotion.
/// </summary>
public sealed record NormalizedList(
    IReadOnlyList<ComponentInstance> Instances,
    IReadOnlyDictionary<string, ComponentSizing> EffectiveSizing,
    IReadOnlyList<string> Repairs);

/// <summary>
/// Repairs a persisted component list. Settings on disk may have been hand-edited or written by a
/// different version, so add-time rules cannot be assumed. An unknown type KEEPS its slot (it renders
/// inert) so that uninstalling and reinstalling a component does not silently reshuffle the bar.
/// </summary>
public static class ComponentListNormalizer
{
    public static NormalizedList Normalize(
        IReadOnlyList<ComponentInstance> persisted,
        Func<string, ComponentManifest?> resolve)
    {
        var repairs = new List<string>();
        var sizing = new Dictionary<string, ComponentSizing>(StringComparer.Ordinal);
        if (persisted is null || persisted.Count == 0)
            return new NormalizedList(Array.Empty<ComponentInstance>(), sizing, repairs);

        var kept = new List<ComponentInstance>(persisted.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var singletonsSeen = new HashSet<string>(StringComparer.Ordinal);
        var greedyTaken = false;

        foreach (var inst in persisted)
        {
            if (inst is null) continue;
            var type = resolve(inst.TypeId);

            // multiInstance:false — keep the first, drop the rest. An unknown type is not a known
            // singleton, so it is never dropped on this rule.
            if (type is { MultiInstance: false } && !singletonsSeen.Add(inst.TypeId))
            {
                repairs.Add($"dropped a duplicate instance of single-instance type '{inst.TypeId}'");
                continue;
            }

            var instance = inst;
            if (string.IsNullOrWhiteSpace(instance.InstanceId) || !ids.Add(instance.InstanceId))
            {
                var fresh = ComponentInstance.NewId();
                while (!ids.Add(fresh)) fresh = ComponentInstance.NewId();
                repairs.Add($"reassigned a duplicate or empty instanceId for '{instance.TypeId}' to '{fresh}'");
                instance = instance with { InstanceId = fresh };
            }

            // Spec §4.3: at most ONE greedy per list. Two greedy children would split slack between
            // them and silently break every arrangement, so later ones are DEMOTED rather than
            // dropped — the component still renders, it just stops claiming slack.
            var declared = type?.Sizing ?? ComponentSizing.Content;
            if (declared == ComponentSizing.Greedy)
            {
                if (greedyTaken)
                {
                    declared = ComponentSizing.Content;
                    repairs.Add($"demoted a second greedy component '{instance.TypeId}' to content sizing");
                }
                else greedyTaken = true;
            }
            sizing[instance.InstanceId] = declared;

            kept.Add(instance);
        }

        return new NormalizedList(kept, sizing, repairs);
    }
}
