using System.Collections.Concurrent;
using System.Text;
using Bevel.Components.V1;
using Bevel.ShellCore.Ipc;
using Google.Protobuf;
using NetMQ;
using NetMQ.Sockets;
using Xunit;

namespace Bevel.ComponentBus.Tests;

/// <summary>
/// bevel-aqr7 Task 10: on Windows NetMQ has no ipc://, so the bus is loopback TCP and ANY local
/// process can connect. The HMAC is therefore the only barrier, not defence-in-depth — and because
/// a Dealer picks its own identity, authentication must also be revocable and bound to an instance.
/// </summary>
[Collection("NetMQ")]
public class ComponentBusAuthTests
{
    private static readonly byte[] Nonce = Encoding.UTF8.GetBytes("test-nonce-0123456789");
    private const string Cap = "components";

    private static DealerSocket Peer(int port, string identity)
    {
        var s = new DealerSocket();
        s.Options.Identity = Encoding.UTF8.GetBytes(identity);
        s.Connect($"tcp://127.0.0.1:{port}");
        return s;
    }

    [Fact]
    public void A_correct_handshake_authenticates_and_binds_the_instance()
    {
        using var server = new ComponentBusServer(Nonce, Cap);
        var port = server.BindLoopback();
        using var client = Peer(port, "peer-1");

        client.SendFrame(ComponentBusServer.BuildHello("inst-1", Nonce, Cap, 1));

        Assert.True(server.WaitForPeer("peer-1", TimeSpan.FromSeconds(5)));
        Assert.True(server.TryGetInstance("peer-1", out var instance));
        Assert.Equal("inst-1", instance);
    }

    [Fact]
    public void A_wrong_hmac_is_rejected_and_discloses_nothing()
    {
        using var server = new ComponentBusServer(Nonce, Cap);
        var port = server.BindLoopback();
        using var client = Peer(port, "attacker");

        var forged = new ComponentEnvelope
        {
            Hello = new Hello
            {
                InstanceId = "inst-1",
                Handshake = ByteString.CopyFrom(Encoding.UTF8.GetBytes($"{Cap}:deadbeef")),
                ContractVersion = 1,
            },
        }.ToByteArray();
        client.SendFrame(forged);

        Assert.False(server.WaitForPeer("attacker", TimeSpan.FromSeconds(1)));
        Assert.DoesNotContain("attacker", server.AuthenticatedPeers);
        Assert.False(client.TryReceiveFrameBytes(TimeSpan.FromMilliseconds(300), out _));
    }

