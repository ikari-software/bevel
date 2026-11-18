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
                    // NOT bound to the server's token: a core torn down mid-Hello must never answer it (the
                    // server would otherwise reply to a cancelled handler with an empty response).
                    await Release.Task.ConfigureAwait(false);
                }
                return CoreProtocol.Serialize(CoreResponse.Success());
            });
            Server.Start();
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            Release.TrySetResult();   // free any held handler; its reply goes nowhere
        }
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

    /// <summary>A caller's token bounds only ITS wait (bevel-j5mr review): a start cancelled while the
    /// link's Hello is unanswered returns promptly, and the Hello it was waiting on is not torn down —
    /// the next start rides the same claim (still one Hello).</summary>
    [Fact]
    public async Task A_session_start_honours_cancellation_while_its_Hello_is_unanswered()
    {
        var path = NewSocketPath();
        var nonce = RandomNumberGenerator.GetBytes(32);
        await using var gated = new GatedCore(path, nonce);
        await using var core = new ShellCoreClient(path, nonce);

        using var cts = new CancellationTokenSource();
        var cancelled = core.StartSessionAsync(cts.Token);
        await gated.HelloSeen.Task.WaitAsync(Ct);
        cts.Cancel();
        // Bounded by a TimeSpan, whose expiry throws TimeoutException — so a start that ignored its token
        // fails here instead of passing on the bound's own cancellation.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(TimeSpan.FromSeconds(10)));

        var next = core.StartSessionAsync(Ct);
        Assert.False(next.IsCompleted, "the claimed Hello is still unanswered");
        gated.Release.SetResult();
        await next.WaitAsync(Ct);
        Assert.Equal(1, Volatile.Read(ref gated.Hellos));
    }

    /// <summary>A core that is up but never answers: the WINNING Hello faults at the bound, every waiter
    /// rethrows it, and the claim is released — the next start sends a fresh Hello of its own.</summary>
    [Fact]
    public async Task A_Hello_the_core_never_answers_faults_within_the_bound_and_releases_the_claim()
    {
        var path = NewSocketPath();
        var nonce = RandomNumberGenerator.GetBytes(32);
        await using var gated = new GatedCore(path, nonce);
        await using var core = new ShellCoreClient(path, nonce) { HelloTimeout = TimeSpan.FromMilliseconds(300) };

        var first = core.StartSessionAsync(Ct);
        await gated.HelloSeen.Task.WaitAsync(Ct);
        var second = core.StartSessionAsync(Ct);   // rides the same, doomed claim

        // Exact type, under Ct (whose expiry is an OperationCanceledException): only the Hello bound passes.
        await Assert.ThrowsAsync<TimeoutException>(() => first.WaitAsync(Ct));
        await Assert.ThrowsAsync<TimeoutException>(() => second.WaitAsync(Ct));
        Assert.Equal(1, Volatile.Read(ref gated.Hellos));

        gated.Release.SetResult();                 // the core recovers
        await core.StartSessionAsync(Ct).WaitAsync(Ct);
        Assert.Equal(2, Volatile.Read(ref gated.Hellos));   // a FRESH Hello — the faulted claim was released
    }

    /// <summary>The link drops while its Hello is pending: that Hello fails (it can never be answered on a
    /// dead link), and the NEW link sends its own Hello. The old link's claim neither satisfies a start on
    /// the new link nor blocks it.</summary>
    [Fact]
    public async Task A_reconnect_while_a_Hello_is_pending_sends_the_new_links_own_Hello()
    {
        var path = NewSocketPath();
        var nonce = RandomNumberGenerator.GetBytes(32);
        var gated = new GatedCore(path, nonce);
        await using var core = new ShellCoreClient(path, nonce);

        var first = core.StartSessionAsync(Ct);
        await gated.HelloSeen.Task.WaitAsync(Ct);
        await gated.DisposeAsync();                // the core dies with the Hello unanswered
        // Failed BY THE DROP — not by the Hello bound, nor by this test's own deadline.
        await Assert.ThrowsAsync<System.IO.IOException>(() => first.WaitAsync(Ct));

        await using var gated2 = new GatedCore(path, nonce);
        gated2.Release.SetResult();
        await gated2.HelloSeen.Task.WaitAsync(Ct); // the reconnect re-Hellos the new link by itself
        await core.StartSessionAsync(Ct).WaitAsync(Ct);
        Assert.Equal(1, Volatile.Read(ref gated2.Hellos));  // the start rode the new link's Hello
        Assert.Equal(1, Volatile.Read(ref gated.Hellos));
    }
}
