using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The curated Start-menu left column (bevel): a capped list of newly-added apps on top, then the
/// most-frequently-used, backed by a persistent ProgramUsageStore.
/// </summary>
public sealed class ProgramCurationTests
{
    private static string TempDir()
        => Path.Combine(Path.GetTempPath(), "bevel-usage-" + Guid.NewGuid().ToString("N"));

    // The store persists on a fire-and-forget task; deleting the directory while a write is in flight throws
    // IOException on Windows ("program-usage.json is being used by another process"). Drain every store's write
    // chain for the directory before removing it.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentBag<ProgramUsageStore>> Stores = new();

    private static ProgramUsageStore NewStore(string dir)
    {
        var store = new ProgramUsageStore(dir);
        Stores.GetOrAdd(dir, _ => new()).Add(store);
        return store;
    }

    private static void Cleanup(string dir)
    {
        if (Stores.TryRemove(dir, out var bag))
            Task.WaitAll(bag.Select(st => st.FlushAsync()).ToArray(), TimeSpan.FromSeconds(10));
        Directory.Delete(dir, true);
    }

    private static void LoadPrograms(ShellModel model, int expected)
    {
        model.Start();
        model.StartProgramsAsync();
        Bevel.TestSupport.Settle.Until(() => model.Programs.Count >= expected, "expected programs to load");
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void Usage_store_persists_launches_and_first_seen_across_reload()
    {
        var dir = TempDir();
        try
        {
            var store = NewStore(dir);
            var seen = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.True(store.MarkSeen("com.a", seen));
            Assert.False(store.MarkSeen("com.a", seen.AddYears(1)));   // already recorded → no overwrite
            store.RecordLaunch("com.a");
            store.RecordLaunch("com.a");

            store.FlushAsync().Wait();   // writes are now fire-and-forget off the UI thread (ce-review)
            var reloaded = NewStore(dir);
            Assert.Equal(2, reloaded.LaunchCount("com.a"));
            Assert.Equal(seen, reloaded.FirstSeen("com.a"));
        }
        finally { Cleanup(dir); }
    }

    [AvaloniaFact]
    public void Frequent_programs_rank_by_launch_count()
    {
        var dir = TempDir();
        try
        {
            var t = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var store = NewStore(dir);
            store.Set("com.a", launchCount: 1, t);
            store.Set("com.b", launchCount: 5, t);
            store.Set("com.c", launchCount: 3, t);
            var appEnv = new StubAppEnvironment(
                new InstalledApp("com.a", "Alpha", null),
                new InstalledApp("com.b", "Beta", null),
                new InstalledApp("com.c", "Cee", null));
            using var model = new ShellModel(null, appEnv, null, usage: store);
            LoadPrograms(model, 3);

            // All share one first-seen → all "frequent" → ordered by launches desc.
            Assert.Equal(3, model.FrequentPrograms.Count);
            Assert.Equal("Beta", model.FrequentPrograms[0].DisplayName);
            Assert.Equal("Cee", model.FrequentPrograms[1].DisplayName);
            Assert.Equal("Alpha", model.FrequentPrograms[2].DisplayName);
        }
        finally { Cleanup(dir); }
    }

    [AvaloniaFact]
    public void Newly_added_app_sorts_above_a_heavily_used_old_one()
    {
        var dir = TempDir();
        try
        {
            var store = NewStore(dir);
            store.Set("com.a", launchCount: 10, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)); // first-run baseline, heavily used
            store.Set("com.b", launchCount: 0, DateTime.UtcNow.AddHours(-1));                          // installed just now
            var appEnv = new StubAppEnvironment(
                new InstalledApp("com.a", "Alpha", null),
                new InstalledApp("com.b", "Beta", null));
            using var model = new ShellModel(null, appEnv, null, usage: store);
            LoadPrograms(model, 2);

            Assert.Equal("Beta", model.FrequentPrograms[0].DisplayName);   // newest on top, above the frequent one
            Assert.Equal("Alpha", model.FrequentPrograms[1].DisplayName);
        }
        finally { Cleanup(dir); }
    }

