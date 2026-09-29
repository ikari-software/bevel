using System.Linq;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bevel.App.ShellCore;
using Bevel.Core;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Regression coverage for the bevel-kclq REGRESSION reported 2026-09-29: right after a fresh shell
/// start, the Start menu opens a row-height above the taskbar; a restart clears it.
///
/// <see cref="StartButtonCapTests"/> guards the STATIC fix (StartButton.MaxHeight clamped to the bar's
/// OWN row count). This class drives the actual startup ORDERING that reintroduced the symptom in a new
/// state: a split-process taskbar's <see cref="RemoteSettingsService"/> can paint its very first
/// <c>Current</c> snapshot (rows=1, the default) before the persisted row count (rows=2) has actually
/// arrived from the shell core — a cold/stale on-disk peer cache, or a core connect still in flight are
/// both ordinary ways for that to happen. <see cref="TaskbarWindow"/>/<see cref="TaskbarView"/> get built
/// from that stale snapshot, so <c>StartButton.MaxHeight</c> — the value the kclq fix derives the popup's
/// anchor from — is computed for the WRONG (too small) row count. When the real snapshot then lands, nothing
/// pushed the correction into the already-built window: <c>App.OnFrameworkInitializationCompleted</c>'s
/// generic <c>settings.Changed</c> handler re-applies only the theme + folder options, never
/// <c>TaskbarView.ApplyLiveSettings</c>. The mismatch was frozen for the rest of the session — exactly
/// "restart clears it", since the next boot reads an already-warm, correct cache.
///
/// Uses a REAL <see cref="ShellCoreServer"/> over a live Unix socket and a REAL
/// <see cref="RemoteSettingsService"/>, mirroring <see cref="RemoteSettingsFirstPaintTests"/> — this is
/// the actual peer-settings machinery, not a hand-rolled stand-in for it.
/// </summary>
public sealed class StartMenuSettingsRaceTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static CancellationToken Ct => new CancellationTokenSource(Timeout).Token;

    private readonly string _coreDir = Path.Combine(Path.GetTempPath(), $"bvlkclq-core-{Guid.NewGuid():N}");
    private readonly string _socket = Path.Combine(Path.GetTempPath(), $"bvlkclq-{Guid.NewGuid():N}"[..14] + ".sock");
    private readonly byte[] _nonce = RandomNumberGenerator.GetBytes(32);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        try { Directory.Delete(_coreDir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private static async Task PumpUntil(Func<bool> done, string because)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!done() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(done(), because);
    }

    /// <summary>
    /// The real regression: TaskbarWindow/TaskbarView are constructed and LAID OUT (OnLoaded has already
    /// run, exactly like <c>taskbarWin.Show()</c> in <c>App.CreateTaskbarSurfaceCore</c>) from a peer
    /// settings snapshot that has NOT yet caught up to the persisted row count — no cache on disk, and the
    /// core is not reachable yet, so <see cref="RemoteSettingsService.LoadAsync"/> seeds the DEFAULT
    /// (rows=1). Only afterwards does the real core come up with the persisted rows=2 and push its
    /// on-connect snapshot. Before the fix, nothing carried that correction into the already-built window:
    /// this test fails on the pre-fix code (Start button parked at rows=1's cap forever) and passes once
    /// <c>TaskbarView</c> re-applies settings on its own <c>ISettingsService.Changed</c>.
    /// </summary>
    [AvaloniaFact]
    public async Task Start_button_cap_catches_up_when_the_persisted_row_count_arrives_after_the_bar_is_already_shown()
    {
        // No cache, no core listening yet — the peer's first LoadAsync falls back to defaults
        // (TaskbarRows=1), exactly the "taskbar comes up at the default row count" startup race.
        await using var client = new ShellCoreClient(_socket, _nonce);
        await using var remote = new RemoteSettingsService(client, cache: null);
        await remote.LoadAsync(Ct);
        Assert.Equal(1, remote.Current.TaskbarRows);
        Assert.False(remote.HasLiveSnapshot);

        // Build the real taskbar surface from that stale snapshot — mirrors App.CreateTaskbarSurfaceCore:
        // TaskbarTheme.Configure, TaskbarView.Initialize(settings.Current, ..., settingsService: settings),
        // then a TaskbarWindow sized from settings.Current.TaskbarRows, shown.
        var view = new TaskbarView();
        view.Initialize(remote.Current, settingsService: remote);
        var window = new TaskbarWindow(null, rows: remote.Current.TaskbarRows) { Content = view, Width = 1200 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var start = view.GetVisualDescendants().OfType<Button>().First(b => b.Name == "StartButton");

        // Sanity: confirms the stale state is really in effect before the correction lands — a test that
        // could pass without ever observing the bad state would prove nothing.
        Assert.Equal(1, window.Rows);
        Assert.Equal(TaskbarTheme.HeightForRows(1), start.MaxHeight);

        // The real core comes up NOW, after the bar is already built and shown, with the actually
        // persisted row count. The client's reconnect supervisor (armed since construction) picks it up
        // on its own and the on-connect SettingsSnapshot corrects the peer live.
        var core = new SettingsService(_coreDir);
        await core.LoadAsync(Ct);
        await core.UpdateAsync(s => s.TaskbarRows = 2, Ct);
        await using var server = new ShellCoreServer(
            new Bevel.Pal.Fake.FakeWindowManager(), new Bevel.Pal.Fake.FakeAppEnvironment(),
            new Bevel.Pal.Fake.FakeSystemTrayHost(), core, _socket, _nonce);
        await server.StartAsync(Ct);

        await PumpUntil(() => remote.HasLiveSnapshot, "the on-connect settings snapshot should land");
        await PumpUntil(() => window.Rows == 2, "the corrected row count should reach the already-built TaskbarWindow");

        var barHeight = TaskbarTheme.HeightForRows(window.Rows);
        Assert.Equal(TaskbarTheme.HeightForRows(2), barHeight);
        Assert.True(start.MaxHeight <= barHeight,
            $"Start button capped at {start.MaxHeight} in a {barHeight}-tall bar after the persisted row " +
            "count arrived late — the popup anchored above this button would float off the bar");
        // Not just "no worse than the bar" — actually caught up to the NEW row count, not frozen at the
        // stale one (a cap that never moved would also happen to satisfy "<= a 1-row bar" trivially).
        Assert.Equal(TaskbarTheme.HeightForRows(2), start.MaxHeight);
    }
}
