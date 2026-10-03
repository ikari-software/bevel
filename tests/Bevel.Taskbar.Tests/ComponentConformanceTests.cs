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
    private static (IComponentChannel Channel, IDisposable Scope) Make(string kind, ComponentInstance inst)
    {
        if (kind == "local")
            return (new LocalComponentChannel(
                i => new ComponentState(i.InstanceId,
                    new Dictionary<string, string> { ["folder"] = i.Settings.GetValueOrDefault("folder", "") },
                    false)),
                new NullScope());

        // Start() is separate from the constructor on purpose: an assertion thrown inside a ctor
        // leaves the half-built scope unreachable by `using`, so the bus is never disposed and the
        // test host hangs on NetMQ's threads.
        var scope = new RemoteScope(inst);
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
        var (ch, scope) = Make(kind, inst);
        using (scope)
        await using (ch)
        {
            var state = await ch.ConnectAsync(inst, CancellationToken.None);
            Assert.Equal(inst.InstanceId, state.InstanceId);
            Assert.False(state.Inert);
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

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task Input_is_accepted_without_throwing(string kind)
    {
        var inst = StackInstance("/Downloads");
        var (ch, scope) = Make(kind, inst);
        using (scope)
        await using (ch)
        {
            await ch.ConnectAsync(inst, CancellationToken.None);
            await ch.SendAsync(new ComponentInput(inst.InstanceId, "grid", "tapped"), CancellationToken.None);
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
