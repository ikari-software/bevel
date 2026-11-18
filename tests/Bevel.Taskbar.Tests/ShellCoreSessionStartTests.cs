using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Bevel.App.ShellCore;
using Bevel.ShellCore.Ipc;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// <see cref="ShellCoreClient.StartSessionAsync"/> completes only once the core has ANSWERED the Hello
/// (bevel-j5mr).
///
/// <para>Two paths send the first Hello on a link — StartSessionAsync itself and ConnectOnceAsync's
/// on-connect re-Hello (bevel-pa3j) — and a per-link claim lets only one of them go. The loser used to
/// return at once, so a session start could "complete" while its Hello was still in flight. The core
/// registers a connection for broadcasts only AFTER its handshake reply, and reads requests only after
/// registering, so an answered Hello is the one thing that proves this client receives every delta that
/// follows. A caller that raised deltas right after an early return could lose them: that was the
/// CoreWindowTrafficTests flake ("all six changes arrive").</para>
/// </summary>
public sealed class ShellCoreSessionStartTests
{
    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private static string NewSocketPath()
        => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bvlsss-{Guid.NewGuid():N}"[..14] + ".sock");

    /// <summary>A core whose Hello answer is held until the test releases it — so "is the Hello
    /// answered yet" is decided by the test, not by the scheduler.</summary>
    private sealed class GatedCore : IAsyncDisposable
    {
        public readonly TaskCompletionSource HelloSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Hellos;
        public readonly UdsMessageServer Server;

        public GatedCore(string path, byte[] nonce)
        {
            Server = new UdsMessageServer(path, nonce, async (_, payload, ct) =>
            {
                if (CoreProtocol.Deserialize<CoreCommand>(payload.Span).Kind == CoreCommandKind.Hello)
                {
                    Interlocked.Increment(ref Hellos);
                    HelloSeen.TrySetResult();
                    await Release.Task.WaitAsync(ct).ConfigureAwait(false);
                }
                return CoreProtocol.Serialize(CoreResponse.Success());
            });
            Server.Start();
        }

        public ValueTask DisposeAsync() => Server.DisposeAsync();
    }

    /// <summary>The deterministic form of the race: a second session start that finds the link's Hello
    /// already claimed must wait for THAT Hello's answer, not return because someone else sent it. The
    /// first caller is held to the same rule whichever internal path won its claim.</summary>
    [Fact]
    public async Task A_session_start_does_not_complete_before_the_core_answers_its_Hello()
    {
        var path = NewSocketPath();
        var nonce = RandomNumberGenerator.GetBytes(32);
        await using var gated = new GatedCore(path, nonce);
        await using var core = new ShellCoreClient(path, nonce);

        var first = core.StartSessionAsync(Ct);
        await gated.HelloSeen.Task.WaitAsync(Ct);        // the link's Hello is at the core, unanswered
        var second = core.StartSessionAsync(Ct);

        Assert.False(first.IsCompleted, "the session start returned while its Hello was still unanswered");
        Assert.False(second.IsCompleted, "a session start that lost the per-link Hello claim returned "
            + "without waiting for the winner's Hello to be answered");

        gated.Release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(Ct);

        Assert.Equal(1, Volatile.Read(ref gated.Hellos));   // still ONE Hello per link — the claim holds
    }
}
