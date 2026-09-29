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

    // 7. Seed completion = epoch bump. A client that Hello'd while the core was still seeding got the
    //    empty epoch-1 snapshot; when the seed lands, the core bumps the epoch and re-pushes the fresh
    //    snapshots WITHOUT the client reconnecting. This is the principled version of the old
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
        core.EventReceived += e =>
        {
            if (e.Kind == CoreEventKind.WindowSnapshot)
                snapshots.Add((e.Epoch ?? 0, (e.Windows ?? Array.Empty<ForeignWindow>()).Count));
        };

        await core.StartSessionAsync(Ct);   // Hello lands MID-SEED: empty projection, epoch 1
        await WaitFor(() => snapshots.Any(s => s.Epoch == 1),
            "the mid-seed Hello should deliver an epoch-1 snapshot");
        Assert.True(snapshots.All(s => s.Windows == 0), "the projection is still empty while the seed is stalled");

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
        await core.UpdateAsync(s => s.ThemeId = "luna", Ct); // seed an explicit key the merge must preserve

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

            Assert.Equal("luna", remote.Current.ThemeId);
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
            Assert.Equal("luna", remote.Current.ThemeId);     // the untouched key merged through, not clobbered
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
        await using var server = new ShellCoreServer(pal, pal, pal, new StubSettings(), path, nonce);

        await server.StartAsync(Ct);            // must not throw

        await using var core = new ShellCoreClient(path, nonce);
        var opened = new ConcurrentBag<string>();
        var wm = new ShellCoreWindowManager(core);
        wm.WindowOpened += (_, w) => opened.Add(w.Id.Value);
        await core.StartSessionAsync(Ct);
        await WaitFor(() => opened.Contains("a"), "a failing apps/tray seed must not empty the window projection");
        Assert.Contains("a", opened);           // windows still seeded despite apps/tray failing
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
        public Capabilities Capabilities { get; } =
            new(Available: true, TrayMode: TrayCapability.Mirrored, Notes: Array.Empty<string>(), SupportsReposition: true);

        /// <summary>Test seam (bevel-ejon): exceptions to throw from the next N window enumerates before
        /// succeeding — stands in for the helper's transport faulting mid-seed on a cold boot.</summary>
        public readonly Queue<Exception> EnumerateFaults = new();
        public int EnumerateCalls;
        public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
        {
            EnumerateCalls++;
            if (EnumerateFaults.Count > 0) throw EnumerateFaults.Dequeue();
            return ValueTask.FromResult<IReadOnlyList<ForeignWindow>>(_windows.ToArray());
        }

        public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) { Activated.Add(id.Value); return Task.CompletedTask; }
        public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAndActivateAsync(ForeignWindowId id, CancellationToken ct = default) { RestoredAndActivated.Add(id.Value); return Task.CompletedTask; }
        public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, CancellationToken ct = default) => Task.CompletedTask;

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
        public ValueTask<IReadOnlyList<InstalledApp>> EnumerateInstalledAppsAsync(CancellationToken ct = default) =>
            InstalledAppsFault is { } ex ? throw ex : ValueTask.FromResult(_installed);

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
        public ValueTask<IReadOnlyList<TrayItem>> GetItemsAsync(CancellationToken ct = default)
        {
            if (TrayFault is { } ex) throw ex;
            OnGetItems?.Invoke();
            return ValueTask.FromResult<IReadOnlyList<TrayItem>>(_trayItems.ToArray());
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
