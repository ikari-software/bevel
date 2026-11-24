# Performance — hot paths, baselines, budgets (bevel-zhmr)

Two-tier perf tracking for the frequently-run shell operations:

1. **`benchmarks/Bevel.Benchmarks`** — a [BenchmarkDotNet] project with precise measurements. Run manually
   for tracking; **not** in `Bevel.sln` and **not** CI-gated (BDN's absolute numbers are too machine-
   dependent to fail a build on).
2. **`PerfBudgetTests`** (in `Bevel.Taskbar.Tests`) — coarse budget tripwires that DO run in CI. Each
   asserts an average (over many warmed iterations) under a threshold set ~100–250× the measured median,
   so it survives runner variance yet still fails on a gross regression (an accidental O(n²), a per-call
   allocation blowup).

## Run the benchmarks

```
dotnet run -c Release --project benchmarks/Bevel.Benchmarks          # all
dotnet run -c Release --project benchmarks/Bevel.Benchmarks -- --filter '*Layout*'
```

Results land in `BenchmarkDotNet.Artifacts/` (git-ignored). A short-run job is pinned so a full sweep is a
few minutes, not tens.

## Baseline (Apple Silicon, .NET 10, Release, ShortRun)

Frequently-run pure-CPU paths. Times are per single call.

| Path | Case | Mean | Alloc | Notes |
|------|------|-----:|------:|-------|
| `TaskbarView.ComputeButtonLayout` | any window count | **~1.3 ns** | 0 B | O(1) width/label policy; flat across 10/50/100 |
| `TaskbarGrouping.Plan` (ungrouped) | 10 / 50 / 100 windows | 0.20 / 0.84 / **1.68 µs** | 1.0 / 4.6 / 9.0 KB | pass-through projection |
| `TaskbarGrouping.Plan` (grouping) | 10 / 50 / 100 windows | 0.79 / 3.84 / **8.32 µs** | 1.9 / 7.7 / 15.3 KB | GroupBy + fold; the part that scales with window count |
| `TrayRowsPanel.AssignRows` | 8 / 32 / 200 items × 3 rows | 18 / 53 / **418 ns** | 56 / 152 / 824 B | width-balanced row split; 200 is a stress ceiling |
| `TrayIconTint.Process` | 16px / 32px glyph | 8.6 / **16.0 µs** | 3.5 / 9.5 KB | decode + full-pixel template scan + recolour, per mirrored icon per capture |
| `SettingsService.SaveAsync` | full re-serialize + SQLite write | **~199 µs** | 4.2 KB | I/O-bound; noisy — informational, not budget-gated |

**Read:** nothing here is a bottleneck. Button layout is effectively free; the grouped plan for 100 windows
(far past any real taskbar) is ~8 µs; a tray capture tick tints each icon in ~16 µs. Settings save is
sub-millisecond even with the disk write.

## Budgets (the CI tripwires)

`PerfBudgetTests`, average over warmed iterations:

| Test | Budget | vs. median |
|------|-------:|-----------:|
| grouping `Plan(100)` | < 3000 µs | ~360× |
| `ComputeButtonLayout` | < 50 µs | huge (O(1)) |
| `AssignRows(200×3)` | < 200 µs | ~480× |
| `Tint(32px)` | < 3000 µs | ~190× |

Wide on purpose: they catch *complexity/allocation* regressions, not micro-variance. If a real optimization
lands, tighten the matching budget and update the table above.

## Not covered (needs a live harness)

- **Icon geometry build** (`Glyphs`/`ToolbarIcons`) — constructing an Avalonia `Control`/`Path` requires the
  UI/dispatcher thread, which BDN's engine thread isn't; it throws "Call from invalid thread". Bitmaps
  (`TrayIconTint`) are thread-agnostic and benchmark fine. A future UI-thread harness could cover glyph
  build.
- **Taskbar reflow end-to-end** and **IPC round-trips** (taskbar↔core↔helper) — these need a live Avalonia
  app / running processes, out of scope for the pure-CPU micro-harness. Profile with `dotnet-trace` under
  real churn when they're suspected.

## Theme apply (measured on the UI thread — bevel-yd2n, bevel-vw90)

BDN can't host this (brush construction `VerifyAccess`es the UI thread, which BDN's engine thread isn't), so
`benchmarks/Bevel.Benchmarks` has a dedicated mode that times the colourway switch on the main/UI thread with
a window of 240 `DynamicResource` consumers realized:

```
dotnet run -c Release --project benchmarks/Bevel.Benchmarks -- --measure-theme-switch
```

Baselines (Apple Silicon M2 Ultra, .NET 10, Release):

- Colourway switch (Blue↔Silver, Hybrid gloss, 240 consumers realized, 30 iters after 5 warmup):
  **mean ~20 ms, median ~20 ms, p95 ~25 ms, max ~29 ms, ~4.4 MiB/op** — under the 50 ms per-switch budget,
  so the per-key direct writes stay (no merged-dictionary rewrite; bevel-yd2n closed as measured).
- Full theme round-trip (swap to Blue2001 + Purple/Hybrid variant + revert, no window realized): **~0.8 ms**
  average (30 iters after 5 warmup) — CI-gated as `Perf_theme_apply_roundtrip_under_budget` in
  `PerfBudgetTests` at a 20× budget of 16 ms (bevel-vw90).

[BenchmarkDotNet]: https://benchmarkdotnet.org
