using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Bevel.App.ShellCore;
using Bevel.Core;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// What the core→taskbar window traffic costs, and the two reductions in bevel-ougs.
///
/// <para><b>1. An app icon per delta.</b> Every WindowChanged carried the owning app's icon PNG,
/// base64'd into the JSON frame — and WindowChanged fires on every title change, which for a browser
/// or an editor is near enough every keystroke. The icon is per-APP and practically never changes.</para>
///
/// <para><b>2. A helper enumeration per mitigation pass.</b> <c>EnumerateWindows</c> re-runs the Swift
/// helper's CG + AX pass on every call. <c>WorkAreaMitigator</c> calls it on a 3 s safety poll, 100 ms
/// after every window open/change/focus event, and again to verify each correction — in the same
/// process that must answer the next Activate. The core already holds the projection those deltas
/// built, so the mitigator reads that instead.</para>
/// </summary>
public sealed class CoreWindowTrafficTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static CancellationToken Ct => new CancellationTokenSource(Timeout).Token;

    private static string NewSocketPath()
        => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bvltrf-{Guid.NewGuid():N}"[..14] + ".sock");

    private static byte[] NewNonce() => RandomNumberGenerator.GetBytes(32);

    /// <summary>Settings rooted in a throwaway directory — the parameterless ctor resolves the
    /// developer's real ~/.config/bevel and is refused in a test host.</summary>
    private static SettingsService ScratchSettings()
        => new(System.IO.Directory.CreateDirectory(System.IO.Path.Combine(
               System.IO.Path.GetTempPath(), "bevel-tests", Guid.NewGuid().ToString("n"))).FullName);

    /// <summary>A stand-in for a real macOS app icon, at a real size.
    ///
    /// <para>6,463 bytes is an actual 64x64 PNG rendered from a system app's .icns on the dev machine;
    /// a 20-icon sample ran 3.9-7.5 KB with a 4.9 KB median, which matches the helper's own note that
    /// its fixed 64x64 renders come out "~4-8 KB" (WindowServiceImpl.appIconPNG). Only the LENGTH
    /// matters here — base64 and JSON treat these bytes the same as a real icon's.</para></summary>
    private static byte[] RealisticIcon() => RandomNumberGenerator.GetBytes(6463);

    private static ForeignWindow Win(string id, string title, byte[]? icon = null) =>
        new(new ForeignWindowId(id), title, "App", IsMinimized: false, IsFocused: false,
            Bounds: new PalRect(0, 0, 800, 600), IconPng: icon, BundleId: "com.example." + id);

    private static async Task WaitFor(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(15);
        Assert.True(condition(), because);
    }

    private sealed class Pal : IWindowManager, IAppEnvironment, ISystemTrayHost
    {
        private readonly List<ForeignWindow> _windows = [];
        public int EnumerateCalls;

        public void Seed(params ForeignWindow[] w) => _windows.AddRange(w);
        public void RaiseOpened(ForeignWindow w) => WindowOpened?.Invoke(this, w);
        public void RaiseChanged(ForeignWindow w) => WindowChanged?.Invoke(this, w);
        public void RaiseClosed(ForeignWindow w) => WindowClosed?.Invoke(this, w);

        public Capabilities Capabilities { get; } =
            new(true, TrayCapability.Mirrored, [], SupportsReposition: true);

        public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref EnumerateCalls);
            return ValueTask.FromResult<IReadOnlyList<ForeignWindow>>(_windows.ToArray());
        }

        public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public List<ForeignWindowId> Zooms { get; } = [];
        public Task ZoomAsync(ForeignWindowId id, CancellationToken ct = default)
        {
            lock (Zooms) Zooms.Add(id);
            return Task.CompletedTask;
        }
        public List<(ForeignWindowId Id, ulong SpaceId, int OwnerPid)> SpaceMoves { get; } = [];
        public Task MoveWindowToSpaceAsync(ForeignWindowId id, ulong spaceId, int ownerPid = 0, CancellationToken ct = default)
        {
            lock (SpaceMoves) SpaceMoves.Add((id, spaceId, ownerPid));
            return Task.CompletedTask;
        }
        public SpaceList NextSpaces { get; set; } = new(Array.Empty<SpaceInfo>());
        public string? LastSpacesWindowId;
        public ValueTask<SpaceList> ListSpacesAsync(ForeignWindowId? windowId = null, CancellationToken ct = default)
        {
            LastSpacesWindowId = windowId?.Value;
            return ValueTask.FromResult(NextSpaces);
        }
        public List<(ForeignWindowId Id, PalRect Bounds, PalRect? Expected)> Repositions { get; } = [];
        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, PalRect? expected = null, CancellationToken ct = default)
        {
            lock (Repositions) Repositions.Add((id, bounds, expected));
            return Task.CompletedTask;
        }
        public Task<byte[]?> CaptureWindowAsync(ForeignWindowId id, int w, int h, CancellationToken ct = default)
            => Task.FromResult<byte[]?>(null);

        public event EventHandler<ForeignWindow>? WindowOpened;
        public event EventHandler<ForeignWindow>? WindowClosed;
        public event EventHandler<ForeignWindow>? WindowChanged;
        public event EventHandler<ForeignWindow>? ForegroundChanged;

        public ValueTask<IReadOnlyList<InstalledApp>> EnumerateInstalledAppsAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<InstalledApp>>(Array.Empty<InstalledApp>());
        public ValueTask<IReadOnlyList<RunningApp>> GetRunningAppsAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<RunningApp>>(Array.Empty<RunningApp>());
        public Task LaunchAsync(string appIdOrPath, CancellationToken ct = default) => Task.CompletedTask;
        public event EventHandler<RunningApp>? AppLaunched;
        public event EventHandler<RunningApp>? AppTerminated;
        public event EventHandler<IReadOnlyList<InstalledApp>>? InstalledAppsChanged;

        public ValueTask<IReadOnlyList<TrayItem>> GetItemsAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<TrayItem>>(Array.Empty<TrayItem>());
        public Task SetNativeTrayHiddenAsync(bool hidden, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ForwardClickAsync(TrayItemId id, TrayButton button, TrayModifiers modifiers,
            bool park = false, CancellationToken ct = default) => Task.FromResult(true);
        public event EventHandler<TrayItem>? ItemAdded;
        public event EventHandler<TrayItem>? ItemRemoved;
        public event EventHandler<TrayItem>? ItemUpdated;
    }

    private sealed record Harness(Pal Pal, ShellCoreServer Server, ShellCoreClient Core,
        ConcurrentQueue<CoreEvent> Events) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Core.DisposeAsync();
            await Server.DisposeAsync();
        }
    }

    private static async Task<Harness> StartAsync(Pal pal)
    {
        var path = NewSocketPath();
        var nonce = NewNonce();
        var server = new ShellCoreServer(pal, pal, pal, ScratchSettings(), path, nonce);
        await server.StartAsync(Ct);
        var core = new ShellCoreClient(path, nonce);
        var events = new ConcurrentQueue<CoreEvent>();
        core.EventReceived += events.Enqueue;
        await core.StartSessionAsync(Ct);
        // Every test here raises PAL deltas the moment this returns and counts them, so the session must
        // be LIVE: an answered Hello is what proves the core has registered this client for broadcasts
        // (bevel-j5mr — before, a session start could return early and the first deltas miss the client).
        Assert.Equal(1, server.SessionsStarted);
        return new Harness(pal, server, core, events);
    }

    private static List<CoreEvent> Deltas(ConcurrentQueue<CoreEvent> q, CoreEventKind kind) =>
        q.Where(e => e.Kind == kind).ToList();

    // ── 1. icon de-duplication ───────────────────────────────────────────

    /// <summary>A stream of title changes carries the icon ONCE. This is the whole win: a per-app,
    /// essentially constant blob was riding every keystroke-frequency event.</summary>
    [Fact]
    public async Task Repeated_title_changes_carry_the_app_icon_only_once()
    {
        var icon = RealisticIcon();
        var pal = new Pal();
        await using var h = await StartAsync(pal);

        pal.RaiseOpened(Win("w1", "Doc", icon));
        for (var i = 0; i < 6; i++)
            pal.RaiseChanged(Win("w1", $"Doc{i}", icon));

        await WaitFor(() => Deltas(h.Events, CoreEventKind.WindowChanged).Count == 6, "all six changes arrive");

        var changes = Deltas(h.Events, CoreEventKind.WindowChanged);
        Assert.All(changes, e => Assert.Null(e.Window!.IconPng));
        Assert.NotNull(Assert.Single(Deltas(h.Events, CoreEventKind.WindowOpened)).Window!.IconPng);
    }

    /// <summary>The case that must NOT break: a window first seen before its app icon was ready
    /// arrives icon-less, and the taskbar depends on a LATER event to carry it (ShellModel.ApplyUpdate
    /// retries only while the button has no icon). Absent-then-present is a change, so it is sent.
    /// Without this, de-duplication would leave such a button blank for life.</summary>
    [Fact]
    public async Task An_icon_that_arrives_after_the_window_is_still_delivered()
    {
        var pal = new Pal();
        await using var h = await StartAsync(pal);

        pal.RaiseOpened(Win("w1", "Doc"));                      // no icon yet
        pal.RaiseChanged(Win("w1", "Doc", RealisticIcon()));     // icon now available

        await WaitFor(() => Deltas(h.Events, CoreEventKind.WindowChanged).Count == 1, "the change arrives");
        Assert.NotNull(Assert.Single(Deltas(h.Events, CoreEventKind.WindowChanged)).Window!.IconPng);
    }

    /// <summary>An icon that genuinely changes is re-sent — de-duplication is by CONTENT, not a
    /// one-shot latch.</summary>
    [Fact]
    public async Task A_changed_icon_is_re_sent()
    {
        var pal = new Pal();
        await using var h = await StartAsync(pal);

        pal.RaiseOpened(Win("w1", "Doc", RealisticIcon()));
        pal.RaiseChanged(Win("w1", "Doc", RealisticIcon()));     // different bytes
        pal.RaiseChanged(Win("w1", "Doc2", null));               // genuinely absent

        await WaitFor(() => Deltas(h.Events, CoreEventKind.WindowChanged).Count == 2, "both changes arrive");
        var changes = Deltas(h.Events, CoreEventKind.WindowChanged);
        Assert.NotNull(changes[0].Window!.IconPng);
    }

    /// <summary>A window id that comes back (macOS reuses window numbers) must re-send its icon rather
    /// than inherit the closed window's "already sent" claim.</summary>
    [Fact]
    public async Task A_reused_window_id_sends_its_icon_again()
    {
        var icon = RealisticIcon();
        var pal = new Pal();
        await using var h = await StartAsync(pal);

        pal.RaiseOpened(Win("w1", "First", icon));
        pal.RaiseClosed(Win("w1", "First", icon));
        await WaitFor(() => Deltas(h.Events, CoreEventKind.WindowClosed).Count == 1, "the close arrives");

        pal.RaiseChanged(Win("w1", "Second", icon));   // same id, same icon, new window
        await WaitFor(() => Deltas(h.Events, CoreEventKind.WindowChanged).Count == 1, "the change arrives");

        Assert.NotNull(Assert.Single(Deltas(h.Events, CoreEventKind.WindowChanged)).Window!.IconPng);
    }

    /// <summary>What the omission is worth on the wire, measured rather than asserted in the
    /// abstract: the same event with and without a realistic icon, through the real serializer.</summary>
    [Fact]
    public void Omitting_the_icon_shrinks_the_frame_by_most_of_its_size()
    {
        var withIcon = CoreProtocol.Serialize(
            new CoreEvent(CoreEventKind.WindowChanged, Window: Win("w1", "Doc", RealisticIcon())));
        var without = CoreProtocol.Serialize(
            new CoreEvent(CoreEventKind.WindowChanged, Window: Win("w1", "Doc")));

        // base64 inflates the icon by 4/3, so a ~6.3 KB PNG is ~8.6 KB of frame on top of a few
        // hundred bytes of window fields.
        Assert.True(without.Length * 10 < withIcon.Length,
            $"frame with icon {withIcon.Length} bytes, without {without.Length} bytes — the icon should "
            + "dominate the frame, and dropping it should leave well under a tenth");
    }

    // ── 2. the mitigator stops driving helper passes ─────────────────────

    /// <summary>The projected enumerate answers from the core's own projection: no helper pass, and the
    /// icons dropped (nobody asking "where are the windows" wants a base64 PNG each).</summary>
    [Fact]
    public async Task The_projected_enumerate_never_touches_the_window_source()
    {
        var pal = new Pal();
        pal.Seed(Win("w1", "Doc", RealisticIcon()));
        await using var h = await StartAsync(pal);

        var seedCalls = pal.EnumerateCalls;           // the boot seed's own pass
        var wm = new ShellCoreWindowManager(h.Core);

        for (var i = 0; i < 5; i++)
        {
            var windows = await wm.EnumerateProjectedAsync(Ct);
            Assert.Equal("w1", Assert.Single(windows).Id.Value);
            Assert.Null(windows[0].IconPng);
        }

        Assert.Equal(seedCalls, pal.EnumerateCalls);
    }

    /// <summary>...while the ordinary enumerate still goes to the real source. That distinction is the
    /// point: ShellModel's reconcile loop exists to catch events the helper never delivered, and a poll
    /// of the projection those same events built could only ever agree with itself.</summary>
    [Fact]
    public async Task The_ordinary_enumerate_still_reaches_the_window_source()
    {
        var pal = new Pal();
        pal.Seed(Win("w1", "Doc"));
        await using var h = await StartAsync(pal);

        var before = pal.EnumerateCalls;
        var wm = new ShellCoreWindowManager(h.Core);
        await wm.EnumerateAsync(Ct);

        Assert.Equal(before + 1, pal.EnumerateCalls);
    }

    /// <summary>And the mitigator takes the projected path when its window manager offers one. Without
    /// this the whole change would be an unused interface — bevel-3gad's lesson.</summary>
    [Fact]
    public async Task The_mitigator_reads_the_projection_when_one_is_available()
    {
        var pal = new Pal();
        pal.Seed(Win("w1", "Doc"));
        await using var h = await StartAsync(pal);

        var wm = new ShellCoreWindowManager(h.Core);
        using var mitigator = new WorkAreaMitigator(wm, ScratchSettings(),
            () => new PalRect(0, 900, 1440, 40), () => new PalRect(0, 30, 1440, 910));

        var before = pal.EnumerateCalls;
        await mitigator.MitigateOnceAsync(Ct);

        Assert.Equal(1, mitigator.ProjectedEnumerations);
        Assert.Equal(0, mitigator.DirectEnumerations);
        Assert.Equal(before, pal.EnumerateCalls);   // no helper pass behind it
    }

    // bevel-tm1n: the compare-and-set frame has to survive the taskbar -> core hop. Dropping it would
    // silently turn every guarded move back into the unguarded one that caused the bug, with no test red.
    [Fact]
    public async Task Reposition_carries_its_expected_frame_across_the_core()
    {
        var pal = new Pal();
        pal.Seed(Win("w1", "Doc"));
        await using var h = await StartAsync(pal);
        var wm = new ShellCoreWindowManager(h.Core);
        var target = new PalRect(0, 30, 1440, 870);
        var expected = new PalRect(0, 30, 1440, 910);

        await wm.RepositionAsync(new ForeignWindowId("w1"), target, expected, Ct);

        var got = Assert.Single(pal.Repositions);
        Assert.Equal(target, got.Bounds);
        Assert.Equal(expected, got.Expected);
    }

    [Fact]
    public async Task Reposition_without_an_expected_frame_stays_unconditional()
    {
        var pal = new Pal();
        pal.Seed(Win("w1", "Doc"));
        await using var h = await StartAsync(pal);
        var wm = new ShellCoreWindowManager(h.Core);

        await wm.RepositionAsync(new ForeignWindowId("w1"), new PalRect(0, 0, 800, 600), ct: Ct);

        Assert.Null(Assert.Single(pal.Repositions).Expected);
    }

    // bevel-kpkx: the zoom verb must survive the taskbar -> core hop like every other window command —
    // dropping it would turn the menu's Maximize item into a silent no-op in split mode.
    [Fact]
    public async Task Zoom_command_reaches_the_window_manager_across_the_core()
    {
        var pal = new Pal();
        pal.Seed(Win("w1", "Doc"));
        await using var h = await StartAsync(pal);
        var wm = new ShellCoreWindowManager(h.Core);

        await wm.ZoomAsync(new ForeignWindowId("w1"), Ct);

        Assert.Equal(new[] { new ForeignWindowId("w1") }, pal.Zooms);
    }

    // bevel-44vg review: ListSpaces is a pull query across the core (never queued) — the reply's
    // Spaces plus the WindowSpaceId (the Space that window lives on, for the menu's disabled row)
    // must survive the taskbar -> core hop, and the window id must be forwarded to the PAL.
    [Fact]
    public async Task ListSpaces_pulls_spaces_and_window_space_across_the_core()
    {
        var pal = new Pal();
        pal.Seed(Win("w1", "Doc"));
        pal.NextSpaces = new SpaceList(
            [new SpaceInfo(100, 0, "Desktop 1", "UUID-A", IsCurrent: true),
             new SpaceInfo(250, 0, "Desktop 2", "UUID-A", IsCurrent: false)],
            WindowSpaceId: 250);
        await using var h = await StartAsync(pal);
        var wm = new ShellCoreWindowManager(h.Core);

        var spaces = await wm.ListSpacesAsync(new ForeignWindowId("w1"), Ct);

        Assert.Equal("w1", pal.LastSpacesWindowId);
        Assert.Equal(2, spaces.Spaces.Count);
        Assert.Equal(new[] { 100UL, 250UL }, spaces.Spaces.Select(s => s.Id).ToArray());
        Assert.Equal(250UL, spaces.WindowSpaceId);
    }

    // bevel-44vg review: the move must cross the core with all three arguments intact — window id,
    // target Space id, and the owner pid the caller saw at enumeration (recycled-id guard).
    [Fact]
    public async Task MoveWindowToSpace_carries_window_space_and_owner_pid_across_the_core()
    {
        var pal = new Pal();
        pal.Seed(Win("w1", "Doc"));
        await using var h = await StartAsync(pal);
        var wm = new ShellCoreWindowManager(h.Core);

        await wm.MoveWindowToSpaceAsync(new ForeignWindowId("w1"), 250, ownerPid: 4242, Ct);

        Assert.Equal(new[] { (new ForeignWindowId("w1"), 250UL, 4242) }, pal.SpaceMoves);
    }

    // bevel-44vg review: ListSpaces on a client that never connected is a non-queued pull — it
    // returns empty immediately instead of throwing or hanging on the dead link.
    [Fact]
    public async Task ListSpaces_on_a_never_connected_client_returns_empty_without_throwing()
    {
        await using var core = new ShellCoreClient(NewSocketPath(), NewNonce());
        var wm = new ShellCoreWindowManager(core);

        var spaces = await wm.ListSpacesAsync(new ForeignWindowId("w1"), Ct);

        Assert.Empty(spaces.Spaces);
        Assert.Null(spaces.WindowSpaceId);
    }

    /// <summary>On a PAL that IS the window source there is no projection, so the mitigator uses the
    /// ordinary enumerate — the capability must not silently disable mitigation off the split shell.</summary>
    [Fact]
    public async Task The_mitigator_falls_back_to_the_window_source_when_there_is_no_projection()
    {
        var pal = new Pal();
        pal.Seed(Win("w1", "Doc"));
        using var mitigator = new WorkAreaMitigator(pal, ScratchSettings(),
            () => new PalRect(0, 900, 1440, 40), () => new PalRect(0, 30, 1440, 910));

        await mitigator.MitigateOnceAsync(Ct);

        Assert.Equal(0, mitigator.ProjectedEnumerations);
        Assert.Equal(1, mitigator.DirectEnumerations);
        Assert.Equal(1, pal.EnumerateCalls);
    }
}
