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
        if (!e.Socket.TryReceiveFrameBytes(out var payload, out var hasMore)) return;

        if (hasMore)
        {
            // The protocol is fixed at exactly two frames (identity, stripped by the Router, plus
            // one payload frame). Any peer sending a third frame is malformed — this socket is
            // reachable by any local process, so drain and drop the whole message rather than
            // leaving trailing frames to desync the next read.
            while (e.Socket.TryReceiveFrameBytes(out _, out hasMore) && hasMore) { }
            return;
        }

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

        // A repeat Hello is dropped HERE, before the instance-binding check below ever runs for it
        // — that check does NOT "still apply" to it (whole-branch review Fix 8: the prior comment
        // was wrong about this). InstanceOf's switch has no Hello case, so if this early return were
        // removed a repeat Hello would fall through to line 119 with InstanceOf(env) returning null,
        // trivially passing the binding check — letting an already-authenticated identity attempt to
        // REBIND to a different instanceId merely by resending Hello. This return is the actual
        // guard against repeating that past Critical, not a redundant belt-and-suspenders on top of
        // a check that would have caught it anyway.
        if (env.PayloadCase == ComponentEnvelope.PayloadOneofCase.Hello) return;

        // ThemePush is bar→component ONLY — the server pushes it DOWN to components via SendTo
        // (whole-branch review Fix 8). It carries no instance_id (see ThemePush in the .proto), so
        // InstanceOf below returns null for it and the generic binding check can never validate it:
        // a peer sending ThemePush upstream would reach MessageReceived completely unbound to any
        // instance. Drop it outright here rather than relying on a binding check that structurally
        // cannot cover it.
        if (env.PayloadCase == ComponentEnvelope.PayloadOneofCase.Theme) return;

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

    /// <summary>
    /// The instance id a non-Hello payload claims, or null for a payload with no instance_id field.
    /// INVARIANT: every NON-HELLO payload type that carries an instance_id MUST appear in this
    /// switch — an omission does not fail loudly, it returns null, which silently disables the
    /// peer-to-instance binding check in <see cref="OnReceiveReady"/> for that payload type. Hello
    /// itself is handled separately (it is dropped outright once authenticated, never reaching this
    /// switch) and never belongs here. <c>ThemePush</c> is the one payload that structurally CANNOT
    /// appear in this switch — it carries no instance_id field at all (.proto) because it is
    /// bar→component only — so <see cref="OnReceiveReady"/> drops it by PayloadCase before this
    /// method is ever consulted, rather than this switch silently returning null for it.
    /// </summary>
    private static string? InstanceOf(ComponentEnvelope env) => env.PayloadCase switch
    {
        ComponentEnvelope.PayloadOneofCase.State => env.State.InstanceId,
        ComponentEnvelope.PayloadOneofCase.Input => env.Input.InstanceId,
        ComponentEnvelope.PayloadOneofCase.FrameReady => env.FrameReady.InstanceId,
        ComponentEnvelope.PayloadOneofCase.Heartbeat => env.Heartbeat.InstanceId,
        _ => null,
    };

    /// <summary>Blocks until <paramref name="identity"/> authenticates, or the timeout elapses. Tests only
    /// (whole-branch review Fix 8: <c>internal</c>, not <c>public</c> — this assembly already grants
    /// <c>InternalsVisibleTo Bevel.ComponentBus.Tests</c>).</summary>
    internal bool WaitForPeer(string identity, TimeSpan timeout)
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
