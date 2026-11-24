> Part of the [open-beads master plan](README.md). Read README.md first: it sets the rules, the order, and the rulings that override individual cards.
> Written 2026-11-18 from reading the tree at `51582c0`. Nothing here was built or run. `UNVERIFIED:` marks a claim the researcher could not confirm — check it before relying on it.

## Cluster B: Core loop, process model, IPC and startup

Verified against the tree at `51582c0` (PR #6 `perf/filer-spawn-and-icons` merged as `9e65425`).

**Window/tray state flow today.** The Swift helper is the differ: AX observers plus its own 500 ms CG reconcile emit a `Changes` server-stream (`SNAPSHOT` items first, then deltas; no end-of-snapshot marker — `proto/bevel.helper.v1.proto`). Only `--role=core` consumes it: `MacOSWindowManager.StreamLoopAsync` / `MacOSSystemTrayHost.StreamLoopAsync` → `ShellCoreServer` projection (`_windowById`, `_trayById`, `_installed`) → `BroadcastStamped` deltas, each stamped `(Epoch, Seq)` per client. The core pushes nothing on connect; the taskbar sends `Hello` (`ShellCoreClient.StartSessionAsync`) and gets four snapshots (`BuildSnapshotEvents`); a seq gap or reconnect re-Hellos. **A snapshot is upsert-only at every hop**: `ShellCoreWindowManager.OnCoreEvent` replays it as `WindowOpened`, `ShellCoreSystemTrayHost.OnCoreEvent` as `ItemAdded`, and the core's own helper-stream `Snapshot` kind maps to `WindowChanged`/`ItemAdded`. Removal after an outage is done only by **a poll on top of the stream**: `ShellModel.ReconcileLoopAsync` (2 s, poked by `OnConnectionChanged`) calls `EnumerateWindows`, which makes the core re-run the helper's full `ListWindows` (CG + AX, icons included) and does NOT repair the core's own projection. The tray has no such poll, so tray ghosts are real (bevel-ri8c). Contradicts north star 2.

**Icon pool.** `MmfBgraPool` (append-only, lock-free publish cursor, 512 slots × 96×96 BGRA, `$TMPDIR/bevel-core/icons.pool`) is written by the **taskbar** since `17420db` (`poolIsWriter = role is ShellRole.Taskbar`, `CompositionRoot.cs:152,254`); every other role is a reader. Key = `path|size|lastWriteTicks` (`PooledIconProvider.SourceStamp`). No eviction. Task-button icons bypass it (window PNG). Contradicts north star 3 ("core owns pool writes") — deliberate: the core is headless.

**Filer processes.** `FilerSupervisor` (launcher sibling of `RoleProcessSupervisor`): one supervised `--role=filer` per window, exit code 0 = closed (no respawn), else crash-respawn; ONE hidden `--park` child claimed by a `Show` dial (`ParkedFilerWindowHost.Show`). All opens now go `IFilerSpawner` → `LauncherFilerSpawner` → `LauncherControl.TrySpawnFiler` (Start menu fixed in `d3665fb`). Ctrl+N still builds a second window in-process (`App.axaml.cs` `NewWindowRequested += factory.Create`).

**Drag out of Filer.** `ItemView.StartDragAsync` builds a legacy `DataObject` with the private `FileDropPayload` plus `DataFormats.Files`, the latter only if the press-time `ResolveStorageItemsAsync` task already completed (`79ed2c3`).

**Filer enumeration** runs synchronously on the UI thread: `LocalFsProvider.EnumerateAsync` has no await and all four UI consumers iterate it inline. **Startup:** launcher takes `ShellInstanceLock` → spawns core + taskbar → binds `launcher.sock` (busy-probe happens here, too late) → parks a filer. Taskbar paints from `peer-settings-cache.json`, but `ShellModel.Start()` (app enumeration + reconcile loop) runs BEFORE `taskbarWin.Show()`.

**Target:** snapshots are full reconciles at every hop; the taskbar poll shrinks to a measured backstop and then goes; enumeration leaves the UI thread; "already running" is decided before any child exists; post-paint work is deferred and batched.

## Order

1. bevel-so27 — the largest UI-thread block left in daily use (every folder open, every watcher burst).
2. bevel-ri8c — real, recurring user-visible ghost tray icons; establishes the snapshot-reconcile pattern.
3. bevel-wheq — same pattern for windows; masked today, prerequisite for retiring the poll.
4. bevel-tnii — removes the per-2 s icon re-ship and the core's ghost projection; adds the counter that decides the poll's future.
5. bevel-9790 — one crash currently compounds into a stuck shell with a hidden Dock.
6. bevel-u2x6 — core gesture; code landed, one cross-process hole and live verification remain.
7. bevel-hsvx — fixed for Start menu; one remaining call site decision.
8. bevel-3gad — done; close.
9. bevel-lej1 — done; close.
10. bevel-5no9 — done; close.
11. bevel-ujx8 — O(n²) Start-menu population on the UI thread at boot; tray seed race.
12. bevel-qf95 — move post-paint work after first paint; depends on ujx8's batching.
13. bevel-web5 — pool fills silently within weeks of app updates.
14. bevel-yg7h — tray appears ~5 s late on every boot; needs Swift + real grants.
15. bevel-glnc — latent launcher abort; small.
16. bevel-hprc — an update costs two shell blackouts instead of one.
17. bevel-f4g5 — Hold dialog wording and actions.
18. bevel-mhvu — capability truth duplicated in two proxies.
19. bevel-gww.9 — last AOT reflection bindings (now 5 sites, not 3).
20. bevel-gww.7 — AOT packaging; needs signing infrastructure.
21. bevel-gww — epic; closes when .7 and .9 close.
22. bevel-1nxe — deprecation only.
23. bevel-gw2 — maintainability refactor.
24. bevel-alb — maintainability refactor on a version-skew-sensitive wire format.
25. bevel-1jvr — fallback path only; cosmetic robustness.

## Cards

### bevel-so27 — Filer enumeration still runs ON THE UI THREAD (syscall count fixed; threading blocked on callers)
- **Meta:** P2 · bug · size L · AGENT
- **State today:** PARTLY DONE — stat count fixed (`779d240`, corrected by `e70c99d`: one `EnumerateFileSystemInfos` pass + `File.GetAttributes`). Threading NOT done: `src/Bevel.Core/Vfs/LocalFsProvider.cs:35-64` has no await; UI-thread consumers are `FileManagerWindow.axaml.cs:606` (`OnTreeNodeExpanded`), `:862` (`LoadDirectory`), `:1428` (`ReloadDifferential`) and `Components/FolderPickerDialog.axaml.cs:104`. `ItemViewModel.Update` (`Components/ItemViewModel.cs:30-40`) raises 6 notifications unconditionally. A previous attempt was reverted because tests pump with `Dispatcher.UIThread.RunJobs()` and a pool walker swallowed `DirectoryNotFoundException`.
- **Depends on:** none
- **Files:** `src/Bevel.Core/Vfs/VfsBatchReader.cs` (new); `src/Bevel.FileManager/FileManagerWindow.axaml.cs`; `src/Bevel.FileManager/Components/FolderPickerDialog.axaml.cs`; `src/Bevel.FileManager/Components/ItemViewModel.cs`; `tests/Bevel.Core.Tests/VfsBatchReaderTests.cs` (new); `tests/Bevel.FileManager.Tests/{FolderPickerDialogTests,FileManagerWindowTreeTests,FeatureInteractionTests,EnumerationErrorTest}.cs`
- **Decision:** Do not change `IVfsProvider.EnumerateAsync` (other callers rely on it). Add `public static class VfsBatchReader { public static IAsyncEnumerable<IReadOnlyList<IVfsNode>> ReadBatchesAsync(VfsRoot root, VfsPath folder, EnumerateOptions options, int batchSize, CancellationToken ct) }` (new): a `Task.Run` producer iterates the provider, touches `node.Size` and `node.Modified` on the pool (so the lazy stat is paid there), writes arrays into a bounded `Channel` (capacity 4), and completes the channel WITH the exception (`writer.Complete(ex)`) so "folder is gone" still throws at the consumer. The four UI consumers `await foreach` batches on the UI thread and check `ct.IsCancellationRequested` after every await (stale-chunk guard). Reason: one helper fixes every instance; the provider contract and the error contract stay intact.
- **Test first:** `tests/Bevel.Core.Tests/VfsBatchReaderTests.cs` (new): `Enumeration_runs_off_the_calling_thread` (a recording provider captures `Environment.CurrentManagedThreadId`; assert it differs from the caller's — UNVERIFIED: how tests register a custom provider on `VfsRoot`; read `src/Bevel.Core/Vfs/VfsRoot.cs` first), `A_missing_directory_still_throws_at_the_consumer`, `Cancellation_stops_the_producer`. `ItemViewModel` test: `Update_with_identical_values_raises_nothing`.
- **Steps:**
  - [ ] Write `VfsBatchReader` + its three tests; they pass.
  - [ ] Switch `LoadDirectory` to it (batch 32); delete the inner `Dispatcher.UIThread.InvokeAsync` hop (the loop body is already on the UI thread); keep the catch blocks.
  - [ ] Switch `ReloadDifferential` (collect all batches, then `ItemView.ReconcileItems`).
  - [ ] Extract `internal Task ExpandTreeNodeAsync(TreeViewItem node)` in `FileManagerWindow` and `FolderPickerDialog`; the `async void` handlers call it.
  - [ ] In the three failing test classes replace `RunJobs()`-only waits with a local `PumpUntil(Func<bool> done)` (new) that loops `Dispatcher.UIThread.RunJobs()` + `Task.Wait(5 ms)` on the returned task with a 10 s deadline. No bare sleeps.
  - [ ] `ItemViewModel.Update`: compare old vs new `DisplayName`, `Size`, `Modified`, `IconKey`; raise only what changed (always raise `Node`).
  - [ ] Update the "STILL SYNCHRONOUS" comment at `LocalFsProvider.cs:48-51`.
- **Verify:** `dotnet test tests/Bevel.FileManager.Tests/Bevel.FileManager.Tests.csproj` → 0 failed; `dotnet test tests/Bevel.Core.Tests/Bevel.Core.Tests.csproj --filter "FullyQualifiedName~VfsBatchReader"` → 3 passed
- **Done when:** no `_vfsRoot.EnumerateAsync(` call remains in `FileManagerWindow.axaml.cs` or `FolderPickerDialog.axaml.cs`; then `bd close bevel-so27 --reason "enumeration moved to the pool via VfsBatchReader; all four UI consumers switched"`

### bevel-ri8c — Tray reconnect snapshot only upserts -- ghost icons survive a core restart
- **Master-plan override (read first):** put the helper in `src/Bevel.Pal.Abstractions/SnapshotReconcile.cs` (new), namespace `Bevel.Pal.Abstractions`, as `public static class SnapshotReconcile { public static IReadOnlyList<T> Absent<T>(IReadOnlyDictionary<string,T> known, IEnumerable<T> snapshot, Func<T,string> keyOf) }` — NOT in `src/Bevel.App/ShellCore/`. `Bevel.Pal.MacOS` must be able to call it (bevel-voqo). Everything else in this card stands.
- **Meta:** P2 · bug · size M · AGENT
- **State today:** Confirmed. `src/Bevel.App/ShellCore/ShellCoreSystemTrayHost.cs:53-57` replays `TraySnapshot` as `ItemAdded` only; `TrayViewModel.Upsert` (`src/Bevel.Taskbar/Model/TrayViewModel.cs:221`) never removes. No poll masks it (`GetItemsAsync` is called once, `TrayViewModel.cs:206`).
- **Depends on:** none
- **Files:** `src/Bevel.App/ShellCore/SnapshotReconcile.cs` (new); `src/Bevel.App/ShellCore/ShellCoreSystemTrayHost.cs`; `src/Bevel.App/ShellCore/ShellCoreServer.cs`; `tests/Bevel.Taskbar.Tests/SnapshotReconcileTests.cs` (new); `tests/Bevel.Taskbar.Tests/ShellCoreIntegrationTests.cs`
- **Decision:** Reconcile in the adapter, not the view-model, so the `ISystemTrayHost` contract is unchanged. Add `internal static class SnapshotReconcile { internal static IReadOnlyList<T> Absent<T>(IReadOnlyDictionary<string,T> known, IEnumerable<T> snapshot, Func<T,string> keyOf) }` (new). The host keeps `Dictionary<string,TrayItem> _known` under a lock (updated on Added/Updated/Removed); on `TraySnapshot` it raises `ItemRemoved` for every absent item, then `ItemAdded` for the snapshot. In-flight icon renders need no handling: `TrayViewModel.Remove` unsubscribes and drops the VM, and a late decode writes to an orphan. Guard: a Hello answered while the core is still seeding carries an empty projection and would wipe the tray — so `ShellCoreServer.PushSnapshotTo` must omit `WindowSnapshot`/`TraySnapshot` while `_seedBuffer is not null` (the epoch-bump burst at `ShellCoreServer.cs:166-168` follows and is authoritative).
- **Test first:** `SnapshotReconcileTests.Absent_returns_known_items_missing_from_the_snapshot`; in `ShellCoreIntegrationTests` (model on `Tray_remove_during_the_snapshot_gap_does_not_ghost`, uses `ControllablePal`): `A_resent_tray_snapshot_removes_items_that_vanished_while_the_link_was_down` and `A_Hello_during_seeding_does_not_send_an_empty_tray_snapshot`.
- **Steps:**
  - [ ] Add `SnapshotReconcile.Absent` + unit test.
  - [ ] Add `_known` tracking and the remove-then-add replay in `ShellCoreSystemTrayHost.OnCoreEvent`.
  - [ ] Add the seeding guard in `PushSnapshotTo`; confirm `Seed_completion_bumps_the_epoch_and_repushes_without_a_reconnect` still passes.
  - [ ] Fix the stale comment "TrayViewModel upserts idempotently by id".
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~ShellCoreIntegrationTests|FullyQualifiedName~SnapshotReconcile"` → 0 failed
- **Done when:** a second `TraySnapshot` lacking an item raises `ItemRemoved` for it; then `bd close bevel-ri8c --reason "tray snapshot is a reconciling replace in ShellCoreSystemTrayHost"`

### bevel-wheq — Window-list reconnect snapshot only upserts -- possible stale taskbar buttons after core restart
- **Meta:** P2 · bug · size S · AGENT
- **State today:** Confirmed in code, MASKED in practice. `src/Bevel.App/ShellCore/ShellCoreWindowManager.cs:47-51` replays the snapshot as `WindowOpened`; `ShellModel.ReconcileLoopAsync` (`src/Bevel.Taskbar/Model/ShellModel.cs:168-226`) prunes within 2 s and `OnConnectionChanged` (`:230-236`) pokes it on reconnect. So no user-visible symptom today, but the poll is the only thing hiding it and bevel-tnii's follow-up removes the poll.
- **Depends on:** bevel-ri8c (reuses `SnapshotReconcile.Absent` and the seeding guard)
- **Files:** `src/Bevel.App/ShellCore/ShellCoreWindowManager.cs`; `tests/Bevel.Taskbar.Tests/ShellCoreIntegrationTests.cs`
- **Decision:** Same adapter-level fix: `Dictionary<string,ForeignWindow> _known`; on `WindowSnapshot` raise `WindowClosed` for absent ids, then `WindowOpened` for the snapshot. Do not downgrade/close on the "poll masks it" finding — north star 2 requires the snapshot itself to be the reconcile.
- **Test first:** `ShellCoreIntegrationTests.A_resent_window_snapshot_closes_windows_that_vanished_while_the_link_was_down` (new): seed 2 windows, connect, drop one from the PAL without firing `WindowClosed`, force a `Resync`, assert the adapter raised `WindowClosed` for it.
- **Steps:**
  - [ ] Add `_known` tracking in `OnCoreEvent` (Opened/Changed/Foreground add, Closed removes).
  - [ ] Replace the snapshot branch with remove-then-open; fix the comment.
  - [ ] File a new bead: "Core projection never removes on a helper-stream resubscribe" — `MacOSWindowManager.StreamLoopAsync` maps `Snapshot` to `WindowChanged` (`src/Bevel.Pal.MacOS/MacOSWindowManager.cs:222-228`) and `MacOSSystemTrayHost` maps it to `ItemAdded` (`:158-160`); fix by one `ListWindows`/`ListTrayItems` diff per resubscribe (not periodic).
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~ShellCoreIntegrationTests"` → 0 failed
- **Done when:** the new test passes and the follow-up bead exists; then `bd close bevel-wheq --reason "window snapshot is a reconciling replace; core-side resubscribe gap filed separately"`

### bevel-tnii — Redundant 2s full-ListWindows poll re-ships every window icon on top of the delta stream
- **Meta:** P2 · task · size M · AGENT
- **State today:** PARTLY DONE. `f5abd0e` made deltas omit unchanged icons (`ShellCoreServer.WithoutUnchangedIcon`, `:314-322`) and moved `WorkAreaMitigator` to `EnumerateWindowsProjected`. Still true: `ShellModel.ReconcileLoopAsync` → `CoreCommandKind.EnumerateWindows` → `ShellCoreServer.cs:398-399` returns the helper's live list verbatim, icons base64'd for every window every 2 s, and never repairs `_windowById` (a ghost stays in the core's projection and in every later Hello snapshot). The bead's Option A (`include_icons` in the proto) is NOT implemented (`ListWindowsRequest {}` at `proto/bevel.helper.v1.proto:227`).
- **Depends on:** bevel-wheq
- **Files:** `src/Bevel.App/ShellCore/ShellCoreServer.cs`; `src/Bevel.Taskbar/Model/ShellModel.cs`; `tests/Bevel.Taskbar.Tests/CoreWindowTrafficTests.cs`; `CODEMAP.md`
- **Decision:** Fix it in the core, no proto/Swift change. In the `EnumerateWindows` handler: (1) snapshot `_windowById.Keys` under `_gate` BEFORE awaiting the PAL; (2) after the live list returns, under `_gate` and only if `_seedBuffer is null`, `Forget` every id that was in that pre-await set and is absent from the live list, and upsert the live windows; (3) reply with `WithoutUnchangedIcon(w)` per window (a window never sent still carries its icon — this is the bead's Option B for free); (4) outside the gate `BroadcastStamped(WindowClosed)` for each forgotten id. Using the pre-await key set avoids deleting a window that opened during the enumerate. Keep the 2 s loop for now (it also carries the Dock badge poll, which has no push source) but add `internal int BackstopCorrections` (new) to `ShellModel`, incremented where it already logs `RECONCILE (2s backstop)` (`ShellModel.cs:262-267`). Rejected: an interval bump (weakens the backstop, owner's note).
- **Test first:** `CoreWindowTrafficTests`: `Enumerate_reply_omits_icons_the_client_already_received`, `Enumerate_reply_carries_the_icon_for_a_window_never_sent`, `Enumerate_prunes_a_ghost_from_the_projection_and_broadcasts_Closed`, `Enumerate_does_not_prune_a_window_opened_during_the_call`.
- **Steps:**
  - [ ] Implement the handler change; four tests pass.
  - [ ] Add `BackstopCorrections`; assert it in `tests/Bevel.Taskbar.Tests/ShellModelTests.cs`.
  - [ ] Update the "Two window pulls" paragraph in `CODEMAP.md`.
  - [ ] File two beads: "helper `ListWindows` `include_icons=false` for the helper→core leg" (Swift, AGENT+HUMAN-VERIFY) and "Retire the taskbar 2 s window re-list once `BackstopCorrections` stays 0 for a week of use" (HUMAN decision).
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~CoreWindowTrafficTests"` → 0 failed
- **Done when:** a steady-state enumerate reply contains no `IconPng`; then `bd close bevel-tnii --reason "core strips unchanged icons from the poll reply and reconciles its projection; poll retirement filed"`

### bevel-9790 — Probe the launcher control socket BESIDE the instance lock, before spawning anything
- **Meta:** P2 · bug · size S · AGENT
- **State today:** Confirmed. `Program.RunLauncher` takes the lock at `src/Bevel.App/Program.cs:231`, spawns core + taskbar at `:320`, starts `FilerSupervisor` at `:329`, and only discovers a busy `launcher.sock` at `control.Start()` (`:392-423`), where it must tear its children down again.
- **Depends on:** none
- **Files:** `src/Bevel.App/Supervision/IncumbentProbe.cs` (new); `src/Bevel.App/Program.cs`; `tests/Bevel.Taskbar.Tests/IncumbentProbeTests.cs` (new)
- **Decision:** Keep the lock (it covers the 3 s predecessor-teardown wait and the window before any socket exists) and add a probe right after `LauncherControl.CreateServerEndpoint()` at `:246`, before `QuitRequest.Clear()`: `internal static string? IncumbentProbe.FirstLiveSocket(params string[] socketPaths)` (new) returns the first path for which `UdsSocketClaim.IsServing` is true. Probe BOTH `controlSocket` and `coreSocket` — the 2026-10-12 incident was an orphan core holding `core.sock`. On a hit: log, print "another Bevel is still serving <path>; run packaging/macos/bevel-recover.sh", set `Environment.ExitCode = 3`, return with nothing spawned. Keep the existing `catch (UdsSocketBusyException)` as belt-and-braces.
- **Test first:** `IncumbentProbeTests`: `A_live_listener_is_reported` (bind with `UdsSocketClaim.BindListener` in a temp dir), `A_stale_socket_file_is_not_reported`, `A_missing_path_is_not_reported`.
- **Steps:**
  - [ ] Add `IncumbentProbe` + tests.
  - [ ] Call it in `RunLauncher` before `processes` is built; stand down as above.
  - [ ] Update the comment at `Program.cs:407-411` ("Detecting it this late is itself the design flaw") to say the early probe now exists.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~IncumbentProbe"` → 3 passed
- **Done when:** a launch against a live `launcher.sock` or `core.sock` exits 3 without calling `supervisor.StartAsync`; then `bd close bevel-9790 --reason "incumbent sockets probed before any child is spawned"`

### bevel-u2x6 — Drags out of Filer carry no files — dropping on Finder/Mail/browser does nothing
- **Meta:** P1 · bug · size S · AGENT+HUMAN-VERIFY
- **State today:** PARTLY DONE — `79ed2c3` sets both formats (`src/Bevel.FileManager/Components/ItemView.axaml.cs:803-816`), resolves on press (`:716`), folders via `TryGetFolderFromPathAsync` (`:781`); guarded by `tests/Bevel.FileManager.Tests/ItemViewDragSourceTests.cs` (4 structural tests). Remaining: (a) `OnDrop` (`:827-833`) takes the `FileDropPayload` branch whenever the format is present and never falls back to `DataFormats.Files` if `Get` returns null — exactly what a drag from ANOTHER filer process may deliver; (b) the bead's "path-list string" was not added; (c) nobody has verified a live drop on Finder.
- **Depends on:** none
- **Files:** `src/Bevel.FileManager/Components/ItemView.axaml.cs`; `tests/Bevel.FileManager.Tests/ItemViewDragSourceTests.cs`
- **Decision:** Extract `internal static List<VfsPath>? ResolveDropPaths(IDataObject data)` (new): try the payload, and if that yields null or empty, try `GetFiles()`. No separate path-list string: `DataFormats.Files` already crosses processes and move/copy is decided by key modifiers in `OnDrop`, not by the payload. Reason: one fallback fixes cross-process drops without a third format.
- **Test first:** `ItemViewDragSourceTests.A_drop_whose_internal_payload_did_not_survive_falls_back_to_the_file_list` (new): a `DataObject` with `FileDropPayload.DataFormat` set to a non-payload object plus `DataFormats.Files` → `ResolveDropPaths` returns the file paths.
- **Steps:**
  - [ ] Add `ResolveDropPaths`, call it from `OnDrop`; test passes.
  - [ ] HUMAN: with the dev `.app`, drag one file and one mixed file+folder selection from Filer to Finder, to Mail, and to a second Filer window; record results in the bead.
- **Verify:** `dotnet test tests/Bevel.FileManager.Tests/Bevel.FileManager.Tests.csproj --filter "FullyQualifiedName~ItemViewDragSource"` → 5 passed
- **Done when:** the three live drops work; then `bd close bevel-u2x6 --reason "Files format set (79ed2c3), cross-process drop fallback added, verified live"`

### bevel-hsvx — Start-menu folder opens bypass the parked Filer and spawn unsupervised
- **Meta:** P1 · bug · size S · AGENT
- **State today:** PARTLY DONE — fixed for Start menu in `d3665fb`: `App.OpenFilerAt`/`OpenFilerSearch` → `SpawnFilerSupervised` (`src/Bevel.App/App.axaml.cs:456-505`, `Task.Run` + `IFilerSpawner`); guarded by `tests/Bevel.Taskbar.Tests/StartMenuFilerSpawnTests.cs`. `Program.SpawnFiler` is now referenced only by `ProcessFilerSpawner`. Open item: the bead's "decide" — `StackViewModel.OpenFolderAsync` (`src/Bevel.Taskbar/Model/StackViewModel.cs:125-132`) calls `_appEnv.LaunchAsync(FolderPath)`, which opens Finder.
- **Depends on:** none
- **Files:** `src/Bevel.Taskbar/Model/StackViewModel.cs`; the code that constructs `StackViewModel` (find with `grep -rn "new StackViewModel" src`); `src/Bevel.App/App.axaml.cs`; `tests/Bevel.Taskbar.Tests/StackTests.cs`
- **Decision:** Route it to Filer: a shell replacement that opens Finder for its own "Open folder" is the gap. Add an optional `Action<string>? openFolder` constructor parameter to `StackViewModel`; when set, `OpenFolderAsync` calls it, otherwise keeps `LaunchAsync` (tests, no host). `App` passes `path => SpawnFilerSupervised(services, path)` by the same route that already carries `openFolder` into `TaskbarView.Initialize`.
- **Test first:** `StackTests.Open_folder_uses_the_injected_filer_opener_when_present` (new): construct with a recording delegate, run the open command, assert the delegate got `FolderPath` and `LaunchAsync` was not called.
- **Steps:**
  - [ ] Add the parameter and branch.
  - [ ] Thread the delegate from `App.CreateTaskbarSurfaceCore` to the stack's construction site.
  - [ ] Test passes; `StartMenuFilerSpawnTests` still passes.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~StackTests|FullyQualifiedName~StartMenuFilerSpawn"` → 0 failed
- **Done when:** the stack's "Open folder" opens a supervised Filer; then `bd close bevel-hsvx --reason "Start menu fixed in d3665fb; stack Open folder routed through IFilerSpawner"`

### bevel-3gad — Shared icon pool has no writer — the cross-process cache is inert
- **Meta:** P1 · bug · size S · AGENT
- **State today:** LIKELY DONE — `17420db`: `poolIsWriter = role is ShellRole.Taskbar` at `src/Bevel.App/CompositionRoot.cs:152` and `:254`; `tests/Bevel.Taskbar.Tests/IconPoolWiringTests.cs` pins it (3 tests). Stale-entry stamp added in `32e6688` (bevel-iwju). Capacity concern split out to bevel-web5.
- **Depends on:** none
- **Files:** none
- **Decision:** Close. The bead's "better" option (core pre-renders at idle) is not taken: the core is headless; revisit only under the epic's ownership question (see bevel-gww).
- **Test first:** existing `IconPoolWiringTests`.
- **Steps:**
  - [ ] Run the verify command.
  - [ ] On a running dev shell: `ls -l "$TMPDIR/bevel-core/icons.pool"` shows a file.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~IconPoolWiring"` → 3 passed
- **Done when:** both checks pass; then `bd close bevel-3gad --reason "taskbar is the pool writer since 17420db; wiring tests pin it"`

### bevel-lej1 — [bug] Shell starts unskinned (Win2000) and the theme changes under it mid-session
- **Meta:** P2 · bug · size S · AGENT
- **State today:** LIKELY DONE — `80ed440` / merge `59ee6f3` (titled bevel-7s9n, bevel-lej1). First paint reads `peer-settings-cache.json` (`src/Bevel.Core/SettingsSnapshotCache.cs`); an unparseable blob is refused (`SettingsService.TryProjectBlob`, `src/Bevel.Core/SettingsService.cs:348`, used at `src/Bevel.App/ShellCore/RemoteSettingsService.cs:261`); the 750 ms poll is gone. All three acceptance lines have tests in `tests/Bevel.Taskbar.Tests/RemoteSettingsFirstPaintTests.cs`: `Cold_boot_with_no_core_paints_the_cached_theme_without_waiting_on_the_socket`, `A_snapshot_that_is_not_a_settings_object_never_flips_the_theme_to_defaults`, `A_live_snapshot_with_a_lower_or_equal_version_is_dropped_after_the_first_one`.
- **Depends on:** none
- **Files:** none
- **Decision:** Close. Known accepted residue: a first-ever launch with no cache paints defaults, then corrects once (`No_cache_and_no_core_seeds_defaults_then_the_first_live_snapshot_corrects_them`).
- **Test first:** existing tests above.
- **Steps:**
  - [ ] Run the verify command.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~RemoteSettingsFirstPaint"` → 0 failed
- **Done when:** green; then `bd close bevel-lej1 --reason "fixed with bevel-7s9n in 80ed440: cached first paint, defaulted snapshot refused, poll retired"`

### bevel-5no9 — Version switch relaunches the whole shell 2-3 times instead of once
- **Meta:** P2 · task · size S · AGENT
- **State today:** LIKELY DONE — `296931f` (2026-09-30, same day the bead was filed; bead has no description). `RoleProcessSupervisor` fires `_onLauncherStale` once on the first `VersionSkew` verdict and skips child kills (`src/Bevel.App/Supervision/RoleProcessSupervisor.cs:415-427`); `Program.RunLauncher` wires it to `RequestStop` + `Relaunch(args)` (`src/Bevel.App/Program.cs:303-308`, `:457-461`).
- **Depends on:** none
- **Files:** none
- **Decision:** Close. The remaining double blackout on an explicit RestartAll is bevel-hprc.
- **Test first:** existing `RoleProcessSupervisorTests.VersionSkew_with_a_relaunch_hook_relauunches_the_whole_shell_once` (the typo is in the real name).
- **Steps:**
  - [ ] Run the verify command.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~VersionSkew_with_a_relaunch_hook"` → 1 passed
- **Done when:** green; then `bd close bevel-5no9 --reason "fixed in 296931f: skew triggers one whole-shell relaunch"`

### bevel-ujx8 — Taskbar: tray seed/stream ordering + StartMenu O(n^2) startup + All-Programs flyout rebuild
- **Meta:** P3 · task · size M · AGENT
- **State today:** All three code items still present. (1) `TrayViewModel.Start` (`src/Bevel.Taskbar/Model/TrayViewModel.cs:195-212`) subscribes, then upserts the pulled list — an item removed in between is resurrected. (2) `ShellModel` ctor: `Programs.CollectionChanged += (_, _) => RecomputeFrequent()` (`src/Bevel.Taskbar/Model/ShellModel.cs:84`); `ObservableReconciler.Reconcile` inserts one at a time, and `RecomputeFrequent` (`:532-560`) does `Min` + two sorts + `Clear`/re-add each time — ~327 full recomputes on first population. (3) `StartMenu.RebuildAllProgramsList` (`src/Bevel.Taskbar/StartMenu.axaml.cs:214-220`) clears and rebuilds every row on any `Programs` change (`:211`). (4) test gaps unverified, not planned here.
- **Depends on:** none (ri8c touches the tray adapter, not this file)
- **Files:** `src/Bevel.Taskbar/Model/TrayViewModel.cs`; `src/Bevel.Taskbar/Model/ShellModel.cs`; `src/Bevel.Taskbar/StartMenu.axaml.cs`; `tests/Bevel.Taskbar.Tests/{TrayViewModelTests,ShellModelTests,StartMenuTests}.cs`
- **Decision:** (1) `bool _seeding` + `List<Action> _seedReplay` in `TrayViewModel`: while the pull is in flight the three event handlers queue; on the UI thread apply the pulled list, then replay the queue in order. (2) `int _programsBatchDepth` in `ShellModel`: both `ObservableReconciler.Reconcile(Programs, …)` sites (`:150-155`, `:587-…`) run inside a batch scope; the `CollectionChanged` handler returns early while depth > 0 and `RecomputeFrequent()` runs once at scope exit. Also make `RecomputeFrequent` diff into `FrequentPrograms` via `ObservableReconciler.Reconcile` instead of `Clear`. (3) Keep a `Dictionary<string, Control>` row cache keyed by `AppId` in `StartMenu` and reconcile `_allProgramsList.Children` in place. (4) File a separate bead for the listed test gaps.
- **Test first:** `TrayViewModelTests.An_item_removed_during_the_initial_pull_is_not_resurrected` (use the existing `StubTray`, make `GetItemsAsync` complete after a fired `ItemRemoved`); `ShellModelTests.Populating_programs_recomputes_frequent_once` (count `FrequentPrograms.CollectionChanged` resets); `StartMenuTests.A_programs_change_keeps_existing_all_programs_rows` (same `Control` instance before and after an add).
- **Steps:**
  - [x] Implement (1) and its test.
  - [x] Implement (2) and its test.
  - [x] Implement (3) and its test.
  - [x] File the test-gap bead (consolidation reflow, `WorkAreaMitigator` gate, task-menu race guard, grouped `TaskGroupViewModel` menu branch).
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~TrayViewModelTests|FullyQualifiedName~ShellModelTests|FullyQualifiedName~StartMenuTests"` → 0 failed
- **Done when:** three new tests pass; then `bd close bevel-ujx8 --reason "tray seed buffered, Programs population batched, All Programs diffed in place; test gaps refiled"`

### bevel-qf95 — Startup: stagger the taskbar post-paint installed-app enumeration + icon rendering
- **Meta:** P3 · task · size M · AGENT+HUMAN-VERIFY
- **State today:** Partly overtaken by PR #6: the app scan is 23 ms and served from the core's cache (`753f2e2`), and the pool has a writer so a warm start reads icons at 0.006 ms each (`17420db`). Still true: `shellModel.Start()` runs at `src/Bevel.App/App.axaml.cs:383`, BEFORE `taskbarWin.Show()` at `:424`, so `LoadProgramsAsync` and `ReconcileLoopAsync` compete with first paint; and the population is O(n²) on the UI thread (bevel-ujx8). The bead's 99-108 % CPU sawtooth was measured before PR #6 — UNVERIFIED whether it still exists.
- **Depends on:** bevel-ujx8
- **Files:** `src/Bevel.App/App.axaml.cs`; `src/Bevel.Taskbar/Model/ShellModel.cs`; `tests/Bevel.Taskbar.Tests/ShellModelTests.cs`; `docs/perf.md`
- **Decision:** Split `ShellModel.Start()` into `Start()` (subscribe window events + start the reconcile loop — needed for the strip) and `StartProgramsAsync()` (new; the `LoadProgramsAsync` kick). `App` calls `StartProgramsAsync` from `taskbarWin.Opened` via `Dispatcher.UIThread.Post(…, DispatcherPriority.Background)`. Do not add timers or sleeps. Re-measure before doing more: the icon path on a pool miss must not block (it does not: `IconLoader` is off-thread — confirm by reading `src/Bevel.Taskbar/Model/IconLoader.cs`, UNVERIFIED path).
- **Test first:** `ShellModelTests.Start_does_not_enumerate_installed_apps` (a counting `IAppEnvironment` stub sees 0 calls after `Start()`, ≥1 after `StartProgramsAsync()`).
- **Steps:**
  - [ ] Split the method; update every `Start()` caller (`grep -rn "shellModel.Start\|ShellModel.*\.Start()" src tests`).
  - [ ] Wire the post-`Opened` call in `App.CreateTaskbarSurfaceCore`.
  - [ ] HUMAN: cold-boot the dev `.app`, sample taskbar CPU for 15 s (`top -l 15 -pid <taskbar pid>`), record before/after in `docs/perf.md`.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~ShellModelTests"` → 0 failed
- **Done when:** app enumeration starts after the first frame and the measurement is recorded; then `bd close bevel-qf95 --reason "program load deferred to after first paint; numbers in docs/perf.md"`

### bevel-web5 — Shared icon pool has no eviction; a full pool silently stops sharing
- **Meta:** P2 · bug · size M · AGENT
- **State today:** Confirmed. `IconPoolSlotCapacity = 512` (`src/Bevel.App/CompositionRoot.cs:76`); `MmfBgraPool.TryAdd` returns false when full and `PooledIconProvider` logs once (`src/Bevel.UI/PooledIconProvider.cs:80-97`). Every superseded `path|size|stamp` key keeps its slot. No measurement of real key counts exists.
- **Depends on:** none
- **Files:** `src/Bevel.App/CompositionRoot.cs`; `src/Bevel.App/Program.cs`; `src/Bevel.UI/MmfBgraPool.cs`; `src/Bevel.UI/PooledIconProvider.cs`; `tests/Bevel.UI.Tests/MmfBgraPoolTests.cs`; `tests/Bevel.Taskbar.Tests/IconPoolWiringTests.cs`
- **Decision:** Options (a) + a boot-time reset; reject (b) and (c) (both need readers to re-map or break the frozen-entry invariant). (1) Raise capacity to 2048 (file is sparse; a geometry change already re-initialises it at `MmfBgraPool.cs:163-176`). (2) Reclaim at the one moment no reader can be mapped: in `Program.RunLauncher`, after the instance lock and the bevel-9790 probe and before any spawn, open the pool read-only, and if `PublishedCount >= 0.8 * SlotCapacity` delete the file. Put this in `internal static bool MmfBgraPool.DeleteIfNearlyFull(string path, int slotCapacity, int maxBgraBytes, double threshold)` (new). (3) Measure: the writer logs `PublishedCount` once to `RestartDiag` 60 s after start. Expose `IconPoolPath`/capacity constants as `internal` so the launcher uses the same values.
- **Test first:** `MmfBgraPoolTests`: `DeleteIfNearlyFull_removes_a_pool_past_the_threshold`, `DeleteIfNearlyFull_keeps_a_pool_below_it`, `DeleteIfNearlyFull_ignores_a_missing_or_foreign_file`.
- **Steps:**
  - [x] Add `DeleteIfNearlyFull` + tests.
  - [x] Raise the capacity; update the "512 slots … ≈ 19 MB" comment (2048 × 36,864 B ≈ 75 MB sparse).
  - [x] Call the reset in `RunLauncher` before `supervisor.StartAsync`.
  - [x] Add the one-shot `PublishedCount` log line in the writer branch.
  - [x] Update the pool paragraph in `CODEMAP.md`.
- **Verify:** `dotnet test tests/Bevel.UI.Tests/Bevel.UI.Tests.csproj --filter "FullyQualifiedName~MmfBgraPool"` → 0 failed
- **Done when:** a pool at ≥80 % is dropped on the next launcher boot and the count is logged; then `bd close bevel-web5 --reason "capacity 2048 + launcher-boot reset when nearly full; published count logged"`

### bevel-yg7h — Startup: defer the helper ScreenCaptureKit/AX tray self-test off the first-paint window
- **Meta:** P2 · task · size M · AGENT+HUMAN-VERIFY
- **State today:** Confirmed. `TrayServiceImpl.enumerateWithCapture` (`native/helper-macos/Sources/BevelHelper/TrayServiceImpl.swift:621-641`) does `await ensureSelfTested()` BEFORE returning any items, so the first tray list waits for `runSelfTest` (`:816-831`, incl. `selfTestCapture`). The core already serves before the seed (`ShellCoreServer.StartAsync`), so only the tray is late. The 1.3 s / 4.8 s numbers predate PR #6 — UNVERIFIED today.
- **Depends on:** none
- **Files:** `native/helper-macos/Sources/BevelHelper/TrayServiceImpl.swift`; `native/helper-macos/Tests/BevelHelperTests/TrayServiceTests.swift`
- **Decision:** Never block an enumeration on the self-test. While `selfTestDone == false`, `enumerateWithCapture` returns the limited-mode items (app icons) immediately and starts the self-test once in `Task.detached(priority: .utility)`; when it finishes, the next poll tick upgrades items to live capture through the existing `Updated` path. Extract the gate as `static func shouldCaptureLive(selfTestDone: Bool, liveEnabled: Bool, granted: Bool) -> Bool` (new) so it is unit-testable like `selfTestShouldRun`. No fixed delay (a guess); priority + non-blocking is the fix.
- **Test first:** `TrayServiceTests.testEnumerationDoesNotWaitForTheSelfTest` (new; pure test of `shouldCaptureLive`: false while `selfTestDone` is false regardless of grant) plus keep `testSelfTestReArmsOnceOnTheUngrantedToGrantedTransition` green.
- **Steps:**
  - [ ] Add `shouldCaptureLive` + test.
  - [ ] Replace the `await ensureSelfTested()` at `:624` with a non-awaited, once-only detached start (guard with `selfTestLock`).
  - [ ] HUMAN: `./packaging/macos/build-app.sh && ./packaging/macos/dev-sign.sh`, cold boot with Screen Recording granted; confirm tray shows app icons within ~1 s of the taskbar and upgrades to live glyphs; check the helper log line `self-test … → liveMirroring=true`.
- **Verify:** `cd native/helper-macos && swift test --filter TrayServiceTests` → 0 failures
- **Done when:** tray items appear before the self-test log line; then `bd close bevel-yg7h --reason "self-test detached at utility priority; first enumeration returns limited-mode items immediately"`

### bevel-glnc — ShellInstanceLock: IOException can escape, and RecordPid leaks the lock stream
- **Meta:** P3 · bug · size S · AGENT
- **State today:** PARTLY STALE. Defect 1 confirmed: `Directory.CreateDirectory` sits outside the try (`src/Bevel.App/Supervision/ShellInstanceLock.cs:53-59`) and `Program.TryTakeInstanceLock` filters `when (ex is not IOException)` (`src/Bevel.App/Program.cs:475`), so an `IOException` there aborts the launcher. Defect 2 is NOT reproducible: `RecordPid` wraps its write in `try { … } catch { }` (`ShellInstanceLock.cs:101-105`) and has since the file was created (`a59d23d`), so it cannot throw into the retry loop.
- **Depends on:** none
- **Files:** `src/Bevel.App/Supervision/ShellInstanceLock.cs`; `src/Bevel.App/Program.cs`; `tests/Bevel.Taskbar.Tests/ShellInstanceLockTests.cs`
- **Decision:** Move the "unavailable" policy into the type so it is testable: `public static ShellInstanceLock? TryAcquireOrUnavailable(string path, TimeSpan wait, out int incumbentPid, out bool unavailable)` (new) wraps `TryAcquire` in `catch (Exception)` (all types — contention never escapes `TryAcquire`, so anything that does means "cannot lock"). `Program.TryTakeInstanceLock` becomes a call to it plus the two log lines. Also make the open construct-then-commit (`try { held.RecordPid(); } catch { fs.Dispose(); throw; }`) as insurance for a future throwing `RecordPid`.
- **Test first:** `ShellInstanceLockTests.A_lock_whose_directory_cannot_be_created_reports_unavailable_instead_of_throwing` (new): create a regular file `X`, ask for lock path `X/shell.lock` → returns null with `unavailable == true`.
- **Steps:**
  - [ ] Add the method + test (red first by calling `TryAcquire` directly, which throws).
  - [ ] Switch `Program.TryTakeInstanceLock` to it.
  - [ ] Record in the bead that defect 2 was not reproducible and why.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~ShellInstanceLock"` → 6 passed
- **Done when:** no exception type can leave the lock acquisition; then `bd close bevel-glnc --reason "all acquisition failures map to unavailable; RecordPid defect not reproducible (already swallowed)"`

### bevel-hprc — [design] Launcher self-update on RestartAll (version-stamped handshake, re-exec)
- **Meta:** P3 · task · size M · AGENT
- **State today:** PARTLY DONE by other work. Stamp exists (`Supervision/BuildStamp.cs`, re-read from disk on every `Current()`); skew → one whole-shell relaunch exists (`296931f`); `coreReadyProbe` is no longer `File.Exists` but the authenticated `CoreSocketProbe.WaitUntilServingAsync` (`src/Bevel.App/Program.cs:302`). Remaining: `RestartAll` on a stale launcher first restarts every child (`RoleProcessSupervisor.RestartAllAsync`, `:109-127`), then the monitor sees skew and relaunches the whole shell — two blackouts. Kills are serial and each `RoleProcess.Kill` blocks up to `GraceMs` (`src/Bevel.App/Supervision/RoleProcess.cs:66-80`).
- **Depends on:** none (bevel-5no9's fix is already in)
- **Files:** `src/Bevel.App/Supervision/RoleProcessSupervisor.cs`; `src/Bevel.App/Program.cs`; `tests/Bevel.Taskbar.Tests/RoleProcessSupervisorTests.cs`
- **Decision:** Add ctor parameter `Func<bool>? launcherIsStale = null` to `RoleProcessSupervisor`. `RestartAllAsync` checks it first: if true and `_onLauncherStale` is set, fire the hook once (same `_launcherStaleFired` latch) and return without killing anything — the launcher's existing teardown + `Relaunch(args)` does the rest. `Program` passes `() => BuildStamp.Current() != startStamp` where `startStamp` is the value put into `childEnv[BuildStamp.EnvVar]`. Then make the UI-role kills parallel (`Task.WhenAll` over `Task.Run(p.Kill)` for non-core roles, core last). Rejected: "taskbar back before core" — the core-first start order is the dependency the ready probe guards. `filerSupervisor.RestartAllAsync` must be skipped on the stale path too (`Program.cs:340-341`).
- **Test first:** `RoleProcessSupervisorTests.RestartAll_on_a_stale_launcher_relaunches_once_and_kills_no_child` (new; model on `VersionSkew_with_a_relaunch_hook_relauunches_the_whole_shell_once`), `RestartAll_on_a_current_launcher_restarts_children_in_place` (new).
- **Steps:**
  - [x] Add the parameter and early return; make `RestartAllAsync` return a bool "relaunching" so the verb handler skips the filer restart.
  - [x] Wire `startStamp` in `RunLauncher`.
  - [x] Parallelise UI kills; existing ordering tests still pass.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~RoleProcessSupervisorTests"` → 0 failed
- **Done when:** RestartAll after a rebuild produces exactly one relaunch in `$TMPDIR/bevel-restart.log`; then `bd close bevel-hprc --reason "RestartAll escalates straight to a whole-shell relaunch when the launcher is stale; UI kills parallel"`

### bevel-f4g5 — Version-skew message needs to be readable and offer actions
- **Meta:** P2 · task · size M · AGENT+HUMAN-VERIFY
- **State today:** PARTLY STALE. Since `296931f` a `VersionSkew` verdict in the real launcher never reaches the Hold dialog (`RoleProcessSupervisor.cs:415-427` returns first), so the quoted dialog can no longer appear in production. What remains: the raw stamp triple is still the `Detail` (`ShellHealthMonitor.cs:180`) written to logs/status, and every other Hold (startup stuck, etc.) shows `ShellHealthAlert.Format` text with a single "OK" button (`src/Bevel.App/Supervision/ShellHealthAlert.cs:15-21`, `:59-62`) and no way forward.
- **Depends on:** none
- **Files:** `src/Bevel.App/Supervision/BuildStamp.cs`; `src/Bevel.App/Supervision/ShellHealthAlert.cs`; `src/Bevel.App/Program.cs`; `tests/Bevel.Taskbar.Tests/ShellHealthAlertTests.cs` (new)
- **Decision:** (1) `internal static string BuildStamp.Describe(string stamp)` (new): `"1|1.0.0+1f4cc8f…|6392…"` → `"1.0.0 (1f4cc8f, built 2026-09-29 19:23)"` (version before `+`, first 7 of the SHA, ticks → local time; unparseable input returned unchanged). (2) `Format` leads with one plain sentence per `HealthFault` (skew: "Bevel was updated while it was running, so the taskbar and the launcher are from different builds."), then the two described builds, and keeps the full detail under a "Details:" line. (3) Hold dialogs get two buttons: macOS `buttons {"Quit Bevel","Restart Bevel"} default button 2` (read `button returned:` from osascript stdout); Windows `MB_RETRYCANCEL`. New enum `HealthAlertChoice { Restart, Quit }` (new) and `Show(HealthVerdict v, Action<HealthAlertChoice>? onChoice)`. `RunLauncher` wires Restart to the same path as `onLauncherStale`, Quit to `stop.Set()`.
- **Test first:** `ShellHealthAlertTests`: `Describe_turns_a_stamp_into_version_short_sha_and_build_time`, `Describe_returns_garbage_unchanged`, `Format_for_skew_leads_with_a_sentence_and_hides_the_raw_stamp_below_Details`, `A_Hold_reports_the_users_choice` (inject the native dialog).
- **Steps:**
  - [x] `BuildStamp.Describe` + tests.
  - [x] Rewrite `Format`; change the `native` seam to return the pressed button.
  - [x] Wire the choice in `RunLauncher`.
  - [ ] HUMAN: force a Hold (run a build whose taskbar fails at startup) and check both buttons on macOS.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~ShellHealthAlert"` → 4 passed
- **Done when:** a Hold dialog reads as a sentence and both buttons act; then `bd close bevel-f4g5 --reason "health alerts are human-readable with Restart/Quit; skew itself auto-relaunches since 296931f"`

### bevel-mhvu — ShellCoreSystemTrayHost duplicates tray capabilities instead of forwarding the core's
- **Meta:** P2 · bug · size S · AGENT
- **State today:** Confirmed, and there is a second instance. `ShellCoreSystemTrayHost.Capabilities` is a per-OS literal (`src/Bevel.App/ShellCore/ShellCoreSystemTrayHost.cs:44-48`); `ShellCoreWindowManager.Capabilities` is a literal with `TrayMode: Mirrored, SupportsReposition: true` on every platform (`ShellCoreWindowManager.cs:40-41`). Readers: `OnboardingWindow.axaml.cs:65` (`TrayMode`), `WorkAreaMitigator.cs:190` (`SupportsReposition`).
- **Depends on:** none
- **Files:** `src/Bevel.App/ShellCore/CoreProtocol.cs`; `src/Bevel.App/ShellCore/ShellCoreServer.cs`; `src/Bevel.App/ShellCore/ShellCoreSystemTrayHost.cs`; `src/Bevel.App/ShellCore/ShellCoreWindowManager.cs`; `tests/Bevel.Taskbar.Tests/ShellCoreIntegrationTests.cs`
- **Decision:** Add two nullable slots to `CoreEvent`: `Capabilities? TrayCapabilities` and `Capabilities? WindowCapabilities` (new), filled by `BuildSnapshotEvents` on `TraySnapshot` / `WindowSnapshot` from `_tray.Capabilities` / `_windows.Capabilities`. Each proxy stores the last received value in a `volatile` field and returns it; the current literal stays only as the pre-snapshot fallback. Nullable slots keep an older core compatible (JSON `WhenWritingNull`). Fix both proxies in one change (same pattern).
- **Test first:** `ShellCoreIntegrationTests.Proxies_report_the_cores_capabilities_after_the_snapshot` (new): `ControllablePal` reports `TrayMode: Authoritative, SupportsReposition: false`; after Hello the tray proxy and window proxy report exactly that.
- **Steps:**
  - [x] Add the slots (check `CoreJsonContext` generates `Capabilities` — it is reached through `CoreEvent`).
  - [x] Fill them in `BuildSnapshotEvents`.
  - [x] Store + return in both proxies; shorten the long apology comment in the tray host.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~Proxies_report_the_cores_capabilities"` → 1 passed
- **Done when:** neither proxy's post-snapshot value comes from a literal; then `bd close bevel-mhvu --reason "capabilities ride the window/tray snapshots; both proxies forward them"`

### bevel-gww.9 — AOT: convert 3 code-behind reflection Bindings to compiled/manual (StartMenu icon, ItemView selection colors)
- **Meta:** P3 · task · size M · AGENT
- **State today:** Open, and the count is now 5, at moved lines: `src/Bevel.Taskbar/StartMenu.axaml.cs:226` and `:616` (`Image.Source` ← `ProgramItemViewModel.IconSource`, `Source = vm`); `src/Bevel.FileManager/Components/ItemView.axaml.cs:292`, `:295` (`IsSelected` → brushes via `SelFgConv`/`SelBgConv`) and `:308` (`AutomationProperties.Name` ← `DisplayName`). The three `ItemView` ones are DataContext-relative inside `FuncDataTemplate<ItemViewModel>` builders that already receive `vm`.
- **Depends on:** none
- **Files:** `src/Bevel.UI/InpcBind.cs` (new); `src/Bevel.Taskbar/StartMenu.axaml.cs`; `src/Bevel.FileManager/Components/ItemView.axaml.cs`; `tests/Bevel.UI.Tests/InpcBindTests.cs` (new)
- **Decision:** One reflection-free helper: `public static class InpcBind { public static void Attach(Control host, INotifyPropertyChanged source, string propertyName, Action apply) }` (new). It calls `apply()` and subscribes on `AttachedToVisualTree`, unsubscribes on `DetachedFromVisualTree` — so a recycled/virtualised row never leaks a handler onto a long-lived view-model. Callers pass a lambda that reads the property and sets the control property directly. `NameCell` and `Row` gain an `ItemViewModel vm` parameter (all call sites are inside templates that have it). Rejected: `CompiledBindingPathBuilder` in code-behind — verbose and still easy to get wrong.
- **Test first:** `InpcBindTests`: `Apply_runs_on_attach_and_on_each_matching_change`, `Detach_unsubscribes` (assert the source's `PropertyChanged` invocation list is empty after detach), `Other_property_names_are_ignored`.
- **Steps:**
  - [x] Add `InpcBind` + tests.
  - [x] Convert the two `StartMenu` sites.
  - [x] Convert the three `ItemView` sites; `grep -rn "new Binding\|new Avalonia.Data.Binding" src --include='*.cs'` returns nothing.
  - [x] Run the selection and accessibility tests (`ClickSelectionTests`, `MarqueeSelectionTests`, `AccessibilityNamingTests`).
- **Verify:** `dotnet test tests/Bevel.FileManager.Tests/Bevel.FileManager.Tests.csproj --filter "FullyQualifiedName~Selection|FullyQualifiedName~AccessibilityNaming"` → 0 failed; `dotnet test tests/Bevel.UI.Tests/Bevel.UI.Tests.csproj --filter "FullyQualifiedName~InpcBind"` → 3 passed
- **Done when:** zero reflection `Binding` constructions in `src`; then `bd close bevel-gww.9 --reason "5 code-behind bindings replaced by InpcBind"`

### bevel-gww.7 — P7: NativeAOT publish (the per-process memory payoff)
- **Meta:** P2 · task · size L · HUMAN
- **State today:** PARTLY DONE — foundation in `0ff207c`: `AvaloniaUseCompiledBindingsByDefault` (`Directory.Build.props:17`), JSON source-gen (`CoreJsonContext`, `SettingsJsonContext`), opt-in `-p:BevelPublishAot=true` (`src/Bevel.App/Bevel.App.csproj:20-29`); a 28 MB Mach-O booted `--role=core --pal=fake`. Remaining per the notes: (1) reflection bindings → bevel-gww.9; (2) the AOT native link needs Homebrew `openssl@3` + `brotli` on `LIBRARY_PATH`; (3) single-Mach-O packaging, drop JIT entitlements (`packaging/macos/Bevel.entitlements`), revalidate Velopack + inside-out codesign + notarization.
- **Depends on:** bevel-gww.9; signing identity and notarization credentials (owner)
- **Files:** `src/Bevel.App/Bevel.App.csproj`; `packaging/macos/build-app.sh`; `packaging/macos/Bevel.entitlements`; `packaging/macos/verify-bundle.py`
- **Decision:** HUMAN because (2) is a distribution choice (static-link vs bundle dylibs vs replace the gRPC HTTP stack) and (3) needs the signing identity, a notarization round trip and a real TCC re-grant. Recommended split into three new beads: "AOT: make the native link self-contained (no Homebrew on LIBRARY_PATH)", "AOT: packaging variant `BEVEL_AOT=1` in build-app.sh + entitlements without JIT", "AOT: measure per-role RSS vs R2R and record in docs/perf.md" (the payoff must be a number before it becomes the default).
- **Test first:** `dotnet publish src/Bevel.App -r osx-arm64 -p:BevelPublishAot=true` completes with 0 IL2xxx/IL3xxx warnings (owner's machine; not to be run by cluster agents).
- **Steps:**
  - [ ] Land bevel-gww.9.
  - [ ] Owner decides the native-dependency strategy.
  - [ ] Create the three beads above; keep this bead as their parent or close it into them.
- **Verify:** the AOT bundle boots all roles and the helper handshake succeeds on a notarized build.
- **Done when:** the three follow-up beads exist and the dependency strategy is recorded; then `bd close bevel-gww.7 --reason "foundation landed in 0ff207c; remaining work split into link / packaging / measurement beads"`

### bevel-gww — Multi-process shell: --role split + shell-core owner + shared caches + AOT
- **Meta:** P2 · epic · size L · AGENT
- **State today:** 7 of 9 children closed (gww.1–.6, .8). Open: bevel-gww.9, bevel-gww.7.
- **Depends on:** bevel-gww.9, bevel-gww.7
- **Files:** none
- **Decision:** Child order: bevel-gww.9 → bevel-gww.7 (and its three split beads). Exit criterion: an AOT bundle with zero trim warnings boots every role, and a recorded per-role RSS comparison against R2R exists. Work no child covers — create:
  - "Decide and document the icon-pool owner" — the plan says the core owns pool writes; the code makes the taskbar the writer (`CompositionRoot.cs:152`) and Filer processes can never publish file-type icons. Either amend the rule to "one writer: taskbar" or add a core/taskbar `RenderIcon` request so readers can ask the writer.
  - "Core projection never removes on a helper-stream resubscribe" (from bevel-wheq).
  - "Retire the taskbar 2 s window re-list" (from bevel-tnii).
  - "Ctrl+N opens a second window inside one filer process" — `FileManagerWindow.NewWindowRequested += path => factory.Create(path)` (`src/Bevel.App/App.axaml.cs`), contradicting one-process-per-window; route through `IFilerSpawner`.
- **Test first:** n/a (epic).
- **Steps:**
  - [ ] Create the four beads above as children.
  - [ ] Close when all children are closed.
- **Verify:** `bd show bevel-gww` → all children ✓
- **Done when:** exit criterion met; then `bd close bevel-gww --reason "multi-process split complete incl. AOT"`

### bevel-1nxe — Migrate drag-drop to Avalonia DataTransfer API (retire IDataObject)
- **Meta:** P3 · task · size M · AGENT+HUMAN-VERIFY
- **State today:** Open. Avalonia is 11.3.18 (`Directory.Packages.props:8`); `DataTransfer`, `DataTransferItem.CreateFile`, `DataFormat.CreateStringApplicationFormat`, `DataTransferExtensions.TryGetFiles` exist in its reference assembly (checked in `~/.nuget/packages/avalonia/11.3.18/ref/net8.0/Avalonia.Base.xml`). Legacy sites today: `src/Bevel.FileManager/Components/ItemView.axaml.cs:807-813` (source), `:822`, `:827-833` (target); `src/Bevel.Taskbar/StackFlyoutView.axaml.cs:73-75`. The bead's `TaskbarView.axaml.cs ~L320` site no longer exists — it moved to `StackFlyoutView`.
- **Depends on:** bevel-u2x6 (lands the drop-path helper this migrates)
- **Files:** `src/Bevel.FileManager/Components/ItemView.axaml.cs`; `src/Bevel.FileManager/FileOperations/FileDropPayload.cs`; `src/Bevel.Taskbar/StackFlyoutView.axaml.cs`; `tests/Bevel.FileManager.Tests/ItemViewDragSourceTests.cs`
- **Decision:** The private payload becomes a STRING application format (newline-joined `scheme:path` list) via `DataFormat.CreateStringApplicationFormat("Bevel.FileDropPayload")` — a string crosses process boundaries, a managed object does not, which also settles the cross-process question for good. Files go in as one `DataTransferItem.CreateFile` per item. UNVERIFIED: the exact `DragDrop.DoDragDropAsync` signature and the `DragEventArgs` read property name in 11.3.18 — read the XML doc for `M:Avalonia.Input.DragDrop.DoDragDropAsync` before writing code. Keep "resolve on press, start the drag synchronously".
- **Test first:** update `ItemViewDragSourceTests` (they are source-text assertions) to the new API names; add `FileDropPayloadTests.Paths_round_trip_through_the_string_format` (new).
- **Steps:**
  - [ ] Add `FileDropPayload.Serialize()` / `TryParse(string)` (new) + round-trip test.
  - [ ] Migrate `StartDragAsync`, `OnDragOver`, `ResolveDropPaths`.
  - [ ] Migrate `StackFlyoutView`.
  - [ ] Build Release with CS0618 visible; zero drag-drop warnings.
  - [ ] HUMAN: live item drag, folder drop, empty-space drop, stack drag, Filer→Finder.
- **Verify:** `dotnet build Bevel.sln -c Release -clp:ErrorsOnly -warnaserror:CS0618` → Build succeeded
- **Done when:** acceptance criteria in the bead met; then `bd close bevel-1nxe --reason "drag-drop on DataTransfer; internal payload is a string format"`

### bevel-gw2 — Factor shared gRPC helper-client base (WindowManager + SystemTrayHost)
- **Meta:** P3 · task · size S · AGENT
- **State today:** Confirmed duplicate: `MacOSWindowManager` (`src/Bevel.Pal.MacOS/MacOSWindowManager.cs:208-286`: `StreamLoopAsync` retry shell, `GetWindowClient`, `AuthHeader`) vs `MacOSSystemTrayHost` (`MacOSSystemTrayHost.cs:143-196`), near line-for-line.
- **Depends on:** do AFTER the "core resubscribe reconcile" bead from bevel-wheq, which adds an on-resubscribe hook to both loops — extracting first means doing it twice.
- **Files:** `src/Bevel.Pal.MacOS/HelperStreamRunner.cs` (new); `src/Bevel.Pal.MacOS/MacOSWindowManager.cs`; `src/Bevel.Pal.MacOS/MacOSSystemTrayHost.cs`; `tests/Bevel.Pal.MacOS.Tests/HelperStreamRunnerTests.cs` (new)
- **Decision:** Composition, not a generic base class: `internal static class HelperStreamRunner { internal static Task RunAsync(Func<CancellationToken, Task> subscribeAndDrain, IClock clock, TimeSpan retryInterval, ILogger logger, string name, CancellationToken ct) }` (new) owns the while/try/catch/delay shell (the three catch arms are identical in both files). `GetClient`/`AuthHeader` stay per class — they are three lines and differ by client type and capability string ("window" vs the tray's). Reason: the loop is the duplicated logic; a `HelperGrpcServiceBase<TClient>` would add inheritance for two one-liners.
- **Test first:** `HelperStreamRunnerTests`: `Resubscribes_after_Unavailable`, `Resubscribes_after_any_other_failure`, `Stops_on_cancellation` (fake `IClock` from `src/Bevel.Pal.MacOS/IClock.cs`; no real delays).
- **Steps:**
  - [ ] Add the runner + tests.
  - [ ] Replace both `StreamLoopAsync` shells; bodies become the `subscribeAndDrain` lambda.
  - [ ] `dotnet test tests/Bevel.Pal.MacOS.Tests` green.
- **Verify:** `dotnet test tests/Bevel.Pal.MacOS.Tests/Bevel.Pal.MacOS.Tests.csproj --filter "FullyQualifiedName~HelperStreamRunner"` → 3 passed
- **Done when:** neither class contains a `while (!ct.IsCancellationRequested)` retry shell; then `bd close bevel-gw2 --reason "stream retry shell extracted to HelperStreamRunner"`

### bevel-alb — ShellCore CoreCommand/CoreResponse: replace optional-field grab-bag with typed payloads
- **Meta:** P3 · task · size L · AGENT
- **State today:** Worse than filed: `CoreCommand` has 12 optional fields for 21 `CoreCommandKind` values, `CoreResponse` 9, `CoreEvent` 10 (`src/Bevel.App/ShellCore/CoreProtocol.cs:92-110`, `:158-195`). The flat shape is a stated design choice in the file's own doc ("keeps the JSON flat and the STJ round-trip trivial", source-generated for AOT).
- **Depends on:** bevel-mhvu (adds slots to `CoreEvent`); do not start while any other card in this cluster that edits `CoreProtocol.cs` is open.
- **Files:** `src/Bevel.App/ShellCore/CoreProtocol.cs`; `tests/Bevel.Taskbar.Tests/CoreProtocolShapeTests.cs` (new)
- **Decision:** Do NOT change the wire format — a polymorphic JSON hierarchy would be a `BuildStamp.Protocol` bump and a version-skew relaunch for every user, for a maintainability gain. Instead keep the records and enforce validity at the edges: (1) static factories on `CoreCommand` (`CoreCommand.Activate(string windowId)`, `.Reposition(string windowId, PalRect bounds)`, `.ForwardTrayClick(…)`, one per kind) and make callers use them; (2) `internal static string? CoreCommand.Validate()` (new) returning an error when a kind's required field is missing, called at the top of `ShellCoreServer`'s dispatch so a malformed command yields `CoreResponse.Fail` instead of a null-deref. Reason: type-enforced construction + checked receipt, zero wire risk.
- **Test first:** `CoreProtocolShapeTests`: `Every_command_kind_has_a_factory` (reflection over the enum vs factory names), `Validate_rejects_a_window_command_without_an_id`, `Factory_output_round_trips_through_CoreProtocol`.
- **Steps:**
  - [ ] Add factories + `Validate` + tests.
  - [ ] Replace every `new CoreCommand(` outside `CoreProtocol.cs` (`grep -rn "new CoreCommand(" src`) with a factory.
  - [ ] Call `Validate` in the server dispatch.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~CoreProtocolShape|FullyQualifiedName~ShellCoreIntegrationTests"` → 0 failed
- **Done when:** no `new CoreCommand(` remains outside the factories; then `bd close bevel-alb --reason "typed factories + receipt validation; wire format deliberately unchanged"`

### bevel-1jvr — Startup: replace ShellCoreEndpoint.ReadNonce 5s Thread.Sleep poll with async wait (latent hang)
- **Meta:** P4 · bug · size S · AGENT
- **State today:** Unchanged: `src/Bevel.App/ShellCore/ShellCoreEndpoint.cs:44-57` — env fast path, else 50 × `Thread.Sleep(100)`, then throw. Only caller: `CreateClient` (`:60-61`). Every supervised child and every launcher-spawned Filer inherits `BEVEL_CORE_TOKEN` (`Program.cs:249-266`), so the loop runs only for a hand-launched role. Owner already downgraded it and ruled out threading async through the constructor.
- **Depends on:** none
- **Files:** `src/Bevel.App/ShellCore/ShellCoreEndpoint.cs`; `tests/Bevel.Taskbar.Tests/ShellCoreEndpointTests.cs` (new)
- **Decision:** Smallest honest fix, no async plumbing: remove the sleep loop. With no env token, read the token file once; if it is absent, throw immediately with the existing message. A mis-launched process then fails in milliseconds with a clear error instead of freezing a UI thread for 5 s first; a standalone dev run that starts the UI before the core is the only loser, and the message tells them why. Add `internal static byte[] ReadNonce(string? envToken, string tokenPath)` (new overload) so it is testable without touching process environment.
- **Test first:** `ShellCoreEndpointTests`: `An_env_token_wins_without_touching_the_file`, `A_token_file_is_read_when_no_env_token_is_set`, `A_missing_token_fails_immediately` (assert it throws in under 500 ms).
- **Steps:**
  - [ ] Add the overload + tests.
  - [ ] Make the parameterless `ReadNonce()` delegate to it; delete the loop.
  - [ ] Update the doc comment ("Waits briefly…" is no longer true).
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~ShellCoreEndpoint"` → 3 passed
- **Done when:** no `Thread.Sleep` remains in `ShellCoreEndpoint.cs`; then `bd close bevel-1jvr --reason "token poll removed; a missing token fails fast"`
