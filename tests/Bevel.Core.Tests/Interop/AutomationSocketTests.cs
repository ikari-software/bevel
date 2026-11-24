using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Bevel.Core.Vfs;
using Bevel.Interop;
using Bevel.Interop.Cli;
using Xunit;

namespace Bevel.Core.Tests.Interop;

/// <summary>
/// Round-trips the bevelctl transport (08-os-interop §3.2) over a real Unix-domain socket: server +
/// client + framing + the parse→route path a real bevelctl invocation takes. Keeps a short socket
/// path (macOS caps UDS paths at ~104 chars).
/// </summary>
public sealed class AutomationSocketTests
{
    private static string TempSocket() =>
        Path.Combine(Path.GetTempPath(), $"bvl-{Guid.NewGuid():N}"[..12] + ".sock");

    private static AutomationSocketServer StartServer(string path, IShellAutomation automation)
    {
        var router = new AutomationCommandRouter(automation);
        var server = new AutomationSocketServer(path, async (args, ct) =>
        {
            var (cmd, err) = BevelCtlParser.Parse(args);
            return err is not null ? new CommandResult(ExitCodes.BadArgs, err) : await router.ExecuteAsync(cmd!, ct);
        });
        server.Start();
        return server;
    }

    [Fact]
    public async Task Round_trips_query_version()
    {
        var path = TempSocket();
        await using var server = StartServer(path, new StubAutomation());

        var result = await AutomationSocketClient.SendAsync(path, new[] { "query", "version" });

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Equal("9.9", result.Output);
    }

    [Fact]
    public async Task Round_trips_multiline_output_intact()
    {
        var path = TempSocket();
        await using var server = StartServer(path, new StubAutomation());

        // duplicate of two items → two lines: the framing must not truncate at the newline.
        var result = await AutomationSocketClient.SendAsync(path, new[] { "duplicate", "/a", "/b" });

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Equal("/a copy\n/b copy", result.Output);
    }

    [Fact]
    public async Task Bad_args_return_exit_two_over_the_wire()
    {
        var path = TempSocket();
        await using var server = StartServer(path, new StubAutomation());

        var result = await AutomationSocketClient.SendAsync(path, new[] { "frobnicate" });

        Assert.Equal(ExitCodes.BadArgs, result.ExitCode);
        Assert.Contains("unknown command", result.Output);
    }

    [Fact]
    public async Task Second_instance_on_a_live_bevelctl_socket_stands_down_and_leaves_the_first_serving()
    {
        // bevel-wio0: the old Start() unlinked the path unconditionally, so a second shell silently
        // took bevelctl over and the first's exit then deleted the second's socket. Now the second
        // stands down (Start throws) and neither its Start nor its Dispose touches the incumbent.
        var path = TempSocket();
        await using var first = StartServer(path, new StubAutomation());

        var second = new AutomationSocketServer(path, (_, _) => Task.FromResult(new CommandResult(ExitCodes.Ok, "second")));
        Assert.Throws<Bevel.ShellCore.Ipc.UdsSocketBusyException>(second.Start);
        await second.DisposeAsync();

        Assert.True(File.Exists(path));
        var result = await AutomationSocketClient.SendAsync(path, new[] { "query", "version" });
        Assert.Equal("9.9", result.Output); // answered by the FIRST server
    }

    [Fact]
    public async Task Stale_bevelctl_socket_from_a_crashed_run_is_reclaimed()
    {
        // Windows: Socket.Dispose deletes BoundFileName — no leftover stale socket file to reclaim.
        if (OperatingSystem.IsWindows()) return;

        var path = TempSocket();
        // Crash: the listener's fd closes but its file outlives it (a managed Socket.Bind would delete
        // the path on Dispose and hide the scenario — the claim's own listener does not).
        Bevel.ShellCore.Ipc.UdsSocketClaim.BindListener(path, backlog: 1).Dispose();
        Assert.True(File.Exists(path));

        await using var server = StartServer(path, new StubAutomation()); // must not throw

        var result = await AutomationSocketClient.SendAsync(path, new[] { "query", "version" });
        Assert.Equal(ExitCodes.Ok, result.ExitCode);
    }