    [Fact]
    public void A_handshake_for_a_different_capability_is_rejected()
    {
        using var server = new ComponentBusServer(Nonce, Cap);
        var port = server.BindLoopback();
        using var client = Peer(port, "wrong-cap");

        // Correctly HMAC'd, but for a capability this bus does not serve.
        client.SendFrame(ComponentBusServer.BuildHello("inst-1", Nonce, "some.other.capability", 1));

        Assert.False(server.WaitForPeer("wrong-cap", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_non_hello_first_frame_is_rejected()
    {
        using var server = new ComponentBusServer(Nonce, Cap);
        var port = server.BindLoopback();
        using var client = Peer(port, "rude");

        client.SendFrame(ComponentBusServer.BuildState("inst-1"));

        Assert.False(server.WaitForPeer("rude", TimeSpan.FromSeconds(1)));
    }

    // A Dealer chooses its own identity. If authentication is never revoked, then after the
    // legitimate component exits — and crashing is the EXPECTED path, since quarantine exists —
    // any local process may reconnect under the same identity and skip the handshake entirely.
    [Fact]
    public void Revoking_an_identity_forces_the_next_connection_to_handshake_again()
    {
        using var server = new ComponentBusServer(Nonce, Cap);
        var port = server.BindLoopback();

        using (var first = Peer(port, "peer-1"))
        {
            first.SendFrame(ComponentBusServer.BuildHello("inst-1", Nonce, Cap, 1));
            Assert.True(server.WaitForPeer("peer-1", TimeSpan.FromSeconds(5)));
        }

        server.Revoke("peer-1");
        Assert.DoesNotContain("peer-1", server.AuthenticatedPeers);
        Assert.False(server.TryGetInstance("peer-1", out _));

        // An impostor reusing the identity with no handshake stays unauthenticated.
        using var impostor = Peer(port, "peer-1");
        impostor.SendFrame(ComponentBusServer.BuildState("inst-1"));
        Assert.False(server.WaitForPeer("peer-1", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_zero_contract_version_is_rejected()
    {
        using var server = new ComponentBusServer(Nonce, Cap);
        var port = server.BindLoopback();
        using var client = Peer(port, "v0");

        client.SendFrame(ComponentBusServer.BuildHello("inst-1", Nonce, Cap, 0));

        Assert.False(server.WaitForPeer("v0", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_malformed_frame_is_dropped_without_killing_the_bus()
    {
        using var server = new ComponentBusServer(Nonce, Cap);
        var port = server.BindLoopback();
        using var noise = Peer(port, "noise");
        noise.SendFrame(new byte[] { 0xff, 0xfe, 0xfd, 0xfc });

        // The bus must still accept a legitimate peer afterwards.
        using var good = Peer(port, "peer-2");
        good.SendFrame(ComponentBusServer.BuildHello("inst-2", Nonce, Cap, 1));
        Assert.True(server.WaitForPeer("peer-2", TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void The_two_buses_use_different_nonces_so_a_component_nonce_does_not_open_the_internal_bus()
    {
        var internalNonce = Encoding.UTF8.GetBytes("internal-nonce-xxxxx");
        Assert.NotEqual(Handshake.ComputeHmac(Nonce, Cap), Handshake.ComputeHmac(internalNonce, Cap));
    }

    [Fact]
    public void The_endpoint_file_round_trips_port_and_nonce()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bevel-bus-{Guid.NewGuid():n}.json");
        try
        {
            ComponentBusEndpointFile.Write(path, 54321, Nonce);
            var (port, nonce) = ComponentBusEndpointFile.Read(path);
            Assert.Equal(54321, port);
            Assert.Equal(Nonce, nonce);
        }
        finally { File.Delete(path); }
    }

    // Fix round 1, Finding 1 + 2: InstanceOf omitted InputEvent, so an authenticated peer could
    // claim ANY instance id in an InputEvent with no rejection — the exact "speak for a different
    // instance" case the binding check exists to prevent. This is the regression guard: it must
    // fail (the InputEvent reaches the handler) before the InstanceOf fix and pass after it.
    [Fact]
    public void An_authenticated_peer_cannot_speak_for_a_different_instance()
    {
        using var server = new ComponentBusServer(Nonce, Cap);
        var port = server.BindLoopback();
        using var client = Peer(port, "peer-1");

        var received = new ConcurrentBag<ComponentEnvelope>();
        server.MessageReceived += (_, env) => received.Add(env);

        client.SendFrame(ComponentBusServer.BuildHello("inst-1", Nonce, Cap, 1));
        Assert.True(server.WaitForPeer("peer-1", TimeSpan.FromSeconds(5)));

        // (a) A StatePublish claiming a different instance must never reach the handler.
        client.SendFrame(new ComponentEnvelope
        {
            State = new StatePublish { InstanceId = "inst-2" },
        }.ToByteArray());

        // (b) An InputEvent claiming a different instance must never reach the handler.
        client.SendFrame(new ComponentEnvelope
        {
            Input = new InputEvent { InstanceId = "inst-2", PrimitiveKey = "start-button", Kind = "click" },
        }.ToByteArray());

        // A matching-instance message DOES reach the handler — proving the guard discriminates
        // rather than silently dropping everything.
        client.SendFrame(new ComponentEnvelope
        {
            Heartbeat = new Heartbeat { InstanceId = "inst-1" },
        }.ToByteArray());

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline
               && !received.Any(e => e.PayloadCase == ComponentEnvelope.PayloadOneofCase.Heartbeat))
        {
            Thread.Sleep(20);
        }

        Assert.Contains(received, e => e.PayloadCase == ComponentEnvelope.PayloadOneofCase.Heartbeat);
        Assert.DoesNotContain(received, e => e.PayloadCase == ComponentEnvelope.PayloadOneofCase.State);
        Assert.DoesNotContain(received, e => e.PayloadCase == ComponentEnvelope.PayloadOneofCase.Input);
    }

    // Fix round 1, Finding 3: nothing previously subscribed a throwing handler, so the catch in
    // OnReceiveReady that is supposed to keep the poller alive was unverified.
    [Fact]
    public void A_throwing_subscriber_does_not_take_the_poller_down()
    {
        using var server = new ComponentBusServer(Nonce, Cap);
        var port = server.BindLoopback();
        using var client = Peer(port, "peer-1");

        server.MessageReceived += (_, _) => throw new InvalidOperationException("boom");

        client.SendFrame(ComponentBusServer.BuildHello("inst-1", Nonce, Cap, 1));
        Assert.True(server.WaitForPeer("peer-1", TimeSpan.FromSeconds(5)));

        // Trigger the throwing handler.
        client.SendFrame(new ComponentEnvelope
        {
            Heartbeat = new Heartbeat { InstanceId = "inst-1" },
        }.ToByteArray());

        // The poller must still be alive afterwards: an unrelated second peer can still
        // authenticate. One bad component must not take the whole bus — and therefore the bar —
        // down.
        using var good = Peer(port, "peer-2");
        good.SendFrame(ComponentBusServer.BuildHello("inst-2", Nonce, Cap, 1));
        Assert.True(server.WaitForPeer("peer-2", TimeSpan.FromSeconds(5)));
    }
}
