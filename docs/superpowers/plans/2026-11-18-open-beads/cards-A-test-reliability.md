> Part of the [open-beads master plan](README.md). Read README.md first: it sets the rules, the order, and the rulings that override individual cards.
> Written 2026-11-18 from reading the tree at `51582c0`. Nothing here was built or run. `UNVERIFIED:` marks a claim the researcher could not confirm — check it before relying on it.

## Cluster A: Test reliability, CI and measurement

Owners of this area:
- **Headless test harness** — `tests/Bevel.{UI,FileManager,Taskbar}.Tests/TestAppBuilder.cs` (`[assembly: AvaloniaTestApplication]` +
  `DisableTestParallelization`), shared helpers in `tests/Shared/` (`SiteShot.cs`, `DispatcherFrameSupport.cs`, linked via `<Compile Include>`).
  No `AvaloniaTestIsolation` attribute exists, so Avalonia.Headless 11.3.18 runs its DEFAULT `PerTest` isolation: every `[AvaloniaFact]`
  gets a NEW locator scope, a NEW `Bevel.App.App` and a NEW `Dispatcher.UIThread` (`HeadlessUnitTestSession.EnsureIsolatedApplication`).
  Process statics (`ThemeService`, `ColorSchemeService`, `Blue2001VariantService`, leaked background tasks) survive across tests. Two beads
  (2nn2, i145) are consequences of that one fact.
- **Render/site pipeline** — `Render*` tests in `tests/Bevel.Taskbar.Tests`, `site/harvest-shots.sh`, `site/check-shot-sizes.py`, CI job `site-shots-drift`.
- **CI** — `.github/workflows/ci.yml` (jobs `build-test` x3 OS, `windows-smoke`, `publish`, `swift` (build only), `theme-drift`, `site-shots-drift`, `proto-drift`).
- **macOS live rigs** — `native/helper-macos/Tests/BevelHelperTests/*`, `tests/rigs/macos-lite/WindowSpawner`, `tests/Bevel.Pal.MacOS.Tests/LiveMacOSWindowManagerTests.cs`.
- **Measurement** — `benchmarks/Bevel.Benchmarks` (not in `Bevel.sln`), `tests/Bevel.Taskbar.Tests/PerfBudgetTests.cs`, `docs/perf.md`; debug logs `TaskbarLog`, `RestartDiag`, `HelperDiagnostics`.

Target design: a test run is a pure function of the commit — no stray thread can change a test's dispatcher, no static carries one
test's theme into the next, no test writes tracked files unless asked, every seam (stream loop, osascript runner) has a fake-driven test,
and CI actually executes every gate it claims (three gaps found, listed under "New beads to create").

## Order

1. bevel-2nn2 — blocks every PR; mechanism now derived from Avalonia source, fix is ~10 lines per test assembly.
2. bevel-i145 — same root fact (fresh Application per test vs process-static theme state); removes a second random-red source.
3. bevel-tv19 — churn already fixed by 35ea1c0; finishing it stops tests writing into tracked files (clean `git status` for everyone).
4. bevel-umgr — only deterministic coverage for the helper->core stream loops (north star 2); seam half exists already.
5. bevel-6tpi — the only assertion of popup placement; guards a shipped regression class (kclq) and deletes scaffolding.
6. bevel-hr5k — permission bugs can only be reproduced from a Finder launch, which has no helper logs; half done.
7. bevel-hs1j — three window-manager tests execute nowhere; needs the owner's TCC grant once.
8. bevel-79r — flaky close test; cannot even be reproduced until hs1j gives it a host.
9. bevel-2tlp — small seam + tests for the osascript runner; isolated, low risk.
10. bevel-adbo — one remaining 1x landing-page shot; cosmetic.
11. bevel-lgpn — evidence-gathering for one exempted shot; cosmetic.
12. bevel-zhmr — already delivered (442b6f8); close and split the leftovers.

## Cards

