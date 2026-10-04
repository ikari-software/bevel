using System.Collections.Generic;
using System.Linq;
using Bevel.Core.Components;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>
/// bevel-aqr7 Task 4: a persisted list may have been hand-edited or written by another version.
/// Normalizing REPAIRS it; it never throws and never drops the whole bar.
/// </summary>
public class ComponentListNormalizerTests
{
    private static ComponentManifest Type(string id, bool multi, ComponentSizing sizing = ComponentSizing.Content) =>
        new(id, 1, id, "", multi, sizing, null,
            Array.Empty<ComponentSettingsField>(),
            new ComponentPrimitive[] { new LabelPrimitive("l", "x") });

    private static readonly Dictionary<string, ComponentManifest> Types = new()
    {
        ["run.bevel.start"] = Type("run.bevel.start", multi: false),
        ["run.bevel.clock"] = Type("run.bevel.clock", multi: true),
        ["run.bevel.spacer"] = Type("run.bevel.spacer", multi: true),
        ["run.bevel.strip"] = Type("run.bevel.strip", multi: true, ComponentSizing.Greedy),
        ["run.bevel.strip2"] = Type("run.bevel.strip2", multi: true, ComponentSizing.Greedy),
    };

    private static ComponentManifest? Resolve(string id) => Types.GetValueOrDefault(id);

    private static ComponentInstance I(string instanceId, string typeId)
        => new(instanceId, typeId, new Dictionary<string, string>(), true);

    // Review Focus 1
    [Fact]
    public void A_second_instance_of_a_single_instance_type_is_dropped_keeping_the_first()
    {
        var r = ComponentListNormalizer.Normalize(
            new[] { I("a", "run.bevel.start"), I("b", "run.bevel.start"), I("c", "run.bevel.clock") },
            Resolve);

        Assert.Equal(new[] { "a", "c" }, r.Instances.Select(x => x.InstanceId));
        Assert.Contains(r.Repairs, m => m.Contains("run.bevel.start"));
    }

    // Review Focus 4
    [Fact]
    public void A_duplicate_instance_id_is_reassigned_not_dropped()
    {
        var r = ComponentListNormalizer.Normalize(
            new[] { I("dup", "run.bevel.clock"), I("dup", "run.bevel.clock") }, Resolve);

        Assert.Equal(2, r.Instances.Count);
        Assert.Equal(2, r.Instances.Select(x => x.InstanceId).Distinct().Count());
        Assert.Contains(r.Repairs, m => m.Contains("instanceId"));
    }

    [Fact]
    public void An_unknown_type_keeps_its_slot_and_its_order()
    {
        var r = ComponentListNormalizer.Normalize(
            new[] { I("a", "run.bevel.clock"), I("b", "com.example.gone"), I("c", "run.bevel.spacer") },
            Resolve);

        Assert.Equal(new[] { "a", "b", "c" }, r.Instances.Select(x => x.InstanceId));
    }

    // Spec §4.3 and the Acceptance criteria: at most one greedy component per list, validated at LOAD.
    // Two greedy children would split slack between them and silently break every arrangement.
    [Fact]
    public void A_second_greedy_component_is_demoted_to_content_keeping_the_first_greedy()
    {
        var r = ComponentListNormalizer.Normalize(
            new[] { I("a", "run.bevel.strip"), I("b", "run.bevel.strip2"), I("c", "run.bevel.clock") },
            Resolve);

        Assert.Equal(ComponentSizing.Greedy, r.EffectiveSizing["a"]);
        Assert.Equal(ComponentSizing.Content, r.EffectiveSizing["b"]);
        Assert.Contains(r.Repairs, m => m.Contains("greedy"));
    }

    [Fact]
    public void A_spacer_counts_as_greedy_for_that_rule_only_when_the_manifest_says_so()
    {
        var r = ComponentListNormalizer.Normalize(
            new[] { I("a", "run.bevel.spacer"), I("b", "run.bevel.clock") }, Resolve);
        Assert.Equal(ComponentSizing.Content, r.EffectiveSizing["a"]);   // this spacer type is Content
    }

    [Fact]
    public void An_empty_list_normalizes_to_empty_without_throwing()
        => Assert.Empty(ComponentListNormalizer.Normalize(Array.Empty<ComponentInstance>(), Resolve).Instances);

    [Fact]
    public void A_null_list_normalizes_to_empty_without_throwing()
        => Assert.Empty(ComponentListNormalizer.Normalize(null!, Resolve).Instances);
}
