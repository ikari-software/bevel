using Bevel.Core.Components;

namespace Bevel.Taskbar.Components;

/// <summary>
/// A built-in component's channel: the same <see cref="IComponentChannel"/> contract with the IPC hop
/// removed. Built-ins are first-party and share the taskbar's trust, so a clock does not cost a
/// process — but it goes through the identical manifest, primitive vocabulary and settings schema, and
/// the conformance suite asserts the two paths behave the same.
/// </summary>
public sealed class LocalComponentChannel : IComponentChannel
{
    private readonly Func<ComponentInstance, ComponentState> _project;

    public LocalComponentChannel(Func<ComponentInstance, ComponentState> project) => _project = project;

    public event Action<ComponentState>? StateChanged;

    public Task<ComponentState> ConnectAsync(ComponentInstance instance, CancellationToken ct)
    {
        var state = _project(instance);
        StateChanged?.Invoke(state);
        return Task.FromResult(state);
    }

    public Task SendAsync(ComponentInput input, CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