### bevel-2nn2 — PushFrame PlatformNotSupportedException in headless tests
- **Meta:** P1 · bug · size S · AGENT
- **State today:** Not "dispatcher built once too early". Avalonia 11.3.18 source (tag 11.3.18, `src/Headless/Avalonia.Headless/HeadlessUnitTestSession.cs`): with default PerTest isolation each test runs `AvaloniaLocator.EnterScope(); Dispatcher.ResetBeforeUnitTests()` (`s_uiThread = null`) `; _appBuilder.SetupUnsafe()`. `SetupUnsafe` runs options, runtime and RENDERING init (`UseSkia`) BEFORE `WindowingSubsystemInitializer` binds `IDispatcherImpl` (`AvaloniaHeadlessPlatform.Initialize`). `Dispatcher.UIThread` is `s_uiThread ??= CreateUIThreadDispatcher()`, unsynchronised, callable from ANY thread; with no `IDispatcherImpl` in the locator it builds `new Dispatcher(new NullDispatcherImpl())` — `_controlledImpl == null`, and `NullDispatcherImpl.CurrentThreadIsLoopThread => true`, which is exactly why `VerifyAccess` passes and `PushFrame` throws `PlatformNotSupportedException`. So ONE test is poisoned when a leftover background thread from an earlier test (21 files in `src/` call `Dispatcher.UIThread.Post/InvokeAsync`, e.g. `src/Bevel.FileManager/FileOperations/ProgressDialog.axaml.cs`) touches `Dispatcher.UIThread` inside that window. Next test resets again, which is why `DispatcherFrameSupport.The_ui_dispatcher_can_push_frames` passed in run 44289972930. Victims are tests whose xUnit invoker task is not already complete: `async Task` tests AND sync tests whose product code posts to the SynchronizationContext (e.g. `FeatureInteractionTests.NewWindow_raises_...` is `void`). UNVERIFIED: not reproduced in CI; the test below reproduces the mechanism locally.
- **Depends on:** none
- **Files:** `tests/Shared/HeadlessDispatcherRoot.cs` (new); `tests/Shared/DispatcherFrameSupport.cs`; `tests/Bevel.UI.Tests/TestAppBuilder.cs`, `tests/Bevel.FileManager.Tests/TestAppBuilder.cs`, `tests/Bevel.Taskbar.Tests/TestAppBuilder.cs`; the three test `.csproj` (add `<Compile Include="..\Shared\HeadlessDispatcherRoot.cs" Link="HeadlessDispatcherRoot.cs" />`); `.github/workflows/ci.yml`.
- **Decision:** Remove the null path instead of chasing touchers. `TestAppBuilder.BuildAvaloniaApp()` is invoked by `AppBuilder.Configure(Type)` ON the session thread, once, before any scope exists (`HeadlessUnitTestSession.StartNew`). There, bind a controlled impl into the ROOT locator: `Bevel.Tests.Shared.HeadlessDispatcherRoot.Install()` (new, idempotent via a static bool) = `AvaloniaLocator.CurrentMutable.Bind<IDispatcherImpl>().ToConstant(RootImpl = new ManagedDispatcherImpl(null));` (`Avalonia.Controls.Platform.ManagedDispatcherImpl` captures `Thread.CurrentThread` as loop thread = session thread, so `VerifyAccess` still passes). Child test scopes fall back to it (`AvaloniaLocator.GetService` -> `_parentScope`), so a stray touch now builds a dispatcher that CAN push frames. Do NOT switch to `AvaloniaTestIsolationLevel.PerAssembly` (changes 800+ tests' semantics). Do NOT bind from a module initializer (wrong thread — the trap noted in the bead). UNVERIFIED: `IDispatcherImpl`/`ManagedDispatcherImpl` are `[PrivateApi]`/`[Unstable]`; if the build emits CS0618 wrap the line in `#pragma warning disable CS0618`.
- **Test first:** in `tests/Shared/DispatcherFrameSupport.cs` add a plain `[Fact]` `A_dispatcher_built_outside_a_test_scope_still_supports_run_loops`: `HeadlessUnitTestSession.GetOrStartForAssembly(typeof(DispatcherFrameSupport).Assembly); Assert.True(Dispatcher.UIThread.SupportsRunLoops);` — it IS a stray touch (xUnit thread, no scope active). Red today (NullDispatcherImpl), green after `Install()`.
- **Steps:**
  - [ ] Add the `[Fact]` above; run it in `Bevel.FileManager.Tests`; confirm it FAILS on `SupportsRunLoops` (this is the local reproduction the bead asked for). If it passes before the fix, stop and report — the mechanism is wrong.
  - [ ] Create `HeadlessDispatcherRoot` (`public static IDispatcherImpl? RootImpl`, `public static void Install()`), link it into the three csproj, call `HeadlessDispatcherRoot.Install();` as the first statement of each `BuildAvaloniaApp()` (convert the expression body to a block).
  - [ ] Detector: in each `BuildAvaloniaApp()` append `.AfterSetup(_ => HeadlessDispatcherRoot.ReportIfStray())`; `ReportIfStray()` reads `Dispatcher`'s private `_impl` field by reflection and, if `ReferenceEquals(impl, RootImpl)`, appends one timestamped line to `Path.Combine(Environment.GetEnvironmentVariable("BEVEL_TEST_DIAG_DIR") ?? Path.GetTempPath(), "bevel-2nn2-stray.log")` (best-effort try/catch).
  - [ ] `ci.yml` job `build-test`: add `env: BEVEL_TEST_DIAG_DIR: ${{ runner.temp }}` on the Test step, then a step `if: always()`, `shell: bash`, `run: cat "$RUNNER_TEMP/bevel-2nn2-stray.log" 2>/dev/null || echo "no stray dispatcher touches"`.
  - [ ] Rewrite the doc comment of `DispatcherFrameSupport` and the stale "loser of that race" paragraphs in the two `TestAppBuilder.cs` to the mechanism above. Keep `DisableTestParallelization`.
- **Verify:** `dotnet test tests/Bevel.FileManager.Tests/Bevel.FileManager.Tests.csproj --filter "FullyQualifiedName~DispatcherFrameSupport"` → 2 passed; then full `dotnet test Bevel.sln` → 0 failed.
- **Done when:** 10 consecutive CI runs with no `PlatformNotSupportedException` on ubuntu+windows (a run that prints stray-log lines AND is green is direct proof); then `bd close bevel-2nn2 --reason "root-scope controlled IDispatcherImpl; stray touch in the Reset..bind window can no longer build a NullDispatcherImpl dispatcher"`.

### bevel-i145 — Luna render tests flake on shared headless Application state
- **Meta:** P3 · bug · size M · AGENT
- **State today:** The bead's premise is inverted: the Application is NOT shared — it is rebuilt per test (see cluster summary) — while the theme engines keep PROCESS statics that assume one Application forever: `ThemeService._applied/_appliedStyles/_appliedId` (`src/Bevel.UI/ThemeService.cs:53-55`, no-op guard `if (theme == _appliedId) return true;` at :90), `ColorSchemeService._applied/_appliedId/_aliasesMerged` (`src/Bevel.UI/ColorSchemeService.cs:46-50`, guard :80), `Blue2001VariantService._appliedColor/_appliedGloss/_injected` (`src/Bevel.UI/Blue2001/Blue2001Variant.cs:175-178`, guard :199). Any test that exits without its `finally` reset leaves statics saying "applied" against a fresh App, so the next `Apply` is a silent no-op and the render is the wrong skin. Separately `RenderFileMenuBarTest.Render_luna_start_menu_under_purple_variant` (`tests/Bevel.Taskbar.Tests/RenderFileMenuBarTest.cs:108-160`) settles with `await Task.Delay(10)` loops (wall-clock, and `async` makes it a 2nn2 victim). UNVERIFIED which of the two produces the 1/12 failure.
- **Depends on:** bevel-2nn2 (land first so remaining reds are attributable)
- **Files:** `src/Bevel.UI/ThemeService.cs`, `src/Bevel.UI/ColorSchemeService.cs`, `src/Bevel.UI/Blue2001/Blue2001Variant.cs`, `tests/Bevel.Taskbar.Tests/ThemeStaticsFreshAppTests.cs` (new), `tests/Bevel.Taskbar.Tests/RenderFileMenuBarTest.cs`, `tests/Shared/SiteShot.cs`.
- **Decision:** Make "already applied" mean "this Application actually carries it" (membership check), not "a static says so". No reset hooks, no fixtures: production has one Application so behaviour there is unchanged. `ThemeService.Apply`: move the `Application.Current` check above the guard; guard becomes `theme == _appliedId && (_appliedStyles is null || app.Styles.Contains(_appliedStyles))`. `ColorSchemeService.Apply`: guard also requires `appResources.MergedDictionaries.Contains(_applied)`; replace `bool _aliasesMerged` with `ResourceInclude? _aliases` checked by `Contains`. `Blue2001VariantService.Apply`: guard also requires `res.ContainsKey(_injected[0])`. Expose `public static WriteableBitmap CaptureSettled(Window window)` on `SiteShot` (extract from the existing private `WaitForStableFrame`) and use it in the render test.
- **Test first:** `ThemeStaticsFreshAppTests` (new, `[Collection("TaskbarTheme")]`), `[AvaloniaFact] Apply_reapplies_when_the_application_lost_the_theme`: `ThemeService.Apply(ThemeIds.Blue2001)`; `var n = app.Styles.Count`; `app.Styles.RemoveAt(n - 1)`; `ThemeService.Apply(ThemeIds.Blue2001)`; assert `app.Styles.Count == n`; `finally` applies `ThemeIds.Industrial1999`. Red today. Add the analogous test per engine (scheme: remove `_applied` dict from `MergedDictionaries`; variant: `res.Remove("<first Surfaces key>")`).
- **Steps:**
  - [ ] Write the three tests; confirm all three fail.
  - [ ] Apply the three guard changes; tests pass.
  - [ ] In `RenderFileMenuBarTest`, replace each `for (...) { RunJobs(); await Task.Delay(10); frame = CaptureRenderedFrame(); }` capture loop with `SiteShot.CaptureSettled(window)`; where no other `await` remains, make the test `void`.
  - [ ] Run `Bevel.Taskbar.Tests` 12 times in a loop; record failures.
- **Verify:** `for i in $(seq 12); do dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --no-build -v q || break; done` → 12 green runs.
- **Done when:** 12/12 local runs green and the three new tests pass; then `bd close bevel-i145 --reason "theme engines verify membership in the current Application; render test settles on frames, not sleeps"`.

### bevel-tv19 — RenderFlatAndPastelComparisonTest dirties 5 checked-in PNGs on every run
- **Meta:** P3 · bug · size S · AGENT
- **State today:** PARTLY DONE. Churn (decision 1) fixed by 35ea1c0 / PR #8: `SaveCrop` goes through `SiteShot.Save` (settle loop) and `RenderTaskbar` ticks `AvaloniaHeadlessPlatform.ForceRenderTimerTick()` past the 250 ms width transition (`RenderFlatAndPastelComparisonTest.cs:57-63, 240-252`); commit message reports three byte-identical runs. Decision 2 is NOT done: `OutDir` still defaults to the tracked `docs/design/flat/renders` (:33-37), so every `dotnet test` on Linux/Windows (different glyph rasteriser) or after any real change rewrites tracked files. One save path still bypasses the settle: `frame!.Save(Path.Combine(OutDir, fileName))` at :217.
- **Depends on:** none
- **Files:** `tests/Bevel.Taskbar.Tests/RenderFlatAndPastelComparisonTest.cs`, `docs/design/flat/look-delta.md`.
- **Decision:** Tests never write tracked files by default (same rule as every other `Render*` test). `OutDir` precedence: `BEVEL_FLATPASTEL_DIR` (site harvest, 2x — unchanged) → else if `BEVEL_FLATPASTEL_DOCS=1` the docs dir at 1x → else `Path.Combine(Path.GetTempPath(), "bevel-flatpastel")` at 1x (create the directory). `ForSite` stays keyed on `BEVEL_FLATPASTEL_DIR` only. Route the :217 save through `SaveCrop(window, fileName)`.
- **Test first:** non-code check — before the change `dotnet test ... --filter RenderFlatAndPastelComparisonTest` touches mtime of `docs/design/flat/renders/*.png`; after, `git status --porcelain docs/design/flat/renders` is empty and `stat -f %m` of those files is unchanged.
- **Steps:**
  - [ ] Change `OutDir` as decided; `Directory.CreateDirectory(OutDir)` once.
  - [ ] Replace the direct `frame!.Save` at :217 with `SaveCrop`.
  - [ ] Add to `docs/design/flat/look-delta.md` one line: regenerate with `BEVEL_FLATPASTEL_DOCS=1 dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~RenderFlatAndPastelComparisonTest"` (on macOS).
  - [ ] Run with `BEVEL_FLATPASTEL_DOCS=1` twice on macOS; confirm `git status` stays clean (proves 35ea1c0's determinism).
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~RenderFlatAndPastelComparisonTest" && git status --porcelain docs/design/flat/renders` → no output.
- **Done when:** a plain test run leaves the tree clean and the opt-in run is byte-stable twice; then `bd close bevel-tv19 --reason "renders settled (35ea1c0) and docs writes are opt-in via BEVEL_FLATPASTEL_DOCS"`.

### bevel-umgr — Stream-resubscribe test seams for MacOSWindowManager + MacOSSystemTrayHost
- **Meta:** P2 · task · size M · AGENT
- **State today:** PARTLY DONE. The clock half of the seam exists: both classes have an `internal` ctor taking `IClock clock, TimeSpan streamRetryInterval` (`src/Bevel.Pal.MacOS/MacOSWindowManager.cs:45`, `MacOSSystemTrayHost.cs:46`) and the loops call `_clock.Delay`. The client half does not: `StreamLoopAsync` builds a concrete gRPC client from `_helperLifecycle.Client.GetChannel()` (`MacOSWindowManager.cs:208-270`, `MacOSSystemTrayHost.cs:143-180`), and no test uses the internal ctors (only `LiveMacOSWindowManagerTests`). `_knownWindowIds` is written at :225/:232/:237 and never read. Ghost reconciliation is missing on BOTH: a `Snapshot` after resubscribe only upserts (contradicts north star 2), and the proto has no end-of-snapshot marker (`proto/bevel.helper.v1.proto:129-138, 210-223`).
- **Depends on:** none
- **Files:** `src/Bevel.Pal.MacOS/MacOSWindowManager.cs`, `src/Bevel.Pal.MacOS/MacOSSystemTrayHost.cs`, `tests/Bevel.Pal.MacOS.Tests/ManualClock.cs` (new — move the private nested `ManualClock` out of `HelperLifecycleTests.cs:51`), `tests/Bevel.Pal.MacOS.Tests/MacOSStreamLoopTests.cs` (new).
- **Decision:** Add one optional trailing parameter to each internal ctor: `Func<CancellationToken, IAsyncEnumerable<WindowChange>>? changesSource = null` (tray: `IAsyncEnumerable<TrayChange>`). Default = a private `async IAsyncEnumerable<...> GrpcChanges([EnumeratorCancellation] CancellationToken ct)` containing today's `GetWindowClient()` + `ws.Changes(...)` + `ReadAllAsync`. `StreamLoopAsync` does `await foreach (var change in _changesSource(ct))`. A delegate, not an interface: one call site, and `HelperLifecycle` stays the only owner of the channel. Delete `_knownWindowIds`. Ghost reconciliation is a wire-contract change (helper + proto + both hosts) — out of scope here, filed as a new bead (see end of file).
- **Test first:** `MacOSStreamLoopTests` with `ManualClock` and `new HelperLifecycle(NullLogger<HelperLifecycle>.Instance)` (never started): `Unavailable_delays_one_retry_interval_then_resubscribes` (source throws `RpcException(StatusCode.Unavailable)` once; assert `clock.Requested == [interval]`, then second subscription happens after `Tick`), `Generic_exception_delays_then_resubscribes`, `Cancellation_exits_without_a_delay`, `[Theory] Each_window_change_kind_raises_its_event` (Snapshot→`WindowChanged`, Opened→`WindowOpened`, Closed→`WindowClosed`, Focused with `IsFocused`→`WindowChanged`+`ForegroundChanged`, TitleChanged/Moved/Minimized/Deminimized→`WindowChanged`), and the tray equivalents (Snapshot/Added→`ItemAdded`, Updated→`ItemUpdated`, Removed→`ItemRemoved`).
- **Steps:**
  - [ ] Move `ManualClock` to its own internal file; `HelperLifecycleTests` still green.
  - [ ] Write the tests against the new ctor parameter (compile error = red).
  - [ ] Add the parameter + `GrpcChanges` in both classes; start the loop with the existing `StartPollAsync`.
  - [ ] Delete `_knownWindowIds` and its three writes.
- **Verify:** `dotnet test tests/Bevel.Pal.MacOS.Tests/Bevel.Pal.MacOS.Tests.csproj --filter "FullyQualifiedName~MacOSStreamLoopTests"` → all pass, no test uses `Task.Delay`.
- **Done when:** both loops have fake-driven tests for retry, cancel and every change kind; then `bd close bevel-umgr --reason "changesSource seam + ManualClock tests; _knownWindowIds removed; snapshot reconcile split to its own bead"`.

### bevel-6tpi — Nothing asserts popup PLACEMENT
- **Meta:** P2 · task · size M · AGENT
- **State today:** True. `StartMenu.axaml:16-18` declares `<Popup x:Name="MenuPopup" Placement="Top">`; `StartMenu.OpenAsync` sets `MenuPopup.PlacementTarget = placementTarget` (`StartMenu.axaml.cs:108`). The only guard is the proxy `StartButtonCapTests.Start_button_is_never_taller_than_the_bar`. `TaskbarView.TraceMenuPlacement()` (`TaskbarView.axaml.cs:446`, called at :634) still logs instead of asserting. (The bead says `RenderLunaStartMenuTest`; the file is now `RenderBlue2001StartMenuTest.cs`.)
- **Depends on:** none
- **Files:** `tests/Bevel.Taskbar.Tests/StartMenuPlacementTests.cs` (new), `src/Bevel.Taskbar/TaskbarView.axaml.cs`.
- **Decision:** With `Placement="Top"` the popup's bottom edge IS the placement target's top edge, so the property to assert is the anchor rectangle, which lays out correctly headlessly: the Start button's top edge must coincide with the bar's top edge (within the skin's button margin). Add `internal Rect StartMenuAnchorInWindow()` to `TaskbarView` returning `new Rect(StartButton.TranslatePoint(default, _window!)!.Value, StartButton.Bounds.Size)` and have `ToggleStartMenu` keep passing `StartButton`. Assert `0 <= anchor.Y <= 2` and `anchor.Bottom <= window.Height` for every row count up to `StartMaxRows` (3). If, and only if, the first step shows the real `PopupRoot` has non-zero bounds headlessly, ALSO assert `host.PointToScreen(default).Y + host.Bounds.Height == window.PointToScreen(default).Y`.
- **Test first:** `StartMenuPlacementTests` (`[Collection("TaskbarTheme")]`): `[AvaloniaTheory] [InlineData(1)] [InlineData(2)] [InlineData(3)] Start_menu_anchor_is_flush_with_the_bar_top(int rows)` and `[AvaloniaFact] Anchor_stays_flush_across_row_changes` doing `window.SetRows(1)`, `SetRows(3)`, `SetRows(2)` with `Dispatcher.UIThread.RunJobs()` after each and the same assertion after each. Prove it bites: temporarily change `StartButtonMaxHeight()` back to `TaskbarTheme.HeightForRows(StartMaxRows)` → the rows=2 case must fail.
- **Steps:**
  - [ ] Spike in the new test: open the menu through `view` (raise `Button.ClickEvent` on the control named `StartButton`), read `MenuPopupControl.Host as PopupRoot`; note whether `Bounds.Height > 0`.
  - [ ] Add `StartMenuAnchorInWindow()`; write the theory + row-change test.
  - [ ] Do the temporary-regression check above and revert it.
  - [ ] Delete `TraceMenuPlacement()` and its call at `TaskbarView.axaml.cs:634` (keep `EnterMenuScope()`).
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~StartMenuPlacementTests"` → pass; `grep -rn TraceMenuPlacement src` → no output.
- **Done when:** the test fails under the reverted kclq fix and passes on main, covering 1→3→2; then `bd close bevel-6tpi --reason "anchor-flush test across row changes; TraceMenuPlacement deleted"`.

### bevel-hr5k — Debug traces are lost when the shell is launched the way users launch it
- **Meta:** P2 · task · size M · AGENT+HUMAN-VERIFY
- **State today:** PARTLY DONE. .NET side: 50b9e80 mirrors `TaskbarLog.Debug` to `~/.config/bevel/logs/taskbar-<role>-<pid>.log` when `BEVEL_DEBUG_TASKBAR=1` (`src/Bevel.Taskbar/Model/TaskbarLog.cs:53-83`). Gaps: (a) no rotation or pruning; (b) `WriteToFile` is a synchronous `File.AppendAllText` under a lock on the CALLER's thread, usually the UI thread (north star 1); (c) helper side: `HelperProcessHost.cs:111-120` forwards only lines starting `[BEVEL-WIN]` to `HelperDiagnostics.Raise` (persisted to `$TMPDIR/bevel-restart.log` by `Program.cs:157`); `[BEVEL-TRAY]` lines (`TrayServiceImpl.swift:59-67`, including the always-on self-test `log()`) go only to `_logger`, i.e. nowhere on a Finder launch; (d) nothing documents how a Finder launch gets the env var.
- **Depends on:** none
- **Files:** `src/Bevel.Taskbar/Model/TaskbarLog.cs`, `src/Bevel.Pal.MacOS/HelperProcessHost.cs`, `src/Bevel.App/RestartDiag.cs`, `tests/Bevel.Taskbar.Tests/TaskbarLogFileTests.cs` (new), `CLAUDE.md` (Diagnosing a crash section).
- **Decision:** Files, not `os_log` (works identically for .NET and Swift output, greppable, no entitlement questions). The core already drains helper stderr, so the helper needs no code: widen the mirror to every line starting `"[BEVEL-"`. `TaskbarLog`: replace the lock+append with an unbounded `System.Threading.Channels.Channel<string>` drained by one background task (writer never blocks a caller); on open delete `*.log` in the logs dir older than 7 days; when the current file exceeds 8 MB rename it to `<name>.1` (overwrite) and continue. `RestartDiag.Log`: if `bevel-restart.log` exceeds 8 MB at first use, rename to `bevel-restart.log.1`. Stay off by default (env-gated) except the helper's always-on lines, which are low volume.
- **Test first:** `TaskbarLogFileTests`: extract the writer as `internal sealed class DebugLogFile(string path, long maxBytes)` inside `TaskbarLog.cs` with `Enqueue(string)` and `Task FlushAsync()`; tests `Rolls_to_dot1_when_over_the_cap` (cap 1 KB, write 3 KB, assert both files exist and current < cap) and `Prunes_logs_older_than_seven_days` (set `File.SetLastWriteTimeUtc`). For the helper: unit-test a new `internal static bool HelperProcessHost.ShouldMirror(string line)` → true for `[BEVEL-TRAY] x` and `[BEVEL-WIN] x`, false for `hello`.
- **Steps:**
  - [ ] Write the three tests (red), then implement `DebugLogFile`, pruning, and `ShouldMirror`.
  - [ ] Add `RestartDiag` size roll.
  - [ ] Document in `CLAUDE.md`: `launchctl setenv BEVEL_DEBUG_TASKBAR 1; launchctl setenv BEVEL_DEBUG_TRAY 1`, relaunch from Finder, read `~/.config/bevel/logs/` and `grep helper "$TMPDIR/bevel-restart.log"`; `launchctl unsetenv` afterwards.
  - [ ] HUMAN: launch `/Applications/Bevel.app` from Finder with the vars set; confirm the `[BEVEL-TRAY]` self-test verdict appears. UNVERIFIED that `launchctl setenv` reaches a LaunchServices launch on this macOS version.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~TaskbarLogFileTests"` → pass.
- **Done when:** a Finder launch yields a readable trace containing `[BEVEL-TRAY]` lines, files roll at 8 MB; then `bd close bevel-hr5k --reason "helper [BEVEL-*] lines mirrored, debug file is async + rotated, Finder recipe documented"`.

### bevel-hs1j — Spawner tests need an AX-granted host to actually run
- **Meta:** P3 · task · size M · AGENT+HUMAN-VERIFY
- **State today:** True. `WindowServiceSpawnerTests.requireWindowAccessOrSkip` (`native/helper-macos/Tests/BevelHelperTests/WindowServiceSpawnerTests.swift:24-58`) skips whenever the `swift test` runner cannot really use AX, which is always. CI never runs `swift test` at all (job `swift` is `swift build` only). A host that DOES hold the grant already exists: the dev-signed helper binary (`packaging/macos/dev-sign.sh` signs `native/helper-macos/.build/debug/BevelHelper` as `pl.ikari.bevel.helper`), and `LiveMacOSWindowManagerTests.Real_helper_roundtrip_backed_by_spawner` (`tests/Bevel.Pal.MacOS.Tests/LiveMacOSWindowManagerTests.cs:53`, opt-in `BEVEL_LIVE_HELPER_TESTS=1`) already drives it over gRPC with the WindowSpawner rig — but treats "window not found" as a soft skip (:98-104).
- **Depends on:** none (bevel-79r depends on this)
- **Files:** `tests/Bevel.Pal.MacOS.Tests/LiveMacOSWindowManagerTests.cs`, `native/helper-macos/Tests/BevelHelperTests/WindowServiceSpawnerTests.swift` (skip text only), `CLAUDE.md`.
- **Decision:** Take the bead's second option: the AX-granted host is the real helper binary, driven from C#. Signing the xctest bundle does not help — the process that runs it is Apple's `xctest`. Add three live tests mirroring the Swift ones and make them STRICT under a second flag `BEVEL_LIVE_HELPER_AX=1` (helper is known to be granted → "not found" is a failure, not a skip). CI cannot hold a TCC grant: document it as local-only.
- **Test first:** in `LiveMacOSWindowManagerTests`: `Live_enumerate_finds_spawned_window`, `Live_rename_is_reflected`, `Live_close_removes_window` — each returns early unless both flags are `1`, launches `HelperLifecycle` + `SpawnerProcess.TryLaunch`, and asserts with the existing `PollEnumerateAsync`. `SpawnerProcess` needs `Rename(key, title)` and `Close(key)` (rig protocol: `rename <key> <title>` → `ok rename <key>`, `close <key>` → `ok close <key>`, see `tests/rigs/macos-lite/WindowSpawner/Sources/WindowSpawner/main.swift:14-19`).
- **Steps:**
  - [ ] Add `Rename`/`Close` to the nested `SpawnerProcess`; add the three tests.
  - [ ] Update the Swift `XCTSkip` text to point at the C# live tests and the two flags.
  - [ ] Add to `CLAUDE.md` Build & Test: `swift build --package-path native/helper-macos && swift build --package-path tests/rigs/macos-lite/WindowSpawner && ./packaging/macos/dev-sign.sh && BEVEL_LIVE_HELPER_TESTS=1 BEVEL_LIVE_HELPER_AX=1 dotnet test tests/Bevel.Pal.MacOS.Tests/Bevel.Pal.MacOS.Tests.csproj --filter "FullyQualifiedName~LiveMacOSWindowManagerTests"`, and the sentence "CI cannot run these: no TCC grant on hosted runners."
  - [ ] HUMAN: grant Accessibility to the dev-signed helper once, run the command, paste the result into the bead.
- **Verify:** the command above on the owner's Mac → 3 live tests executed and passed (test output shows them as run, not early-returned: each writes `_output.WriteLine("live AX leg executed")`).
- **Done when:** the three scenarios execute in the documented local setup and CI's inability is written down; then `bd close bevel-hs1j --reason "live spawner scenarios run against the dev-signed helper; documented as local-only"`.

### bevel-79r — Harden flaky WindowSpawner close enumeration test
- **Meta:** P2 · bug · size S · AGENT+HUMAN-VERIFY
- **State today:** Cannot be reproduced today: `testCloseRemovesWindowFromEnumeration` (`WindowServiceSpawnerTests.swift:136-156`) always skips (bevel-hs1j). The lingering entry is designed-for behaviour, not a rig bug: a closed window's CG tombstone keeps its Space and lingers 10 s+ (CODEMAP, `OffscreenWindowTracker`), and the helper's tombstone counter advances once per RECONCILE pass, while the test polls `svc.enumerateWindows()` in-process with no reconcile loop running — so a 12 s wall-clock wait measures nothing the product relies on.
- **Depends on:** bevel-hs1j
- **Files:** `tests/Bevel.Pal.MacOS.Tests/LiveMacOSWindowManagerTests.cs`, `native/helper-macos/Tests/BevelHelperTests/WindowServiceSpawnerTests.swift`.
- **Decision:** Assert what the shell consumes: the `Closed` delta on the helper's Changes stream (push, north star 2), not absence from a polled list. In the C# live test from hs1j, subscribe `wm.WindowClosed` before `spawner.Close("a")`, call `wm.StartPollAsync()`, and await a `TaskCompletionSource` completed when the event carries the spawned id (timeout 15 s, no sleeps). Do NOT change helper policy to "reject lingering closed AX entries": that would drop minimized/hidden windows (the bead's own caveat, and `WindowPolicyTests` pins it). Delete the Swift close test rather than keep a permanently skipped duplicate.
- **Test first:** `Live_close_removes_window` (from hs1j) rewritten as above; it must also assert a follow-up `EnumerateAsync()` no longer contains the id AFTER the event arrived.
- **Steps:**
  - [ ] Implement the event-based assertion in `Live_close_removes_window`.
  - [ ] Remove `testCloseRemovesWindowFromEnumeration` from the Swift file; leave a one-line comment naming the C# test.
  - [ ] HUMAN: run the hs1j command 10 times in a loop; record pass count.
- **Verify:** `for i in $(seq 10); do BEVEL_LIVE_HELPER_TESTS=1 BEVEL_LIVE_HELPER_AX=1 dotnet test tests/Bevel.Pal.MacOS.Tests/Bevel.Pal.MacOS.Tests.csproj --no-build --filter "FullyQualifiedName~Live_close_removes_window" || break; done` → 10 passes.
- **Done when:** 10/10 on the granted machine; if it still flakes, the failure is a real helper bug (missing Closed delta) — reopen with the `BEVEL_DEBUG_WINDOWS=1` log; else `bd close bevel-79r --reason "close asserted via the Closed delta against the granted helper; polled 12s wait removed"`.

### bevel-2tlp — MacOSTabProvider: seam + tests for the osascript runner
- **Meta:** P3 · task · size S · AGENT
- **State today:** True. `RunOsaScriptAsync` is `private static` (`src/Bevel.Pal.MacOS/MacOSTabProvider.cs:272-315`) with the 4 s `Timeout` (:29), `Kill(entireProcessTree: true)` (:307) and non-zero exit → `""` (:295) all untested. `Activate_refuses_invalid_refs` (`tests/Bevel.Pal.MacOS.Tests/MacOSTabProviderTests.cs:157`) infers "no spawn" from elapsed < 500 ms and is vacuous off macOS (the runner returns `""` immediately there). `InternalsVisibleTo Bevel.Pal.MacOS.Tests` already exists.
- **Depends on:** none
- **Files:** `src/Bevel.Pal.MacOS/MacOSTabProvider.cs`, `tests/Bevel.Pal.MacOS.Tests/MacOSTabProviderTests.cs`.
- **Decision:** Two seams, both minimal. (1) Generalise the runner: `internal static Task<string> RunProcessAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)` holding today's body; `RunOsaScriptAsync(script, ct)` becomes `RunProcessAsync("/usr/bin/osascript", ["-e", script], Timeout, ct)` behind the existing `IsMacOS` guard. (2) Instance seam: `internal Func<string, CancellationToken, Task<string>> ScriptRunner { get; init; } = RunOsaScriptAsync;` used at the two call sites (:59, :83). A delegate rather than `virtual`: the class stays sealed-shaped and tests need no subclass.
- **Test first:** in `MacOSTabProviderTests`: `Runner_times_out_and_reaps_the_child` (`RunProcessAsync("/bin/sleep", ["10"], TimeSpan.FromMilliseconds(300), default)` returns `""` in < 3 s and no `sleep 10` child of this process remains — check via the `Process` handle captured through an `internal static Action<Process>? OnStartedForTests` hook, assert `HasExited`); `Runner_returns_empty_on_non_zero_exit` (`/bin/sh`, `["-c", "echo out; exit 3"]` → `""`); `Runner_returns_stdout_on_success` (`/bin/echo`, `["hi"]` → `"hi\n"`); rewrite `Activate_refuses_invalid_refs` to construct the provider with a recording `ScriptRunner` and assert it was never called (drop the stopwatch). Guard the three process tests with `if (OperatingSystem.IsWindows()) return;`.
- **Steps:**
  - [ ] Write the four tests (compile error = red).
  - [ ] Extract `RunProcessAsync`, add `ScriptRunner`, route both call sites.
  - [ ] Confirm the pinning test for batched `every tab of window wi` reads still passes.
- **Verify:** `dotnet test tests/Bevel.Pal.MacOS.Tests/Bevel.Pal.MacOS.Tests.csproj --filter "FullyQualifiedName~MacOSTabProviderTests"` → all pass.
- **Done when:** timeout, kill and non-zero-exit each have a test that fails when the behaviour is removed; then `bd close bevel-2tlp --reason "RunProcessAsync + ScriptRunner seams with timeout/kill/exit tests"`.

### bevel-adbo — Landing-page shots are all 1x — harvest them at 2x for HiDPI
- **Meta:** P2 · task · size M · AGENT
- **State today:** PARTLY DONE — one shot left, and PR #8 did not touch it. `SiteShot.Scale = 2.0` (`tests/Shared/SiteShot.cs:34`) is used by every `SiteShot.Save` caller except `RenderOnboardingTest.Render_appearance_tab_to_png`, which passes `scale: 1` (`tests/Bevel.Taskbar.Tests/RenderOnboardingTest.cs:98`). The file was renamed since the bead: it is `theme-industrial1999.png` (not `theme-win2000.png`), the single entry of `MANUAL_1X` in `site/check-shot-sizes.py:24-26`. The same mis-scale would hit any other TabControl window (`SettingsWindow`, `FolderOptionsWindow`, `PropertiesDialog`, `ThemePreviewWindow`).
- **Depends on:** none
- **Files:** `tests/Bevel.Taskbar.Tests/RenderOnboardingTest.cs`, `tests/Shared/SiteShot.cs`, `site/check-shot-sizes.py`, `site/shots/theme-industrial1999.png`, `site/index.html` (only if the logical size changes); possibly `third_party/classic-avalonia/Classic.Avalonia.Theme/Styles/TabControl.axaml` or `src/Bevel.Themes.Industrial1999/Industrial1999Theme.axaml`.
- **Decision:** Option 1 first (find the layer), time-boxed by a fixed bisect; fall back to rendering in two passes, never to dropping the tab strip (the shot must show the real dialog — "keep the copy honest"). Bisect with a temporary test that renders at 2x and checks the top-left quadrant differs from the full frame: (a) the `TabControl` alone in a bare `Window`; (b) with the selected tab's `ScrollViewer` replaced by its `StackPanel`; (c) with `Opacity`/`OpacityMask`/`Effect`/`BoxShadow` setters removed one at a time from the TabControl/TabItem templates. If a single template setter is the cause and removing it leaves the 1x render byte-identical, remove it. Otherwise there is no in-repo fix: keep `scale: 1`, save a minimal repro, and leave the bead open for an upstream Avalonia report (HUMAN files it). Never upscale the 1x render — that is the smear the 2x work exists to remove.
- **Test first:** in `RenderOnboardingTest` add an assertion after saving at 2x: the saved PNG is exactly `940x1000` AND the pixel at (2*x, 2*y) of a known client-area control (the `CrispBevelsCheck` box centre, via `TranslatePoint`) is not the window background colour — red today at `scale: 2` (content magnified and clipped).
- **Steps:**
  - [ ] Add the assertion and switch to `SiteShot.Save(win, outPath)` — confirm red.
  - [ ] Run bisect (a)→(b)→(c); write the finding into the bead as a comment.
  - [ ] If fixed: remove the entry from `MANUAL_1X`, update the `SiteShot.Save` doc comment, run `./site/harvest-shots.sh`, commit the new 2x PNG.
  - [ ] If not fixable in-repo: revert to `scale: 1`, keep the bisect notes, add a minimal repro file under `tests/compat/` and stop.
- **Verify:** `./site/harvest-shots.sh --check` → exit 0 with no "(skipped, still 1x" line.
- **Done when:** `MANUAL_1X` is empty and the drift check passes; then `bd close bevel-adbo --reason "last 1x shot now rendered at SiteShot.Scale"`.

### bevel-lgpn — Prove why hero-blue2001.png is not byte-reproducible across machines
- **Meta:** P3 · task · size S · AGENT+HUMAN-VERIFY
- **State today:** Unproven, as the bead says. `site/harvest-shots.sh:111` still lists `NONDETERMINISTIC="stack-grid.png hero-blue2001.png"`. The parts are produced into `$TMP/hero` (:115-118) and `trap 'rm -rf "$TMP"' EXIT` (:24) deletes them, so CI keeps no evidence. Job `site-shots-drift` (`.github/workflows/ci.yml`) uploads nothing. Since PR #8 the three part renders settle through `SiteShot.Save`, so one plausible cause (unsettled part) may already be gone — nobody has re-checked.
- **Depends on:** none
- **Files:** `site/harvest-shots.sh`, `.github/workflows/ci.yml`.
- **Decision:** Smallest evidence path: when `BEVEL_HARVEST_KEEP` names a directory, the script copies `$OUT/hero-blue2001.png` and `$HERO_PARTS/*.png` there before exit; CI sets it to `${{ runner.temp }}/hero-evidence` and uploads with `actions/upload-artifact@v4`, `if: always()`, `retention-days: 3` (four small PNGs; the org's 2 GB artifact quota is why other uploads are tag-only — this stays under 1 MB). First experiment before anything else: remove `hero-blue2001.png` from `NONDETERMINISTIC` on a branch and see whether CI is already green after PR #8.
- **Test first:** the check is the CI result itself: branch with the exemption removed → `site-shots-drift` green means done; red means download the artifact.
- **Steps:**
  - [ ] Add the `BEVEL_HARVEST_KEEP` copy (before the audit section) and the CI env + upload step.
  - [ ] Push a branch with `hero-blue2001.png` removed from `NONDETERMINISTIC`.
  - [ ] If green on two consecutive runs: keep the removal, delete the "SUSPECTED" comment block, close.
  - [ ] If red: `gh run download <run-id> -n hero-evidence`, run `./site/harvest-shots.sh` locally with `BEVEL_HARVEST_KEEP=/tmp/local-hero`, and `cmp` each part (`start.png`, `window.png`, `taskbar.png`) — report which parts differ and whether differing pixels are confined to glyph bounding boxes. HUMAN judges "fixable" vs "rasteriser".
- **Verify:** `gh run view <run-id> --json jobs -q '.jobs[] | select(.name|startswith("Landing page")) | .conclusion'` → `success` twice with the exemption removed, or an attached per-part diff report.
- **Done when:** the exemption is either removed (enforced again) or justified by a recorded per-part diff; then `bd close bevel-lgpn --reason "<removed: reproducible since SiteShot settle | proven: differs only in glyph interiors of part X>"`.

### bevel-zhmr — Perf benchmark harness for frequent taskbar/shell operations
- **Meta:** P2 · task · size S · AGENT
- **State today:** LIKELY DONE — commit 442b6f8 delivered `benchmarks/Bevel.Benchmarks` (`LayoutBenchmarks.cs`, `TrayBenchmarks.cs`, `SettingsBenchmarks.cs`), `tests/Bevel.Taskbar.Tests/PerfBudgetTests.cs` (4 CI-gated budgets: `Perf_grouping_plan_100_windows_under_budget`, `Perf_button_layout_is_flat_under_budget`, `Perf_tray_assign_rows_200x3_under_budget`, `Perf_tray_tint_32px_under_budget`) and `docs/perf.md` with baselines. Two acceptance items were never met and are listed honestly in `docs/perf.md` "Not covered": the `dotnet-trace` profile under real churn (STEP 0), and theme-apply / reflow / IPC measurements. Also the benchmark project is not in `Bevel.sln` and not built by CI, so it can stop compiling unnoticed.
- **Depends on:** none
- **Files:** none (tracker only); follow-ups touch `.github/workflows/ci.yml`, `tests/Bevel.Taskbar.Tests/PerfBudgetTests.cs`, `docs/perf.md`.
- **Decision:** Close as delivered; do not keep a done harness open as a container for unrelated measurements. File the three leftovers as their own beads (below), each with one measurable exit.
- **Test first:** confirmation only — the four budget tests pass.
- **Steps:**
  - [ ] Run the Verify command.
  - [ ] Create follow-up beads: (1) "CI: build benchmarks/Bevel.Benchmarks (Release) so it cannot rot" — one `dotnet build benchmarks/Bevel.Benchmarks -c Release` step in job `build-test` on ubuntu only; (2) "Perf budget for theme apply" — `[AvaloniaFact] Perf_theme_apply_roundtrip_under_budget` timing `ThemeService.Apply(ThemeIds.Blue2001)` + `Blue2001VariantService.Apply("Purple","Hybrid")` + revert, budget set at 20x the measured median; (3) HUMAN "dotnet-trace the live shell under churn and record top 5 frames in docs/perf.md".
  - [ ] Close.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~PerfBudgetTests"` → 4 passed.
- **Done when:** follow-ups exist; then `bd close bevel-zhmr --reason "harness + budgets + docs/perf.md shipped in 442b6f8; profile, theme-apply budget and CI build split into follow-up beads"`.

## New beads to create (found while verifying; no existing bead covers them)

- **CI theme-drift gate diffs a path that no longer exists** — `.github/workflows/ci.yml:187` runs `git diff --exit-code -- src/Bevel.Themes.Win2000/Tokens.axaml ...`; that directory was renamed, and `tools/ThemeGen/Program.cs:14` writes `src/Bevel.Themes.Industrial1999/Tokens.axaml`. A pathspec matching nothing exits 0, so token drift in the default theme is ungated. Fix the path (P1, size S).
- **CI never runs `swift test`** — job `swift` only does `swift build`; `WindowPolicyTests`, `AuthTests`, `TrayServiceTests`, `WindowServiceTests` gate nothing. Add `swift test --package-path native/helper-macos` (spawner tests self-skip) (P2, size S).
- **(Withdrawn by the master plan)** The `SNAPSHOT_END` proto marker is NOT to be built. Snapshot reconcile is done without a proto change: one list call per resubscribe, diffed with `SnapshotReconcile.Absent` (bevel-ri8c, bevel-wheq, bevel-voqo, and the follow-up bead bevel-wheq files for the core window hop).
- **Tests leak background UI-thread posts past their end** — once bevel-2nn2's stray log exists, use it to find and fix the leaking product paths (cancel on window close / dispose) instead of relying on the root dispatcher (P3, size M).
- **Render tests settle with wall-clock sleeps** — 14 `Task.Delay`/`Thread.Sleep` calls across `tests/Bevel.Taskbar.Tests/Render*.cs` plus `SiteShot.WaitForStableFrame` (`Thread.Sleep(15)`); converge on `SiteShot.CaptureSettled` (from bevel-i145) (P3, size M).
