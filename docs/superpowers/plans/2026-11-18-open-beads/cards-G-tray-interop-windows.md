> Part of the [open-beads master plan](README.md). Read README.md first: it sets the rules, the order, and the rulings that override individual cards.
> Written 2026-11-18 from reading the tree at `51582c0`. Nothing here was built or run. `UNVERIFIED:` marks a claim the researcher could not confirm — check it before relying on it.

## Cluster G: Tray mirroring (M3), Apple Events (M4), native interop, Windows PAL

**Who owns what (verified in code, 2026-11-18):**
- **Helper (Swift)** `native/helper-macos/Sources/BevelHelper/TrayServiceImpl.swift` is the only tray discoverer: `enumerateTrayItems()` (CGWindowList layer 25) -> `enumerateWithCapture()` (per-item `LegacyWindowCapture.image(windowID:)`, NOT ScreenCaptureKit - SCK tray capture was deleted in bevel-p6g4) -> `Changes` stream (snapshot burst, then a 2 s / 900 ms poll+diff).
- **Core** `src/Bevel.Pal.MacOS/MacOSSystemTrayHost.cs` holds the one helper stream; `src/Bevel.App/ShellCore/ShellCoreServer.cs` keeps `_trayById` and broadcasts `TraySnapshot` / `TrayItemAdded|Updated|Removed`.
- **Taskbar** `src/Bevel.App/ShellCore/ShellCoreSystemTrayHost.cs` -> `src/Bevel.Taskbar/Model/TrayViewModel.cs` (`TrayItemViewModel`, off-thread `TrayIconTint.Process`) -> `src/Bevel.Taskbar/TrayIconCell.axaml`. The native hide runs in the TASKBAR process through the `applyLocalHide` seam (`MacMenuBarControl.SetHidden`); `MacMenuBarOverlay` exists but has no caller.
- **Apple Events:** `src/Bevel.Pal.MacOS/AppleEventInbound.cs` (+`AppleEventQuery.cs`, `AppleEventSpecifier.cs`) -> `src/Bevel.App/AppleEventBridge.cs` -> `IShellAutomation`; wired only in the Taskbar role (`App.axaml.cs:239-243`).
- **Windows:** `src/Bevel.Pal.Windows/WindowsDesktop.cs` (`WindowsDesktopEnvironment`, `WindowsDockController`), `WindowsSystemTrayHost.cs` (empty by design), `src/Bevel.Taskbar/TaskbarGeometry.cs`.

