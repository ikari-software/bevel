using System.Text;
using NetMQ;
using NetMQ.Sockets;

namespace Bevel.ComponentBus;

/// <summary>
/// A reachable NetMQ round-trip, shipped in the product so an AOT publish cannot trim it away.
/// Exists to answer one question before the transport becomes load-bearing: does NetMQ work in a
/// NativeAOT binary with no JIT?
/// </summary>
public static class BusSelfTest
{
    /// <summary>Binds a loopback router, round-trips one frame through a dealer, returns success.</summary>
    public static bool RoundTrip()
    {
        try
        {
            using var server = new RouterSocket();
            var port = server.BindRandomPort("tcp://127.0.0.1");
            using var client = new DealerSocket();
            client.Options.Identity = Encoding.UTF8.GetBytes("selftest");
            client.Connect($"tcp://127.0.0.1:{port}");

            client.SendFrame(Encoding.UTF8.GetBytes("ping"));

            var wait = TimeSpan.FromSeconds(5);
            if (!server.TryReceiveFrameBytes(wait, out var identity)) return false;
            if (!server.TryReceiveFrameBytes(wait, out var payload)) return false;
            if (Encoding.UTF8.GetString(payload!) != "ping") return false;

            server.SendMoreFrame(identity!).SendFrame(Encoding.UTF8.GetBytes("pong"));
            if (!client.TryReceiveFrameBytes(wait, out var reply)) return false;
            return Encoding.UTF8.GetString(reply!) == "pong";
        }
        catch (Exception)
        {
            // An AOT-stripped reflection path throws rather than returning false — that is the
            // finding this method exists to surface, so report it as failure rather than crashing.
            return false;
        }
    }
}
