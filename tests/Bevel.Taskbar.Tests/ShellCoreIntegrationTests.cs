using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Bevel.App.ShellCore;
using Bevel.Core;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// End-to-end shell-core test (bevel-gww.3): a real <see cref="ShellCoreServer"/> over a live Unix
/// domain socket, driven by a controllable in-memory PAL, with the actual UI-side adapters
/// (<see cref="ShellCoreWindowManager"/> / <see cref="ShellCoreAppEnvironment"/>) as the client. It
/// proves the three things the split depends on: the core hands a NEW client its full snapshot on
/// connect, PAL deltas fan out to the client as ordinary <see cref="IWindowManager"/> events, and a
/// client command round-trips to the real PAL. PAL-agnostic — no helper, no GUI, no dispatcher.
/// </summary>
public sealed class ShellCoreIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static CancellationToken Ct => new CancellationTokenSource(Timeout).Token;

    private static string NewSocketPath()
        // macOS sun_path is 104 bytes — keep the stem short (mirrors the Ipc transport tests).
        => Path.Combine(Path.GetTempPath(), $"bvlcore-{Guid.NewGuid():N}"[..14] + ".sock");

    private static byte[] NewNonce() => RandomNumberGenerator.GetBytes(32);

    private static string NewConfigDir() => Path.Combine(Path.GetTempPath(), $"bvlset-{Guid.NewGuid():N}");

    private static async Task WaitFor(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(15);
        Assert.True(condition(), because);
    }

    private static ForeignWindow Win(string id, string title, bool focused = false) =>
        new(new ForeignWindowId(id), title, AppId: "com.example." + id, IsMinimized: false,
            IsFocused: focused, Bounds: new PalRect(0, 0, 800, 600));

    private static TrayItem Tray(string id, string tooltip) => new(new TrayItemId(id), tooltip);

    // bevel-mhvu: after the Hello snapshots, both proxies must report the CORE's capabilities
    // verbatim — not their per-platform literals.
    [Fact]
    public async Task Proxies_report_the_cores_capabilities_after_the_snapshot()
    {
        // The proxies' pre-snapshot fallback is per-platform (macOS mirrors, else authoritative —
        // see ShellCoreSystemTrayHost.Fallback), so give the core the OPPOSITE mode: the asserts below
        // then prove the forwarding on every OS, not just where the fallback differs from Authoritative.
        var fallbackTrayMode = OperatingSystem.IsMacOS() ? TrayCapability.Mirrored : TrayCapability.Authoritative;
        var coreTrayMode = fallbackTrayMode == TrayCapability.Mirrored ? TrayCapability.Authoritative : TrayCapability.Mirrored;
        var pal = new ControllablePal
        {
            Capabilities = new Capabilities(Available: true, TrayMode: coreTrayMode,
                Notes: new[] { "test-pal" }, SupportsReposition: false),
        };
        pal.SeedWindows(Win("a", "Alpha"));
        pal.SeedTray(Tray("1:10", "Clock"));

        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var wm = new ShellCoreWindowManager(core);
        var tray = new ShellCoreSystemTrayHost(core);
        // Pre-snapshot the proxies still answer from their literal fallback, not the core's slot.
        Assert.NotEqual(coreTrayMode, tray.Capabilities.TrayMode);

        await core.EnsureConnectedAsync(Ct);
        await core.StartSessionAsync(Ct);
        await WaitFor(() => tray.Capabilities.TrayMode == coreTrayMode
                         && !wm.Capabilities.SupportsReposition,
            "the proxies should forward the core's capabilities after the snapshots");
        // (Record equality would compare the Notes list by reference post-JSON; compare fields.)
        foreach (var proxy in new[] { tray.Capabilities, wm.Capabilities })
        {
            Assert.Equal(pal.Capabilities.Available, proxy.Available);
            Assert.Equal(pal.Capabilities.TrayMode, proxy.TrayMode);
            Assert.Equal(pal.Capabilities.SupportsReposition, proxy.SupportsReposition);
            Assert.Equal(pal.Capabilities.Notes, proxy.Notes);
        }
    }

    // 1. On connect, the core replays its whole window projection to the new client as WindowOpened
    //    events, and the installed-app registry is answerable by a pull — the snapshot path.
    [Fact]
    public async Task NewClient_ReceivesWindowSnapshot_AndInstalledApps()
    {
        var pal = new ControllablePal();
        pal.SeedWindows(Win("a", "Alpha"), Win("b", "Beta", focused: true));
        pal.SeedInstalled(new InstalledApp("com.example.a", "Alpha", IconPath: null));

        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var opened = new ConcurrentBag<string>();
        var wm = new ShellCoreWindowManager(core);
        wm.WindowOpened += (_, w) => opened.Add(w.Id.Value);

        // Session protocol (bevel-4zfs): the snapshot arrives only on Hello — sent after the adapters
        // (which subscribed in their ctors) are wired. Connect alone must deliver nothing.
        await core.EnsureConnectedAsync(Ct);
        await Task.Delay(150);
        Assert.Empty(opened);
        await core.StartSessionAsync(Ct);
        await WaitFor(() => opened.Count == 2, "Hello should replay both seeded windows as WindowOpened");
        Assert.Equal(new[] { "a", "b" }, opened.OrderBy(x => x).ToArray());

        // Installed apps are a pull (round-trip), not a pushed event.
        var apps = new ShellCoreAppEnvironment(core);
        var installed = await apps.EnumerateInstalledAppsAsync(Ct);
        Assert.Equal("Alpha", Assert.Single(installed).DisplayName);
    }

    // 2. After connect, each PAL delta the core observes is re-raised on the client as the matching
    //    IWindowManager / IAppEnvironment event.
    [Fact]
    public async Task PalDeltas_FanOutToClient_AsWindowAndAppEvents()
    {
        var pal = new ControllablePal();
        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var wm = new ShellCoreWindowManager(core);
        var apps = new ShellCoreAppEnvironment(core);

        var opened = new TaskCompletionSource<ForeignWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        var foreground = new TaskCompletionSource<ForeignWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource<ForeignWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launched = new TaskCompletionSource<RunningApp>(TaskCreationOptions.RunContinuationsAsynchronously);
        wm.WindowOpened += (_, w) => opened.TrySetResult(w);
        wm.ForegroundChanged += (_, w) => foreground.TrySetResult(w);
        wm.WindowClosed += (_, w) => closed.TrySetResult(w);
        apps.AppLaunched += (_, a) => launched.TrySetResult(a);

        await core.EnsureConnectedAsync(Ct);
        await WaitFor(() => server.ClientCount == 1, "client should be connected before raising deltas");

        pal.RaiseWindowOpened(Win("z", "Zed"));
        Assert.Equal("Zed", (await opened.Task.WaitAsync(Timeout)).Title);

        pal.RaiseForegroundChanged(Win("z", "Zed", focused: true));
        Assert.True((await foreground.Task.WaitAsync(Timeout)).IsFocused);

        pal.RaiseWindowClosed(Win("z", "Zed"));
        Assert.Equal("z", (await closed.Task.WaitAsync(Timeout)).Id.Value);

        pal.RaiseAppLaunched(new RunningApp("com.example.z", "Zed", ProcessId: 4242));
        Assert.Equal(4242, (await launched.Task.WaitAsync(Timeout)).ProcessId);
    }

    // 3. A client command is executed against the REAL PAL (only the core touches it): activate a
    //    window, launch an app — the stub records each, proving the request round-trip.
    [Fact]
    public async Task ClientCommands_ExecuteAgainstRealPal()
    {
        var pal = new ControllablePal();
        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var wm = new ShellCoreWindowManager(core);
        var apps = new ShellCoreAppEnvironment(core);

        await wm.ActivateAsync(new ForeignWindowId("win-7"), Ct);
        await apps.LaunchAsync("/Applications/Calculator.app", Ct);

        await WaitFor(() => pal.Activated.Contains("win-7"), "core should have activated the window on the PAL");
        Assert.Contains("/Applications/Calculator.app", pal.Launched);

        // bevel-nxic: the atomic restore+activate command passes through the core to the PAL as one op.
        await wm.RestoreAndActivateAsync(new ForeignWindowId("win-9"), Ct);
        await WaitFor(() => pal.RestoredAndActivated.Contains("win-9"),
            "core should have restore+activated the window on the PAL");
    }

    // 4. A window action issued while the core link is DOWN is queued (not a dead click), then
    //    replayed against the real PAL once the client reconnects to a core on the same endpoint.
    [Fact]
    public async Task CommandIssuedWhileDisconnected_IsReplayedOnReconnect()
    {
        var pal = new ControllablePal();
        var path = NewSocketPath();
        var nonce = NewNonce();

        var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var wm = new ShellCoreWindowManager(core);
        await core.EnsureConnectedAsync(Ct);
        await WaitFor(() => core.IsConnected, "client should connect to the core");

        // Drop the link: stopping the server faults the client's receive loop -> IsConnected flips.
        await server.DisposeAsync();
        await WaitFor(() => !core.IsConnected, "client should observe the disconnect");

        // Issue a command while down: it must NOT throw and must NOT reach the PAL yet — it's queued.
        await wm.ActivateAsync(new ForeignWindowId("win-42"), Ct);
        Assert.DoesNotContain("win-42", pal.Activated);

        // Bring a core back on the SAME socket + nonce (as the supervisor-respawned core would be).
        await using var server2 = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server2.StartAsync(Ct);

        // The reconnect supervisor re-dials with backoff; on reconnect the queued action replays.
        await WaitFor(() => pal.Activated.Contains("win-42"),
            "the command queued while disconnected should replay against the PAL after reconnect");
    }

    // 4d. bevel-44vg review: the OPPOSITE for MoveWindowToSpace — a move is NOT replayable. A replayed
    //     move could hit a recycled window id after the reconnect (the WindowServer id space is reused
    //     as windows close/open), so an action issued while the link is down is dropped, not queued.
    [Fact]
    public async Task MoveWindowToSpaceIssuedWhileDisconnected_IsDroppedNotReplayedOnReconnect()
    {
        var pal = new ControllablePal();
        var path = NewSocketPath();
        var nonce = NewNonce();

        var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var wm = new ShellCoreWindowManager(core);
        await core.EnsureConnectedAsync(Ct);
        await WaitFor(() => core.IsConnected, "client should connect to the core");

        // Drop the link.
        await server.DisposeAsync();
        await WaitFor(() => !core.IsConnected, "client should observe the disconnect");

        // A move issued while down must NOT throw and must NOT reach the PAL.
        await wm.MoveWindowToSpaceAsync(new ForeignWindowId("win-42"), 250, ownerPid: 4242, Ct);
        Assert.Empty(pal.SpaceMoves);

        // Bring a core back on the SAME socket + nonce; the client reconnects…
        await using var server2 = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server2.StartAsync(Ct);
        await WaitFor(() => core.IsConnected, "client should reconnect to the respawned core");

        // …but the move is never replayed: a replayed move could hit a recycled window id.
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        Assert.Empty(pal.SpaceMoves);
    }

    // bevel-44vg review: on a LIVE link, ListSpaces (a pull, never queued) round-trips the Spaces plus
    // the window's own Space id, and MoveWindowToSpace reaches the PAL with window/space/owner pid.
    [Fact]
    public async Task ListSpacesAndMoveWindowToSpace_pass_through_on_a_connected_core()
    {
        var pal = new ControllablePal();
        pal.NextSpaces = new SpaceList(
            [new SpaceInfo(100, 0, "Desktop 1", "UUID-A", IsCurrent: true),
             new SpaceInfo(250, 0, "Desktop 2", "UUID-A", IsCurrent: false)],
            WindowSpaceId: 100);
        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var wm = new ShellCoreWindowManager(core);
        await core.EnsureConnectedAsync(Ct);
        await WaitFor(() => core.IsConnected, "client should connect to the core");

        var spaces = await wm.ListSpacesAsync(new ForeignWindowId("win-7"), Ct);
        Assert.Equal("win-7", pal.LastSpacesWindowId);
        Assert.Equal(new[] { 100UL, 250UL }, spaces.Spaces.Select(s => s.Id).ToArray());
        Assert.Equal(100UL, spaces.WindowSpaceId);

        await wm.MoveWindowToSpaceAsync(new ForeignWindowId("win-7"), 250, ownerPid: 4242, Ct);
        await WaitFor(() => pal.SpaceMoves.Count == 1, "core should have moved the window on the PAL");
        Assert.Contains(("win-7", 250UL, 4242), pal.SpaceMoves);
    }

    // 4b. A client whose VERY FIRST connect fails (it dialed before the core bound its socket — exactly
    //     what RemoteSettingsService does in LoadAsync at startup, racing core boot) must still keep
    //     retrying and connect once the core comes up. Before the fix the reconnect supervisor was armed
    //     only by the DROP of an ESTABLISHED link, so a never-connected client stayed stuck forever and
    //     the split taskbar booted on DEFAULT settings — the core's real snapshot never reached it.
    [Fact]
    public async Task ClientThatFailsItsFirstConnect_StillReconnectsWhenTheCoreComesUp()
    {
        var pal = new ControllablePal();
        var path = NewSocketPath();
        var nonce = NewNonce();

        // No server yet: the first connect must fail (nothing to dial).
        await using var core = new ShellCoreClient(path, nonce);
        await Assert.ThrowsAnyAsync<Exception>(() => core.EnsureConnectedAsync(Ct));
        Assert.False(core.IsConnected);

        // The core now comes up on the same endpoint. The supervisor — armed by the failed first connect —
        // must re-dial and connect with NO further explicit EnsureConnectedAsync call.
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await WaitFor(() => core.IsConnected,
            "a client that failed its first connect should reconnect once the core comes up");
    }

    // 4c. ...and the recovered link must actually CARRY THE SESSION (bevel-pa3j). 4b proved the socket
    //     comes back; it never checked that any data follows, and it didn't: StartSessionAsync set
    //     _sessionStarted only AFTER its connect succeeded, so a first connect that lost the race left the
    //     flag false — and the supervisor's on-connect re-Hello is gated on it. The core pushes nothing on
    //     connect, so that process stayed connected-but-empty for its whole life: no tray, and a task strip
    //     carried only by the window reconcile backstop, which the tray has no equivalent of.
    [Fact]
    public async Task A_session_requested_before_the_core_was_up_still_gets_its_snapshot()
    {
        var pal = new ControllablePal();
        pal.SeedTray(Tray("1:10", "Alpha"));
        pal.SeedWindows(Win("a", "Alpha"));
        var path = NewSocketPath();
        var nonce = NewNonce();

        // The real shape: the role wires its adapters and fires StartSessionAsync BEFORE the core has
        // bound its socket (App.axaml.cs does this fire-and-forget, so the throw is swallowed there).
        await using var core = new ShellCoreClient(path, nonce);
        var tray = new ShellCoreSystemTrayHost(core);
        var added = new ConcurrentBag<string>();
        tray.ItemAdded += (_, t) => added.Add(t.Id.Value);
        var hellos = 0;
        core.HelloSent += () => Interlocked.Increment(ref hellos);

        await Assert.ThrowsAnyAsync<Exception>(() => core.StartSessionAsync(Ct));
        Assert.False(core.IsConnected);
        Assert.Equal(0, hellos);

        // The core comes up. Nobody calls StartSessionAsync again — that call already happened and its
        // caller is long gone. The reconnect must honour the session the client was asked for.
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await WaitFor(() => added.Contains("1:10"),
            "a session requested before the core was up must be Hello'd on the recovered link, "
            + "or the peer stays connected-but-empty forever");
    }

    // 4d. Exactly ONE Hello on a normal start. Both paths can now fire for a first connect (the
    //     on-connect re-Hello no longer excludes it), and a Hello costs the core a full four-snapshot
    //     burst, so the dedup is load-bearing rather than tidiness.
    [Fact]
    public async Task A_normal_session_start_sends_exactly_one_Hello()
    {
        var pal = new ControllablePal();
        pal.SeedTray(Tray("1:10", "Alpha"));

        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var hellos = 0;
        core.HelloSent += () => Interlocked.Increment(ref hellos);
        var tray = new ShellCoreSystemTrayHost(core);
        var added = new ConcurrentBag<string>();
        tray.ItemAdded += (_, t) => added.Add(t.Id.Value);

        await core.StartSessionAsync(Ct);
        await WaitFor(() => added.Contains("1:10"), "the snapshot should arrive on the Hello");
        await Task.Delay(200);   // let a duplicate, if any, land
        Assert.Equal(1, hellos);
    }

    // 5. The tray mirrors over the shell-core bridge (bevel-m3.1.1): the core replays its tray
    //    projection as a snapshot on connect, streams live add/remove deltas, and a click issued on
    //    the UI-side host round-trips to the core's real tray host.
    [Fact]
    public async Task Tray_snapshot_deltas_and_click_bridge_through_the_core()
    {
        var pal = new ControllablePal();
        pal.SeedTray(Tray("1:10", "Alpha"), Tray("2:20", "Beta"));

        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var tray = new ShellCoreSystemTrayHost(core);
        var added = new ConcurrentBag<string>();
        var removed = new ConcurrentBag<string>();
        tray.ItemAdded += (_, t) => added.Add(t.Id.Value);
        tray.ItemRemoved += (_, t) => removed.Add(t.Id.Value);

        await core.StartSessionAsync(Ct);
        await WaitFor(() => added.Count == 2, "tray snapshot should replay both seeded items as ItemAdded");

        pal.RaiseTrayItemAdded(Tray("3:30", "Gamma"));
        await WaitFor(() => added.Contains("3:30"), "an added item should stream to the client");
        pal.RaiseTrayItemRemoved(Tray("1:10", "Alpha"));
        await WaitFor(() => removed.Contains("1:10"), "a removed item should stream to the client");

        // Click forwarding round-trips (button + modifiers + park, bevel-6fin) to the real host on the core side.
        var ok = await tray.ForwardClickAsync(new TrayItemId("2:20"), TrayButton.Right, TrayModifiers.Command);
        Assert.True(ok);
        await WaitFor(() => pal.TrayClicks.Contains("2:20:Right:Command:park=False"),
            "the forwarded click should reach the core's tray host with its button + modifiers");
        var ok2 = await tray.ForwardClickAsync(new TrayItemId("1:10"), TrayButton.Left, TrayModifiers.None, park: true);
        Assert.True(ok2);
        await WaitFor(() => pal.TrayClicks.Contains("1:10:Left:None:park=True"),
            "a park (single-item reveal) click should bridge through the core with park set");
    }

    // bevel-8ck: a tray item that appears in the subscribe→snapshot gap must NOT be lost. The core now
    // subscribes before snapshotting and buffers gap deltas; here the add fires during GetItemsAsync
    // (so it lands in the gap) and must still reach the client's connect-time snapshot.
    [Fact]
    public async Task Tray_add_during_the_snapshot_gap_is_not_lost()
    {
        var pal = new ControllablePal();
        pal.SeedTray(Tray("1:10", "Alpha"), Tray("2:20", "Beta"));
        pal.OnGetItems = () => pal.RaiseTrayItemAdded(Tray("3:30", "Gamma")); // lands in the gap

        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var tray = new ShellCoreSystemTrayHost(core);
        var added = new ConcurrentBag<string>();
        tray.ItemAdded += (_, t) => added.Add(t.Id.Value);

        await core.StartSessionAsync(Ct);
        // All three — the two seeded AND the gap add — must be in the Hello snapshot.
        await WaitFor(() => added.Contains("3:30") && added.Contains("1:10") && added.Contains("2:20"),
            "the item added during the snapshot gap must survive into the client's snapshot");
    }

    // ── Session protocol (bevel-4zfs) ─────────────────────────────────────────

    // 6. The tray's new pull backstop: GetTrayItems answers the core's live projection as plain
    //    request/response — no Hello needed, no dependence on the stream, which is what makes the
    //    tray finally reconcilable like windows/apps/settings.
    [Fact]
    public async Task GetTrayItems_pull_answers_the_cores_projection_without_a_session()
    {
        var pal = new ControllablePal();
        pal.SeedTray(Tray("1:10", "Alpha"), Tray("2:20", "Beta"));

        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var tray = new ShellCoreSystemTrayHost(core);

        var items = await tray.GetItemsAsync(Ct);   // connects lazily; no StartSessionAsync anywhere

        Assert.Equal(new[] { "1:10", "2:20" }, items.Select(i => i.Id.Value).OrderBy(v => v).ToArray());
    }

    // 7. Seed completion = epoch bump. A client that Hello'd while the core was still seeding got only the
    //    apps+settings snapshots (window/tray are withheld mid-seed, bevel-ri8c); when the seed lands, the
    //    core bumps the epoch and pushes the real window/tray snapshots WITHOUT the client reconnecting. This is the principled version of the old
    //    BroadcastSnapshot band-aid — and the reason the tray no longer strands on an empty snapshot.
    [Fact]
    public async Task Seed_completion_bumps_the_epoch_and_repushes_without_a_reconnect()
    {
        var pal = new ControllablePal();
        pal.SeedWindows(Win("a", "Alpha"));
        // Stall the window seed: each fault costs one 150ms retry, so ~25 faults hold the core in its
        // seeding window long enough for the client to connect and Hello mid-seed.
        for (var i = 0; i < 25; i++)
            pal.EnumerateFaults.Enqueue(new TransportFault("gRPC call disposed."));

        var path = NewSocketPath();
        var nonce = NewNonce();
        var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        var startTask = server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var snapshots = new ConcurrentBag<(int Epoch, int Windows)>();
        var midSeedBaseline = 0;
        core.EventReceived += e =>
        {
            if (e.Kind == CoreEventKind.WindowSnapshot)
                snapshots.Add((e.Epoch ?? 0, (e.Windows ?? Array.Empty<ForeignWindow>()).Count));
            if (e.Kind == CoreEventKind.SettingsSnapshot && e.Epoch == 1)
                Volatile.Write(ref midSeedBaseline, 1);
        };

        await core.StartSessionAsync(Ct);   // Hello lands MID-SEED: epoch 1
        await WaitFor(() => Volatile.Read(ref midSeedBaseline) == 1,
            "the mid-seed Hello should deliver an epoch-1 baseline (settings)");
        // bevel-ri8c: the still-empty window projection is withheld mid-seed (it would wipe a reconciling client).
        Assert.Empty(snapshots);

        await startTask;                    // seed completes -> epoch bump + re-push to everyone

        await WaitFor(() => snapshots.Any(s => s.Epoch == 2 && s.Windows == 1),
            "the epoch bump must re-push the seeded snapshot without a reconnect");
        Assert.True(core.IsConnected, "the link never dropped — the re-push rode the same connection");
        Assert.Equal(1, server.ClientCount);
    }

    // 8. A detected seq gap re-issues Hello (rate-limited) — the safety net that replaces the tray's
    //    missing reconcile backstop. The gap is injected through the client's internal test seam; the
    //    re-Hello is observable via HelloSent.
    [Fact]
    public async Task A_sequence_gap_re_issues_Hello_without_a_reconnect()
    {
        var pal = new ControllablePal();
        pal.SeedTray(Tray("1:10", "Alpha"));

        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var hellos = 0;
        core.HelloSent += () => Interlocked.Increment(ref hellos);
        var last = (epoch: 0, seq: 0L);
        core.EventReceived += e =>
        {
            if (e.Epoch is { } ep && e.Seq is { } sq && sq > last.seq) last = (ep, sq);
        };

        await core.StartSessionAsync(Ct);
        Assert.Equal(1, hellos);
        await WaitFor(() => last.epoch != 0, "the Hello burst should establish a baseline");

        // A frame "lost": the next event the client sees jumps the seq.
        core.NoteSessionEventForTest(new CoreEvent(CoreEventKind.TrayItemAdded,
            TrayItem: Tray("9:99", "Ghost"), Epoch: last.epoch, Seq: last.seq + 100));

        await WaitFor(() => hellos == 2, "a detected gap must re-issue Hello (rate-limited)");
        Assert.True(core.IsConnected, "the gap healed over the same link");
    }

    // bevel-b79z: deltas carry the epoch, so they are part of the client's sequence. Asserted by
    // construction on the stamps themselves: the delta has an epoch, and the first frame of the following
    // Resync burst has the SAME epoch and exactly delta.Seq + 1 — the client sees no gap. (Before the fix the
    // delta had no epoch, so the tracker never recorded its seq and the burst looked like a lost frame.)
    [Fact]
    public async Task A_delta_followed_by_a_snapshot_burst_is_not_a_sequence_gap()
    {
        var pal = new ControllablePal();
        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var hellos = 0;
        var seen = new ConcurrentQueue<CoreEvent>();
        core.HelloSent += () => Interlocked.Increment(ref hellos);
        core.EventReceived += e => seen.Enqueue(e);
        await core.StartSessionAsync(Ct);
        await WaitFor(() => seen.Any(e => e.Kind == CoreEventKind.SettingsSnapshot), "Hello burst");

        pal.RaiseTrayItemAdded(Tray("1:10", "Alpha"));
        await WaitFor(() => seen.Any(e => e.Kind == CoreEventKind.TrayItemAdded), "the delta reaches the client");
        var delta = seen.First(e => e.Kind == CoreEventKind.TrayItemAdded);
        Assert.NotNull(delta.Epoch);

        await core.SendAsync(new CoreCommand(CoreCommandKind.Resync), Ct);
        await WaitFor(() => seen.Count(e => e.Kind == CoreEventKind.SettingsSnapshot) >= 2, "Resync burst");
        var burstHead = seen.SkipWhile(e => !ReferenceEquals(e, delta)).Skip(1).First(e => e.Seq is not null);
        Assert.Equal(delta.Epoch, burstHead.Epoch);
        Assert.Equal(delta.Seq + 1, burstHead.Seq);   // contiguous: the client's tracker would see no gap
        Assert.Equal(1, Volatile.Read(ref hellos));   // secondary: and no spurious re-Hello fired
    }

    // bevel-b79z: a REAL lost delta (the core stamps it, the frame never arrives) is still detected: the next
    // delta jumps the seq within the epoch and the client re-issues Hello. End to end, no test hook on the client.
    [Fact]
    public async Task A_genuinely_lost_delta_makes_the_client_re_Hello()
    {
        var pal = new ControllablePal();
        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var hellos = 0;
        var deltas = 0;
        core.HelloSent += () => Interlocked.Increment(ref hellos);
        core.EventReceived += e => { if (e.Kind == CoreEventKind.TrayItemAdded) Interlocked.Increment(ref deltas); };
        await core.StartSessionAsync(Ct);
        pal.RaiseTrayItemAdded(Tray("1:10", "Alpha"));
        await WaitFor(() => Volatile.Read(ref deltas) == 1, "the first delta baselines the tracker");
        Assert.Equal(1, Volatile.Read(ref hellos));

        server.DropNextBroadcastFramesForTest = 1;
        pal.RaiseTrayItemAdded(Tray("2:20", "Lost"));      // stamped, never sent
        pal.RaiseTrayItemAdded(Tray("3:30", "After"));     // seq jumps by 2 -> gap
        await WaitFor(() => Volatile.Read(ref hellos) == 2, "a real lost delta must trigger a re-Hello");
    }

    // 9. Sharing one client is the whole point (the taskbar's settings peer and the tray adapters ride
    //    the SAME connection now): a settings pull before any Hello must not eat anyone's snapshot —
    //    the tray still gets its full snapshot when the session starts.
    [Fact]
    public async Task A_settings_pull_before_Hello_does_not_eat_the_later_snapshot()
    {
        var pal = new ControllablePal();
        pal.SeedTray(Tray("1:10", "Alpha"));

        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var settings = new RemoteSettingsService(core);    // subscribes to the shared stream
        await settings.LoadAsync(Ct);                       // connect + GetSettings PULL — before any Hello

        var tray = new ShellCoreSystemTrayHost(core);      // subscribed AFTER the early connect
        var added = new ConcurrentBag<string>();
        tray.ItemAdded += (_, t) => added.Add(t.Id.Value);

        await core.StartSessionAsync(Ct);                  // the session starts NOW
        await WaitFor(() => added.Contains("1:10"),
            "the tray must receive its snapshot even though the settings peer connected first");
    }

    // bevel-8ck: the mirror case — an item REMOVED in the gap must not linger as a ghost. The snapshot
    // (taken before the removal) still lists it, but the buffered remove replays over the seed.
    [Fact]
    public async Task Tray_remove_during_the_snapshot_gap_does_not_ghost()
    {
        var pal = new ControllablePal();
        pal.SeedTray(Tray("1:10", "Alpha"), Tray("2:20", "Beta"));
        pal.OnGetItems = () => pal.RaiseTrayItemRemoved(Tray("1:10", "Alpha")); // removed in the gap

        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var tray = new ShellCoreSystemTrayHost(core);
        var added = new ConcurrentBag<string>();
        tray.ItemAdded += (_, t) => added.Add(t.Id.Value);

        await core.StartSessionAsync(Ct);
        await WaitFor(() => added.Contains("2:20"), "the surviving item should be in the snapshot");
        // Give any erroneous "1:10" a chance to arrive, then assert it never did (removed-in-gap wins).
        await Task.Delay(150);
        Assert.DoesNotContain("1:10", added);
    }

    // bevel-ri8c: a TraySnapshot is a full reconcile — an item the adapter raised earlier but the snapshot
    // omits vanished while the link was down, and must be removed (it used to linger as a ghost).
    [Fact]
    public async Task A_resent_tray_snapshot_removes_items_that_vanished_while_the_link_was_down()
    {
        await using var core = new ShellCoreClient(NewSocketPath(), NewNonce());
        var tray = new ShellCoreSystemTrayHost(core);
        var removed = new List<string>();
        var added = new List<string>();
        tray.ItemRemoved += (_, t) => removed.Add(t.Id.Value);
        tray.ItemAdded += (_, t) => added.Add(t.Id.Value);

        tray.OnCoreEvent(new CoreEvent(CoreEventKind.TraySnapshot,
            TrayItems: new[] { Tray("1:10", "Alpha"), Tray("2:20", "Beta") }));
        Assert.Empty(removed);

        // The link dropped; "1:10" vanished meanwhile; the reconnect Hello resends the projection.
        tray.OnCoreEvent(new CoreEvent(CoreEventKind.TraySnapshot, TrayItems: new[] { Tray("2:20", "Beta") }));

        Assert.Equal(new[] { "1:10" }, removed.ToArray());
        Assert.Equal(3, added.Count);   // 1:10, 2:20, then 2:20 re-added (idempotent upsert downstream)

        // A delta-removed item is forgotten too: it must not be reported removed a second time.
        tray.OnCoreEvent(new CoreEvent(CoreEventKind.TrayItemRemoved, TrayItem: Tray("2:20", "Beta")));
        tray.OnCoreEvent(new CoreEvent(CoreEventKind.TraySnapshot, TrayItems: Array.Empty<TrayItem>()));
        Assert.Equal(new[] { "1:10", "2:20" }, removed.ToArray());
    }

    // bevel-ri8c: a Hello answered while the core is still seeding must not carry an empty tray (or
    // window) snapshot — a reconciling client would wipe its tray on it. The epoch-bump burst at seed
    // completion is the authoritative one.
    [Fact]
    public async Task A_Hello_during_seeding_does_not_send_an_empty_tray_snapshot()
    {
        var pal = new ControllablePal();
        pal.SeedTray(Tray("1:10", "Alpha"));
        for (var i = 0; i < 25; i++)
            pal.EnumerateFaults.Enqueue(new TransportFault("gRPC call disposed."));

        var path = NewSocketPath();
        var nonce = NewNonce();
        var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        var startTask = server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var kinds = new ConcurrentBag<(CoreEventKind Kind, int Epoch, int Tray)>();
        core.EventReceived += e => kinds.Add((e.Kind, e.Epoch ?? 0, e.TrayItems?.Count ?? 0));

        await core.StartSessionAsync(Ct);   // Hello lands MID-SEED
        await WaitFor(() => kinds.Any(k => k.Kind == CoreEventKind.SettingsSnapshot && k.Epoch == 1),
            "the mid-seed Hello still answers the non-projection snapshots");
        Assert.DoesNotContain(kinds, k => k.Kind == CoreEventKind.TraySnapshot);
        Assert.DoesNotContain(kinds, k => k.Kind == CoreEventKind.WindowSnapshot);

        await startTask;
        await WaitFor(() => kinds.Any(k => k.Kind == CoreEventKind.TraySnapshot && k.Epoch == 2 && k.Tray == 1),
            "the seed-complete burst delivers the real tray");
    }

    // bevel-ri8c: the full path — reconnect -> Hello -> TraySnapshot -> adapter reconcile. A core restarts
    // without an item the taskbar held; the taskbar must raise ItemRemoved for exactly that id.
    [Fact]
    public async Task A_core_restart_without_an_item_removes_it_from_a_surviving_taskbar()
    {
        var pal = new ControllablePal();
        pal.SeedTray(Tray("1:10", "Alpha"), Tray("2:20", "Beta"));
        var path = NewSocketPath();
        var nonce = NewNonce();
        var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var tray = new ShellCoreSystemTrayHost(core);
        var added = new ConcurrentBag<string>();
        var removed = new ConcurrentBag<string>();
        tray.ItemAdded += (_, t) => added.Add(t.Id.Value);
        tray.ItemRemoved += (_, t) => removed.Add(t.Id.Value);
        await core.StartSessionAsync(Ct);
        await WaitFor(() => added.Contains("1:10") && added.Contains("2:20"), "initial snapshot");

        await server.DisposeAsync();
        await WaitFor(() => !core.IsConnected, "client should observe the disconnect");

        var pal2 = new ControllablePal();           // the restarted core's world: "1:10" is gone
        pal2.SeedTray(Tray("2:20", "Beta"));
        await using var server2 = new ShellCoreServer(pal2, pal2, pal2, new StubSettings(), path, nonce);
        await server2.StartAsync(Ct);

        await WaitFor(() => removed.Contains("1:10"), "the reconnect Hello's snapshot must reap the vanished item");
        await Task.Delay(150);
        Assert.Equal(new[] { "1:10" }, removed.ToArray());
    }

    // bevel-ri8c: a FAILED tray seed is not an authoritative empty tray — it must not clear a taskbar that
    // survived the core restart.
    [Fact]
    public async Task A_failed_tray_seed_does_not_clear_a_surviving_taskbars_tray()
    {
        var pal = new ControllablePal();
        pal.SeedTray(Tray("1:10", "Alpha"));
        var path = NewSocketPath();
        var nonce = NewNonce();
        var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var tray = new ShellCoreSystemTrayHost(core);
        var added = new ConcurrentBag<string>();
        var removed = new ConcurrentBag<string>();
        tray.ItemAdded += (_, t) => added.Add(t.Id.Value);
        tray.ItemRemoved += (_, t) => removed.Add(t.Id.Value);
        await core.StartSessionAsync(Ct);
        await WaitFor(() => added.Contains("1:10"), "initial snapshot");

        await server.DisposeAsync();
        await WaitFor(() => !core.IsConnected, "client should observe the disconnect");

        var pal2 = new ControllablePal { TrayFault = new TransportFault("helper down") };
        await using var server2 = new ShellCoreServer(pal2, pal2, pal2, new StubSettings(), path, nonce) { SeedDeadline = ShortSeedDeadline };
        await server2.StartAsync(Ct);
        await WaitFor(() => core.IsConnected, "client reconnects");
        await Task.Delay(500);   // let the Hello + seed-complete burst land
        Assert.Empty(removed);
    }

    // bevel-ri8c: the REAL failure shape — the host swallows "helper unreachable" and returns empty
    // (MacOSSystemTrayHost does), it does not throw. The core must still not publish that as authoritative.
    [Fact]
    public async Task A_swallowed_tray_seed_failure_does_not_clear_a_surviving_taskbars_tray()
    {
        var pal = new ControllablePal();
        pal.SeedTray(Tray("1:10", "Alpha"));
        var path = NewSocketPath();
        var nonce = NewNonce();
        var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var tray = new ShellCoreSystemTrayHost(core);
        var added = new ConcurrentBag<string>();
        var removed = new ConcurrentBag<string>();
        tray.ItemAdded += (_, t) => added.Add(t.Id.Value);
        tray.ItemRemoved += (_, t) => removed.Add(t.Id.Value);
        await core.StartSessionAsync(Ct);
        await WaitFor(() => added.Contains("1:10"), "initial snapshot");

        await server.DisposeAsync();
        await WaitFor(() => !core.IsConnected, "client should observe the disconnect");

        var pal2 = new ControllablePal { TrayUnreachable = true };   // returns EMPTY, does not throw
        await using var server2 = new ShellCoreServer(pal2, pal2, pal2, new StubSettings(), path, nonce) { SeedDeadline = ShortSeedDeadline };
        await server2.StartAsync(Ct);
        await WaitFor(() => core.IsConnected, "client reconnects");
        await Task.Delay(500);
        Assert.Empty(removed);
    }

    // bevel-ri8c: a tray REMOVE proves nothing about the projection being complete — after a failed seed it
    // must not unlock the (empty) TraySnapshot.
    [Fact]
    public async Task A_tray_remove_delta_does_not_unlock_the_tray_snapshot_after_a_failed_seed()
    {
        var pal = new ControllablePal { TrayUnreachable = true };
        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce) { SeedDeadline = ShortSeedDeadline };
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var traySnapshots = 0;
        var resynced = 0;
        core.EventReceived += e =>
        {
            if (e.Kind == CoreEventKind.TraySnapshot) Interlocked.Increment(ref traySnapshots);
            if (e.Kind == CoreEventKind.SettingsSnapshot) Interlocked.Increment(ref resynced);
        };
        await core.StartSessionAsync(Ct);
        await WaitFor(() => Volatile.Read(ref resynced) >= 1, "Hello burst");

        pal.RaiseTrayItemRemoved(Tray("5:50", "Gone"));
        var before = Volatile.Read(ref resynced);
        await core.SendAsync(new CoreCommand(CoreCommandKind.Resync), Ct);
        await WaitFor(() => Volatile.Read(ref resynced) > before, "Resync burst");
        Assert.Equal(0, Volatile.Read(ref traySnapshots));

        pal.RaiseTrayItemAdded(Tray("6:60", "Live"));       // an add proves the stream is live
        before = Volatile.Read(ref resynced);
        await core.SendAsync(new CoreCommand(CoreCommandKind.Resync), Ct);
        await WaitFor(() => Volatile.Read(ref resynced) > before, "second Resync burst");
        Assert.Equal(1, Volatile.Read(ref traySnapshots));
    }

    // bevel-ri8c: a pull that was in flight when a remove delta arrived is older than the remove — it must
    // not resurrect the item (neither in the returned list nor in the known set).
    [Fact]
    public async Task A_stale_pull_landing_after_a_remove_does_not_resurrect_the_item()
    {
        var pal = new ControllablePal();
        pal.SeedTray(Tray("9:90", "Pulled"));
        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var tray = new ShellCoreSystemTrayHost(core);
        var removed = new List<string>();
        tray.ItemRemoved += (_, t) => removed.Add(t.Id.Value);

        // The reply is "in flight": the remove delta lands after it arrived but before it is merged.
        tray.BeforePullMergeForTest = () =>
        {
            tray.OnCoreEvent(new CoreEvent(CoreEventKind.TrayItemRemoved, TrayItem: Tray("9:90", "Pulled")));
            return Task.CompletedTask;
        };
        var pull = tray.GetItemsAsync(Ct).AsTask();

        Assert.Empty(await pull);                                           // not resurrected for the caller
        tray.OnCoreEvent(new CoreEvent(CoreEventKind.TraySnapshot, TrayItems: Array.Empty<TrayItem>()));
        Assert.Equal(new[] { "9:90" }, removed.ToArray());                  // and not a ghost in _known (no 2nd remove)
    }

    // ...whereas a seed that SUCCEEDED with zero items is authoritative: the empty snapshot reaps everything.
    [Fact]
    public async Task A_successful_empty_tray_seed_is_authoritative()
    {
        var pal = new ControllablePal();
        pal.SeedTray(Tray("1:10", "Alpha"));
        var path = NewSocketPath();
        var nonce = NewNonce();
        var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var tray = new ShellCoreSystemTrayHost(core);
        var added = new ConcurrentBag<string>();
        var removed = new ConcurrentBag<string>();
        tray.ItemAdded += (_, t) => added.Add(t.Id.Value);
        tray.ItemRemoved += (_, t) => removed.Add(t.Id.Value);
        await core.StartSessionAsync(Ct);
        await WaitFor(() => added.Contains("1:10"), "initial snapshot");

        await server.DisposeAsync();
        await WaitFor(() => !core.IsConnected, "client should observe the disconnect");

        var pal2 = new ControllablePal();   // seed succeeds, zero items
        await using var server2 = new ShellCoreServer(pal2, pal2, pal2, new StubSettings(), path, nonce);
        await server2.StartAsync(Ct);
        await WaitFor(() => removed.Contains("1:10"), "an authoritative empty tray removes what the taskbar held");
    }

    // bevel-ri8c: items that arrive via the GetItemsAsync pull are tracked too, so a later snapshot can reap them.
    [Fact]
    public async Task Items_from_the_pull_are_reaped_by_a_later_snapshot()
    {
        var pal = new ControllablePal();
        pal.SeedTray(Tray("9:90", "Pulled"));
        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var tray = new ShellCoreSystemTrayHost(core);
        var removed = new List<string>();
        tray.ItemRemoved += (_, t) => removed.Add(t.Id.Value);

        var pulled = await tray.GetItemsAsync(Ct);
        Assert.Single(pulled);
        tray.OnCoreEvent(new CoreEvent(CoreEventKind.TraySnapshot, TrayItems: Array.Empty<TrayItem>()));
        Assert.Equal(new[] { "9:90" }, removed.ToArray());
    }

    // ── bevel-wheq: the WINDOW twin of the tray reconcile ────────────────────────────────────────────

    private static CoreEvent WindowSnapshot(params ForeignWindow[] windows) =>
        new(CoreEventKind.WindowSnapshot, Windows: windows);

    // bevel-wheq: a WindowSnapshot is a full reconcile — a window the adapter raised earlier but the snapshot
    // omits closed while the link was down, and must be closed (it used to linger as a stale button, masked
    // only by ShellModel's 2s re-list poll). Fed straight into the adapter: on a live server a PAL close
    // always broadcasts a delta, so "vanished without a WindowClosed" is only reachable across a core restart
    // (covered end-to-end below).
    [Fact]
    public async Task A_resent_window_snapshot_closes_windows_that_vanished_while_the_link_was_down()
    {
        await using var core = new ShellCoreClient(NewSocketPath(), NewNonce());
        var wm = new ShellCoreWindowManager(core);
        var closed = new List<string>();
        var opened = new List<string>();
        wm.WindowClosed += (_, w) => closed.Add(w.Id.Value);
        wm.WindowOpened += (_, w) => opened.Add(w.Id.Value);

        wm.OnCoreEvent(WindowSnapshot(Win("a", "Alpha"), Win("b", "Beta")));
        Assert.Empty(closed);

        // The link dropped; "a" closed meanwhile; the reconnect Hello resends the projection.
        wm.OnCoreEvent(WindowSnapshot(Win("b", "Beta")));
        Assert.Equal(new[] { "a" }, closed.ToArray());
        Assert.Equal(new[] { "a", "b", "b" }, opened.ToArray());   // b re-raised: ShellModel upserts by id

        // Ordering: a WindowClosed delta followed by a later snapshot — the delta already forgot "b", so the
        // snapshot that omits it must not close it a second time.
        wm.OnCoreEvent(new CoreEvent(CoreEventKind.WindowClosed, Window: Win("b", "Beta")));
        wm.OnCoreEvent(WindowSnapshot());
        Assert.Equal(new[] { "a", "b" }, closed.ToArray());
    }

    // bevel-wheq: ordering the other way — a snapshot followed by a late delta for a window the snapshot did
    // not contain. ShellModel creates a button for it on ANY of these kinds, so the adapter must track it, and
    // the next snapshot that omits it must close it. Minimized / other-Space windows are ordinary windows
    // here (their flags ride WindowChanged), so the same holds for them.
    [Theory]
    [InlineData(CoreEventKind.WindowOpened)]
    [InlineData(CoreEventKind.WindowChanged)]
    [InlineData(CoreEventKind.WindowForeground)]
    public async Task A_window_first_seen_in_a_late_delta_is_closed_by_the_next_snapshot(CoreEventKind kind)
    {
        await using var core = new ShellCoreClient(NewSocketPath(), NewNonce());
        var wm = new ShellCoreWindowManager(core);
        var closed = new List<string>();
        wm.WindowClosed += (_, w) => closed.Add(w.Id.Value);

        wm.OnCoreEvent(WindowSnapshot(Win("a", "Alpha")));
        wm.OnCoreEvent(new CoreEvent(kind, Window: Win("c", "Late", focused: kind == CoreEventKind.WindowForeground)
            with { IsMinimized = kind == CoreEventKind.WindowChanged, IsOnOtherSpace = kind == CoreEventKind.WindowChanged }));
        wm.OnCoreEvent(WindowSnapshot(Win("a", "Alpha")));

        Assert.Equal(new[] { "c" }, closed.ToArray());
    }

    // bevel-wheq: the full path — reconnect -> Hello -> WindowSnapshot -> adapter reconcile. A core restarts
    // without a window the taskbar held; the taskbar must raise WindowClosed for exactly that id.
    [Fact]
    public async Task A_core_restart_without_a_window_closes_it_on_a_surviving_taskbar()
    {
        var pal = new ControllablePal();
        pal.SeedWindows(Win("a", "Alpha"), Win("b", "Beta"));
        var path = NewSocketPath();
        var nonce = NewNonce();
        var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var wm = new ShellCoreWindowManager(core);
        var opened = new ConcurrentBag<string>();
        var closed = new ConcurrentBag<string>();
        wm.WindowOpened += (_, w) => opened.Add(w.Id.Value);
        wm.WindowClosed += (_, w) => closed.Add(w.Id.Value);
        await core.StartSessionAsync(Ct);
        await WaitFor(() => opened.Contains("a") && opened.Contains("b"), "initial snapshot");

        await server.DisposeAsync();
        await WaitFor(() => !core.IsConnected, "client should observe the disconnect");

        var pal2 = new ControllablePal();           // the restarted core's world: "a" is gone
        pal2.SeedWindows(Win("b", "Beta"));
        await using var server2 = new ShellCoreServer(pal2, pal2, pal2, new StubSettings(), path, nonce);
        await server2.StartAsync(Ct);

        await WaitFor(() => closed.Contains("a"), "the reconnect Hello's snapshot must close the vanished window");
        await Task.Delay(150);
        Assert.Equal(new[] { "a" }, closed.ToArray());
    }

    // bevel-wheq: THE FAILED-SEED HAZARD. MacOSWindowManager.EnumerateAsync swallows "helper unreachable"
    // into an EMPTY list (it does not throw). Once snapshots reconcile, publishing that as the window
    // projection would close every button on a taskbar that survived the core restart.
    [Fact]
    public async Task A_swallowed_window_seed_failure_does_not_close_a_surviving_taskbars_windows()
    {
        var pal = new ControllablePal();
        pal.SeedWindows(Win("a", "Alpha"), Win("b", "Beta"));
        var path = NewSocketPath();
        var nonce = NewNonce();
        var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var wm = new ShellCoreWindowManager(core);
        var opened = new ConcurrentBag<string>();
        var closed = new ConcurrentBag<string>();
        var windowSnapshots = 0;
        var settingsSnapshots = 0;
        wm.WindowOpened += (_, w) => opened.Add(w.Id.Value);
        wm.WindowClosed += (_, w) => closed.Add(w.Id.Value);
        core.EventReceived += e =>
        {
            if (e.Kind == CoreEventKind.WindowSnapshot) Interlocked.Increment(ref windowSnapshots);
            if (e.Kind == CoreEventKind.SettingsSnapshot) Interlocked.Increment(ref settingsSnapshots);
        };
        await core.StartSessionAsync(Ct);
        await WaitFor(() => opened.Contains("a") && opened.Contains("b"), "initial snapshot");

        await server.DisposeAsync();
        await WaitFor(() => !core.IsConnected, "client should observe the disconnect");

        var pal2 = new ControllablePal { WindowsUnreachable = true };   // returns EMPTY, does not throw
        var snapshotsBefore = Volatile.Read(ref windowSnapshots);
        var settingsBefore = Volatile.Read(ref settingsSnapshots);
        await using var server2 = new ShellCoreServer(pal2, pal2, pal2, new StubSettings(), path, nonce)
            { SeedDeadline = ShortSeedDeadline };
        await server2.StartAsync(Ct);
        Assert.True(pal2.WindowSeedCalls > 1, "a null answer is retried until the (shortened) deadline");
        await WaitFor(() => Volatile.Read(ref settingsSnapshots) > settingsBefore,
            "the reconnect Hello burst landed (settings is always in it)");
        await Task.Delay(300);   // and any seed-complete burst after it
        Assert.Equal(snapshotsBefore, Volatile.Read(ref windowSnapshots));   // withheld, not sent empty
        Assert.Empty(closed);
    }

    // ...whereas a window seed that SUCCEEDED with zero windows is authoritative: it closes everything.
    [Fact]
    public async Task A_successful_empty_window_seed_is_authoritative()
    {
        var pal = new ControllablePal();
        pal.SeedWindows(Win("a", "Alpha"));
        var path = NewSocketPath();
        var nonce = NewNonce();
        var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var wm = new ShellCoreWindowManager(core);
        var opened = new ConcurrentBag<string>();
        var closed = new ConcurrentBag<string>();
        wm.WindowOpened += (_, w) => opened.Add(w.Id.Value);
        wm.WindowClosed += (_, w) => closed.Add(w.Id.Value);
        await core.StartSessionAsync(Ct);
        await WaitFor(() => opened.Contains("a"), "initial snapshot");

        await server.DisposeAsync();
        await WaitFor(() => !core.IsConnected, "client should observe the disconnect");

        var pal2 = new ControllablePal();   // seed succeeds, zero windows
        await using var server2 = new ShellCoreServer(pal2, pal2, pal2, new StubSettings(), path, nonce);
        await server2.StartAsync(Ct);
        await WaitFor(() => closed.Contains("a"), "an authoritative empty window list closes what the taskbar held");
    }

    // bevel-wheq: after a failed window seed, a CLOSE proves nothing about the projection being complete, so it
    // must not unlock the (incomplete) WindowSnapshot; an open/change/foreground proves the stream is live.
    [Fact]
    public async Task A_window_close_delta_does_not_unlock_the_window_snapshot_after_a_failed_seed()
    {
        var pal = new ControllablePal { WindowsUnreachable = true };
        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce) { SeedDeadline = ShortSeedDeadline };
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var windowSnapshots = 0;
        var resynced = 0;
        core.EventReceived += e =>
        {
            if (e.Kind == CoreEventKind.WindowSnapshot) Interlocked.Increment(ref windowSnapshots);
            if (e.Kind == CoreEventKind.SettingsSnapshot) Interlocked.Increment(ref resynced);
        };
        await core.StartSessionAsync(Ct);
        await WaitFor(() => Volatile.Read(ref resynced) >= 1, "Hello burst");
        Assert.Equal(0, Volatile.Read(ref windowSnapshots));

        pal.RaiseWindowClosed(Win("x", "Gone"));
        var before = Volatile.Read(ref resynced);
        await core.SendAsync(new CoreCommand(CoreCommandKind.Resync), Ct);
        await WaitFor(() => Volatile.Read(ref resynced) > before, "Resync burst");
        Assert.Equal(0, Volatile.Read(ref windowSnapshots));

        pal.RaiseWindowChanged(Win("y", "Live"));      // a change proves the stream is live
        await core.SendAsync(new CoreCommand(CoreCommandKind.Resync), Ct);
        // >= 1, not == 1: the client may also re-Hello on its own (another burst), which is fine here.
        await WaitFor(() => Volatile.Read(ref windowSnapshots) >= 1, "the Resync burst now carries the windows");
    }

    // bevel-wheq: windows that arrive via the EnumerateAsync pull (ShellModel's reconcile) get buttons too, so
    // they are tracked and a later snapshot can close them.
    [Fact]
    public async Task Windows_from_the_pull_are_closed_by_a_later_snapshot()
    {
        var pal = new ControllablePal();
        pal.SeedWindows(Win("p", "Pulled"));
        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var wm = new ShellCoreWindowManager(core);
        var closed = new List<string>();
        wm.WindowClosed += (_, w) => closed.Add(w.Id.Value);

        Assert.Single(await wm.EnumerateAsync(Ct));
        wm.OnCoreEvent(WindowSnapshot());
        Assert.Equal(new[] { "p" }, closed.ToArray());
    }

    // bevel-wheq: a pull that was in flight when a close delta arrived is older than the close — it must not
    // resurrect the window (neither in the returned list, which ShellModel reconciles to, nor in the tracking).
    [Fact]
    public async Task A_stale_window_pull_landing_after_a_close_does_not_resurrect_it()
    {
        var pal = new ControllablePal();
        pal.SeedWindows(Win("p", "Pulled"));
        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var wm = new ShellCoreWindowManager(core);
        var closed = new List<string>();
        wm.WindowClosed += (_, w) => closed.Add(w.Id.Value);

        wm.BeforePullMergeForTest = () =>
        {
            wm.OnCoreEvent(new CoreEvent(CoreEventKind.WindowClosed, Window: Win("p", "Pulled")));
            return Task.CompletedTask;
        };
        Assert.Empty(await wm.EnumerateAsync(Ct));                  // not resurrected for the caller
        wm.OnCoreEvent(WindowSnapshot());
        Assert.Equal(new[] { "p" }, closed.ToArray());              // and not a ghost in the tracking (no 2nd close)
    }


    /// <summary>A seed deadline for sources that never answer — long enough for several retries. The deadline
    /// starts just before the FIRST attempt, so it must also outlast a cold first attempt on a loaded CI
    /// runner (JIT, a starved thread pool): at 500 ms a macOS runner stalled long enough to make a single
    /// call, and every "retried at least once" assertion read it as a regression (seen on PR #32).</summary>
    private static readonly TimeSpan ShortSeedDeadline = TimeSpan.FromMilliseconds(1000);

    // bevel-wheq review, fix round 2: the core reports ready only after StartAsync, and the launcher restarts a
    // core that is not ready within ShellHealthMonitor.DefaultReadyTimeout (then Holds after 3). The whole
    // helper-dependent seed must end well inside it — with room left for host start, settings and apps.
    [Fact]
    public void The_seed_deadline_ends_well_inside_the_launchers_ready_timeout()
    {
        Assert.True(ShellCoreServer.DefaultSeedDeadline <= Bevel.App.Supervision.ShellHealthMonitor.DefaultReadyTimeout / 2,
            $"seed deadline {ShellCoreServer.DefaultSeedDeadline} vs ready timeout "
            + $"{Bevel.App.Supervision.ShellHealthMonitor.DefaultReadyTimeout}");
    }

    // ...and behaviourally, with the REAL deadline: a helper that never serves (window AND tray seeds answer
    // null forever) must still let StartAsync return — i.e. ReportReady — within that bound, with both
    // snapshots withheld. Window and tray must not each spend a full budget back to back.
    [Fact]
    public async Task A_helper_that_never_serves_still_lets_the_core_get_ready_in_time_with_both_snapshots_withheld()
    {
        var pal = new ControllablePal { WindowsUnreachable = true, TrayUnreachable = true };
        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);

        var sw = Stopwatch.StartNew();
        await server.StartAsync(Ct);
        sw.Stop();
        Assert.True(sw.Elapsed < ShellCoreServer.DefaultSeedDeadline + TimeSpan.FromSeconds(2),
            $"StartAsync took {sw.Elapsed}; the ready timeout is {Bevel.App.Supervision.ShellHealthMonitor.DefaultReadyTimeout}");
        Assert.True(pal.WindowSeedCalls > 1, "the window seed retried until the deadline");

        await using var core = new ShellCoreClient(path, nonce);
        var got = await HelloAndCollectAsync(core);
        await Task.Delay(150);
        Assert.DoesNotContain(got, g => g.Kind == CoreEventKind.WindowSnapshot);
        Assert.DoesNotContain(got, g => g.Kind == CoreEventKind.TraySnapshot);
    }

    // A shutdown during the seed cancels it promptly instead of running out the deadline (Program passes the
    // role's shutdown token into StartAsync).
    [Fact]
    public async Task A_shutdown_during_the_seed_cancels_it()
    {
        var pal = new ControllablePal { WindowsUnreachable = true };
        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.StartAsync(shutdown.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"cancellation took {sw.Elapsed}");
    }

    // ── bevel-wheq review, fix round 1: the seed retries "not yet", and a buffered live delta unlocks ──────

    /// <summary>Hello and collect the burst's projection snapshots: (kind, ids).</summary>
    private static async Task<ConcurrentBag<(CoreEventKind Kind, string[] Ids)>> HelloAndCollectAsync(
        ShellCoreClient core)
    {
        var got = new ConcurrentBag<(CoreEventKind, string[])>();
        var settings = 0;
        core.EventReceived += e =>
        {
            if (e.Kind == CoreEventKind.WindowSnapshot)
                got.Add((e.Kind, (e.Windows ?? []).Select(w => w.Id.Value).ToArray()));
            if (e.Kind == CoreEventKind.TraySnapshot)
                got.Add((e.Kind, (e.TrayItems ?? []).Select(t => t.Id.Value).ToArray()));
            if (e.Kind == CoreEventKind.SettingsSnapshot) Interlocked.Increment(ref settings);
        };
        await core.StartSessionAsync(Ct);
        await WaitFor(() => Volatile.Read(ref settings) >= 1, "Hello burst (settings is always in it)");
        return got;
    }

    // The helper host opens its channel BEFORE the first Ping succeeds, so on a cold boot the seed can get
    // Unavailable (null) from a channel that exists. That is "not yet", like a throw: retried, and once the
    // source answers the snapshot is published.
    [Fact]
    public async Task A_window_seed_that_is_unreachable_at_first_is_retried_and_then_published()
    {
        var pal = new ControllablePal { WindowSeedNullsRemaining = 3 };
        pal.SeedWindows(Win("a", "Alpha"));
        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);
        Assert.Equal(4, pal.WindowSeedCalls);

        await using var core = new ShellCoreClient(path, nonce);
        var got = await HelloAndCollectAsync(core);
        await WaitFor(() => got.Any(g => g.Kind == CoreEventKind.WindowSnapshot && g.Ids.SequenceEqual(["a"])),
            "the retried seed answered, so the window snapshot is published");
    }

    [Fact]
    public async Task A_tray_seed_that_is_unreachable_at_first_is_retried_and_then_published()
    {
        var pal = new ControllablePal { TraySeedNullsRemaining = 3 };
        pal.SeedTray(Tray("1:10", "Alpha"));
        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);
        Assert.Equal(4, pal.TraySeedCalls);

        await using var core = new ShellCoreClient(path, nonce);
        var got = await HelloAndCollectAsync(core);
        await WaitFor(() => got.Any(g => g.Kind == CoreEventKind.TraySnapshot && g.Ids.SequenceEqual(["1:10"])),
            "the retried seed answered, so the tray snapshot is published");
    }

    // A tray source unreachable for the whole budget: retried, then withheld — never published empty.
    [Fact]
    public async Task A_tray_seed_unreachable_for_the_whole_budget_is_withheld()
    {
        var pal = new ControllablePal { TrayUnreachable = true };
        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce)
            { SeedDeadline = ShortSeedDeadline };
        await server.StartAsync(Ct);
        Assert.True(pal.TraySeedCalls > 1, "a null answer is retried until the (shortened) deadline");

        await using var core = new ShellCoreClient(path, nonce);
        var got = await HelloAndCollectAsync(core);
        await Task.Delay(150);
        Assert.DoesNotContain(got, g => g.Kind == CoreEventKind.TraySnapshot);
    }

    // A live window delta that lands WHILE the core is seeding is buffered and replayed. After a failed seed
    // an open/change/foreground still proves the stream is live, so it must unlock WindowSnapshot on replay —
    // a quiet desktop may send no other delta. A close proves nothing and must not.
    [Theory]
    [InlineData(CoreEventKind.WindowOpened, true)]
    [InlineData(CoreEventKind.WindowChanged, true)]
    [InlineData(CoreEventKind.WindowForeground, true)]
    [InlineData(CoreEventKind.WindowClosed, false)]
    public async Task A_window_delta_buffered_during_a_failed_seed_unlocks_the_snapshot_unless_it_is_a_close(
        CoreEventKind kind, bool unlocks)
    {
        var pal = new ControllablePal { WindowsUnreachable = true };
        var raised = 0;
        pal.OnTrySeedWindows = () =>
        {
            if (Interlocked.Exchange(ref raised, 1) != 0) return;
            var w = Win("d", "During seed", focused: kind == CoreEventKind.WindowForeground);
            switch (kind)
            {
                case CoreEventKind.WindowOpened: pal.RaiseWindowOpened(w); break;
                case CoreEventKind.WindowChanged: pal.RaiseWindowChanged(w); break;
                case CoreEventKind.WindowForeground: pal.RaiseForegroundChanged(w); break;
                default: pal.RaiseWindowClosed(w); break;
            }
        };
        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce)
            { SeedDeadline = ShortSeedDeadline };
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var got = await HelloAndCollectAsync(core);
        if (unlocks)
            await WaitFor(() => got.Any(g => g.Kind == CoreEventKind.WindowSnapshot && g.Ids.SequenceEqual(["d"])),
                "the replayed live delta unlocks the window snapshot");
        else
        {
            await Task.Delay(150);
            Assert.DoesNotContain(got, g => g.Kind == CoreEventKind.WindowSnapshot);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_tray_add_buffered_during_a_failed_seed_unlocks_the_snapshot_but_a_remove_does_not(bool add)
    {
        var pal = new ControllablePal { TrayUnreachable = true };
        var raised = 0;
        pal.OnTrySeedItems = () =>
        {
            if (Interlocked.Exchange(ref raised, 1) != 0) return;
            if (add) pal.RaiseTrayItemAdded(Tray("7:70", "During seed"));
            else pal.RaiseTrayItemRemoved(Tray("7:70", "During seed"));
        };
        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce)
            { SeedDeadline = ShortSeedDeadline };
        await server.StartAsync(Ct);

        await using var core = new ShellCoreClient(path, nonce);
        var got = await HelloAndCollectAsync(core);
        if (add)
            await WaitFor(() => got.Any(g => g.Kind == CoreEventKind.TraySnapshot && g.Ids.SequenceEqual(["7:70"])),
                "the replayed live add unlocks the tray snapshot");
        else
        {
            await Task.Delay(150);
            Assert.DoesNotContain(got, g => g.Kind == CoreEventKind.TraySnapshot);
        }
    }

    /// <summary>
    /// An in-memory PAL that plays both roles the core owns — window manager and app environment. It
    /// seeds the initial projection, lets a test raise deltas on demand, and records the commands the
    /// core forwards, so the whole server↔client path is exercised without a real platform backend.
    /// </summary>
    [Fact]
    public async Task CaptureWindow_round_trips_png_bytes_through_the_core()
    {
        var pal = new ControllablePal();
        var expected = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 250, 200, 0, 255 };
        pal.CapturePng = expected;

        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);
        await using var core = new ShellCoreClient(path, nonce);
        var wm = new ShellCoreWindowManager(core);
        await core.EnsureConnectedAsync(Ct);

        var png = await wm.CaptureWindowAsync(new ForeignWindowId("win-7"), 240, 160, Ct);

        Assert.Equal(expected, png);                          // byte-for-byte over the base64 JSON wire
        Assert.Equal(("win-7", 240, 160), pal.LastCapture);   // request fields threaded through the core
    }

    [Fact]
    public async Task CaptureWindow_returns_null_when_the_core_link_is_down()
    {
        var pal = new ControllablePal();
        pal.CapturePng = new byte[] { 1, 2, 3 };
        var path = NewSocketPath();
        var nonce = NewNonce();
        // No server started, no EnsureConnectedAsync → the client is not connected.
        await using var core = new ShellCoreClient(path, nonce);
        var wm = new ShellCoreWindowManager(core);

        var png = await wm.CaptureWindowAsync(new ForeignWindowId("win-7"), 240, 160, Ct);
        Assert.Null(png);   // not-connected short-circuits to null without throwing
    }

    // ── core-owns-settings (bevel-6nve) ────────────────────────────────────────────────────────────

    // A connected client gets the settings blob as a SettingsSnapshot the moment it connects, and a
    // SettingsChanged (fresh blob + higher version) after any ApplySettingsUpdate — the raw wire behaviour
    // the RemoteSettingsService is built on. Server side is a real DB-backed SettingsService(tmpdir).
    [Fact]
    public async Task Settings_snapshot_on_connect_and_change_after_apply()
    {
        var dir = NewConfigDir();
        var settings = new SettingsService(dir);
        await settings.LoadAsync(Ct);
        await settings.UpdateAsync(s => s.TaskbarOpacity = 70, Ct); // a non-default explicit key to see in the blob

        var pal = new ControllablePal();
        var path = NewSocketPath();
        var nonce = NewNonce();
        try
        {
            await using var server = new ShellCoreServer(pal, pal, pal, settings, path, nonce);
            await server.StartAsync(Ct);

            await using var client = new ShellCoreClient(path, nonce);
            var gotSnapshot = new TaskCompletionSource<CoreEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gotChange = new TaskCompletionSource<CoreEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.EventReceived += e =>
            {
                if (e.Kind == CoreEventKind.SettingsSnapshot) gotSnapshot.TrySetResult(e);
                if (e.Kind == CoreEventKind.SettingsChanged) gotChange.TrySetResult(e);
            };
            await client.StartSessionAsync(Ct);

            var snap = await gotSnapshot.Task.WaitAsync(Timeout);
            Assert.Contains("taskbarOpacity", snap.SettingsJson!);
            Assert.True(snap.SettingsVersion >= 1);

            var resp = await client.SendAsync(
                new CoreCommand(CoreCommandKind.ApplySettingsUpdate, SettingsPatchJson: """{ "taskbarShowClock": false }"""), Ct);
            Assert.True(resp.Ok);

            var change = await gotChange.Task.WaitAsync(Timeout);
            Assert.True(change.SettingsVersion > snap.SettingsVersion);
            Assert.Contains("taskbarShowClock", change.SettingsJson!);
            Assert.False(settings.Current.TaskbarShowClock); // the core actually applied + persisted it
        }
        finally
        {
            settings.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // The RemoteSettingsService (peer role) seam: LoadAsync pulls the snapshot into Current, an UpdateAsync
    // forwards a changed-keys patch to the core whose SettingsChanged broadcast updates Current + fires
    // Changed (merging — the untouched key survives), the core is the actual writer, and ReloadIfChangedAsync
    // is a no-op. The peer never opens the DB.
    [Fact]
    public async Task RemoteSettingsService_loads_snapshot_and_forwards_updates_via_the_core()
    {
        var dir = NewConfigDir();
        var core = new SettingsService(dir);
        await core.LoadAsync(Ct);
        await core.UpdateAsync(s => s.ThemeId = ThemeIds.Blue2001, Ct); // seed an explicit key the merge must preserve

        var pal = new ControllablePal();
        var path = NewSocketPath();
        var nonce = NewNonce();
        try
        {
            await using var server = new ShellCoreServer(pal, pal, pal, core, path, nonce);
            await server.StartAsync(Ct);

            await using var client = new ShellCoreClient(path, nonce);
            await using var remote = new RemoteSettingsService(client);
            await remote.LoadAsync(Ct);

            Assert.Equal(ThemeIds.Blue2001, remote.Current.ThemeId);
            var loadedVersion = remote.Version;
            Assert.True(loadedVersion >= 1);
            Assert.False(await remote.ReloadIfChangedAsync(Ct)); // peer reload never claims a change

            // Subscribe AFTER load (the load-time snapshot already applied + was deduped), then update: the
            // core's broadcast is what advances Current — the peer does not mutate it locally.
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            remote.Changed += () => changed.TrySetResult();

            await remote.UpdateAsync(s => s.TaskbarOpacity = 55, Ct);
            await changed.Task.WaitAsync(Timeout);

            Assert.Equal(55, remote.Current.TaskbarOpacity);  // the broadcast landed
            Assert.Equal(ThemeIds.Blue2001, remote.Current.ThemeId);     // the untouched key merged through, not clobbered
            Assert.True(remote.Version > loadedVersion);
            Assert.Equal(55, core.Current.TaskbarOpacity);    // the core is the real writer
        }
        finally
        {
            core.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>A no-op <see cref="ISettingsService"/> for the window/app/tray tests that don't exercise
    /// settings — the server needs one to snapshot, but these tests only care about the other projections.</summary>
    private sealed class StubSettings : ISettingsService
    {
        public BevelSettings Current { get; } = new();
        public int Version => 0;
        public event Action? Changed { add { } remove { } }
        public ThemeOverrides ThemeOverridesFor(string themeId) => new();
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateAsync(Action<BevelSettings> update, CancellationToken ct = default) { update(Current); return Task.CompletedTask; }
        public Task UpdateThemeOverridesAsync(string themeId, Action<ThemeOverrides> update, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ReloadIfChangedAsync(CancellationToken ct = default) => Task.FromResult(false);
        public string SnapshotJson() => "{}";
        public Task ApplyPatchJsonAsync(string patchJson, CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    /// <summary>Stands in for a transport fault that is NOT an InvalidOperationException — the shape the
    /// helper's gRPC channel actually throws when it is disposed mid-call ("gRPC call disposed.").</summary>
    private sealed class TransportFault(string message) : Exception(message);

    // bevel-ejon: on a cold boot the Swift helper bounces WHILE the core is seeding, so the first
    // enumerate fails with a transport fault rather than the "Helper not connected"
    // InvalidOperationException the retry was typed to. That escaped StartAsync → RunShellCore → Main and
    // aborted the headless core (SIGABRT), which the supervisor then crash-looped. Seeding is best-effort
    // by contract: any non-cancellation failure must be retried, then degrade — never propagate.
    [Fact]
    public async Task StartAsync_SurvivesNonInvalidOperationFault_DuringWindowSeed_AndStillSeeds()
    {
        var pal = new ControllablePal();
        pal.SeedWindows(Win("a", "Alpha"));
        pal.EnumerateFaults.Enqueue(new TransportFault("gRPC call disposed."));

        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);

        await server.StartAsync(Ct);            // must not throw

        Assert.Equal(2, pal.EnumerateCalls);    // faulted once, retried, then succeeded

        // And the retry's result is actually seeded — a survived seed must not be an empty one.
        await using var core = new ShellCoreClient(path, nonce);
        var opened = new ConcurrentBag<string>();
        var wm = new ShellCoreWindowManager(core);
        wm.WindowOpened += (_, w) => opened.Add(w.Id.Value);
        await core.StartSessionAsync(Ct);
        await WaitFor(() => opened.Contains("a"), "the retried seed should still replay the window");
        Assert.Contains("a", opened);
    }

    // Same contract for the apps/tray seeds, which ran their own narrower catches (bevel-ejon): a faulting
    // source degrades that ONE snapshot to empty and leaves the rest of the projection intact.
    [Fact]
    public async Task StartAsync_SurvivesNonInvalidOperationFault_DuringAppsAndTraySeed()
    {
        var pal = new ControllablePal();
        pal.SeedWindows(Win("a", "Alpha"));
        pal.InstalledAppsFault = new TransportFault("gRPC call disposed.");
        pal.TrayFault = new TransportFault("gRPC call disposed.");

        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce) { SeedDeadline = ShortSeedDeadline };

        await server.StartAsync(Ct);            // must not throw

        await using var core = new ShellCoreClient(path, nonce);
        var opened = new ConcurrentBag<string>();
        var wm = new ShellCoreWindowManager(core);
        wm.WindowOpened += (_, w) => opened.Add(w.Id.Value);
        await core.StartSessionAsync(Ct);
        await WaitFor(() => opened.Contains("a"), "a failing apps/tray seed must not empty the window projection");
        Assert.Contains("a", opened);           // windows still seeded despite apps/tray failing
    }

    // bevel-pa3j: the apps and tray seeds are independent reads of two different sources, but the commit
    // — and so the snapshot burst every peer is waiting on — was gated on their SUM. Nothing ordered one
    // before the other; they were simply written on consecutive lines. Asserted as an OVERLAP rather than
    // a wall-clock total, because a timing bound would be the flaky way to say the same thing.
    [Fact]
    public async Task The_apps_and_tray_seeds_run_concurrently()
    {
        var pal = new ControllablePal();
        pal.SeedWindows(Win("a", "Alpha"));
        pal.SeedInstalled(new InstalledApp("com.example.a", "Alpha", IconPath: null));
        pal.SeedTray(Tray("1:10", "Alpha"));
        pal.AppsSeedDelay = TimeSpan.FromMilliseconds(250);
        pal.TraySeedDelay = TimeSpan.FromMilliseconds(250);

        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        Assert.True(pal.AppsEnter < pal.TrayExit && pal.TrayEnter < pal.AppsExit,
            "both seeds should be in flight at once; one strictly after the other means the core still "
            + "pays the sum of the two before it can broadcast");

        // ...and the concurrency must not cost correctness: both snapshots still reach a client.
        await using var core = new ShellCoreClient(path, nonce);
        var tray = new ShellCoreSystemTrayHost(core);
        var added = new ConcurrentBag<string>();
        tray.ItemAdded += (_, t) => added.Add(t.Id.Value);
        var apps = new ShellCoreAppEnvironment(core);
        await core.StartSessionAsync(Ct);
        await WaitFor(() => added.Contains("1:10"), "the tray seed should still be in the projection");
        Assert.Equal("Alpha", Assert.Single(await apps.EnumerateInstalledAppsAsync(Ct)).DisplayName);
    }

    // The ordering that must SURVIVE the concurrency: apps/tray still start only after the windows seed,
    // because that call doubles as the helper-readiness probe — they share the helper's connection and
    // have no retry budget of their own, so starting them early would degrade both to empty on a cold
    // boot. (Here the "helper" faults the first window enumerate, as bevel-ejon's real one did.)
    [Fact]
    public async Task The_apps_and_tray_seeds_wait_for_the_window_seed_to_prove_the_helper_is_up()
    {
        var pal = new ControllablePal();
        pal.SeedWindows(Win("a", "Alpha"));
        pal.SeedTray(Tray("1:10", "Alpha"));
        pal.EnumerateFaults.Enqueue(new TransportFault("gRPC call disposed."));

        var path = NewSocketPath();
        var nonce = NewNonce();
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);
        await server.StartAsync(Ct);

        Assert.Equal(2, pal.EnumerateCalls);     // the probe faulted once, then succeeded
        Assert.True(pal.TrayEnter > pal.LastEnumerateEnter && pal.AppsEnter > pal.LastEnumerateEnter,
            "both seeds must start AFTER the successful window enumerate — moving them alongside it "
            + "would run them against a helper that has not answered yet");

        // And the projection is intact, so the ordering isn't being bought with an empty seed.
        await using var core = new ShellCoreClient(path, nonce);
        var tray = new ShellCoreSystemTrayHost(core);
        var added = new ConcurrentBag<string>();
        tray.ItemAdded += (_, t) => added.Add(t.Id.Value);
        await core.StartSessionAsync(Ct);
        await WaitFor(() => added.Contains("1:10"),
            "the tray seed must run after the window probe proved the helper up, not against a cold one");
    }

    private sealed class ControllablePal : IWindowManager, IAppEnvironment, ISystemTrayHost
    {
        private readonly List<ForeignWindow> _windows = new();
        private readonly List<TrayItem> _trayItems = new();
        private IReadOnlyList<InstalledApp> _installed = Array.Empty<InstalledApp>();
        public ConcurrentBag<string> Activated { get; } = new();
        public ConcurrentBag<string> RestoredAndActivated { get; } = new();
        public ConcurrentBag<string> Launched { get; } = new();
        public ConcurrentBag<string> TrayClicks { get; } = new();

        public void SeedWindows(params ForeignWindow[] windows) => _windows.AddRange(windows);
        public void SeedInstalled(params InstalledApp[] apps) => _installed = apps;
        public void SeedTray(params TrayItem[] items) => _trayItems.AddRange(items);
        public void RaiseTrayItemAdded(TrayItem t) => ItemAdded?.Invoke(this, t);
        public void RaiseTrayItemRemoved(TrayItem t) => ItemRemoved?.Invoke(this, t);

        public void RaiseWindowOpened(ForeignWindow w) => WindowOpened?.Invoke(this, w);
        public void RaiseWindowClosed(ForeignWindow w) => WindowClosed?.Invoke(this, w);
        public void RaiseWindowChanged(ForeignWindow w) => WindowChanged?.Invoke(this, w);
        public void RaiseForegroundChanged(ForeignWindow w) => ForegroundChanged?.Invoke(this, w);
        public void RaiseAppLaunched(RunningApp a) => AppLaunched?.Invoke(this, a);
        public void RaiseAppTerminated(RunningApp a) => AppTerminated?.Invoke(this, a);

        // ── IWindowManager ──
        public Capabilities Capabilities { get; set; } =
            new(Available: true, TrayMode: TrayCapability.Mirrored, Notes: Array.Empty<string>(), SupportsReposition: true);

        /// <summary>Test seam (bevel-ejon): exceptions to throw from the next N window enumerates before
        /// succeeding — stands in for the helper's transport faulting mid-seed on a cold boot.</summary>
        public readonly Queue<Exception> EnumerateFaults = new();
        public int EnumerateCalls;
        /// <summary>When the most recent window enumerate was entered (bevel-pa3j) — the apps/tray seeds
        /// must start after it, since it is also the helper-readiness probe.</summary>
        public long LastEnumerateEnter;
        public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
        {
            LastEnumerateEnter = Stopwatch.GetTimestamp();
            EnumerateCalls++;
            if (EnumerateFaults.Count > 0) throw EnumerateFaults.Dequeue();
            if (WindowsUnreachable) return ValueTask.FromResult<IReadOnlyList<ForeignWindow>>(Array.Empty<ForeignWindow>());
            return ValueTask.FromResult<IReadOnlyList<ForeignWindow>>(_windows.ToArray());
        }

        /// <summary>Test seam (bevel-wheq): the SWALLOWED-failure shape — like the real MacOSWindowManager,
        /// EnumerateAsync returns EMPTY when the helper is unreachable instead of throwing, and only
        /// TrySeedWindowsAsync can tell (null).</summary>
        public bool WindowsUnreachable;
        /// <summary>Test seams (bevel-wheq review): the next N window seeds answer null (helper not serving yet)
        /// before the source answers; a hook that runs inside each seed call (to land a delta mid-seed); and
        /// how many seed calls were made.</summary>
        public int WindowSeedNullsRemaining;
        public Action? OnTrySeedWindows;
        public int WindowSeedCalls;
        public async ValueTask<IReadOnlyList<ForeignWindow>?> TrySeedWindowsAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref WindowSeedCalls);
            OnTrySeedWindows?.Invoke();
            var windows = await EnumerateAsync(ct);
            if (WindowSeedNullsRemaining > 0) { WindowSeedNullsRemaining--; return null; }
            return WindowsUnreachable ? null : windows;
        }

        public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) { Activated.Add(id.Value); return Task.CompletedTask; }
        public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAndActivateAsync(ForeignWindowId id, CancellationToken ct = default) { RestoredAndActivated.Add(id.Value); return Task.CompletedTask; }
        public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, PalRect? expected = null, CancellationToken ct = default) => Task.CompletedTask;

        // bevel-44vg review seams: record MoveWindowToSpace (window, space, owner pid) and answer
        // ListSpaces with a preset list, recording the window id the core forwarded.
        public ConcurrentBag<(string Window, ulong SpaceId, int OwnerPid)> SpaceMoves { get; } = new();
        public Task MoveWindowToSpaceAsync(ForeignWindowId id, ulong spaceId, int ownerPid = 0, CancellationToken ct = default)
        {
            SpaceMoves.Add((id.Value, spaceId, ownerPid));
            return Task.CompletedTask;
        }
        public SpaceList NextSpaces { get; set; } = new(Array.Empty<SpaceInfo>());
        public string? LastSpacesWindowId;
        public ValueTask<SpaceList> ListSpacesAsync(ForeignWindowId? windowId = null, CancellationToken ct = default)
        {
            LastSpacesWindowId = windowId?.Value;
            return ValueTask.FromResult(NextSpaces);
        }

        public byte[]? CapturePng;                       // set by a test; returned by CaptureWindowAsync
        public (string Id, int W, int H)? LastCapture;   // records the request fields the core forwarded
        public Task<byte[]?> CaptureWindowAsync(ForeignWindowId id, int maxWidth, int maxHeight, CancellationToken ct = default)
        {
            LastCapture = (id.Value, maxWidth, maxHeight);
            return Task.FromResult(CapturePng);
        }

        public event EventHandler<ForeignWindow>? WindowOpened;
        public event EventHandler<ForeignWindow>? WindowClosed;
        public event EventHandler<ForeignWindow>? WindowChanged;
        public event EventHandler<ForeignWindow>? ForegroundChanged;

        // ── IAppEnvironment ──
        public ValueTask<IReadOnlyList<RunningApp>> GetRunningAppsAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<RunningApp>>(Array.Empty<RunningApp>());

        /// <summary>Test seam (bevel-ejon): thrown from the installed-apps seed.</summary>
        public Exception? InstalledAppsFault;

        /// <summary>Test seams (bevel-pa3j): a seed that takes measurable time, and the window during
        /// which it was inside the call. Two seeds that genuinely run concurrently have OVERLAPPING
        /// windows; serial ones cannot. Needed because the other seams complete synchronously, which
        /// makes serial and concurrent code indistinguishable.</summary>
        public TimeSpan AppsSeedDelay, TraySeedDelay;
        public long AppsEnter, AppsExit, TrayEnter, TrayExit;

        public async ValueTask<IReadOnlyList<InstalledApp>> EnumerateInstalledAppsAsync(CancellationToken ct = default)
        {
            AppsEnter = Stopwatch.GetTimestamp();
            if (InstalledAppsFault is { } ex) throw ex;
            if (AppsSeedDelay > TimeSpan.Zero) await Task.Delay(AppsSeedDelay, ct);
            AppsExit = Stopwatch.GetTimestamp();
            return _installed;
        }

        public Task LaunchAsync(string appIdOrPath, CancellationToken ct = default) { Launched.Add(appIdOrPath); return Task.CompletedTask; }

        public event EventHandler<RunningApp>? AppLaunched;
        public event EventHandler<RunningApp>? AppTerminated;
        public event EventHandler<IReadOnlyList<InstalledApp>>? InstalledAppsChanged;
        public void RaiseInstalledAppsChanged(IReadOnlyList<InstalledApp> apps) => InstalledAppsChanged?.Invoke(this, apps);

        // ── ISystemTrayHost ──
        /// <summary>Test seam (bevel-8ck): fires while the core is taking its tray snapshot, so a test can
        /// inject a delta into the exact subscribe→snapshot gap the race lived in.</summary>
        public Action? OnGetItems;
        /// <summary>Test seam (bevel-ejon): thrown from the tray seed.</summary>
        public Exception? TrayFault;
        /// <summary>Test seam (bevel-ri8c): the SWALLOWED-failure shape — like the real MacOSSystemTrayHost,
        /// GetItemsAsync returns EMPTY when the helper is unreachable instead of throwing, and only
        /// TrySeedItemsAsync can tell (null).</summary>
        public bool TrayUnreachable;
        /// <summary>Tray twins of the window seed seams (bevel-wheq review).</summary>
        public int TraySeedNullsRemaining;
        public Action? OnTrySeedItems;
        public int TraySeedCalls;
        public async ValueTask<IReadOnlyList<TrayItem>?> TrySeedItemsAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref TraySeedCalls);
            OnTrySeedItems?.Invoke();
            if (TrayUnreachable) return null;
            if (TraySeedNullsRemaining > 0) { TraySeedNullsRemaining--; return null; }
            return await GetItemsAsync(ct);
        }
        public async ValueTask<IReadOnlyList<TrayItem>> GetItemsAsync(CancellationToken ct = default)
        {
            TrayEnter = Stopwatch.GetTimestamp();
            if (TrayFault is { } ex) throw ex;
            if (TrayUnreachable) return Array.Empty<TrayItem>();
            OnGetItems?.Invoke();
            if (TraySeedDelay > TimeSpan.Zero) await Task.Delay(TraySeedDelay, ct);
            TrayExit = Stopwatch.GetTimestamp();
            return _trayItems.ToArray();
        }

        public Task SetNativeTrayHiddenAsync(bool hidden, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> ForwardClickAsync(TrayItemId id, TrayButton button, TrayModifiers modifiers, bool park = false, CancellationToken ct = default)
        {
            TrayClicks.Add($"{id.Value}:{button}:{modifiers}:park={park}");
            return Task.FromResult(true);
        }

        public event EventHandler<TrayItem>? ItemAdded;
        public event EventHandler<TrayItem>? ItemRemoved;
        public event EventHandler<TrayItem>? ItemUpdated;
    }
}
