using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Bevel.App;
using Bevel.App.ShellCore;
using Bevel.Core.Vfs;
using Bevel.Interop;
using Bevel.ShellCore.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The taskbar↔Explorer automation channel (bevel-uldj): with each Explorer hosting a control server
/// that answers forwarded verbs against its in-process surface, the persistent taskbar can
/// aggregate <c>query windows</c>/<c>query selection</c> across live Explorers and forward <c>select</c>
/// to the one that owns a returned <see cref="WindowRef"/>. These tests stand up REAL control servers
/// (over real Unix-domain sockets) backed by a recording fake surface — no Explorer process, no
/// Avalonia — and drive the real <see cref="TaskbarExplorerControlClient"/>.
/// </summary>
public sealed class ExplorerControlChannelTests : IDisposable
{
    private readonly string _dir;
    private readonly byte[] _nonce = RandomNumberGenerator.GetBytes(16);
    private readonly List<UdsMessageServer> _servers = new();
    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    public ExplorerControlChannelTests()
    {
        // Short stem: macOS sun_path is 104 bytes, and the per-pid socket path nests under this dir.
        _dir = Path.Combine(Path.GetTempPath(), $"bvlex-{Guid.NewGuid():N}"[..12]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        foreach (var s in _servers) s.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static VfsPath F(string p) => new("file", p);

    private TaskbarExplorerControlClient Client() =>
        new(_dir, _nonce, dialTimeout: TimeSpan.FromSeconds(3));

    /// <summary>Binds a control server for <paramref name="pid"/> backed by <paramref name="surface"/>,
    /// exactly as the Explorer role would — the taskbar discovers it by its <c>explorer-&lt;pid&gt;.sock</c>.</summary>
    private FakeExplorer StartExplorer(int pid, params (int Id, string[] Selection)[] windows)
    {
        var surface = new FakeSurface(windows);
        var server = new UdsMessageServer(
            Path.Combine(_dir, $"explorer-{pid}.sock"), _nonce,
            async (_, payload, ct) =>
            {
                var req = ExplorerProtocol.Deserialize<ExplorerRequest>(payload.Span);
                var reply = await surface.HandleAsync(req);
                return ExplorerProtocol.Serialize(reply);
            });
        server.Start();
        _servers.Add(server);
        return new FakeExplorer(pid, surface);
    }

    // ── DI wiring: the taskbar surface gets the channel client; other roles default it to null ─────

    [Fact]
    public void Taskbar_role_wires_the_channel_client_into_the_spawning_surface()
    {
        // Point discovery at env so resolving the client's nonce doesn't touch the default temp token.
        Environment.SetEnvironmentVariable("BEVEL_EXPLORER_TOKEN", Convert.ToHexString(_nonce));
        Environment.SetEnvironmentVariable("BEVEL_EXPLORER_DIR", _dir);
        try
        {
            var services = new ServiceCollection();
            services.AddBevelModules(ShellRole.Taskbar);

            // The channel client is registered for the taskbar role...
            Assert.Contains(services, d => d.ServiceType == typeof(TaskbarExplorerControlClient));

            using var sp = services.BuildServiceProvider();
            // ...and the SpawningShellSurface (with its optional client ctor param) resolves cleanly.
            var surface = sp.GetRequiredService<IShellSurface>();
            Assert.IsType<SpawningShellSurface>(surface);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BEVEL_EXPLORER_TOKEN", null);
            Environment.SetEnvironmentVariable("BEVEL_EXPLORER_DIR", null);
        }
    }

    // ── query windows aggregates across Explorers, frontmost-first ────────────────────────────────

    [Fact]
    public async Task Query_windows_aggregates_across_explorers_frontmost_first()
    {
        // Two Explorers; the second is more recently focused (higher tick), so its windows come first.
        var a = StartExplorer(1001, (1, Array.Empty<string>()));
        a.Surface.FocusTicks = 10;
        var b = StartExplorer(1002, (1, Array.Empty<string>()), (2, Array.Empty<string>()));
        b.Surface.FocusTicks = 20;
        b.Surface.FrontId = 2; // window 2 is frontmost within Explorer b

        var windows = await Client().AggregateWindowsAsync(Ct);

        // Explorer b first (higher focus tick), its front window (2) leading, then its window 1; then a's.
        Assert.Equal(3, windows.Count);
        // The frontmost of all is b's front window — element 0. Each Explorer's windows form a
        // contiguous run sharing one regId; assert the runs' shapes.
        var (regB, localsB) = DecodeExplorer(windows, index: 0, take: 2);
        Assert.Equal(new[] { 2, 1 }, localsB);
        var (regA, localsA) = DecodeExplorer(windows, index: 2, take: 1);
        Assert.Equal(new[] { 1 }, localsA);
        Assert.NotEqual(regA, regB); // the two Explorers got distinct reg namespaces
    }

    // ── the composite WindowRef round-trips: select routes back to the owning Explorer ────────────

    [Fact]
    public async Task Select_forwards_to_the_explorer_that_owns_the_returned_ref()
    {
        var a = StartExplorer(1001, (1, Array.Empty<string>()));
        a.Surface.FocusTicks = 5;
        var b = StartExplorer(1002, (7, Array.Empty<string>()));
        b.Surface.FocusTicks = 9; // b is frontmost
        var client = Client();

        // query windows (assigns stable regIds) → the frontmost ref is b's window 7.
        var windows = await client.AggregateWindowsAsync(Ct);
        var frontmost = windows[0];

        await client.SelectAsync(frontmost, new[] { F("/tmp/pick.txt") }, Ct);

        // The select landed on Explorer b's window 7 — NOT on a.
        Assert.NotNull(b.Surface.LastSelect);
        Assert.Equal(7, b.Surface.LastSelect!.Value.LocalId);
        Assert.Equal(new[] { "vfs://file//tmp/pick.txt" }, b.Surface.LastSelect!.Value.Paths);
        Assert.Null(a.Surface.LastSelect);

        // And a later select on the SAME ref still routes home (regIds are stable for the client's life).
        await client.SelectAsync(frontmost, new[] { F("/tmp/again.txt") }, Ct);
        Assert.Equal(7, b.Surface.LastSelect!.Value.LocalId);
        Assert.Equal(new[] { "vfs://file//tmp/again.txt" }, b.Surface.LastSelect!.Value.Paths);
    }

    [Fact]
    public async Task Query_selection_of_a_specific_window_reads_that_windows_selection()
    {
        var a = StartExplorer(1001, (1, new[] { "vfs://file//tmp/a-one.txt" }));
        a.Surface.FocusTicks = 5;
        var b = StartExplorer(1002, (3, new[] { "vfs://file//tmp/b-three.txt" }));
        b.Surface.FocusTicks = 9;
        var client = Client();

        var windows = await client.AggregateWindowsAsync(Ct);
        // Only Explorer a has a local window id 1 here, so that composite addresses a's window 1.
        var refA1 = windows.Single(w => Decode(w.Id).LocalId == 1);

        var sel = await client.QuerySelectionAsync(refA1, Ct);

        Assert.Equal(new[] { F("/tmp/a-one.txt") }, sel);
    }

    [Fact]
    public async Task Query_selection_frontmost_reads_the_frontmost_explorers_front_window()
    {
        var a = StartExplorer(1001, (1, new[] { "vfs://file//tmp/back.txt" }));
        a.Surface.FocusTicks = 5;
        var b = StartExplorer(1002, (2, new[] { "vfs://file//tmp/front.txt" }));
        b.Surface.FocusTicks = 99; // frontmost Explorer
        b.Surface.FrontId = 2;

        var sel = await Client().QuerySelectionAsync(null, Ct);

        Assert.Equal(new[] { F("/tmp/front.txt") }, sel);
    }

    // ── no Explorer registered → clean empty / no-op, never a crash ───────────────────────────────

    [Fact]
    public async Task No_explorer_registered_returns_empty_aggregates()
    {
        var client = Client(); // rendezvous dir exists but is empty

        Assert.Empty(await client.AggregateWindowsAsync(Ct));
        Assert.Empty(await client.QuerySelectionAsync(null, Ct));
    }

    // ── a dropped Explorer is pruned and select on a stale ref degrades cleanly ───────────────────

    [Fact]
    public async Task Dropped_explorer_is_pruned_and_stale_select_degrades_cleanly()
    {
        var a = StartExplorer(1001, (1, Array.Empty<string>()));
        a.Surface.FocusTicks = 5;
        var b = StartExplorer(1002, (2, Array.Empty<string>()));
        b.Surface.FocusTicks = 9;
        var client = Client();

        var windows = await client.AggregateWindowsAsync(Ct);
        var refToB = windows.First(w => Decode(w.Id).LocalId == 2);
        Assert.Equal(2, windows.Count);

        // Explorer b crashes: tear its server down WITHOUT unlinking (simulate a crash, not clean exit).
        var bServer = _servers[1];
        _servers.RemoveAt(1);
        await bServer.DisposeAsync(); // DisposeAsync unlinks; re-create a stale file to mimic a crash
        File.WriteAllText(Path.Combine(_dir, "explorer-1002.sock"), ""); // a leftover, nothing listening

        // A fresh query now sees only the live Explorer a, and the stale socket file is pruned.
        var after = await client.AggregateWindowsAsync(Ct);
        Assert.Single(after);
        Assert.Equal(1, Decode(after[0].Id).LocalId);
        Assert.False(File.Exists(Path.Combine(_dir, "explorer-1002.sock")), "stale socket should be pruned");

        // select on the now-stale ref to b degrades cleanly to an AutomationException (no crash, no hang).
        await Assert.ThrowsAsync<AutomationException>(() => client.SelectAsync(refToB, new[] { F("/x") }, Ct));
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────

    private static (int RegId, int LocalId) Decode(int composite) => (composite >> 16, composite & 0xFFFF);

    /// <summary>Decodes the composite at <paramref name="index"/> and asserts a run of
    /// <paramref name="take"/> consecutive refs all share its regId, returning (regId, localIds).</summary>
    private static (int RegId, List<int> Locals) DecodeExplorer(IReadOnlyList<WindowRef> windows, int index, int take)
    {
        var reg = Decode(windows[index].Id).RegId;
        var locals = new List<int>();
        for (var i = index; i < index + take; i++)
        {
            var (r, l) = Decode(windows[i].Id);
            Assert.Equal(reg, r);
            locals.Add(l);
        }
        return (reg, locals);
    }

    private sealed record FakeExplorer(int Pid, FakeSurface Surface);

    /// <summary>A minimal stand-in for an Explorer's in-process surface: fixed windows + per-window
    /// selection, a settable focus tick + front id, and a record of the last forwarded select.</summary>
    private sealed class FakeSurface
    {
        private readonly Dictionary<int, string[]> _windows;
        public long FocusTicks;
        public int FrontId;
        public (int LocalId, string[] Paths)? LastSelect;

        public FakeSurface((int Id, string[] Selection)[] windows)
        {
            _windows = windows.ToDictionary(w => w.Id, w => w.Selection);
            FrontId = windows.Length > 0 ? windows[0].Id : 0;
        }

        public Task<ExplorerReply> HandleAsync(ExplorerRequest req) => Task.FromResult(req.Kind switch
        {
            ExplorerCommandKind.QueryWindows => QueryWindows(),
            ExplorerCommandKind.QuerySelection => QuerySelection(req.LocalWindowId),
            ExplorerCommandKind.Select => Select(req),
            _ => ExplorerReply.Fail("unknown"),
        });

        private ExplorerReply QueryWindows()
        {
            // Front id first, then the rest ascending — same contract the real server upholds.
            var ids = _windows.Keys.OrderBy(i => i).ToList();
            if (ids.Remove(FrontId)) ids.Insert(0, FrontId);
            return new ExplorerReply(Ok: true, WindowIds: ids, FocusTicks: FocusTicks);
        }

        private ExplorerReply QuerySelection(int? localId)
        {
            var id = localId ?? FrontId;
            return _windows.TryGetValue(id, out var sel)
                ? new ExplorerReply(Ok: true, Paths: sel)
                : ExplorerReply.Fail($"window {id} not open");
        }

        private ExplorerReply Select(ExplorerRequest req)
        {
            var id = req.LocalWindowId ?? FrontId;
            if (!_windows.ContainsKey(id)) return ExplorerReply.Fail($"window {id} not open");
            LastSelect = (id, (req.Paths ?? Array.Empty<string>()).ToArray());
            return new ExplorerReply(Ok: true);
        }
    }
}
