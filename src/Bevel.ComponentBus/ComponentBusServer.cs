using System.Collections.Concurrent;
using System.Text;
using Bevel.Components.V1;
using Bevel.ShellCore.Ipc;
using Google.Protobuf;
using NetMQ;
using NetMQ.Sockets;

namespace Bevel.ComponentBus;

/// <summary>
/// The component bus. Loopback TCP, because NetMQ has no ipc:// on Windows — which makes the HMAC
/// the ONLY barrier rather than the second one. Three consequences are designed for here:
/// a peer failing the handshake learns nothing; authentication is REVOCABLE, because a Dealer picks
/// its own identity and crashing is the expected component lifecycle; and an authenticated peer is
/// bound to the instance it claimed, so it cannot later speak for another.
/// </summary>
public sealed class ComponentBusServer : IDisposable
{
    private readonly byte[] _nonce;
    private readonly string _capability;

    // ALL socket I/O happens on the poller thread. NetMQ sockets are not thread-safe, so outbound
    // sends are posted through a NetMQQueue rather than touching the socket from a caller's thread.
    private readonly NetMQQueue<(string Identity, byte[] Payload)> _outbound = new();
    private readonly RouterSocket _socket = new();
    private readonly NetMQPoller _poller;
    private readonly ConcurrentDictionary<string, string> _authenticated = new(StringComparer.Ordinal);

    public ComponentBusServer(byte[] nonce, string capability)
    {
        _nonce = nonce;
        _capability = capability;
        _poller = new NetMQPoller { _socket, _outbound };
        _socket.ReceiveReady += OnReceiveReady;
        _outbound.ReceiveReady += OnSendReady;
    }

    /// <summary>Identities that have passed the handshake.</summary>
    public IReadOnlySet<string> AuthenticatedPeers => _authenticated.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>Raised for authenticated peers only. Fires on the poller thread.</summary>
    public event Action<string, ComponentEnvelope>? MessageReceived;

    /// <summary>Binds an EPHEMERAL loopback port and starts the poller. Returns the chosen port.</summary>
    public int BindLoopback()
    {
        var port = _socket.BindRandomPort("tcp://127.0.0.1");
        // BACKGROUND thread: RunAsync defaults to a foreground thread, which keeps a test host or
        // the app alive forever if a bus is ever leaked.
        _poller.RunAsync("bevel-component-bus", isBackgroundThread: true);
        return port;
    }

    /// <summary>Queues an envelope for one peer. Safe to call from any thread.</summary>
    public void SendTo(string identity, byte[] payload) => _outbound.Enqueue((identity, payload));

    /// <summary>The instance id an authenticated peer claimed in its Hello.</summary>
    public bool TryGetInstance(string identity, out string instanceId)
        => _authenticated.TryGetValue(identity, out instanceId!);

    /// <summary>
    /// Drops an identity's authentication. MUST be called when a component exits or is quarantined:
    /// otherwise a later process reusing that identity inherits its authentication without a
    /// handshake, which on loopback TCP is the entire threat model.
    /// </summary>
    public void Revoke(string identity) => _authenticated.TryRemove(identity, out _);

    private void OnSendReady(object? sender, NetMQQueueEventArgs<(string Identity, byte[] Payload)> e)
    {
        while (e.Queue.TryDequeue(out var item, TimeSpan.Zero))
        {
            try { _socket.SendMoreFrame(Encoding.UTF8.GetBytes(item.Identity)).SendFrame(item.Payload); }
            catch (NetMQException) { /* peer vanished mid-send; the watchdog handles the slot */ }
        }
    }

    private void OnReceiveReady(object? sender, NetMQSocketEventArgs e)
    {
        if (!e.Socket.TryReceiveFrameBytes(out var identityBytes)) return;
        if (!e.Socket.TryReceiveFrameBytes(out var payload)) return;

        var identity = Encoding.UTF8.GetString(identityBytes!);
        ComponentEnvelope env;
        try { env = ComponentEnvelope.Parser.ParseFrom(payload); }
        catch (InvalidProtocolBufferException) { return; }   // malformed: drop, never throw

        if (!_authenticated.ContainsKey(identity))
        {
            if (env.PayloadCase != ComponentEnvelope.PayloadOneofCase.Hello) return;
            if (env.Hello.ContractVersion < 1) return;

            // Reuse the shipped, reviewed handshake: it parses "capability:hmacHex", decodes the
            // hex and compares with CryptographicOperations.FixedTimeEquals.
            var presented = Handshake.ValidateHello(_nonce, env.Hello.Handshake.Span);
            if (presented is null || presented != _capability) return;
            if (string.IsNullOrEmpty(env.Hello.InstanceId)) return;

            _authenticated[identity] = env.Hello.InstanceId;
            return;
        }

        // An authenticated peer may only speak for the instance it claimed.
        if (!_authenticated.TryGetValue(identity, out var bound)) return;
        if (InstanceOf(env) is { } claimed && claimed != bound) return;

        try { MessageReceived?.Invoke(identity, env); }
        catch (Exception)
        {
            // A subscriber that throws must not kill the poller: that would take the whole bus —
            // and therefore the bar — down with one bad component (spec §6).
        }
    }

    private static string? InstanceOf(ComponentEnvelope env) => env.PayloadCase switch
    {
        ComponentEnvelope.PayloadOneofCase.State => env.State.InstanceId,
        ComponentEnvelope.PayloadOneofCase.FrameReady => env.FrameReady.InstanceId,
        ComponentEnvelope.PayloadOneofCase.Heartbeat => env.Heartbeat.InstanceId,
        _ => null,
    };

    /// <summary>Blocks until <paramref name="identity"/> authenticates, or the timeout elapses. Tests only.</summary>
    public bool WaitForPeer(string identity, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_authenticated.ContainsKey(identity)) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    /// <summary>Builds a Hello carrying the standard handshake payload.</summary>
    public static byte[] BuildHello(string instanceId, byte[] nonce, string capability, int contractVersion)
        => new ComponentEnvelope
        {
            Hello = new Hello
            {
                InstanceId = instanceId,
                Handshake = ByteString.CopyFrom(Handshake.BuildHelloPayload(nonce, capability)),
                ContractVersion = contractVersion,
            },
        }.ToByteArray();

    public static byte[] BuildState(string instanceId)
        => new ComponentEnvelope { State = new StatePublish { InstanceId = instanceId } }.ToByteArray();

    public void Dispose()
    {
        if (_poller.IsRunning) _poller.Stop();
        _poller.Dispose();
        _outbound.Dispose();
        _socket.Dispose();
    }
}
