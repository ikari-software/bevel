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
/// bevel-aqr7 Task 11: the bar-side handle for a component running in another process. The bar hosts
/// the bus (RouterSocket) and the component connects to it (DealerSocket), so this drives a real
/// socket — no in-process double.
/// </summary>
[Collection("NetMQ")]
public class RemoteComponentChannelTests
{
    private static readonly byte[] Nonce = Encoding.UTF8.GetBytes("remote-nonce-01234567");
    private const string Cap = "components";

    /// <summary>A stand-in for the component PROCESS: connects, says Hello, then answers with state.</summary>
    private sealed class FakePeer : IDisposable
    {
        private readonly DealerSocket _sock = new();
        public FakePeer(int port, string identity, string instanceId)
        {
            _sock.Options.Identity = Encoding.UTF8.GetBytes(identity);
            _sock.Connect($"tcp://127.0.0.1:{port}");
            _sock.SendFrame(ComponentBusServer.BuildHello(instanceId, Nonce, Cap, 1));
        }
        public void PublishFolder(string instanceId, string folder)
        {
            var env = new ComponentEnvelope { State = new StatePublish { InstanceId = instanceId, Inert = false } };
            env.State.Values.Add("folder", folder);
            _sock.SendFrame(env.ToByteArray());
        }
        public void Dispose() => _sock.Dispose();
    }

    [Fact]
    public async Task Connect_surfaces_the_state_the_remote_peer_publishes()
    {
        using var bus = new ComponentBusServer(Nonce, Cap);
        var port = bus.BindLoopback();
        var inst = new ComponentInstance("inst-1", TaskbarComponentTypes.Stack,
            new Dictionary<string, string> { ["folder"] = "/Downloads" }, true);

        using var peer = new FakePeer(port, "peer-1", inst.InstanceId);
        Assert.True(bus.WaitForPeer("peer-1", TimeSpan.FromSeconds(5)));

        await using var channel = new RemoteComponentChannel(bus, "peer-1");
        var connect = channel.ConnectAsync(inst, CancellationToken.None);
        peer.PublishFolder(inst.InstanceId, "/Downloads");

        var state = await connect.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(inst.InstanceId, state.InstanceId);
        Assert.Equal("/Downloads", state.Values["folder"]);
        Assert.False(state.Inert);
    }

    [Fact]
    public async Task A_peer_that_never_answers_yields_an_inert_state_rather_than_hanging_forever()
    {
        using var bus = new ComponentBusServer(Nonce, Cap);
        var port = bus.BindLoopback();
        var inst = new ComponentInstance("inst-2", TaskbarComponentTypes.Stack,
            new Dictionary<string, string>(), true);

        using var peer = new FakePeer(port, "peer-2", inst.InstanceId);   // says Hello, never publishes
        Assert.True(bus.WaitForPeer("peer-2", TimeSpan.FromSeconds(5)));

        await using var channel = new RemoteComponentChannel(bus, "peer-2", connectTimeoutMs: 500);
        var state = await channel.ConnectAsync(inst, CancellationToken.None);
        Assert.True(state.Inert);   // a hang is a failure: the slot goes inert, it does not freeze
    }

    [Fact]
    public async Task State_published_for_a_different_instance_is_ignored()
    {
        using var bus = new ComponentBusServer(Nonce, Cap);
        var port = bus.BindLoopback();
        var mine = new ComponentInstance("inst-mine", TaskbarComponentTypes.Stack,
            new Dictionary<string, string>(), true);

        using var peer = new FakePeer(port, "peer-3", mine.InstanceId);
        Assert.True(bus.WaitForPeer("peer-3", TimeSpan.FromSeconds(5)));

        await using var channel = new RemoteComponentChannel(bus, "peer-3", connectTimeoutMs: 600);
        var connect = channel.ConnectAsync(mine, CancellationToken.None);
        peer.PublishFolder("someone-else", "/Elsewhere");

        var state = await connect.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(state.Inert);                       // our instance never got state
        Assert.DoesNotContain("folder", state.Values);  // and did not absorb another instance's
    }

    /// <summary>
    /// Review fix round 1, Finding 1: a caller that cancels deliberately (bar teardown, a window
    /// closing mid-connect) must see that cancellation, not an inert state indistinguishable from
    /// "this component is unresponsive" — otherwise the health budget would quarantine a component
    /// that was perfectly healthy, because the bar gave up on it rather than the reverse.
    /// </summary>
    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_reporting_inert()
    {
        using var bus = new ComponentBusServer(Nonce, Cap);
        var port = bus.BindLoopback();
        var inst = new ComponentInstance("inst-4", TaskbarComponentTypes.Stack,
            new Dictionary<string, string>(), true);

        using var peer = new FakePeer(port, "peer-4", inst.InstanceId);   // says Hello, never publishes
        Assert.True(bus.WaitForPeer("peer-4", TimeSpan.FromSeconds(5)));

        // A long connect timeout, so only the caller's own cancellation can end the wait — a passing
        // test here must be because of the cancel, not a race against the timeout.
        await using var channel = new RemoteComponentChannel(bus, "peer-4", connectTimeoutMs: 10_000);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => channel.ConnectAsync(inst, cts.Token)).WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Review fix round 1, Item 3: the wire path can never exercise this — <c>ComponentBusServer</c>
    /// binds an authenticated identity to the instance it claimed in its Hello, so a wrong-instance
    /// envelope is dropped by the BUS before <c>MessageReceived</c> fires (see
    /// <see cref="State_published_for_a_different_instance_is_ignored"/>, which proves that bus-level
    /// behaviour but never reaches this channel's own guard). Driving <c>OnBusMessage</c> directly —
    /// via the same <c>InternalsVisibleTo</c> seam this test assembly already uses for
    /// WorkAreaMitigator — isolates the channel's guard from the bus's.
    /// </summary>
    [Fact]
    public async Task OnBusMessage_ignores_state_for_a_different_instance_even_bypassing_the_bus()
    {
        using var bus = new ComponentBusServer(Nonce, Cap);
        bus.BindLoopback();
        var mine = new ComponentInstance("inst-direct", TaskbarComponentTypes.Stack,
            new Dictionary<string, string>(), true);

        await using var channel = new RemoteComponentChannel(bus, "peer-direct", connectTimeoutMs: 600);
        var connect = channel.ConnectAsync(mine, CancellationToken.None);

        var wrongInstance = new ComponentEnvelope { State = new StatePublish { InstanceId = "someone-else" } };
        wrongInstance.State.Values.Add("folder", "/Elsewhere");
        channel.OnBusMessage("peer-direct", wrongInstance);

        var state = await connect.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(state.Inert);
        Assert.DoesNotContain("folder", state.Values);
    }
}
