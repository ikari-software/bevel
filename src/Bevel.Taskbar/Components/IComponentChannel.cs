using Bevel.Core.Components;

namespace Bevel.Taskbar.Components;

/// <summary>
/// One instance's live state: current values for its view's primitive keys. <paramref name="Inert"/>
/// marks a failed or quarantined component, whose slot renders a placeholder rather than stale content.
/// </summary>
public sealed record ComponentState(string InstanceId, IReadOnlyDictionary<string, string> Values, bool Inert);

/// <summary>A semantic input event the bar forwards to a component. The bar handled the gesture.</summary>
public sealed record ComponentInput(string InstanceId, string PrimitiveKey, string Kind);

/// <summary>
/// The ONE seam both hosts implement: built-ins bind locally, third-party components speak over the
/// component bus. Because the interface is identical, the conformance suite runs the same assertions
/// against both paths — which is what keeps the public contract honest.
/// </summary>
public interface IComponentChannel : IAsyncDisposable
{
    /// <summary>Raised whenever the component publishes new state. May arrive off the UI thread.</summary>
    event Action<ComponentState>? StateChanged;

    /// <summary>Starts the component and returns its first state.</summary>
    Task<ComponentState> ConnectAsync(ComponentInstance instance, CancellationToken ct);

    /// <summary>Forwards a semantic input event.</summary>
    Task SendAsync(ComponentInput input, CancellationToken ct);
}
