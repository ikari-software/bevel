using Bevel.Components.V1;
using Bevel.Core.Components;
using Bevel.ComponentBus;
using Google.Protobuf;

namespace Bevel.Taskbar.Components;

/// <summary>
/// The bar-side handle for one component running in another process. Same <see
/// cref="IComponentChannel"/> contract as a built-in, so the conformance suite asserts identical
/// behaviour across both paths — which is the whole argument for the contract being public.
///
/// A peer that authenticates but never publishes is a HANG, not a crash, so nothing else would
/// notice: connecting past the timeout yields an inert state rather than waiting forever.
/// </summary>
public sealed class RemoteComponentChannel : IComponentChannel
{
    private readonly ComponentBusServer _bus;
    private readonly string _peerIdentity;
    private readonly int _connectTimeoutMs;
    private readonly object _gate = new();
    private string? _instanceId;
    private ComponentState? _last;

    public RemoteComponentChannel(ComponentBusServer bus, string peerIdentity, int connectTimeoutMs = 5000)
    {
        _bus = bus;
        _peerIdentity = peerIdentity;
        _connectTimeoutMs = connectTimeoutMs;
        _bus.MessageReceived += OnBusMessage;
    }

    public event Action<ComponentState>? StateChanged;

    public async Task<ComponentState> ConnectAsync(ComponentInstance instance, CancellationToken ct)
    {
        lock (_gate) _instanceId = instance.InstanceId;

        var first = new TaskCompletionSource<ComponentState>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Capture(ComponentState s)
        {
            if (s.InstanceId == instance.InstanceId) first.TrySetResult(s);
        }

        StateChanged += Capture;

        // A component may publish before the bar finishes connecting — the bus pump runs on its own
        // thread — so an already-received state must satisfy this connect rather than being missed.
        lock (_gate)
            if (_last is { } cached && cached.InstanceId == instance.InstanceId)
                first.TrySetResult(cached);
        try
        {
            using var timeout = new CancellationTokenSource(_connectTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            var delay = Task.Delay(Timeout.Infinite, linked.Token);

            var done = await Task.WhenAny(first.Task, delay).ConfigureAwait(false);
            if (done == first.Task) return await first.Task.ConfigureAwait(false);

            // The delay ended either because the connect timeout elapsed or because the CALLER's own
            // token was cancelled (bar teardown, window closing mid-connect). Those are different
            // outcomes and must not be conflated: a deliberate cancel has to propagate as
            // OperationCanceledException, not come back indistinguishable from "this component is
            // unresponsive" — otherwise the health budget would quarantine a component that was
            // perfectly fine, because the bar gave up on it rather than the reverse.
            ct.ThrowIfCancellationRequested();

            // Timed out (not cancelled): inert, so the slot shows a placeholder instead of freezing.
            return new ComponentState(instance.InstanceId, new Dictionary<string, string>(), Inert: true);
        }
        finally { StateChanged -= Capture; }
    }

    public Task SendAsync(ComponentInput input, CancellationToken ct)
    {
        var env = new ComponentEnvelope
        {
            Input = new InputEvent
            {
                InstanceId = input.InstanceId, PrimitiveKey = input.PrimitiveKey, Kind = input.Kind,
            },
        };
        try
        {
            _bus.SendTo(_peerIdentity, env.ToByteArray());
        }
        catch (ObjectDisposedException)
        {
            // Whole-branch review Fix 8: SendTo enqueues onto the bus's NetMQQueue, which
            // ComponentBusServer.Dispose() disposes. This channel's own teardown order is not
            // guaranteed to run before every caller's — a settings re-apply or a bar shutdown can
            // race this against the bus's own Dispose — and a send into a torn-down bus must cost
            // this one input event, not throw out of a component's input path.
        }
        return Task.CompletedTask;
    }

    // internal (not private): Bevel.Taskbar.csproj already declares
    // <InternalsVisibleTo Include="Bevel.Taskbar.Tests" /> for WorkAreaMitigator. The bus itself
    // binds an authenticated identity to its claimed instance, so a wrong-instance envelope never
    // reaches this handler over the wire — this seam lets a test drive the handler directly and
    // isolate THIS guard from that bus-level one.
    internal void OnBusMessage(string identity, ComponentEnvelope env)
    {
        if (identity != _peerIdentity) return;
        if (env.PayloadCase != ComponentEnvelope.PayloadOneofCase.State) return;

        var state = new ComponentState(
            env.State.InstanceId,
            new Dictionary<string, string>(env.State.Values),
            env.State.Inert);

        lock (_gate)
        {
            // A peer may only speak for the instance this channel was created for. Before ConnectAsync
            // has run there is no instance yet, so cache it and let the connect validate.
            if (_instanceId is not null && state.InstanceId != _instanceId) return;
            _last = state;
        }

        StateChanged?.Invoke(state);
    }

    public ValueTask DisposeAsync()
    {
        _bus.MessageReceived -= OnBusMessage;
        // Revoke the peer's authentication. A Dealer picks its own identity, so leaving it
        // authenticated after the component exits would let any later local process reuse that
        // identity and skip the handshake entirely — on loopback TCP that is the whole threat model.
        _bus.Revoke(_peerIdentity);
        return ValueTask.CompletedTask;
    }
}
