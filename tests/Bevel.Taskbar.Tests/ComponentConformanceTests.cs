using System.Text;
using Bevel.Components.V1;
using Bevel.Core.Components;
using Bevel.ComponentBus;
using Bevel.Taskbar.Components;
using Google.Protobuf;
using NetMQ;
using NetMQ.Sockets;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-aqr7 Task 13: the conformance suite. Every assertion runs against BOTH channel
/// implementations, because that identity is the whole argument for the contract being public rather
/// than theoretical — if a built-in can do something a third-party component cannot, the contract has
/// grown a privileged shortcut and this suite fails.
/// </summary>
// [Collection("NetMQ")] is REQUIRED, not decorative: this suite stands up a real
// ComponentBusServer, and NetMqAssemblyFixture's Cleanup must not run until every NetMQ-using
// test in the assembly has finished. Without joining that collection xUnit may finish the "NetMQ"
// collection first and tear down the shared context while these remote cases still need it.
// DisableTestParallelization prevents concurrent corruption but NOT this ordering dependency.
[Collection("NetMQ")]
public class ComponentConformanceTests
{
    public static IEnumerable<object[]> Channels() => new[]
    {
        new object[] { "local" },
        new object[] { "remote" },   // a REAL bus and a real socket, not an in-process double
    };

    /// <summary>
    /// Builds a channel plus whatever must stay alive behind it. "remote" stands up an actual
    /// ComponentBusServer and a Dealer peer, because a conformance suite that compares a built-in
    /// against a test double proves nothing about the IPC path.
    /// </summary>
    /// <param name="beforeStart">
    /// Runs after the channel exists but before the remote peer's one-shot handshake+publish (local's
    /// callback runs before <c>ConnectAsync</c> is ever invoked, which is the only point its own event
    /// can fire, so the hook's placement is a no-op there). Exists ONLY for
    /// <see cref="Connect_returns_state_for_the_instance_that_asked"/>, which must subscribe to
    /// <c>StateChanged</c> before the remote peer's single publish — not after — because that publish
    /// happens synchronously inside <c>Start</c>, on its own schedule, same as a real component process.
    /// Subscribing afterward races the bus's own processing thread: under load (see bevel-aqr7 fix
    /// round 1, Finding 2) the publish is frequently already cached by the time a late subscriber
    /// attaches, so the external listener never observes the live firing even though
    /// <c>ConnectAsync</c>'s return value is still correct via <c>RemoteComponentChannel</c>'s cache.
    /// The other four theories don't call this overload and are unaffected: they only read
    /// <c>ConnectAsync</c>'s return value, which the cache already makes order-independent by design
    /// (ambiguity #3 in the original brief — the one-shot publish deliberately races ahead of connect).
    /// </param>
    private static (IComponentChannel Channel, IDisposable Scope) Make(
        string kind, ComponentInstance inst, Action<IComponentChannel>? beforeStart = null)
    {
        if (kind == "local")
        {
            var local = new LocalComponentChannel(
                i => new ComponentState(i.InstanceId,
                    new Dictionary<string, string> { ["folder"] = i.Settings.GetValueOrDefault("folder", "") },
                    false));
            beforeStart?.Invoke(local);
            return (local, new NullScope());
        }

        // Start() is separate from the constructor on purpose: an assertion thrown inside a ctor
        // leaves the half-built scope unreachable by `using`, so the bus is never disposed and the
        // test host hangs on NetMQ's threads.
        var scope = new RemoteScope(inst);
        beforeStart?.Invoke(scope.Channel);
        try { scope.Start(inst); }
        catch { scope.Dispose(); throw; }
        return (scope.Channel, scope);
    }

    private sealed class NullScope : IDisposable { public void Dispose() { } }

    /// <summary>Owns the bus and the peer for one remote-channel case, and answers its state.</summary>
    private sealed class RemoteScope : IDisposable
    {
        private static readonly byte[] Nonce = Encoding.UTF8.GetBytes("conformance-nonce-012");
        private const string Cap = "components";

        private readonly ComponentBusServer _bus;
        private readonly DealerSocket _peer = new();
        private readonly int _port;
        public RemoteComponentChannel Channel { get; }

        /// <summary>Allocates only. Anything that can throw belongs in <see cref="Start"/>.</summary>
        public RemoteScope(ComponentInstance inst)
        {
            _bus = new ComponentBusServer(Nonce, Cap);
            _port = _bus.BindLoopback();
            Channel = new RemoteComponentChannel(_bus, "peer-" + inst.InstanceId, connectTimeoutMs: 4000);
        }

        /// <summary>Handshakes and publishes the instance's state, as a real component process would.</summary>
        public void Start(ComponentInstance inst)
        {
            var identity = "peer-" + inst.InstanceId;
            _peer.Options.Identity = Encoding.UTF8.GetBytes(identity);
            _peer.Connect($"tcp://127.0.0.1:{_port}");
            _peer.SendFrame(ComponentBusServer.BuildHello(inst.InstanceId, Nonce, Cap, 1));
            Assert.True(_bus.WaitForPeer(identity, TimeSpan.FromSeconds(5)));

            var env = new ComponentEnvelope { State = new StatePublish { InstanceId = inst.InstanceId } };
            env.State.Values.Add("folder", inst.Settings.GetValueOrDefault("folder", ""));
            _peer.SendFrame(env.ToByteArray());
        }

