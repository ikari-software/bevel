using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bevel.Core.Components;
using Bevel.Taskbar.Components;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-aqr7 Task 14: the bar is actually COMPOSED from the list. Without this the registry,
/// normalizer, panel and health budget are untested islands and spec §9 is unmet.
/// </summary>
[Collection("TaskbarTheme")]
public class ComponentBarHostTests
{
    private static ComponentRegistry Registry()
    {
        var r = new ComponentRegistry(new HashSet<string>(StringComparer.Ordinal));
        r.Register(StackComponentManifest.Create(), inst => new LocalComponentChannel(
            i => new ComponentState(i.InstanceId,
                new Dictionary<string, string> { ["folder"] = i.Settings.GetValueOrDefault("folder", "") },
                false)));
        return r;
    }

    private static ComponentInstance Stack(string folder) => new(
        ComponentInstance.NewId(), TaskbarComponentTypes.Stack,
        new Dictionary<string, string> { ["folder"] = folder }, Visible: true);

    private static ComponentBarHost Host() =>
        new(Registry(), new ComponentHealth(), new BarGeometry(1));

    [AvaloniaFact]
    public async Task Each_visible_instance_gets_a_slot_in_list_order()
    {
        var host = Host();
        var a = Stack("/one");
        var b = Stack("/two");
        await host.ApplyAsync(new[] { a, b }, CancellationToken.None);

        Assert.Equal(new[] { a.InstanceId, b.InstanceId }, host.Slots.Select(s => s.InstanceId));
    }

    [AvaloniaFact]
    public async Task A_hidden_instance_gets_no_slot_but_keeps_its_settings()
    {
        var host = Host();
        var hidden = Stack("/kept") with { Visible = false };
        await host.ApplyAsync(new[] { hidden }, CancellationToken.None);

        Assert.Empty(host.Slots);
        Assert.Equal("/kept", hidden.Settings["folder"]);   // removal would have discarded this
    }

    [AvaloniaFact]
    public async Task An_unknown_type_keeps_its_slot_and_renders_inert()
    {
        var host = Host();
        var unknown = new ComponentInstance("gone", "com.example.absent",
            new Dictionary<string, string>(), true);
        await host.ApplyAsync(new[] { Stack("/one"), unknown }, CancellationToken.None);

        Assert.Equal(2, host.Slots.Count);
        var slot = host.Slots.Single(s => s.InstanceId == "gone");
        Assert.True(slot.IsInert);
    }

    [AvaloniaFact]
    public async Task A_bar_with_zero_resolvable_components_still_produces_a_view()
    {
        var host = Host();
        await host.ApplyAsync(Array.Empty<ComponentInstance>(), CancellationToken.None);

        Assert.NotNull(host.View);   // spec §6: an empty bar is still a shell; a crashed bar is not
        Assert.Empty(host.Slots);
    }

    [AvaloniaFact]
    public async Task Re_applying_replaces_slots_rather_than_accumulating_them()
    {
        var host = Host();
        await host.ApplyAsync(new[] { Stack("/one") }, CancellationToken.None);
        await host.ApplyAsync(new[] { Stack("/two"), Stack("/three") }, CancellationToken.None);

        Assert.Equal(2, host.Slots.Count);
    }

    [AvaloniaFact]
    public async Task A_duplicate_instanceId_on_disk_is_repaired_before_slots_are_built()
    {
        var host = Host();
        var dup1 = new ComponentInstance("same", TaskbarComponentTypes.Stack,
            new Dictionary<string, string> { ["folder"] = "/a" }, true);
        var dup2 = new ComponentInstance("same", TaskbarComponentTypes.Stack,
            new Dictionary<string, string> { ["folder"] = "/b" }, true);

        await host.ApplyAsync(new[] { dup1, dup2 }, CancellationToken.None);

        Assert.Equal(2, host.Slots.Count);
        Assert.Equal(2, host.Slots.Select(s => s.InstanceId).Distinct().Count());
    }

    [AvaloniaFact]
    public async Task A_channel_that_throws_on_connect_yields_an_inert_slot_and_the_bar_survives()
    {
        var r = new ComponentRegistry(new HashSet<string>(StringComparer.Ordinal));
        r.Register(StackComponentManifest.Create(), _ => new ThrowingChannel());
        var host = new ComponentBarHost(r, new ComponentHealth(), new BarGeometry(1));

        await host.ApplyAsync(new[] { Stack("/boom") }, CancellationToken.None);

        Assert.NotNull(host.View);
        Assert.True(host.Slots.Single().IsInert);
    }

    private sealed class ThrowingChannel : IComponentChannel
    {
        public event Action<ComponentState>? StateChanged;
        public Task<ComponentState> ConnectAsync(ComponentInstance i, CancellationToken ct)
            => throw new InvalidOperationException("component blew up on start");
        public Task SendAsync(ComponentInput input, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
