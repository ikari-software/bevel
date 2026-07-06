using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Core.Vfs;
using Bevel.FileManager.Components;
using Xunit;

namespace Bevel.FileManager.Tests;

/// <summary>
/// Tests for SearchPaneViewModel: content search reads through the provider so it is not
/// limited to the file scheme (bevel-5ya), and the search CTS is never disposed while the
/// walk still holds the token (bevel-u0z).
/// </summary>
public sealed class SearchPaneViewModelTests : IDisposable
{
    private readonly string _dir;
    private readonly VfsRoot _vfs = new();

    public SearchPaneViewModelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"bevel-search-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _vfs.Register(new LocalFsProvider());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static void PumpUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Content_search_finds_matches_through_the_provider()
    {
        File.WriteAllText(Path.Combine(_dir, "match.txt"), "somewhere the needle hides");
        File.WriteAllText(Path.Combine(_dir, "other.txt"), "only hay in here");

        var vm = new SearchPaneViewModel(_vfs)
        {
            LookInPath = _dir,
            ContainingText = "needle",
        };

        vm.StartSearch();
        PumpUntil(() => !vm.IsSearching);

        Assert.Contains(vm.Results, r => r.DisplayName == "match.txt");
        Assert.DoesNotContain(vm.Results, r => r.DisplayName == "other.txt");
    }

    [AvaloniaFact]
    public void Name_search_finds_matching_files()
    {
        File.WriteAllText(Path.Combine(_dir, "report-final.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "y");

        var vm = new SearchPaneViewModel(_vfs)
        {
            LookInPath = _dir,
            NameContains = "report",
        };

        vm.StartSearch();
        PumpUntil(() => !vm.IsSearching);

        Assert.Contains(vm.Results, r => r.DisplayName == "report-final.txt");
        Assert.DoesNotContain(vm.Results, r => r.DisplayName == "notes.txt");
    }

    [AvaloniaFact]
    public void Stop_and_dispose_after_completion_do_not_throw()
    {
        // bevel-u0z: once the walk completes it disposes its own CTS. A subsequent
        // StopSearch/Dispose must not touch a disposed CTS.
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "x");
        var vm = new SearchPaneViewModel(_vfs) { LookInPath = _dir, NameContains = "a" };

        vm.StartSearch();
        PumpUntil(() => !vm.IsSearching);

        var ex = Record.Exception(() =>
        {
            vm.StopSearch();
            vm.StopSearch();
            vm.Dispose();
        });
        Assert.Null(ex);
    }

    [AvaloniaFact]
    public void Rapid_start_stop_cycles_stay_consistent()
    {
        // Stress the CTS lifecycle: repeatedly starting and cancelling must not throw and
        // must settle with IsSearching == false.
        for (var i = 0; i < 25; i++)
            File.WriteAllText(Path.Combine(_dir, $"f{i}.txt"), "content");

        var vm = new SearchPaneViewModel(_vfs) { LookInPath = _dir, NameContains = "f" };

        var ex = Record.Exception(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                vm.StartSearch();
                Dispatcher.UIThread.RunJobs();
                vm.StopSearch();
            }
            PumpUntil(() => !vm.IsSearching, 3000);
        });

        Assert.Null(ex);
        Assert.False(vm.IsSearching);
    }
}