        /// <summary>
        /// Reads one InputEvent the bar forwarded to this peer, or null if none arrives in time.
        /// Exists so the input leg of the conformance suite can assert DELIVERY rather than merely
        /// that SendAsync did not throw.
        /// </summary>
        public ComponentInput? TryReceiveInput(TimeSpan timeout)
        {
            if (!_peer.TryReceiveFrameBytes(timeout, out var payload)) return null;
            ComponentEnvelope env;
            try { env = ComponentEnvelope.Parser.ParseFrom(payload); }
            catch (InvalidProtocolBufferException) { return null; }
            if (env.PayloadCase != ComponentEnvelope.PayloadOneofCase.Input) return null;
            return new ComponentInput(env.Input.InstanceId, env.Input.PrimitiveKey, env.Input.Kind);
        }

        public void Dispose()
        {
            _peer.Dispose();
            _bus.Dispose();
        }
    }

    private static ComponentInstance StackInstance(string folder) => new(
        ComponentInstance.NewId(), TaskbarComponentTypes.Stack,
        new Dictionary<string, string> { ["folder"] = folder }, Visible: true);

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task Connect_returns_state_for_the_instance_that_asked(string kind)
    {
        var inst = StackInstance("/Downloads");

        // Subscribe BEFORE the remote peer's one-shot publish (not merely before ConnectAsync — see
        // Make's beforeStart doc), and assert the event fired with the same state the connect
        // returned. Without this, deleting `StateChanged?.Invoke(state)` from LocalComponentChannel
        // would leave every conformance case passing — the event is part of the contract a
        // third-party component relies on, so it has to be observed.
        //
        // Captured via a TaskCompletionSource rather than a plain field assigned from the handler:
        // `StateChanged` fires on the NetMQ poller thread (see RemoteComponentChannel's own docs),
        // so a bare `captured = s` field write observed later by a plain read on the test thread has
        // no synchronizing operation tying the two together and was intermittently stale under load
        // even with subscribe-before-publish correctly ordered. TrySetResult/await is the same
        // completion-signalling primitive RemoteComponentChannel's own ConnectAsync already uses
        // internally (its `first`/`Capture` pair) — reusing it here is the proven-correct fix, not a
        // new pattern. The bounded wait below still cannot hang: it races the capture against a 5s
        // timeout and fails explicitly rather than blocking forever.
        var capturedTcs = new TaskCompletionSource<ComponentState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (ch, scope) = Make(kind, inst, c => c.StateChanged += s => capturedTcs.TrySetResult(s));
        using (scope)
        await using (ch)
        {
            var state = await ch.ConnectAsync(inst, CancellationToken.None);
            Assert.Equal(inst.InstanceId, state.InstanceId);
            Assert.False(state.Inert);

            var winner = await Task.WhenAny(capturedTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(capturedTcs.Task, winner);
            var captured = await capturedTcs.Task;
            Assert.Equal(state.InstanceId, captured.InstanceId);
            Assert.Equal(state.Inert, captured.Inert);
        }
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task An_instances_own_settings_reach_its_state(string kind)
    {
        var inst = StackInstance("/Pictures");
        var (ch, scope) = Make(kind, inst);
        using (scope)
        await using (ch)
        {
            var state = await ch.ConnectAsync(inst, CancellationToken.None);
            Assert.Equal("/Pictures", state.Values["folder"]);
        }
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task Two_instances_do_not_share_state(string kind)
    {
        var ia = StackInstance("/one");
        var ib = StackInstance("/two");
        var (ca, sa) = Make(kind, ia);
        var (cb, sb) = Make(kind, ib);
        using (sa) using (sb)
        await using (ca)
        await using (cb)
        {
            var a = await ca.ConnectAsync(ia, CancellationToken.None);
            var b = await cb.ConnectAsync(ib, CancellationToken.None);
            Assert.NotEqual(a.InstanceId, b.InstanceId);
            Assert.Equal("/one", a.Values["folder"]);
            Assert.Equal("/two", b.Values["folder"]);
        }
    }

    // Named for what it actually proves. "Does not throw" is nearly vacuous: LocalComponentChannel's
    // SendAsync is `=> Task.CompletedTask` by construction, so nothing could make the local leg fail,
    // and an implementation that silently dropped every input would pass. The remote leg therefore
    // asserts the envelope REACHES the peer; the local leg cannot be strengthened further until a
    // real built-in stack implementation exists to observe, which is out of this task's scope.
    [Theory]
    [MemberData(nameof(Channels))]
    public async Task Input_reaches_a_remote_peer_and_is_accepted_locally(string kind)
    {
        var inst = StackInstance("/Downloads");
        var (ch, scope) = Make(kind, inst);
        using (scope)
        await using (ch)
        {
            await ch.ConnectAsync(inst, CancellationToken.None);
            await ch.SendAsync(new ComponentInput(inst.InstanceId, "grid", "tapped"), CancellationToken.None);

            if (scope is RemoteScope remote)
            {
                var delivered = remote.TryReceiveInput(TimeSpan.FromSeconds(5));
                Assert.NotNull(delivered);
                Assert.Equal(inst.InstanceId, delivered!.InstanceId);
                Assert.Equal("grid", delivered.PrimitiveKey);
                Assert.Equal("tapped", delivered.Kind);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task Disposing_twice_is_safe(string kind)
    {
        var inst = StackInstance("/Downloads");
        var (ch, scope) = Make(kind, inst);
        using (scope)
        {
            await ch.DisposeAsync();
            await ch.DisposeAsync();
        }
    }

    [Fact]
    public void The_stack_manifest_is_valid_and_multi_instance()
    {
        var m = StackComponentManifest.Create();
        Assert.True(ManifestValidator.Validate(m).IsValid);
        Assert.True(m.MultiInstance);
        Assert.Equal(TaskbarComponentTypes.Stack, m.Id);
        Assert.Contains(m.SettingsSchema, f => f.Key == "folder");
    }
}
