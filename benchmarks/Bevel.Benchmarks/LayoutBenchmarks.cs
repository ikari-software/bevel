using Bevel.Core;
using Bevel.Pal.Abstractions;
using Bevel.Taskbar;
using BenchmarkDotNet.Attributes;

namespace Bevel.Benchmarks;

/// <summary>
/// Taskbar strip layout hot paths — re-run on every window open/close/resize and on each SizeChanged
/// tick while the bar animates. Two pure functions:
/// <list type="bullet">
/// <item><see cref="TaskbarView.ComputeButtonLayout"/> — the width/label policy LayoutButtons calls
/// (O(1) in the window count, but exercised across 10/50/100 to confirm it stays flat).</item>
/// <item><see cref="TaskbarGrouping.Plan"/> — the O(n) grouped-strip projection over the live window
/// list (LINQ GroupBy + dictionary), the part that genuinely scales with window count.</item>
/// </list>
/// </summary>
[MemoryDiagnoser]
public class LayoutBenchmarks
{
    [Params(10, 50, 100)]
    public int Windows;

    private IReadOnlyList<TaskItemViewModel> _ungrouped = null!;
    private IReadOnlyList<TaskItemViewModel> _grouped = null!;

    [GlobalSetup]
    public void Setup()
    {
        var wm = new NoOpWindowManager();

        // Distinct-app windows: nothing collapses, the worst case for pass-through planning.
        var ungrouped = new List<TaskItemViewModel>(Windows);
        for (var i = 0; i < Windows; i++)
        {
            var w = new ForeignWindow(new ForeignWindowId("w" + i), "Window " + i, "com.example.app" + i,
                IsMinimized: false, IsFocused: i == 0, Bounds: new PalRect(0, 0, 800, 600));
            ungrouped.Add(new TaskItemViewModel(w, wm));
        }
        _ungrouped = ungrouped;

        // ~4 windows per app across a handful of apps: heavy grouping (GroupBy + folding).
        var grouped = new List<TaskItemViewModel>(Windows);
        for (var i = 0; i < Windows; i++)
        {
            var app = "com.example.app" + (i % Math.Max(1, Windows / 4));
            var w = new ForeignWindow(new ForeignWindowId("w" + i), "Window " + i, app,
                IsMinimized: false, IsFocused: i == 0, Bounds: new PalRect(0, 0, 800, 600));
            grouped.Add(new TaskItemViewModel(w, wm));
        }
        _grouped = grouped;
    }

    [Benchmark]
    public (double, bool) ComputeButtonLayout_ShrinkToFit()
        => TaskbarView.ComputeButtonLayout(
            TaskbarButtonWidthMode.ShrinkToFit, available: 1280, count: Windows, rows: 2,
            max: 160, minButtonWidth: 80, labels: TaskbarButtonLabels.Auto);

    [Benchmark]
    public int Plan_NoGrouping()
        => TaskbarGrouping.Plan(_ungrouped, grouping: false).Count;

    [Benchmark]
    public int Plan_Grouping()
        => TaskbarGrouping.Plan(_grouped, grouping: true).Count;
}

/// <summary>Data-only window manager — TaskItemViewModel needs one for its commands, but none of the
/// benchmarked planning code invokes them.</summary>
internal sealed class NoOpWindowManager : IWindowManager
{
    public Capabilities Capabilities { get; } = new(true, TrayCapability.Mirrored, []);
    public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<ForeignWindow>>([]);
    public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
    public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
    public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
    public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default) => Task.CompletedTask;
    public Task RepositionAsync(ForeignWindowId id, PalRect bounds, CancellationToken ct = default) => Task.CompletedTask;
    public event EventHandler<ForeignWindow>? WindowOpened { add { } remove { } }
    public event EventHandler<ForeignWindow>? WindowClosed { add { } remove { } }
    public event EventHandler<ForeignWindow>? WindowChanged { add { } remove { } }
    public event EventHandler<ForeignWindow>? ForegroundChanged { add { } remove { } }
}
