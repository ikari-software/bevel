using System.Collections.Generic;
using Bevel.Core;
using Bevel.Core.Components;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>bevel-aqr7 Task 6: the component list round-trips, and snapshots never alias each other.</summary>
public class ComponentPersistenceTests
{
    private static ComponentInstance Clock(string id, string seconds) =>
        new(id, "run.bevel.clock", new Dictionary<string, string> { ["showSeconds"] = seconds }, true);

    [Fact]
    public void Default_component_list_is_not_null()
        => Assert.NotNull(new BevelSettings().TaskbarComponents);

    [Fact]
    public void CopyFrom_deep_clones_the_instances_so_snapshots_do_not_alias()
    {
        var a = new BevelSettings { TaskbarComponents = new[] { Clock("x", "true") } };
        var b = new BevelSettings();
        b.CopyFrom(a);

        b.TaskbarComponents[0].Settings["showSeconds"] = "false";

        Assert.Equal("true", a.TaskbarComponents[0].Settings["showSeconds"]);
        Assert.NotSame(a.TaskbarComponents, b.TaskbarComponents);
        Assert.NotSame(a.TaskbarComponents[0].Settings, b.TaskbarComponents[0].Settings);
    }

    [Fact]
    public void CopyFrom_copies_the_list_contents()
    {
        var a = new BevelSettings { TaskbarComponents = new[] { Clock("x", "true"), Clock("y", "false") } };
        var b = new BevelSettings();
        b.CopyFrom(a);
        Assert.Equal(2, b.TaskbarComponents.Length);
        Assert.Equal("y", b.TaskbarComponents[1].InstanceId);
    }
}