**Tray symptoms grouped by root cause:**
- **RC-A, capture gate latched/invisible** (yduf, qcd9, voqo's "all placeholders" note): fixed in code (bevel-p6g4, 07d1063, placeholder cell + logs). One on-device run (TRAY-QA below) closes yduf + qcd9.
- **RC-B, snapshots are upsert-only** (voqo "don't rebuild"): three layers replay a snapshot as `ItemAdded` and never remove what is absent. Contradicts north star 2. ONE fix (voqo card) closes it.
- **RC-C, off-screen push freezes backing stores** (voqo "go stale" while consolidated; 7hf4): fix = wire `MacMenuBarOverlay` (7hf4 card).
- **RC-D, WindowServer exposes no pixels for ~25% of items** (sd6n; ye27 is the same Control-Center hosting on the name side): not fixable by capture; limited-mode app icon is the designed answer.
- **RC-E, sizing** (7qdi, aogp): the code path 7qdi investigated no longer exists (native sizing + `TrayViewModel.MaxIconHeight`).

**TRAY-QA (the one manual session, referenced by several cards).** On the owner's Mac, signed build:
`./packaging/macos/build-app.sh && ./packaging/macos/dev-sign.sh`, quit Bevel via Start > Turn Off, then run
`BEVEL_DEBUG_TRAY=1 BEVEL_DEBUG_TASKBAR=1 dist/Bevel.app/Contents/MacOS/Bevel 2> /tmp/tray-qa.log` and read
`grep -E "BEVEL-TRAY\] (self-test|captured|live capture)|tray: " /tmp/tray-qa.log`.

## Order

1. bevel-injy - P1; fix is on main and the clean-quit half is already verified on Windows; only the crash-heal check remains.
2. bevel-h0sr - same commit as injy; one Windows-box check closes it.
3. bevel-voqo - only tray bug with an unfixed, deterministic code defect (no remove on snapshot); also supplies the id/bounds cache 7hf4 needs.
4. bevel-yduf - code done; closes with the TRAY-QA run.
5. bevel-qcd9 - code done and merged; closes with the same run.
6. bevel-m3.1 - done; stale in_progress flag holds the M3 epic at 57%.
7. bevel-m2.6 - duplicate of closed M3 children; closing it unblocks the epic.
8. bevel-7hf4 - finishes consolidation without frozen icons; needs voqo's cache; on-device A/B.
9. bevel-sd6n - investigation finished; a doc correction and a close.
10. bevel-tned - human eyes only; rides the TRAY-QA session.
11. bevel-m3 - epic; exit criterion + cuts.
12. bevel-whz - `open` is one of the six core verbs in the M4 gate and is unhandled today.
13. bevel-odf - sync reply still blocks the UI thread and answers window/selection queries from an empty registry.
14. bevel-4qr - the M4 gate itself; needs whz + odf first, then 10 more snippets and a run on the real app.
15. bevel-m4 - epic; exit criterion + cuts.
16. bevel-ye27 - daily-use polish (tooltips say "Item"); bounded AX work.
17. bevel-sdss - dead contract; small, removes a trap for every future permission-reactive surface.
18. bevel-soon - real ABI bug, one-line fix, existing tests cover it.
19. bevel-m3.2.1 - mostly done; remaining is CPU saving, feeds M3's soak criterion.
20. bevel-aogp - dead-code deletion, trivial.
21. bevel-tzx - premise changed (tray no longer uses SCK); small dedupe remains.
22. bevel-7qdi - stale; close after TRAY-QA shows normal sizes.
23. bevel-vs10 - parity nicety, not Finder-compat; cut from the M4 gate.
24. bevel-2kdu - AOT hygiene; large and mechanical, no user-visible effect.
25. bevel-p5bx - same for Windows; needs a Windows CI run to trust.
26. bevel-m3.3.1 - known-hard, per-app, spec already scopes forwarding as fragile.
27. bevel-2t7u - blocked on shell-replacement mode (measured); large.
28. bevel-m3.6 - superseded by Strategy C; cut.
29. bevel-spike-tier3 - post-v1 research; only worth running after M4 ships.
30. bevel-spike-shadow - outcome is pre-decided ("never ships"); not worth running.

## Cards

### bevel-injy — Quit leaves the Windows taskbar hidden (no restore / no crash-heal)
- **Meta:** P1 · bug · size S · AGENT+HUMAN-VERIFY
- **State today:** LIKELY DONE — fix is on main (commit 514269e): `WindowsDockController : IDockController, IDisposable` (`src/Bevel.Pal.Windows/WindowsDesktop.cs:500`) calls `HealCrashIfNeeded()` in its ctor (:526), hooks `AppDomain.CurrentDomain.ProcessExit` (:527), writes `win-taskbar-claimed.json` (:521), handles `Shell_SecondaryTrayWnd` (:802); `TaskbarWindow.OnClosed` calls `_dockController?.SetAutoHideAsync(false)` (`src/Bevel.Taskbar/TaskbarWindow.cs:224`). Clean quit was verified on a Windows box in commit c7d18fa ("native taskbar visible, claim marker gone"). NOT yet verified: the hard-kill heal path.
- **Depends on:** none
- **Files:** none to change (read `src/Bevel.Pal.Windows/WindowsDesktop.cs:482-680`)
- **Decision:** no code change. Close after the one missing manual check below; if it fails, reopen with the marker file contents attached.
- **Test first:** existing `tests/Bevel.Pal.Windows.Tests/WindowsDesktopTests.cs` -> `Dock_dispose_restores_the_native_taskbar_on_windows` and `Dock_hide_restore_round_trips_the_native_taskbar_on_windows` (they run only on Windows CI).
- **Steps:**
  - [ ] HUMAN, on the Windows box: `packaging/windows/publish.sh` then `packaging/windows/deploy-ssh.sh`; start Bevel; confirm the native taskbar is hidden.
  - [ ] HUMAN: Task Manager > End task on every `Bevel.App.exe` (this is the kill -9 case). Expected now: no native taskbar, and `%LOCALAPPDATA%\bevel\win-taskbar-claimed.json` exists (`MarkerDir`, `WindowsDesktop.cs:517-519`).
  - [ ] HUMAN: start Bevel again, then `bevelctl quit`. Expected: native taskbar visible again on every monitor, marker file gone.
- **Verify:** `gh run list --workflow ci --limit 1` → latest Windows job green (it runs the two tests above).
- **Done when:** the three manual steps pass; then `bd close bevel-injy --reason "Fixed in 514269e; clean quit verified c7d18fa; crash-heal verified on NUCBOX <date>"`

### bevel-h0sr — Windows taskbar sits too high and ignores display-size changes
- **Meta:** P2 · bug · size S · AGENT+HUMAN-VERIFY
- **State today:** LIKELY DONE — commit 514269e on main: `ResetWorkAreasToFullCore` with `SPIF_SENDCHANGE` (`src/Bevel.Pal.Windows/WindowsDesktop.cs:123,188`); DIP/pixel math in `src/Bevel.Taskbar/TaskbarGeometry.cs` used at `TaskbarWindow.cs:498-499`; `WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE|WS_EX_TOPMOST` (`src/Bevel.Taskbar/WindowsTaskbarNative.cs:23`); `MonitorsChanged` subscription (`TaskbarWindow.cs:212`, handler :271). c7d18fa records "work area 0..1392, AppBar state 0" restored on quit.
- **Depends on:** none (verify in the same session as bevel-injy)
- **Files:** none to change
- **Decision:** no code change; close on the manual check. Scale != 100% is the case the unit tests cannot prove on real hardware, so the check must include it.
- **Test first:** existing `tests/Bevel.Taskbar.Tests/TaskbarGeometryTests.cs` (all tests in the class).
- **Steps:**
  - [ ] Run the Verify command; all `TaskbarGeometryTests` pass.
  - [ ] HUMAN, Windows box, display scale 100%: start Bevel. Bar's bottom edge touches the physical bottom of the primary monitor; no empty strip below it.
  - [ ] HUMAN: Settings > Display > Scale = 150%. Bar re-anchors to the bottom at full width within ~2 s.
  - [ ] HUMAN: change resolution (e.g. 1920x1080 -> 1280x720) and plug/unplug a second monitor. Bar re-anchors each time; a maximized Notepad does not cover the bar.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~TaskbarGeometryTests"` → all pass
- **Done when:** the three manual checks pass; then `bd close bevel-h0sr --reason "Fixed in 514269e; verified at 100%/150% scale + resolution/monitor change on NUCBOX <date>"`

### bevel-voqo — Tray live-mirror icons go stale / don't rebuild
- **Master-plan override (read first):** this card depends on bevel-ri8c. Do NOT create `TrayReconcile`; use `Bevel.Pal.Abstractions.SnapshotReconcile.Absent` from bevel-ri8c. Skip the `ShellCoreSystemTrayHost` step and its test — bevel-ri8c already did that hop. This card is ONLY the core hop: `MacOSSystemTrayHost.StreamLoopAsync` (list once per resubscribe, remove absent, then add) plus the HUMAN `killall ControlCenter` check. Name the test file `tests/Bevel.Pal.MacOS.Tests/TraySnapshotReconcileTests.cs` (new) if that project can host it, else keep it in `Bevel.Taskbar.Tests`.
- **Meta:** P2 · bug · size M · AGENT+HUMAN-VERIFY
- **State today:** PARTLY DONE. Three causes were filed here: (1) "every item is the same placeholder" = the self-test latch, fixed by bevel-p6g4 (`TrayServiceImpl.swift:846-857`); (2) diff ignored icon bytes, fixed (`TrayServiceImpl.swift:327` now compares `prev.iconPng != item.iconPng`); (3) STILL OPEN: a snapshot never removes. `MacOSSystemTrayHost.StreamLoopAsync` maps `Kind.Snapshot` to `ItemAdded` (`MacOSSystemTrayHost.cs:158-161`) and `ShellCoreSystemTrayHost.OnCoreEvent` replays `TraySnapshot` as `ItemAdded` (`ShellCoreSystemTrayHost.cs:53-57`), so an item that vanished while a stream was down (helper restart, Control Center restart changes every `pid:windowNumber` id) stays in `ShellCoreServer._trayById` and in `TrayViewModel.Items` forever. (4) frozen pixels while consolidated is bevel-7hf4.
- **Depends on:** none
- **Files:** `src/Bevel.Pal.Abstractions/TrayReconcile.cs` (new); `src/Bevel.App/ShellCore/ShellCoreSystemTrayHost.cs`; `src/Bevel.Pal.MacOS/MacOSSystemTrayHost.cs`; `tests/Bevel.Taskbar.Tests/TrayReconcileTests.cs` (new)
- **Decision:** a (re)connect snapshot is a full reconcile at BOTH hops. Add `public static class TrayReconcile { public static IReadOnlyList<TrayItem> Stale(IReadOnlyDictionary<string, TrayItem> known, IEnumerable<TrayItem> snapshot); }` (new) returning the known items whose `Id.Value` is absent from `snapshot`. Each host keeps `private readonly Dictionary<string, TrayItem> _known` (new, under a lock) updated on every add/update/remove. No proto change: the helper's snapshot burst has no end marker, so `MacOSSystemTrayHost` calls its existing `GetItemsAsync` once at the top of every `StreamLoopAsync` iteration and reconciles against that reply.
- **Test first:** `tests/Bevel.Taskbar.Tests/TrayReconcileTests.cs` (new): `Stale_returns_known_items_missing_from_the_snapshot`, `Stale_is_empty_when_the_snapshot_is_a_superset`; and `Snapshot_removes_items_the_core_no_longer_reports` - build a `ShellCoreSystemTrayHost`, feed `TrayItemAdded` for ids A,B then a `TraySnapshot` with only B (use the pattern in `tests/Bevel.Taskbar.Tests/ShellCoreIntegrationTests.cs`), assert `ItemRemoved` fired once for A.
- **Steps:**
  - [ ] Write the three tests; run Verify; they fail to compile (red).
  - [ ] Add `TrayReconcile.Stale`; two pure tests pass.
  - [ ] `ShellCoreSystemTrayHost`: maintain `_known`; in `case CoreEventKind.TraySnapshot` raise `ItemRemoved` for each `TrayReconcile.Stale(_known, e.TrayItems)` BEFORE the `ItemAdded` loop, then replace `_known`.
  - [ ] `MacOSSystemTrayHost.StreamLoopAsync`: before `tray.Changes(...)`, `var full = await GetItemsAsync(ct)`; raise `ItemRemoved` for `TrayReconcile.Stale(_known, full)`, `ItemAdded` for each of `full`; keep `_known` current in the `switch`.
  - [ ] Confirm `TrayViewModel.Remove` needs no change (it already handles `ItemRemoved`, `TrayViewModel.cs:235`).
  - [ ] HUMAN: with Bevel running, `killall ControlCenter`; within ~5 s the tray shows each item once (no duplicates, no ghosts).
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~TrayReconcile|FullyQualifiedName~TrayViewModelTests|FullyQualifiedName~ShellCoreIntegrationTests"` → all pass
- **Done when:** tests green and the `killall ControlCenter` check shows no ghosts; then `bd close bevel-voqo --reason "Snapshot is now a full reconcile at core and taskbar hops; capture-latch and byte-diff causes fixed earlier (p6g4, U4); frozen pixels tracked in bevel-7hf4"`

### bevel-yduf — [bug] Tray mirrors the right items but fails to render their icons
- **Meta:** P2 · bug · size S · AGENT+HUMAN-VERIFY
- **State today:** LIKELY DONE — both acceptance items exist in code. Placeholder: `src/Bevel.Taskbar/TrayIconCell.axaml:27-34` (ink-token frame + dot when `!HasIcon`), `TrayItemViewModel.HasIcon` (`TrayViewModel.cs:375`). Logs: helper `logCaptureStateIfChanged` (`TrayServiceImpl.swift:634-668`), taskbar `TrayViewModel.DescribeIconState` (`TrayViewModel.cs:285`) and the "did not decode" line (:483). Tests exist: `An_item_without_icon_bytes_is_a_placeholder_slot_not_a_blank`, `Undecodable_icon_bytes_fall_back_to_the_placeholder`, `Icon_state_summary_names_the_missing_grant_when_nothing_is_live` in `tests/Bevel.Taskbar.Tests/TrayViewModelTests.cs`. The multi-instance cause is closed by the shell lock (CODEMAP, bevel-wio0).
- **Depends on:** none
- **Files:** none to change
- **Decision:** no code change. Close on a clean single-instance TRAY-QA run.
- **Test first:** the three existing tests named above.
- **Steps:**
  - [ ] Run Verify; green.
  - [ ] HUMAN: run TRAY-QA (cluster summary). `pgrep -fl "Bevel.app/Contents/MacOS/Bevel"` shows exactly one launcher.
  - [ ] HUMAN: the log has `self-test ... grant=true capture=true → liveMirroring=true` and `captured N/M items live` with N > 0; the taskbar line reads `tray: M mirrored item(s) — N live capture(s) ...`.
  - [ ] HUMAN: visually, most tray cells show the real menu-bar glyph; any cell without pixels shows the hollow frame + dot, never an empty gap.
  - [ ] If N = 0 with `grant=false`: grant Screen Recording to BevelHelper in System Settings and repeat - that is a TCC state, not this bug.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~TrayViewModelTests"` → all pass
- **Done when:** TRAY-QA shows N > 0 live on one instance; then `bd close bevel-yduf --reason "Placeholder + capture-state logging shipped; clean single-instance run shows N/M live"`

### bevel-qcd9 — Tray self-test never re-verifies live capture once Screen Recording is granted mid-session
- **Meta:** P2 · bug · size S · AGENT+HUMAN-VERIFY
- **State today:** LIKELY DONE — merged to this branch (commits 07d1063, d077589). `TrayServiceImpl.ensureSelfTested()` re-arms on the rising edge (`TrayServiceImpl.swift:785-796`) via pure `static func selfTestShouldRun(granted:selfTestDone:grantPreviouslySeen:)` (:802); unit test `testSelfTestReArmsOnceOnTheUngrantedToGrantedTransition` (`native/helper-macos/Tests/BevelHelperTests/TrayServiceTests.swift:112`). Open question from the bead (are post-grant icons soft?) was never checked on device.
- **Depends on:** none
- **Files:** none to change
- **Decision:** no code change. The "soft icons" report is a separate hypothesis; if it reproduces after this fix, file a NEW bug rather than keeping this one open.
- **Test first:** existing Swift test above; C# `Limited_to_live_transition_upgrades_the_icon_to_its_full_native_resolution` in `tests/Bevel.Taskbar.Tests/TrayViewModelTests.cs`.
- **Steps:**
  - [ ] Run Verify; green.
  - [ ] HUMAN: `tccutil reset ScreenCapture pl.ikari.bevel.helper`, then start TRAY-QA. Log shows `self-test ... grant=false ... → liveMirroring=true` and `live capture OFF: Screen Recording not granted`.
  - [ ] HUMAN: without quitting, grant Screen Recording to BevelHelper in System Settings. Within one poll (2 s) a SECOND `self-test ... grant=true capture=true` line appears, followed by `captured N/M items live`.
  - [ ] HUMAN: compare one icon before quitting and after a restart. If visibly softer before the restart, `bd create` a new bug "tray icons soft until restart after mid-session grant" with both screenshots.
- **Verify:** `cd native/helper-macos && swift test --filter TrayServiceTests` → all pass
- **Done when:** the second self-test line is observed; then `bd close bevel-qcd9 --reason "Re-arm merged (07d1063/d077589); second self-test observed after mid-session grant"`

### bevel-m3.1 — M3-A: Tray discovery + limited-mode rendering (bevel-m3)
- **Meta:** P2 · feature · size S · AGENT
- **State today:** LIKELY DONE — everything in the description exists: proto `TrayService` (`proto/bevel.helper.v1.proto:72-84`), `TrayServiceImpl.enumerateTrayItems()` (`TrayServiceImpl.swift:519`), `MacOSSystemTrayHost` with capability `tray` (`MacOSSystemTrayHost.cs:196`), `TrayViewModel` + `TrayIconCell`. The bead's two "remaining" items are also done: the split-taskbar bridge is bevel-m3.1.1 (CLOSED; `ShellCoreSystemTrayHost.cs`) and live capture is bevel-m3.2 (CLOSED). The "all-in-one role" the note mentions no longer exists (CODEMAP: no `role=all`).
- **Depends on:** none
- **Files:** none
- **Decision:** close as done; its only unticked item ("live full-GUI verification") is the TRAY-QA run owned by bevel-yduf.
- **Test first:** `cd native/helper-macos && swift test --filter TrayServiceTests` (covers `testEnumerateReturnsWellFormedItems`, `testItemIDsAreUnique`, `testExcludesOwnProcess`).
- **Steps:**
  - [ ] Run Verify; green.
  - [ ] `bd show bevel-m3.1.1` and `bd show bevel-m3.2` both print CLOSED.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~Pulls_initial_snapshot_and_reconciles_events_in_place"` → 1 passed
- **Done when:** both checks pass; then `bd close bevel-m3.1 --reason "Discovery + limited mode + split bridge shipped (a5be8ce, 319caab, m3.1.1); live verification tracked in bevel-yduf"`

### bevel-m2.6 — M3: mirror macOS menu-bar NSStatusItems into tray + autohide LRU + iStat denylist
- **Meta:** P2 · task · size S · AGENT
- **State today:** DUPLICATE OF bevel-m3.1 (mirroring) + bevel-m3.4 (CLOSED: autohide LRU + denylist + overflow flyout). Evidence: denylist with iStat default at `TrayServiceImpl.swift:39-52`, tested by `testDenylistHidesIstatAndNoiseButKeepsRealItems`; overflow/LRU at `TrayViewModel.Reslice` / `PromoteToVisible` (`TrayViewModel.cs:180,307`), tested by `Overflow_caps_the_visible_strip_and_the_flyout_holds_the_rest` and `Using_an_overflowed_item_promotes_it_into_the_visible_set`.
- **Depends on:** bevel-m3.1 (close that first)
- **Files:** none
- **Decision:** close as duplicate; it is a pre-split placeholder whose three parts each shipped under an M3-A..D child.
- **Test first:** the two C# tests and one Swift test named above.
- **Steps:**
  - [ ] `bd show bevel-m3.4` prints CLOSED.
  - [ ] Run Verify; green.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~Overflow_caps|FullyQualifiedName~promotes_it_into_the_visible_set"` → 2 passed
- **Done when:** checks pass; then `bd close bevel-m2.6 --reason "Duplicate: mirroring = bevel-m3.1, LRU/denylist/overflow = bevel-m3.4 (closed)"`

### bevel-7hf4 — Hide mirrored icons from the macOS menu bar when Bevel owns them (Bartender-style)
- **Meta:** P2 · feature · size M · AGENT+HUMAN-VERIFY
- **State today:** PARTLY DONE. The hide SHIPS as the off-screen push: `MacMenuBarControl.SetHidden` (`src/Bevel.Pal.MacOS/MacMenuBarControl.cs:30`), wired at `src/Bevel.App/CompositionRoot.cs:196`, setting `TaskbarConsolidateMenuBar`, single-item reveal (bevel-6fin, CLOSED). The bead's NEXT step is half-built: `src/Bevel.Pal.MacOS/MacMenuBarOverlay.cs` is complete (`SetHidden(bool hidden, PalRect? strip)`, :55) but has ZERO callers (grep), and the bounds cache the note says lives in `MacOSSystemTrayHost` is gone. So consolidated items still sit off every display, which `docs/design/menubar-overlay-hide.md` says freezes their pixels.
- **Depends on:** bevel-voqo (reuses its `_known` id->`TrayItem` cache for the strip)
- **Files:** `src/Bevel.App/ShellCore/ShellCoreSystemTrayHost.cs`; `src/Bevel.App/CompositionRoot.cs`; `tests/Bevel.Taskbar.Tests/TrayStripTests.cs` (new)
- **Decision:** wire the overlay as an OPT-IN alternative, default stays the verified push. Change the seam to `Action<bool, PalRect?>? applyLocalHide`; add `internal static PalRect? StripOf(IEnumerable<TrayItem> items)` (new) = union of non-null `Bounds`. In `CompositionRoot` (macOS Taskbar branch) pick by env `BEVEL_TRAY_HIDE`: `overlay` -> `MacMenuBarOverlay.SetHidden(hidden, strip)`; anything else -> `MacMenuBarControl.SetHidden(hidden)`. Add ctor flag `bool hideMovesItems` (new); when false, `ForwardClickAsync` sends `park: false` (items never move under the overlay). Windows lambda ignores the strip. Reason: two things are unproven on device (AX press through the panel; covered items keep redrawing), so the default must not change until the owner's A/B.
- **Test first:** `TrayStripTests` (new): `StripOf_unions_item_bounds`, `StripOf_is_null_without_bounds`, `Forward_does_not_park_when_the_hide_does_not_move_items`.
- **Steps:**
  - [ ] Write tests (red), add `StripOf` + the two ctor changes, update BOTH `new ShellCoreSystemTrayHost(` call sites (`CompositionRoot.cs:194` and `:289`).
  - [ ] Confirm `SetNativeTrayHiddenAsync` still invokes the seam on the caller's thread (UI thread; `MacMenuBarOverlay` requires it).
  - [ ] HUMAN A/B: enable "consolidate menu bar"; run once normally, once with `BEVEL_TRAY_HIDE=overlay`. In each, watch a changing item (battery %, iStat, Now Playing) in Bevel's tray for 60 s and click three items.
  - [ ] HUMAN go/no-go: overlay wins if icons update AND clicks open menus. Then flip the default in a follow-up; if clicks fail, record it on the bead and keep push.
  - [ ] Delete the TEMP probe `dumpGeometry()` (`TrayServiceImpl.swift:589-613,623`) once the A/B is recorded.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~TrayStripTests|FullyQualifiedName~TrayViewModelTests"` → all pass
- **Done when:** A/B result is written on the bead and the default matches it; then `bd close bevel-7hf4 --reason "Consolidation ships; hide mechanism = <push|overlay> per on-device A/B <date>"`

### bevel-sd6n — macOS 26: ~25 status items expose no capturable pixels in ANY window (limited-mode fallback is correct)
- **Meta:** P2 · bug · size S · AGENT
- **State today:** LIKELY DONE (as an investigation) — the bead's own conclusion: six hypotheses disproven, two capture APIs agree the windows are empty, current fallback is correct. Code matches: blank captures keep the app icon (`TrayServiceImpl.swift:654-659`), counted as `blank=` in the always-logged summary (:667), and the "don't reintroduce SCK" note (:690-695). One loose end the bead names: `docs/design/menubar-management.md:158` still presents the capture path as 4/4.
- **Depends on:** none
- **Files:** `docs/design/menubar-management.md`
- **Decision:** close as a platform limitation. Do NOT implement "capture the bar region and crop": it breaks Req 5.3, bakes the wallpaper into each glyph (no transparency, so `TrayIconTint` cannot recolour), and under consolidation the items are not on screen to grab. If the owner wants it anyway that is a new HUMAN-decided bead.
- **Test first:** non-code: `grep -n "bevel-sd6n" docs/design/menubar-management.md` returns the new paragraph.
- **Steps:**
  - [ ] In `docs/design/menubar-management.md`, next to the "0/16 ... 4/4" result (~line 158), add: measured on a populated macOS 26 (25G229) bar the per-window path yields 61-63 live of 86-88 items; the remainder return correctly sized, fully transparent images from both CG and SCK; those items show the limited-mode app icon by design (bevel-sd6n).
  - [ ] Do not touch Swift code.
- **Verify:** `grep -c "bevel-sd6n" docs/design/menubar-management.md` → `1` or more
- **Done when:** doc updated; then `bd close bevel-sd6n --reason "Not a Bevel defect: WindowServer exposes no per-item pixels for ~25% of items; limited-mode fallback is the designed behaviour; doc corrected"`

### bevel-tned — On-device QA: tray adaptive tinting + multi-row/stacked-date
- **Meta:** P2 · task · size S · HUMAN
- **State today:** logic exists and is unit-tested: `src/Bevel.Taskbar/Model/TrayIconTint.cs` (`tests/Bevel.Taskbar.Tests/TrayIconTintTests.cs`), `src/Bevel.Taskbar/TrayRowsPanel.cs` (`TrayRowsPanelTests.cs`), height clamp `TrayViewModel.MaxIconHeight` (`TrayIconHeightCapTests.cs`). Nobody has looked at it with real captured icons. Theme names in the bead are stale: "Luna" = Bevel 2001 (`Blue2001VariantService`), "Win2000" = Industrial 1999.
- **Depends on:** bevel-yduf (needs live captures working)
- **Files:** none
- **Decision:** pure visual check by the owner during TRAY-QA; each mis-detection becomes its own bug with a screenshot.
- **Test first:** the three test classes above stay green.
- **Steps:**
  - [ ] HUMAN (a): in Settings > Appearance switch Bevel 2001 Blue, Silver, Black, Purple, then Industrial 1999. Monochrome glyphs (Wi-Fi, battery, Bluetooth) are dark on light bars and light on dark bars; colourful icons (Dropbox, 1Password, Slack) keep their colours.
  - [ ] HUMAN (b): drag the taskbar to 2 rows. Tray shows 2 rows; a wide item (iStat strip, clock-like text) is wider than a single glyph, not squeezed to a square.
  - [ ] HUMAN (c): on 2 rows the date sits UNDER the time; back on 1 row it is inline.
  - [ ] For every wrong icon: `bd create --type bug --title "tray tint: <app> <tinted|not tinted> on <theme>"` with a screenshot path.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~TrayIconTintTests|FullyQualifiedName~TrayRowsPanelTests|FullyQualifiedName~TrayIconHeightCapTests"` → all pass
- **Done when:** (a)(b)(c) confirmed and follow-ups filed; then `bd close bevel-tned --reason "On-device QA done <date>; follow-ups: <ids or none>"`

### bevel-m3 — M3 — Systray mirroring (Ice technique)
- **Meta:** P2 · epic · size L · AGENT+HUMAN-VERIFY
- **State today:** 4/7 children closed (m3.2, m3.3, m3.4, m3.5). Open: m3.1 and m2.6 (both closable now, see cards), m3.6 (cut, see card). The real remaining M3 work is in beads that are NOT linked as children.
- **Depends on:** bevel-m2 (closed). Blocks bevel-m4.
- **Files:** n/a
- **Decision:** "M3 done" = a clean single-instance run on macOS 26 mirrors the bar with live glyphs, never shows ghosts or blanks, and degrades visibly without the grant. Children in order: (1) bevel-m3.1 close, (2) bevel-m2.6 close, (3) bevel-voqo, (4) bevel-yduf, (5) bevel-qcd9, (6) bevel-sd6n, (7) bevel-tned, (8) bevel-7hf4. Link 3-8 with `bd dep add <id> bevel-m3 --type parent-child` (HUMAN/orchestrator runs it).
- **Cut from the milestone:** bevel-m3.6 (superseded by Strategy C), bevel-m3.3.1 and bevel-m3.2.1 (P3, tracked but not gating), bevel-ye27 (polish), bevel-7qdi/aogp/tzx (cleanup). Also cut two spec items from `docs/spec/09-engineering-plan.md` M3: the shared-memory frame ring (PNG-over-gRPC is what shipped and item icons are a few KB) and "SCStream" wording (capture is `LegacyWindowCapture`).
- **New beads to create (no child covers them):**
  - "Tray kill switch at runtime (ENG-030)" - today only env `BEVEL_TRAY_DISABLE` read at helper launch (`TrayServiceImpl.swift:71`); needs a setting that empties the tray without restart.
  - "Tray shows a 'connect tray' affordance when Screen Recording is denied (M3 acceptance 3)" - UNVERIFIED: no such control found in `TaskbarView.axaml`; only the Settings page shows the grant.
  - "Measure tray capture CPU over 24 h (M3 acceptance 4, <=3% of one core)" - every poll re-captures and PNG-encodes every item.
  - "Remove the bevel-m3 -> bevel-m4 blocker edge" - HUMAN: M4 code is independent of the tray and is already largely built.
- **Test first:** n/a (epic)
- **Steps:** n/a (epic) - execute the child cards in the order above.
- **Verify:** `bd show bevel-m3` → every listed child closed
- **Done when:** children 1-8 closed and TRAY-QA recorded; then `bd close bevel-m3 --reason "M3 exit: live mirroring verified on macOS 26; m3.6 cut"`

### bevel-whz — Wire AppleScript 'open'/odoc folder-open through IShellAutomation (INT-6)
- **Meta:** P2 · task · size M · AGENT+HUMAN-VERIFY
- **State today:** not done. `AppleEventInbound.Install()` registers ten handlers and none is `aevt`/`odoc` (`src/Bevel.Pal.MacOS/AppleEventInbound.cs:89-98`); `enum Verb { Reveal, Delete, Duplicate, Make, Move, SetSelection }` (:22); the class doc says odoc "stays with Avalonia's File activation" (:17-18), but nothing subscribes to `ActivationKind.File` anywhere in `src` (grep). `Bevel.sdef:22` declares `open` (`aevtodoc`) and `packaging/macos/Info.plist:60-72` registers folders. The bead's `ActivationKind.OpenFiles` does not exist in Avalonia 11.3 (kinds are `File`, `OpenUri`, `Reopen`, `Background`).
- **Depends on:** none
- **Files:** `src/Bevel.Pal.MacOS/AppleEventInbound.cs`; `src/Bevel.App/AppleEventBridge.cs`; `tests/Bevel.Taskbar.Tests/AppleEventBridgeTests.cs`
- **Decision:** claim odoc in OUR handler, not via Avalonia file activation. Reason: corpus form `open folder "Documents" of home` (`tests/compat/applescript-corpus/03-open-folder.applescript`) is an object specifier that only `ParseSpecifier` can resolve; the existing `ParseTargets` already handles both specifiers and file URLs (Finder "Open With"). Add `Verb.Open`; `Register("aevt", "odoc")`; `VerbFor` maps it; `DispatchAsync` adds `case AppleEventInbound.Verb.Open: foreach (var item in items) await automation.OpenAsync(item, new OpenOptions(), default);`.
- **Test first:** to make dispatch testable, change `DispatchAsync` from `private` to `internal` (`Bevel.App` already has `InternalsVisibleTo Bevel.Taskbar.Tests`). New test `Open_routes_each_target_through_OpenAsync` in `AppleEventBridgeTests.cs`: a recording `IShellAutomation` stub, an `AeRequest(Verb.Open, new[] {"/tmp"}, ...)`, assert one `OpenAsync` call with `VfsPath("file", "/tmp")`.
- **Steps:**
  - [ ] Write the test (red: `Verb.Open` missing).
  - [ ] Add `Open` to the enum, the `Register` line, the `VerbFor` arm, the `DispatchAsync` case; fix the class doc comment at `AppleEventInbound.cs:17-18`.
  - [ ] Run Verify; green.
  - [ ] HUMAN (packaged, signed app running): `osascript -e 'tell application "Bevel" to open folder "Documents" of home'` opens a Filer window on ~/Documents; `open -a Bevel ~/Downloads` does the same.
  - [ ] HUMAN: `osascript -e 'tell application "Bevel" to activate'` still does not quit or throw.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~AppleEventBridgeTests"` → all pass
- **Done when:** unit test green and both osascript checks open a Filer window; then `bd close bevel-whz --reason "odoc claimed in AppleEventInbound; routes to IShellAutomation.OpenAsync"`

### bevel-odf — Synchronous AE get/count can freeze the UI thread on huge enumeration
- **Meta:** P2 · task · size L · AGENT+HUMAN-VERIFY
- **State today:** PARTLY DONE. The interim cap shipped under bevel-o3sa (CLOSED): `AppleEventObjectResolver.UiThreadEnumerationCap = 4096` (`src/Bevel.Interop/AppleEvents/AppleEventObjectResolver.cs:27`) used by `AppleEventBridge.Answer` (`src/Bevel.App/AppleEventBridge.cs:75-78`). Still wrong: `Answer` blocks the UI thread with `.GetAwaiter().GetResult()` (:77-78, north star 1), `count` silently truncates at 4096, and - new finding - `WindowCount`/`Selection` read `FileManagerWindowRegistry` (:67-70) in the TASKBAR process, where no Filer window ever lives, so `count windows` returns 0 and `get selection` returns {} in the real multi-process shell.
- **Depends on:** none
- **Files:** `src/Bevel.Pal.MacOS/AppleEventQuery.cs`; `src/Bevel.Pal.MacOS/AppleEventInbound.cs`; `src/Bevel.App/AppleEventBridge.cs`; `tests/Bevel.Taskbar.Tests/AppleEventBridgeTests.cs`
- **Decision:** suspended reply. In `HandleQuery`: answer Version/Home/Desktop/Trash/StartupDisk inline as today; for `ResolvePaths`, `WindowCount`, `Selection` call `NSAppleEventManager` `suspendCurrentAppleEvent`, start `AsyncQueryHandler` (new: `public static Func<AeQuery, Task<AeResult?>>? AsyncQueryHandler`), and on completion post to the UI thread: `replyAppleEventForSuspensionID:` -> existing `WriteReply` -> `resumeWithSuspensionID:`. 10 s timeout writes error -1712 via existing `WriteErrorReply` and resumes (never leave an event suspended). Bridge side: `internal static Task<AeResult?> AnswerAsync(...)` (new) runs `resolver.ResolveAsync(..., enumerationCap: 0)` on the thread pool, and gets windows/selection from `automation.QueryAsync(AutomationQuery.Windows, ct)` / the selection query, which already reach live Filers through `SpawningShellSurface`.
- **Test first:** `AppleEventBridgeTests`: `AnswerAsync_count_is_not_capped` (temp dir with 5000 files -> `AeCount(5000)`), `AnswerAsync_window_count_comes_from_the_automation_query` (stub returns 2 windows -> `AeCount(2)`), `AnswerAsync_missing_folder_counts_zero`.
- **Steps:**
  - [ ] Write tests (red); implement `AnswerAsync`; keep `Answer` only for the five inline kinds.
  - [ ] Add the three ObjC sends next to the other `objc_msgSend` overloads in `AppleEventQuery.cs` (suspension id is a pointer; reuse `AppKitInterop.SendIntPtr` / `SendIntPtr_IntPtr` / `SendVoid_IntPtr`).
  - [ ] Wire `AppleEventInbound.AsyncQueryHandler` in `AppleEventBridge.Wire`; delete the blocking `GetResult()` calls.
  - [ ] `grep -n "GetAwaiter().GetResult()" src/Bevel.App/AppleEventBridge.cs` → no output.
  - [ ] HUMAN: with one Filer window open, `osascript -e 'tell application "Bevel" to count windows'` → 1; `... to count every item of startup disk` returns a number while the taskbar clock keeps ticking.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~AppleEventBridgeTests"` → all pass
- **Done when:** no sync wait remains and the two osascript checks pass; then `bd close bevel-odf --reason "AE queries use suspend/resume async reply; window/selection queries routed through IShellAutomation"`

### bevel-4qr — M4-E: AppleScript compat corpus harness (~40 snippets, exit criteria)
- **Meta:** P2 · task · size M · AGENT+HUMAN-VERIFY
- **State today:** PARTLY DONE. `tests/compat/applescript-corpus/` has 27 in-scope + 3 out-of-scope snippets, `README.md` and a working `run-corpus.sh` (commit 6c95abf). Gaps: the gate is ">=30 of 40" but only 27 in-scope scripts exist, so it cannot pass; `README.md` "Status" still says the runner is a stub and "12 snippets"; the runner only checks osascript's exit code, not the effect; the gate has never been run against the real app. Not linked as a child of bevel-m4.
- **Depends on:** bevel-whz (snippet 03 needs `open`), bevel-odf (snippets 09, 12 need correct window/selection answers)
- **Files:** `tests/compat/applescript-corpus/28-*.applescript` .. `40-*.applescript` (new, 13 files); `tests/compat/applescript-corpus/README.md`; `tests/compat/applescript-corpus/run-corpus.sh`
- **Decision:** grow to exactly 40 in-scope with single-statement forms of verbs Bevel already parses (no new verbs): `exists` on file/folder/missing path, `count every file|folder of`, `get name of`/`every item whose name contains|ends with`, `select` by specifier, `reveal` desktop/home, `open` by POSIX file and by specifier, `get desktop`, `get trash`. Keep exit-code classification (decide: effect comparison against Finder is post-v1, per README), but make the runner print the M4 verdict against 40.
- **Test first:** `bash tests/compat/applescript-corpus/run-corpus.sh` prints `in-scope: 40`.
- **Steps:**
  - [ ] Add 13 snippets, each one `tell application "Finder" to ...` line plus a `--` comment naming the real-world source pattern; touch only paths under `~/` or `/tmp`.
  - [ ] Rewrite README "Status": 40 in-scope + 3 out-of-scope, runner is real, gate must run against the real app.
  - [ ] HUMAN: start the signed app; `BEVEL_TARGET=Bevel bash tests/compat/applescript-corpus/run-corpus.sh`; paste the summary into the bead. Delete/duplicate snippets raise Bevel's consent dialog - click Allow.
  - [ ] HUMAN: confirm which process answers: `BEVEL_AE_TRACE=1` at launch must produce `~/bevel-ae.log` lines. UNVERIFIED risk: Apple Events addressed to `application "Bevel"` could reach the headless launcher (same bundle id) instead of the taskbar that installed the handlers; if the log stays empty, file "AE delivery reaches launcher, not taskbar" and stop.
  - [ ] HUMAN: `time osascript -e 'tell application "Bevel" to reveal POSIX file "/tmp"'` → real < 0.3 s (M4 acceptance 3).
- **Verify:** `ls tests/compat/applescript-corpus/*.applescript | grep -vc "/9"` → `40`
- **Done when:** real-app run prints `VERDICT: meets the M4 exit gate (>=30 pass)`; then `bd close bevel-4qr --reason "Corpus 40 in-scope; real-app run <N>/40 on <date>"`

### bevel-m4 — M4 — Apple Events tier 1
- **Meta:** P2 · epic · size L · AGENT+HUMAN-VERIFY
- **State today:** no children are linked, but the core is built and closed: bevel-376 (handlers + sdef + resolver), bevel-3i4 (get/count/exists/set replies), bevel-o3sa (cap), bevel-twq (destructive consent). Handlers: `src/Bevel.Pal.MacOS/AppleEventInbound.cs`; resolver `src/Bevel.Interop/AppleEvents/AppleEventObjectResolver.cs`; sdef `src/Bevel.App/Bevel.sdef`; corpus `tests/compat/applescript-corpus/`.
- **Depends on:** bevel-m3 (blocker edge; recommend removing - no code dependency). Blocks bevel-m5.
- **Files:** n/a
- **Decision:** "M4 done" = the three acceptance lines of `docs/spec/09-engineering-plan.md:131-134`, measured on the packaged app. Children in order: (1) bevel-whz, (2) bevel-odf, (3) bevel-4qr. Link them with `bd dep add <id> bevel-m4 --type parent-child`.
- **Cut from the milestone:** bevel-vs10 (`launch` is Bevel-only parity, not Finder compatibility), bevel-spike-shadow and bevel-spike-tier3 (research track, explicitly off the critical path in `09-engineering-plan.md:197-199`).
- **New beads to create:**
  - "Confirm Apple Events + bevel:// reach the taskbar process in the multi-process bundle" - the handlers live in the taskbar child (`App.axaml.cs:239-243`) while the launcher owns the bundle launch; nothing in the repo proves delivery. HUMAN check, 15 minutes, do it FIRST.
  - "M4 acceptance 2: 'Reveal in Finder' from Safari/VS Code lands in Bevel" - HUMAN decision: `docs/spec/08-os-interop.md` says v1 lets NSWorkspace reveals fall to real Finder; either strike acceptance 2 from the plan or schedule the file-viewer registration.
  - "AE handler faults reply errAEEventNotHandled" - `HandleEvent` swallows exceptions into a trace only (`AppleEventInbound.cs:131`), so a script sees silent success.
- **Test first:** n/a (epic)
- **Steps:** n/a (epic)
- **Verify:** `BEVEL_TARGET=Bevel bash tests/compat/applescript-corpus/run-corpus.sh` → `VERDICT: meets the M4 exit gate (>=30 pass)`
- **Done when:** whz, odf, 4qr closed and the delivery check recorded; then `bd close bevel-m4 --reason "M4 exit: >=30/40 on the real app, reveal round-trip <300 ms"`

### bevel-ye27 — macOS 26: resolve real names for Control-Center-owned tray items (most show as generic 'Item')
- **Meta:** P2 · task · size M · AGENT+HUMAN-VERIFY
- **State today:** PARTLY DONE. `resolveItem` handles app paths, bundle ids and sub-identifiers (`TrayServiceImpl.swift:889-923`), `friendlyName` strips `com.apple.menuextra.` (:927), and a generic "Item"/"Menu item" is replaced by `axTitle(at:)` (:465, called at :564-567). Two holes: `axTitle` hit-tests a screen POINT, so it returns nothing for items pushed off-screen by consolidation (most of them, per bevel-sd6n's geometry: 92 of 109 off-screen) and needs the Accessibility grant; and it runs on EVERY poll for every generic item (up to 0.3 s AX timeout each, no cache).
- **Depends on:** none
- **Files:** `native/helper-macos/Sources/BevelHelper/TrayServiceImpl.swift`; `native/helper-macos/Tests/BevelHelperTests/TrayServiceTests.swift`
- **Decision:** (1) cache resolved names by `item_id` so AX is asked once per item, not per poll: `private var nameCache: [String: String]` + lock (new), pruned to live ids each enumeration; a name learned while the item was on-screen survives the push off-screen. (2) add a pure, testable `static func displayName(windowName: String, axName: String?, cached: String?) -> String` (new) with precedence cached > non-empty AX > friendly window name. Do NOT add icon-signature matching or a hand-kept table of Apple names (unbounded maintenance).
- **Test first:** `TrayServiceTests.testDisplayNamePrefersCachedThenAXThenWindowName` (new): `("Item-0", "EXO", nil) -> "EXO"`; `("Item-0", nil, "EXO") -> "EXO"`; `("Item-0", nil, nil) -> "Item"`; `("com.apple.menuextra.wifi", nil, nil) -> "Wifi"`.
- **Steps:**
  - [ ] Write the test (red); add `displayName` and the cache; route lines 563-567 through them.
  - [ ] Call `axTitle` only when the cache misses AND `kCGWindowIsOnscreen` is true for that window.
  - [ ] Run Verify; green.
  - [ ] HUMAN (TRAY-QA with `BEVEL_DEBUG_TRAY=1`): the `enumerated N status items: ...` line lists fewer than 5 entries named `Item`/`Menu item`; hover three third-party icons and read real names.
- **Verify:** `cd native/helper-macos && swift test --filter TrayServiceTests` → all pass
- **Done when:** on-device count of generic names is under 5; then `bd close bevel-ye27 --reason "Names cached per item, AX asked once while on-screen; N generic remain on <date>"`

### bevel-sdss — IPermissionBroker.PermissionChanged is declared but never raised anywhere -- dead live-update contract
- **Meta:** P3 · bug · size M · AGENT
- **State today:** still true. Declared at `src/Bevel.Pal.Abstractions/Interfaces.cs:266`; implemented and never invoked in `MacOSPermissionBroker.cs:15`, `FakePal.cs:258` (`FakePermissionBroker`), `WindowsPal.cs:32`. The only live re-check is `OnboardingWindow.StartPermissionPoll` (`src/Bevel.Taskbar/OnboardingWindow.axaml.cs:249`), a 2 s `DispatcherTimer`. Related north-star gap to note, not fix here: the broker checks the CALLING process (`AXIsProcessTrusted`), while the grants that matter belong to BevelHelper (separate TCC identity, CODEMAP "Coupling").
- **Depends on:** none
- **Files:** `src/Bevel.Pal.MacOS/MacOSPermissionBroker.cs`; `src/Bevel.Pal.Fake/FakePal.cs`; `src/Bevel.Taskbar/OnboardingWindow.axaml.cs`; `tests/Bevel.Pal.MacOS.Tests/PermissionChangeTrackerTests.cs` (new)
- **Decision:** option (a), give the contract a producer (rule: wire it, don't hide it). TCC has no change notification, so the producer is one subscriber-gated timer inside the broker: custom `add`/`remove` accessors start a 2 s `System.Threading.Timer` on first subscriber and stop it on last. Pure core: `internal sealed class PermissionChangeTracker { IReadOnlyList<ShellPermission> Observe(PermissionState accessibility, PermissionState screenRecording); }` (new) returns the permissions whose state differs from the last call (first call returns none). `FakePermissionBroker` gets `public void SetState(ShellPermission p, PermissionState s)` (new) that raises the event. Windows stays silent (states are `NotApplicable`; say so in a one-line comment). `OnboardingWindow` drops its own timer and subscribes instead.
- **Test first:** `PermissionChangeTrackerTests`: `First_observation_reports_nothing`, `A_flip_reports_exactly_that_permission`, `No_change_reports_nothing`.
- **Steps:**
  - [ ] Write tests (red); add `PermissionChangeTracker`; green.
  - [ ] Add the gated timer + accessors to `MacOSPermissionBroker`; raise `PermissionChanged` per returned permission.
  - [ ] `OnboardingWindow`: replace `_permPollTimer` with a `PermissionChanged` subscription (handler must `Dispatcher.UIThread.Post`), keep the immediate first `PollPermissionAsync()`, unsubscribe on close.
  - [ ] `grep -rn "PermissionChanged?.Invoke" src` → at least 2 hits.
- **Verify:** `dotnet test tests/Bevel.Pal.MacOS.Tests/Bevel.Pal.MacOS.Tests.csproj --filter "FullyQualifiedName~PermissionChangeTrackerTests"` → 3 passed
- **Done when:** event has a producer and Onboarding has no private timer; then `bd close bevel-sdss --reason "PermissionChanged now raised by a subscriber-gated diff in MacOSPermissionBroker; Onboarding consumes it"`

### bevel-soon — CFNumberCreate declares CFNumberType as int, but it is CFIndex (pointer-sized)
- **Meta:** P3 · bug · size S · AGENT
- **State today:** still true: `public static extern IntPtr CFNumberCreate(IntPtr allocator, int type, ref int value);` at `src/Bevel.Pal.MacOS/CoreFoundationInterop.cs:108` (flagged in the comment above it, :100-106). One caller: `src/Bevel.Pal.MacOS/MacOSThumbnailProvider.cs:226`, with `private const int kCFNumberIntType = 9;` (:264).
- **Depends on:** none (do before bevel-2kdu so the migration starts from a correct signature)
- **Files:** `src/Bevel.Pal.MacOS/CoreFoundationInterop.cs`; `src/Bevel.Pal.MacOS/MacOSThumbnailProvider.cs`
- **Decision:** `CFNumberCreate(IntPtr allocator, nint type, ref int value)`; change the constant to `private const nint kCFNumberIntType = 9;`. `ref int value` stays (kCFNumberIntType means a C `int`). Matches the repo's stated mapping `CFIndex -> nint`.
- **Test first:** existing `tests/Bevel.Pal.MacOS.Tests/MacOSThumbnailProviderTests.cs` (pixel-content tests build the options dictionary through this call) and `InteropConventionTests.cs`.
- **Steps:**
  - [ ] Change the two declarations; delete the "tracked separately" comment block at `CoreFoundationInterop.cs:100-106` and replace with one line: `// CFNumberType is CFIndex (pointer-sized) → nint.`
  - [ ] `grep -rn "CFNumberCreate(" src` → exactly one extern and one caller, both compile.
  - [ ] Run Verify.
- **Verify:** `dotnet test tests/Bevel.Pal.MacOS.Tests/Bevel.Pal.MacOS.Tests.csproj --filter "FullyQualifiedName~MacOSThumbnailProviderTests|FullyQualifiedName~InteropConventionTests"` → all pass
- **Done when:** tests green; then `bd close bevel-soon --reason "CFNumberType widened to nint; thumbnail pixel tests green"`

### bevel-m3.2.1 — M3-B.1: throttled live-capture refresh cadence (Req 5.4) (bevel-m3.2)
- **Meta:** P3 · task · size M · AGENT+HUMAN-VERIFY
- **State today:** PARTLY DONE. The bead's main complaint is fixed: the diff now emits UPDATE when pixels change (`TrayServiceImpl.swift:327`, `prev.iconPng != item.iconPng`), and cadence adapts (900 ms consolidated / 2 s idle, :311). Remaining waste: every poll runs, per item, `LegacyWindowCapture.image` + `trimTransparent` full-pixel scan + PNG encode (`pngFromCGImage`, :704) even when nothing changed, then compares whole PNG blobs. The doc comment on `signature()` (:768-770) is now false ("EXCLUDES the icon bytes ... only identity/geometry changes count").
- **Depends on:** none (bevel-7hf4 decides whether consolidated items ever change at all)
- **Files:** `native/helper-macos/Sources/BevelHelper/TrayServiceImpl.swift`; `native/helper-macos/Tests/BevelHelperTests/TrayServiceTests.swift`
- **Decision:** hash raw pixels before encoding. Keep `private var captureCache: [String: (hash: UInt64, png: Data)]` + lock (new), keyed by `item_id`, pruned to live ids. Add `static func pixelHash(_ image: CGImage) -> UInt64` (new; FNV-1a over `image.dataProvider?.data` bytes, plus width/height). In `enumerateWithCapture`: if the hash equals the cached one, reuse the cached PNG and skip `pngFromCGImage`. Keep the poll (CGWindowList has no change notification - an accepted exception to "push, not poll", confined to the helper). Do not change intervals.
- **Test first:** `TrayServiceTests.testPixelHashIsStableAndContentSensitive` (new): two 4x4 `CGImage`s built from identical byte arrays hash equal; flipping one byte changes the hash; same bytes at 2x8 hash differently.
- **Steps:**
  - [ ] Write the test (red); add `pixelHash` and the cache; green.
  - [ ] Rewrite the `signature()` doc comment to state what it covers today and that pixel change is detected separately at the diff site.
  - [ ] HUMAN: run 10 minutes consolidated and idle; `top -pid $(pgrep -x BevelHelper) -l 5 -stats cpu` averages under 3%.
- **Verify:** `cd native/helper-macos && swift test --filter TrayServiceTests` → all pass
- **Done when:** unchanged items skip PNG encoding and CPU is measured; then `bd close bevel-m3.2.1 --reason "UPDATE-on-pixel-change already shipped (U4); unchanged frames now skip encode; helper CPU <N>%"`

### bevel-aogp — Remove dead trayGlyphLayout sizing code + its test (native-size rendering replaced it)
- **Meta:** P3 · chore · size S · AGENT
- **State today:** still present and unused by production code: `struct TrayGlyphLayout` (`TrayServiceImpl.swift:717-723`), `static func trayGlyphLayout` (:731-741), test `testTrayGlyphSizing` (`TrayServiceTests.swift:159-185`). Two comments reference it as a "seam pattern" (`TrayServiceImpl.swift:800`, `TrayServiceTests.swift:111`). Note `trimTransparent` (:746) is NOT dead - `pngFromCGImage` uses it as the blank-frame guard.
- **Depends on:** none
- **Files:** `native/helper-macos/Sources/BevelHelper/TrayServiceImpl.swift`; `native/helper-macos/Tests/BevelHelperTests/TrayServiceTests.swift`
- **Decision:** delete the struct, the function, its doc comments (:709-730) and the test; reword the two "same seam pattern as trayGlyphLayout" comments to "a pure static seam". Keep `trimTransparent` and `pngFromCGImage` untouched.
- **Test first:** non-code: the grep in Verify.
- **Steps:**
  - [ ] Delete lines 709-741 of `TrayServiceImpl.swift` (re-read first; line numbers drift).
  - [ ] Delete `testTrayGlyphSizing` and fix the two comments.
  - [ ] `cd native/helper-macos && swift test --filter TrayServiceTests` passes.
- **Verify:** `grep -rn "rayGlyphLayout\|TrayGlyphSizing" native/helper-macos/Sources native/helper-macos/Tests` → no output
- **Done when:** grep empty and Swift tests green; then `bd close bevel-aogp --reason "Dead sizing code + test removed"`

### bevel-tzx — Share one ScreenCaptureKit capture helper (tray vs window)
- **Meta:** P3 · task · size S · AGENT
- **State today:** PARTLY DONE / premise stale. `TrayServiceImpl.captureWindow` no longer exists; the tray does not use ScreenCaptureKit at all (`TrayServiceImpl.swift:690-695`). The only SCK flow left is `WindowServiceImpl.captureWindowThumbnail` (`WindowServiceImpl.swift:485-524`). What IS duplicated now: two dlsym'd legacy-CG captures - `LegacyWindowCapture.image(windowID:)` (`LegacyWindowCapture.swift:29`, `CGWindowListCreateImageFromArray`) vs `WindowServiceImpl.cgWindowListThumbnail` (:530, `CGWindowListCreateImage`) - and two `appIconPNG`/`cachedAppIconPNG` pairs (`TrayServiceImpl.swift:944-961`, `WindowServiceImpl.swift:958-975`).
- **Depends on:** none
- **Files:** `native/helper-macos/Sources/BevelHelper/WindowServiceImpl.swift`; `native/helper-macos/Sources/BevelHelper/LegacyWindowCapture.swift`
- **Decision:** route the hover thumbnail's fast path through `LegacyWindowCapture.image(windowID:)` and delete the private dlsym in `cgWindowListThumbnail`, keeping only its downscale-and-encode half as `static func png(from image: CGImage, maxW: Double, maxH: Double) -> Data?` (new, in `LegacyWindowCapture.swift`). Leave the SCK fallback as is (the tray will never share it). Do NOT merge the two app-icon caches: they intentionally render different sizes (tray 32pt x scale, window 64 px). Do NOT gate hover previews on the tray self-test: different pipeline now.
- **Test first:** `native/helper-macos/Tests/BevelHelperTests/WindowServiceTests.swift`: add `testLegacyThumbnailEncoderFitsWithinBounds` (new) - feed a synthetic 400x200 `CGImage` to `LegacyWindowCapture.png(from:maxW:240,maxH:160)`, decode, assert width <= 240 and height <= 160 and aspect 2:1.
- **Steps:**
  - [ ] Write the test (red); move the encode half; point `captureWindowThumbnail` at `LegacyWindowCapture.image` + `png(from:)`.
  - [ ] `grep -n "dlsym" native/helper-macos/Sources/BevelHelper/WindowServiceImpl.swift` → no `CGWindowListCreateImage` line.
  - [ ] Run Verify.
- **Verify:** `cd native/helper-macos && swift test --filter WindowServiceTests` → all pass
- **Done when:** one legacy capture entry point remains; then `bd close bevel-tzx --reason "Tray left SCK in p6g4; legacy-CG capture unified in LegacyWindowCapture"`

### bevel-7qdi — Intermittent: tray icons render too small (not reproduced)
- **Meta:** P3 · task · size S · AGENT+HUMAN-VERIFY
- **State today:** STALE. Everything the bead measured is gone: there is no `CellHeight` in the tray any more (grep finds only `Bevel.Metric.IconGridCellHeight`), captures are no longer normalised to 32 px cells (native-size encode, `TrayServiceImpl.swift:704`), and the cited diagnostics commit 4a6a323 is an unrelated change ("app-centric button menu"). Size today = item bounds x slider scale, clamped by `TrayViewModel.MaxIconHeight(rows)` (`TrayViewModel.cs:70`) via `TrayItemViewModel.Fit` (:398), covered by `tests/Bevel.Taskbar.Tests/TrayIconHeightCapTests.cs`.
- **Depends on:** bevel-tned (same on-device session)
- **Files:** none
- **Decision:** close as stale unless the TRAY-QA session shows shrunken icons. The one plausible modern trigger is `Update` receiving tiny `Bounds` (it accepts any width/height >= 1, `TrayViewModel.cs:441`); if seen, file a fresh bug with the logged bounds instead of reviving this one.
- **Test first:** existing `TrayIconHeightCapTests`.
- **Steps:**
  - [ ] Run Verify; green.
  - [ ] HUMAN during TRAY-QA: change display resolution once and move the taskbar between 1 and 2 rows. Icons stay menu-bar sized (about 22 pt tall at slider 16).
  - [ ] If any icon shrinks: screenshot + the `[BEVEL-TRAY] enumerated` line, `bd create --type bug --title "tray icon shrinks: <app>"`.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~TrayIconHeightCapTests"` → all pass
- **Done when:** no shrink observed; then `bd close bevel-7qdi --reason "Stale: sizing path replaced by native sizing + MaxIconHeight clamp; not reproduced on <date>"`

### bevel-vs10 — Apple Events: add a 'launch' verb (INT-3 parity with bevelctl/bevel://)
- **Meta:** P3 (bead says P2; cut from M4) · task · size M · AGENT+HUMAN-VERIFY
- **State today:** not done. `AppleEventInbound.Verb` has no `Launch` (`AppleEventInbound.cs:22`); `AppleEventBridge.DispatchAsync` has no launch case; `Bevel.sdef` has no launch command. The other two transports have it: `BevelVerb.Launch` -> `AutomationCommandRouter.LaunchAsync` (`src/Bevel.Interop/Cli/AutomationCommandRouter.cs:118`) with the path-rejection guard `internal static bool LooksLikePath` (:135).
- **Depends on:** bevel-whz (same files; do after to avoid conflicting edits)
- **Files:** `src/Bevel.App/Bevel.sdef`; `src/Bevel.Pal.MacOS/AppleEventInbound.cs`; `src/Bevel.App/AppleEventBridge.cs`; `src/Bevel.Interop/Cli/AutomationCommandRouter.cs`; `tests/Bevel.Taskbar.Tests/AppleEventBridgeTests.cs`
- **Decision:** a Bevel-suite command taking a bundle-id STRING, event code `BevlLnch` (new; class `Bevl`, id `Lnch`): `tell application "Bevel" to launch program "com.apple.TextEdit"`. Name the sdef command `launch program` (plain `launch` is a reserved AppleScript application verb and never reaches the app). Carry it as `AeRequest(Verb.Launch, ..., Name: bundleId)`. Reuse the SAME security rule: make `LooksLikePath` `public` and refuse paths in the bridge before calling `automation.LaunchAsync(bundleId, ct)`.
- **Test first:** `AppleEventBridgeTests`: `Launch_passes_the_bundle_id_to_LaunchAsync`, `Launch_refuses_a_filesystem_path` (`"/Applications/X.app"` -> zero `LaunchAsync` calls).
- **Steps:**
  - [ ] Tests (red); add `Verb.Launch`, `Register("Bevl", "Lnch")`, a `BuildLaunch` reading the direct object's `stringValue` (pattern: `BuildMake`'s `NSStr(...)`), the dispatch case.
  - [ ] Add the `<command name="launch program" code="BevlLnch">` with a text direct-parameter to the Bevel Suite in `Bevel.sdef`.
  - [ ] HUMAN: rebuild + sign; `osascript -e 'tell application "Bevel" to launch program "com.apple.TextEdit"'` opens TextEdit; with `"/Applications/TextEdit.app"` nothing launches.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~AppleEventBridgeTests"` → all pass
- **Done when:** both osascript checks behave as stated; then `bd close bevel-vs10 --reason "AE 'launch program' routes to IShellAutomation.LaunchAsync with the bundle-id-only guard"`

### bevel-2kdu — Migrate macOS P/Invoke to source-generated [LibraryImport]
- **Meta:** P3 · task · size L · AGENT
- **State today:** not started: 0 `LibraryImport` in `src`; `DllImport` attribute lines per file: `AppKitInterop.cs` 27, `CoreGraphicsInterop.cs` 23, `CoreFoundationInterop.cs` 18, `AppleEventInbound.cs` 7, `AccessibilityInterop.cs` 6, `AppleEventQuery.cs` 5, `TabFaviconStore.cs` 3, `ImageIOInterop.cs` 2, `MacOSThumbnailProvider.cs` 1 (counts include comment mentions; re-measure). `AllowUnsafeBlocks` is already on (`Bevel.Pal.MacOS.csproj:10`). Guard: `tests/Bevel.Pal.MacOS.Tests/InteropConventionTests.cs` reflects over `DllImport` metadata.
- **Depends on:** bevel-soon (fix the signature first)
- **Files:** the nine files above; `tests/Bevel.Pal.MacOS.Tests/InteropConventionTests.cs`
- **Decision:** one commit per interop class, in this order (smallest blast radius first): `ImageIOInterop`, `CoreFoundationInterop`, `CoreGraphicsInterop`, `AccessibilityInterop`, `AppKitInterop`, then the AppleEvent files. Rules: class becomes `static partial`; `[LibraryImport(Frameworks.X)]` + `static partial` method; every `bool` param/return gets explicit `[MarshalAs(UnmanagedType.U1)]` (CF/AX Boolean) or `I1` (ObjC BOOL) - never bare; `string` params get `StringMarshalling = StringMarshalling.Utf8` (today's `objc_allocateClassPair`/`class_addMethod` rely on ANSI default); `objc_msgSend` overloads keep `EntryPoint = "objc_msgSend"`. Update the convention tests FIRST so they recognise `LibraryImportAttribute` as well, otherwise migrated symbols silently leave the guard.
- **Test first:** in `InteropConventionTests.cs` add `Guard_sees_library_import_declarations` (new): assert the scanner's declaration count for `Bevel.Pal.MacOS` is >= 90 (it is ~96 today) so a migration that hides methods from the guard fails.
- **Steps:**
  - [ ] Extend the test's reflection + source regex to match both attributes; add the count test; green on unmigrated code.
  - [ ] Migrate `ImageIOInterop`; run Verify; commit. Repeat per class in the order above.
  - [ ] After each class: `MacOSThumbnailProviderTests` and `AccessibilityGateTests` must be green (the bead's stated gate).
  - [ ] End: `grep -rn "\[DllImport" src/Bevel.Pal.MacOS` → no output.
- **Verify:** `dotnet test tests/Bevel.Pal.MacOS.Tests/Bevel.Pal.MacOS.Tests.csproj` → all pass, 0 warnings SYSLIB1054
- **Done when:** grep empty and the full MacOS test project is green; then `bd close bevel-2kdu --reason "Bevel.Pal.MacOS fully on LibraryImport; convention guard covers both attributes"`

### bevel-p5bx — Windows P/Invoke has the same silo duplication macOS had
- **Meta:** P3 · task · size L · AGENT+HUMAN-VERIFY
- **State today:** still true, and larger than the bead's count. Measured now by grep of `static extern`: about 26 symbols declared more than once, about 34 redundant declarations (e.g. `ShowWindow` x5; `CloseHandle`, `EnumWindows`, `IsWindowVisible`, `DispatchMessage`, `TranslateMessage` x3). Files: `WindowsWindowManager.cs`, `WindowsDesktop.cs`, `WindowsAppEnvironment.cs`, `WindowsFileStack.cs`, `WindowsShellSession.cs`, `WindowsIconBits.cs`, `WindowsSystemTrayHost.cs`, `WindowsAudio.cs`, plus `src/Bevel.Taskbar/WindowsTaskbarNative.cs` (6, outside the PAL project). `WindowsSystemTrayHost.cs:88` documents the old rule "private to this class, no shared Interop file".
- **Depends on:** none (a Windows CI run or box is required to trust it)
- **Files:** `src/Bevel.Pal.Windows/Interop/User32.cs`, `Kernel32.cs`, `Shell32.cs`, `Dwmapi.cs`, `Gdi32.cs`, `WinLibs.cs` (all new); the nine files above; `tests/Bevel.Pal.Windows.Tests/WindowsInteropConventionTests.cs` (new)
- **Decision:** mirror the macOS shape: one `internal static class` per DLL, library names as constants in `WinLibs` (new), one declaration per symbol with the WIDEST correct signature. Audit rule differs from macOS: a bare `bool` return IS correct for Win32 `BOOL` - keep `[return: MarshalAs(UnmanagedType.Bool)]` where present, never change to `U1`. Leave `WindowsTaskbarNative.cs` in `Bevel.Taskbar` (it cannot reference the PAL's internals) and list it as the one sanctioned exception in the test. Write the guard as a SOURCE scan (regex over `*.cs`), not reflection, so it runs on macOS CI too.
- **Test first:** `WindowsInteropConventionTests` (new): `No_native_entry_point_is_declared_in_two_files` (regex `static extern \S+ (\w+)\(` over `src/Bevel.Pal.Windows/**/*.cs`, group by name + `EntryPoint`), `No_dll_import_names_a_library_literal_outside_WinLibs`. Both fail today - that is the measurement the bead asks for; record the printed duplicate list on the bead.
- **Steps:**
  - [ ] Add the two tests; run; paste the failing list into `bd update bevel-p5bx --notes` (orchestrator runs bd).
  - [ ] Create the Interop files; move declarations one DLL at a time; replace call sites with `using static`.
  - [ ] Overloads that differ only by parameter shape (e.g. `SendMessageTimeout`) keep distinct C# names via `EntryPoint`.
  - [ ] Push; the Windows CI job must be green (runtime smoke + `Bevel.Pal.Windows.Tests`).
  - [ ] HUMAN: deploy to the Windows box; taskbar shows window buttons with icons, Start menu opens, `bevelctl quit` restores the native taskbar.
- **Verify:** `dotnet test tests/Bevel.Pal.Windows.Tests/Bevel.Pal.Windows.Tests.csproj --filter "FullyQualifiedName~WindowsInteropConventionTests"` → 2 passed
- **Done when:** guard green on macOS + Windows CI and the box smoke passes; then `bd close bevel-p5bx --reason "Windows P/Invoke consolidated per DLL; guard test added; <N> duplicates removed"`

### bevel-m3.3.1 — Tray click-forwarding: custom status items unresponsive on macOS 26 (bevel-m3.3)
- **Meta:** P3 · bug · size L · AGENT+HUMAN-VERIFY
- **State today:** still open; code has moved on since the bead. `forwardClick` tries `pressViaAX` (hit-test at the item's centre, `AXPress`/`AXShowMenu`, up to two ancestors; `TrayServiceImpl.swift:431-459`) then `clickViaCGEvent` (HID tap + cursor warp, :481). NEW since the bead: `itemEvent(...)` (:236) builds window/pid-addressed events posted to the SESSION tap - Ice's delivery, which the bead's option (b) never tried for clicks; it is used only for the Cmd-drag move (`moveItemTargeted`, :216), and its `click:` parameter is always passed `false`.
- **Depends on:** none
- **Files:** `native/helper-macos/Sources/BevelHelper/TrayServiceImpl.swift`; `native/helper-macos/Tests/BevelHelperTests/TrayServiceTests.swift`
- **Decision:** add a third rung between AX and the HID click: `private func clickViaTargetedEvent(windowID: Int, pid: pid_t, at point: CGPoint, button:, modifiers:) -> Bool` (new) = `permitRemoteEvents()`, then `itemEvent(.leftMouseDown, ..., cmd: false, click: true)` and the matching up, each `post(tap: .cgSessionEventTap)` + `postToPid`, 60 ms apart (extend `itemEvent` with a button parameter for right-click). `AXPress` returning `.success` while doing nothing cannot be detected, so order per item is decided by a small allowlist: `private let targetedClickFirst: [String] = ["betterdisplay"]` + env `BEVEL_TRAY_TARGETED_CLICK` (comma list, same shape as `BEVEL_TRAY_DENY`). Do not attempt AX child matching by order (the bead already showed positions do not align).
- **Test first:** `TrayServiceTests.testTargetedClickFirstMatchesByIdentitySubstring` (new) for a pure `func prefersTargetedClick(windowName: String, ownerName: String) -> Bool` (new), modelled on `isDenied`.
- **Steps:**
  - [ ] Test (red); add the allowlist + `prefersTargetedClick`; green.
  - [ ] Add `clickViaTargetedEvent`; in `forwardClick`, when `prefersTargetedClick` is true try it first, else keep today's order and use it as rung 2.
  - [ ] HUMAN (needs Accessibility granted to BevelHelper, BetterDisplay installed, NOT consolidated): click BetterDisplay's icon in Bevel's tray → its menu opens at the top of the screen. Repeat for one standard NSMenu item (regression).
  - [ ] HUMAN: if BetterDisplay still does not open, record that on the bead and close as "won't fix, spec 5.6 fragile" - do not iterate further.
- **Verify:** `cd native/helper-macos && swift test --filter TrayServiceTests` → all pass
- **Done when:** on-device result recorded either way; then `bd close bevel-m3.3.1 --reason "<Targeted session-tap click opens custom items | Not achievable on macOS 26; documented>"`

### bevel-2t7u — Windows: mirror third-party notification-area icons into the tray
- **Meta:** P2 · feature · size L · HUMAN
- **State today:** BLOCKED by a measured platform fact, recorded in commit c7d18fa: a working UI-Automation mirror exists as `docs/spike/WindowsTrayMirror.cs.spike` (368 lines) but "Explorer unloads the tray's XAML once its bar is out of the way, so the UIA tree is empty in the only configuration Bevel runs in, and Win11 has no legacy ToolbarWindow32 left". `WindowsSystemTrayHost.GetItemsAsync` returns empty (`src/Bevel.Pal.Windows/WindowsSystemTrayHost.cs:64`); push seams `RaiseAdded/RaiseUpdated/RaiseRemoved` (:70-72) are ready. The bead's "preferred" approach 1 (own `Shell_TrayWnd`) only receives `Shell_NotifyIcon` traffic when Explorer's taskbar is NOT running.
- **Depends on:** OUTSIDE cluster: a decision + working "Bevel is the Winlogon shell" mode (`src/Bevel.Pal.Windows/WindowsShellSession.cs`, bevel-ncfp.5 spike)
- **Files:** `src/Bevel.Pal.Windows/WindowsTrayNotifyHost.cs` (new); `src/Bevel.Pal.Windows/WindowsSystemTrayHost.cs`; `tests/Bevel.Pal.Windows.Tests/WindowsTrayNotifyHostTests.cs` (new)
- **Decision:** HUMAN first: is "Explorer not running" a supported v1 configuration on Windows? If NO - close this bead as "not possible while coexisting with Explorer" and keep the honest empty tray. If YES - implement approach 1 only: a hidden window of class `Shell_TrayWnd` created when `FindWindow("Shell_TrayWnd")` finds none, broadcast `RegisterWindowMessage("TaskbarCreated")`, handle `WM_COPYDATA` with `dwData == 1` carrying `NOTIFYICONDATA` (`NIM_ADD/MODIFY/DELETE`), convert `hIcon` with `WindowsIconBits` (duplicate the handle - it is borrowed), raise through the existing `Raise*` seams; `ForwardClickAsync` posts the item's `uCallbackMessage` to its `hWnd`. Never ship the `ReadProcessMemory` path.
- **Test first:** `WindowsTrayNotifyHostTests` (new): pure parser `internal static bool TryParseNotifyIconData(ReadOnlySpan<byte> copyData, out NotifyIconMessage msg)` (new) - feed hand-built 32-bit and 64-bit `NOTIFYICONDATAW` buffers for `NIM_ADD`, assert hWnd/uID/callback/tip decode.
- **Steps:**
  - [ ] HUMAN: answer the product question above on the bead.
  - [ ] (if YES) write the parser tests + parser (runs on any OS).
  - [ ] (if YES) add the window + message pump on the existing STA/pump thread; wire in `WindowsSystemTrayHost`; update the class doc and `Capabilities` notes.
  - [ ] HUMAN on the Windows box in shell-replacement mode: start an app with a tray icon (e.g. Tailscale); it appears in Bevel's tray within 2 s, left/right click opens its menu, quitting the app removes it.
- **Verify:** `dotnet test tests/Bevel.Pal.Windows.Tests/Bevel.Pal.Windows.Tests.csproj --filter "FullyQualifiedName~WindowsTrayNotifyHostTests"` → all pass
- **Done when:** the product answer is recorded and (if YES) the box check passes; then `bd close bevel-2t7u --reason "<Shell_TrayWnd owner ships in shell mode | Closed: not possible alongside Explorer (c7d18fa measurements)>"`

### bevel-m3.6 — M3-F: Menu-bar reclaim (consent-gated, feature-flagged) (bevel-m3)
- **Meta:** P2 · feature · size S · HUMAN
- **State today:** SUPERSEDED. The bead's two mechanisms were replaced by Strategy C (`docs/plans/2026-08-10-001-feat-menubar-tray-strategy-c-plan.md`): (2) "Ice-style per-item relocation by synthesized Cmd-drag" shipped as bevel-6fin (`moveItemTargeted`, `TrayServiceImpl.swift:216`) plus the control-item hide (bevel-qpir, `MacMenuBarControl.cs`), user-gated by the `TaskbarConsolidateMenuBar` setting (`ConsolidateCheck`, `OnboardingWindow.axaml:411`); (1) "system-wide auto-hide" has no code (grep `_HIHideMenuBar` in `src`: none) and is no longer wanted - with consolidation the native bar's right side is empty rather than hidden.
- **Depends on:** bevel-7hf4 (the live bead for this feature)
- **Files:** none
- **Decision:** cut from M3 and close as superseded by bevel-7hf4. System-wide menu-bar auto-hide writes a global user preference Bevel would have to restore on crash (same class of risk as the Dock claim); do not build it for v1. HUMAN confirms the cut because it removes a spec requirement (Req 5.6/5.7).
- **Test first:** non-code: `grep -rn "_HIHideMenuBar" src` → no output (nothing to remove).
- **Steps:**
  - [ ] HUMAN: confirm "consolidation replaces reclaim for v1".
  - [ ] Add one line to `docs/spec/02-macos-platform.md` §5.4: superseded by Strategy C (plan 2026-08-10-001); system-wide auto-hide deferred post-v1.
- **Verify:** `bd show bevel-7hf4` → open or closed with the consolidation result recorded
- **Done when:** owner confirms; then `bd close bevel-m3.6 --reason "Superseded by Strategy C (bevel-7hf4/6fin/qpir); system-wide auto-hide deferred post-v1"`

### bevel-spike-tier3 — SPIKE: tier-3 headless-Finder mirroring (measure window-escape rate)
- **Meta:** P3 · spike · size L · HUMAN
- **State today:** not started; no description on the bead. Scope is defined in `docs/spec/08-os-interop.md:50` (real Finder kept windowless; helper `AXObserver` on Finder's `kAXWindowCreatedNotification`; read target, close the Finder window, re-issue the reveal into Bevel) and budgeted at 1 engineer-week in `docs/spec/09-engineering-plan.md:199`. Spec position: Tier 3 MUST NOT ship in v1 (INT-5); the spike only gates a post-v1 4-6 week productization.
- **Depends on:** bevel-m4 (Tier 1 must exist to re-issue reveals into); needs Accessibility + Automation->Finder TCC grants on a real Mac
- **Files:** `native/helper-macos/finder-mirror-poc.swift` (new, standalone like `menubar-overlay-poc.swift`); `docs/spike/2026-xx-tier3-finder-mirror.md` (new)
- **Decision:** STILL WORTH RUNNING, but only after M4 closes and never before mac v1. It answers the one question M4 leaves open (acceptance 2: "Reveal in Finder" from Safari/VS Code), and its output is two numbers that decide a 4-6 week investment. Keep it a standalone PoC script; do not touch `BevelHelper` sources. Go threshold (decide now so the result is not argued later): escape rate <= 2% and median flash <= 150 ms over 200 reveals, else record "no-go" and close the Tier 3 line.
- **Test first:** non-code: the PoC prints `reveals=200 mirrored=<n> escaped=<n> flash_ms_p50=<n> flash_ms_p95=<n>`.
- **Steps:**
  - [ ] HUMAN: grant Accessibility + Automation (Finder) to Terminal.
  - [ ] Write the PoC: AXObserver on Finder pid; on window-created read `AXDocument`, then `tell application "Finder" to close window 1`; timestamp created vs closed.
  - [ ] Drive 200 reveals: 100 x `open -R <file>`, 50 x VS Code "Reveal in Finder", 50 x Safari downloads "Show in Finder".
  - [ ] Write the numbers and the go/no-go into the spike doc and into `docs/spec/08-os-interop.md` §2.1.1.
- **Verify:** `ls docs/spike | grep tier3` → the report file exists
- **Done when:** report committed with a go/no-go; then `bd close bevel-spike-tier3 --reason "Escape <x>%, flash p50 <y> ms → <go|no-go>"`

### bevel-spike-shadow — SPIKE: bundle-id shadowing of com.apple.finder (documents failure mode; never ships)
- **Meta:** P3 · spike · size S · HUMAN
- **State today:** not started; no description. `docs/spec/08-os-interop.md:49` already records the expected outcome ("Launch Services refuses or Gatekeeper/AMFI flags it") and the decision that it does NOT ship "in any release build even if it works" (INT-5: "Tier 2 MUST NOT ship in any release").
- **Depends on:** none
- **Files:** `docs/spec/08-os-interop.md` (one line)
- **Decision:** NOT worth running - close without doing it. The spike cannot change any decision: both outcomes lead to "never ships", it costs up to a week, and the experiment itself (registering a fake `com.apple.finder` in the user's LaunchServices database) risks leaving the owner's only machine with a poisoned LS database. The spec's rationale already documents the failure mode at the level a reader needs. HUMAN because cancelling a planned research item is the owner's call.
- **Test first:** non-code: `grep -n "Tier 2" docs/spec/08-os-interop.md` shows the cancellation line.
- **Steps:**
  - [ ] HUMAN: confirm cancellation.
  - [ ] Append to the Tier 2 bullet in `docs/spec/08-os-interop.md:49`: "Experiment cancelled <date> (bevel-spike-shadow): outcome cannot alter the never-ship decision."
  - [ ] Remove the 1 ew line from the research table at `docs/spec/09-engineering-plan.md:197` (or mark it cancelled).
- **Verify:** `grep -c "bevel-spike-shadow" docs/spec/08-os-interop.md` → `1`
- **Done when:** owner confirms and the two doc lines are updated; then `bd close bevel-spike-shadow --reason "Cancelled: result cannot change the INT-5 never-ship decision"`