    // ── bevel-wcqr: every connection must open with an HMAC hello keyed by the token file ──

    [Fact]
    public async Task Connection_without_a_valid_hello_is_refused_and_runs_nothing()
    {
        var path = TempSocket();
        var invoked = 0;
        var server = new AutomationSocketServer(path, (_, _) =>
        {
            invoked++;
            return Task.FromResult(new CommandResult(ExitCodes.Ok, "ran"));
        });
        await using var _ = server;
        server.Start();

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
        await using var stream = new NetworkStream(socket, ownsSocket: true);
        // First frame is raw argv, not a hello — the server must refuse before dispatching.
        await AutomationSocket.WriteFrameAsync(stream,
            AutomationSocket.EncodeArgs(new[] { "query", "version" }), CancellationToken.None);
        var response = await AutomationSocket.ReadFrameAsync(stream, CancellationToken.None);
        var result = AutomationSocket.DecodeResult(response);

        Assert.Equal(ExitCodes.ShellNotRunning, result.ExitCode);
        Assert.Contains("not authorized", result.Output);
        Assert.Equal(0, invoked);
    }

    [Fact]
    public async Task Token_file_is_written_on_start_and_removed_when_the_server_stops()
    {
        var path = TempSocket();
        var tokenPath = AutomationSocket.TokenPathFor(path);
        var server = StartServer(path, new StubAutomation());
        Assert.True(File.Exists(tokenPath), "Start() must drop the token file next to the socket");

        await server.DisposeAsync();

        Assert.False(File.Exists(tokenPath), "Dispose must remove the token file this instance wrote");
    }

    private sealed class StubAutomation : IShellAutomation
    {
        public Task<RevealResult> RevealAsync(IReadOnlyList<VfsPath> items, RevealOptions opts, CancellationToken ct)
            => Task.FromResult(new RevealResult(new WindowRef(1), items.Count));
        public Task<WindowRef> OpenAsync(VfsPath c, OpenOptions o, CancellationToken ct) => Task.FromResult(new WindowRef(1));
        public Task SelectAsync(WindowRef w, IReadOnlyList<VfsPath> items, CancellationToken ct) => Task.CompletedTask;
        public Task<VfsPath> MakeAsync(VfsPath parent, NewItemKind kind, string? name, CancellationToken ct)
            => Task.FromResult(new VfsPath("file", Path.Combine(parent.Value, name ?? "x")));
        public Task DeleteAsync(IReadOnlyList<VfsPath> items, DeleteMode mode, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<VfsPath>> DuplicateAsync(IReadOnlyList<VfsPath> items, VfsPath? target, CancellationToken ct)
        {
            IReadOnlyList<VfsPath> r = items.Select(i => new VfsPath("file", i.Value + " copy")).ToArray();
            return Task.FromResult(r);
        }

        public Task<IReadOnlyList<VfsPath>> MoveAsync(IReadOnlyList<VfsPath> items, VfsPath destination, CancellationToken ct)
        {
            IReadOnlyList<VfsPath> r = items.Select(i => new VfsPath("file", $"{destination.Value}/{i.FileName}")).ToArray();
            return Task.FromResult(r);
        }
        public Task<BevelStateSnapshot> QueryAsync(AutomationQuery query, CancellationToken ct)
            => Task.FromResult(new BevelStateSnapshot { Version = "9.9" });
        public Task SetAsync(AutomationTarget t, AutomationProperty p, string v, CancellationToken ct) => Task.CompletedTask;
        public Task LaunchAsync(string appId, CancellationToken ct) => Task.CompletedTask;

        public Task QuitAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
