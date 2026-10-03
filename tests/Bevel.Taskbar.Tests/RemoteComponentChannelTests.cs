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
}
