using System;
using System.IO;
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

    private static async Task LoadPrograms(ShellModel model, int expected)
    {
        model.Start();
        for (var i = 0; i < 60 && model.Programs.Count < expected; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void Usage_store_persists_launches_and_first_seen_across_reload()
    {
        var dir = TempDir();
        try
        {
            var store = new ProgramUsageStore(dir);
            var seen = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.True(store.MarkSeen("com.a", seen));
            Assert.False(store.MarkSeen("com.a", seen.AddYears(1)));   // already recorded → no overwrite
            store.RecordLaunch("com.a");
            store.RecordLaunch("com.a");

            var reloaded = new ProgramUsageStore(dir);
            Assert.Equal(2, reloaded.LaunchCount("com.a"));
            Assert.Equal(seen, reloaded.FirstSeen("com.a"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public async Task Frequent_programs_rank_by_launch_count()
    {
        var dir = TempDir();
        try
        {
            var t = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var store = new ProgramUsageStore(dir);
            store.Set("com.a", launchCount: 1, t);
            store.Set("com.b", launchCount: 5, t);
            store.Set("com.c", launchCount: 3, t);
            var appEnv = new StubAppEnvironment(
                new InstalledApp("com.a", "Alpha", null),
                new InstalledApp("com.b", "Beta", null),
                new InstalledApp("com.c", "Cee", null));
            using var model = new ShellModel(null, appEnv, null, usage: store);
            await LoadPrograms(model, 3);

            // All share one first-seen → all "frequent" → ordered by launches desc.
            Assert.Equal(3, model.FrequentPrograms.Count);
            Assert.Equal("Beta", model.FrequentPrograms[0].DisplayName);
            Assert.Equal("Cee", model.FrequentPrograms[1].DisplayName);
            Assert.Equal("Alpha", model.FrequentPrograms[2].DisplayName);
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public async Task Newly_added_app_sorts_above_a_heavily_used_old_one()
    {
        var dir = TempDir();
        try
        {
            var store = new ProgramUsageStore(dir);
            store.Set("com.a", launchCount: 10, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)); // first-run baseline, heavily used
            store.Set("com.b", launchCount: 0, DateTime.UtcNow.AddHours(-1));                          // installed just now
            var appEnv = new StubAppEnvironment(
                new InstalledApp("com.a", "Alpha", null),
                new InstalledApp("com.b", "Beta", null));
            using var model = new ShellModel(null, appEnv, null, usage: store);
            await LoadPrograms(model, 2);

            Assert.Equal("Beta", model.FrequentPrograms[0].DisplayName);   // newest on top, above the frequent one
            Assert.Equal("Alpha", model.FrequentPrograms[1].DisplayName);
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public async Task Newly_added_window_expires_so_stale_installs_rejoin_the_frequency_ranking()
    {
        var dir = TempDir();
        try
        {
            var store = new ProgramUsageStore(dir);
            // com.a is the first-run baseline, heavily used. com.b was installed a year later — but that is
            // still long ago (well outside the recent "newly added" window), so it must NOT pin to the top;
            // it rejoins the frequency ranking and sits below the heavily-used app.
            store.Set("com.a", launchCount: 10, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            store.Set("com.b", launchCount: 0, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var appEnv = new StubAppEnvironment(
                new InstalledApp("com.a", "Alpha", null),
                new InstalledApp("com.b", "Beta", null));
            using var model = new ShellModel(null, appEnv, null, usage: store);
            await LoadPrograms(model, 2);

            Assert.Equal("Alpha", model.FrequentPrograms[0].DisplayName);   // stale install no longer starves frequency
            Assert.Equal("Beta", model.FrequentPrograms[1].DisplayName);
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public async Task Frequent_programs_respect_the_cap()
    {
        var dir = TempDir();
        try
        {
            var t = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var store = new ProgramUsageStore(dir);
            var apps = new InstalledApp[5];
            for (var i = 0; i < 5; i++)
            {
                var id = $"com.app{i}";
                store.Set(id, launchCount: i, t);
                apps[i] = new InstalledApp(id, $"App{i}", null);
            }
            var appEnv = new StubAppEnvironment(apps);
            using var model = new ShellModel(null, appEnv, null, usage: store) { FrequentCap = 3 };
            await LoadPrograms(model, 5);

            Assert.Equal(5, model.Programs.Count);       // full list unaffected
            Assert.Equal(3, model.FrequentPrograms.Count); // curated list capped
        }
        finally { Directory.Delete(dir, true); }
    }
}