    [AvaloniaFact]
    public void Newly_added_window_expires_so_stale_installs_rejoin_the_frequency_ranking()
    {
        var dir = TempDir();
        try
        {
            var store = NewStore(dir);
            // com.a is the first-run baseline, heavily used. com.b was installed a year later — but that is
            // still long ago (well outside the recent "newly added" window), so it must NOT pin to the top;
            // it rejoins the frequency ranking and sits below the heavily-used app.
            store.Set("com.a", launchCount: 10, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            store.Set("com.b", launchCount: 0, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var appEnv = new StubAppEnvironment(
                new InstalledApp("com.a", "Alpha", null),
                new InstalledApp("com.b", "Beta", null));
            using var model = new ShellModel(null, appEnv, null, usage: store);
            LoadPrograms(model, 2);

            Assert.Equal("Alpha", model.FrequentPrograms[0].DisplayName);   // stale install no longer starves frequency
            Assert.Equal("Beta", model.FrequentPrograms[1].DisplayName);
        }
        finally { Cleanup(dir); }
    }

    [AvaloniaFact]
    public void Frequent_programs_respect_the_cap()
    {
        var dir = TempDir();
        try
        {
            var t = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var store = NewStore(dir);
            var apps = new InstalledApp[5];
            for (var i = 0; i < 5; i++)
            {
                var id = $"com.app{i}";
                store.Set(id, launchCount: i, t);
                apps[i] = new InstalledApp(id, $"App{i}", null);
            }
            var appEnv = new StubAppEnvironment(apps);
            using var model = new ShellModel(null, appEnv, null, usage: store) { FrequentCap = 3 };
            LoadPrograms(model, 5);

            Assert.Equal(5, model.Programs.Count);       // full list unaffected
            Assert.Equal(3, model.FrequentPrograms.Count); // curated list capped
        }
        finally { Cleanup(dir); }
    }

    // ── bevel-xrx6 Part A: exclude-open apps from the frequent column ──

    private sealed class RaisableWm : IWindowManager
    {
        private readonly List<ForeignWindow> _windows = new();
        public Capabilities Capabilities { get; } = new(Available: true, TrayMode: TrayCapability.Mirrored, Notes: Array.Empty<string>());
        public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<ForeignWindow>>(_windows.ToArray());
        public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RepositionAsync(ForeignWindowId id, PalRect bounds, PalRect? expected = null, CancellationToken ct = default) => Task.CompletedTask;
        public event EventHandler<ForeignWindow>? WindowOpened;
        public event EventHandler<ForeignWindow>? WindowClosed;
        public event EventHandler<ForeignWindow>? WindowChanged;
        public event EventHandler<ForeignWindow>? ForegroundChanged;
        public void Open(ForeignWindow w)
        {
            _windows.Add(w);
            WindowOpened?.Invoke(this, w);
            Dispatcher.UIThread.RunJobs();
        }
        public void Close(ForeignWindowId id)
        {
            var w = _windows.First(x => x.Id == id);
            _windows.Remove(w);
            WindowClosed?.Invoke(this, w);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static ForeignWindow Win(string id, string appId) =>
        new(new ForeignWindowId(id), $"Window of {appId}", appId, false, false, new PalRect(0, 0, 800, 600));

    private static (ShellModel Model, RaisableWm Wm) ModelWithApps(ProgramUsageStore store, int appCount, int cap)
    {
        var t = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var apps = new InstalledApp[appCount];
        for (var i = 0; i < appCount; i++)
        {
            var id = $"com.app{i}";
            store.Set(id, launchCount: appCount - i, t);   // app0 most-used
            apps[i] = new InstalledApp(id, $"App{i}", null);
        }
        var wm = new RaisableWm();
        var model = new ShellModel(wm, new StubAppEnvironment(apps), null, usage: store) { FrequentCap = cap };
        LoadPrograms(model, appCount);
        return (model, wm);
    }

    private static string[] Column(ShellModel m) => m.FrequentPrograms.Select(p => p.DisplayName).ToArray();

    [AvaloniaFact]
    public void Open_apps_are_excluded_and_the_column_backfills_to_the_cap()
    {
        var dir = TempDir();
        try
        {
            var store = NewStore(dir);
            var (model, wm) = ModelWithApps(store, 4, cap: 3);
            using (model)
            {
                Assert.Equal(new[] { "App0", "App1", "App2" }, Column(model));   // raw frequency order

                wm.Open(Win("w1", "com.app0"));   // the most-used app opens a window
                Bevel.TestSupport.Settle.Until(() => Column(model).SequenceEqual(new[] { "App1", "App2", "App3" }),
                    "open app excluded, column backfilled to the cap");
            }
        }
        finally { Cleanup(dir); }
    }

    [AvaloniaFact]
    public void Exclude_open_off_keeps_raw_frequency_order()
    {
        var dir = TempDir();
        try
        {
            var store = NewStore(dir);
            var (model, wm) = ModelWithApps(store, 4, cap: 3);
            using (model)
            {
                model.FrequentExcludeOpen = false;
                wm.Open(Win("w1", "com.app0"));
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(new[] { "App0", "App1", "App2" }, Column(model));   // open app still listed
            }
        }
        finally { Cleanup(dir); }
    }

    [AvaloniaFact]
    public void Closing_the_last_window_returns_the_app_to_the_column()
    {
        var dir = TempDir();
        try
        {
            var store = NewStore(dir);
            var (model, wm) = ModelWithApps(store, 4, cap: 3);
            using (model)
            {
                wm.Open(Win("w1", "com.app0"));
                Bevel.TestSupport.Settle.Until(() => !Column(model).Contains("App0"), "App0 excluded while open");

                wm.Close(new ForeignWindowId("w1"));
                Bevel.TestSupport.Settle.Until(() => Column(model)[0] == "App0", "App0 back on top after close");
            }
        }
        finally { Cleanup(dir); }
    }
}
