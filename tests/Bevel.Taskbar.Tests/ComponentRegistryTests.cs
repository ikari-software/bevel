using System;
using System.Collections.Generic;
using System.Linq;
using Bevel.Core.Components;
using Bevel.Taskbar.Components;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>bevel-aqr7 Task 9: the registry refuses invalid and unavailable types without throwing.</summary>
public class ComponentRegistryTests
{
    private static ComponentManifest Type(string id, string? capability = null) =>
        new(id, 1, id, "", true, ComponentSizing.Content, capability,
            Array.Empty<ComponentSettingsField>(),
            new ComponentPrimitive[] { new LabelPrimitive("l", "x") });

    private static ComponentRegistry Registry(params string[] capabilities)
        => new(new HashSet<string>(capabilities, StringComparer.Ordinal));

    [Fact]
    public void A_registered_type_resolves_by_id()
    {
        var r = Registry();
        r.Register(Type("run.bevel.clock"), _ => new FakeChannel());
        Assert.True(r.TryResolve("run.bevel.clock", out var m));
        Assert.Equal("run.bevel.clock", m.Id);
    }

    [Fact]
    public void An_unregistered_id_does_not_resolve_and_does_not_throw()
        => Assert.False(Registry().TryResolve("com.example.nope", out _));

    [Fact]
    public void A_null_manifest_is_refused_without_throwing()
    {
        // Register's parameter is non-nullable ComponentManifest, so this looks redundant to a
        // same-assembly caller — but the ordinary way a malformed third-party manifest arrives is
        // JsonSerializer.Deserialize<ComponentManifest>(...) returning null for an empty/corrupt
        // file, which bypasses the compiler's nullability check entirely. Register must not throw
        // on that path: a third-party manifest must never be able to take the shell down merely by
        // registering.
        var r = Registry();
        Assert.False(r.Register(null!, _ => new FakeChannel()));
        Assert.False(r.TryResolve("run.bevel.bad", out _));
    }

    [Fact]
    public void An_invalid_manifest_is_refused_at_registration()
    {
        var r = Registry();
        Assert.False(r.Register(Type("run.bevel.bad") with { ContractVersion = 99 }, _ => new FakeChannel()));
        Assert.False(r.TryResolve("run.bevel.bad", out _));
    }

    [Fact]
    public void A_type_whose_capability_is_unavailable_is_absent_rather_than_broken()
    {
        var r = Registry("audio.endpoint");
        Assert.True(r.Register(Type("run.bevel.volume", "audio.endpoint"), _ => new FakeChannel()));
        Assert.False(r.Register(Type("run.bevel.battery", "power.battery"), _ => new FakeChannel()));
        Assert.True(r.TryResolve("run.bevel.volume", out _));
        Assert.False(r.TryResolve("run.bevel.battery", out _));
    }

    [Fact]
    public void Registering_the_same_id_twice_keeps_the_first()
    {
        var r = Registry();
        r.Register(Type("run.bevel.clock"), _ => new FakeChannel());
        Assert.False(r.Register(Type("run.bevel.clock") with { DisplayName = "Second" }, _ => new FakeChannel()));
        r.TryResolve("run.bevel.clock", out var m);
        Assert.Equal("run.bevel.clock", m.DisplayName);
    }

    [Fact]
    public void Manifests_lists_only_what_registered()
    {
        var r = Registry();
        r.Register(Type("a"), _ => new FakeChannel());
        r.Register(Type("b") with { ContractVersion = 99 }, _ => new FakeChannel());
        Assert.Equal(new[] { "a" }, r.Manifests.Select(m => m.Id));
    }

    private sealed class FakeChannel : IComponentChannel
    {
        public event Action<ComponentState>? StateChanged;
        public Task<ComponentState> ConnectAsync(ComponentInstance i, CancellationToken ct)
        {
            StateChanged?.Invoke(new ComponentState(i.InstanceId, new Dictionary<string, string>(), false));
            return Task.FromResult(new ComponentState(i.InstanceId, new Dictionary<string, string>(), false));
        }
        public Task SendAsync(ComponentInput input, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
