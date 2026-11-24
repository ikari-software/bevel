> Part of the [open-beads master plan](README.md). Read README.md first: it sets the rules, the order, and the rulings that override individual cards.
> Written 2026-11-18 from reading the tree at `51582c0`. Nothing here was built or run. `UNVERIFIED:` marks a claim the researcher could not confirm — check it before relying on it.

## Cluster E2: Taskbar customization, stacks/downloads, tabs menus, context menus, multi-monitor

Owners in this area (all verified in the tree):
- `src/Bevel.Taskbar/TaskbarView.axaml.cs` — the bar's code-behind. `Initialize(...)` seeds every field from one `BevelSettings`; **`ApplyLiveSettings(BevelSettings s)` (line 371) is the ONE live-apply funnel**. It is reached two ways: (a) `OnSettingsServiceChanged` (line 293) = `ISettingsService.Changed` → `Dispatcher.UIThread.Post(() => ApplyLiveSettings(service.Current))`; (b) the same-process hook `OnboardingWindow.ApplyLive` / `App.ToggleTaskbarLock` (`src/Bevel.App/App.axaml.cs:278,308`). Both are required: `SettingsService.Changed` fires only on EXTERNAL writes (`SettingsService.cs:62,269`), while in a split shell the taskbar holds `RemoteSettingsService`, whose `UpdateAsync` does NOT mutate `Current` — the value arrives later as a core broadcast that raises `Changed` (`RemoteSettingsService.cs:160-178,298`).
- **Rule for every card here: a setting goes live by adding an idempotent, diff-guarded line to `TaskbarView.ApplyLiveSettings` — never a second `settings.Changed` subscription in a view-model.** (`TaskbarViewModel.OnSettingsChanged` for consolidation is the legacy exception; `ApplyLiveSettings` already repeats it at line 397.)
- `src/Bevel.Taskbar/Model/StackViewModel.cs` (`StackViewModel`, `StackFileViewModel`, `StacksViewModel`, `StackEntry`) + `StackFlyoutView.axaml(.cs)` + `Model/PreviewLoader.cs` — folder stacks. Rendered from `BevelSettings.TaskbarStacks` via `TaskbarView.axaml:343`; the component list (`TaskbarComponents`) composes nothing yet (`ApplyComponentRegion` passes an empty list, `TaskbarView.axaml.cs:285`).
- `src/Bevel.Taskbar/TaskButtonMenu.cs` (`Build`/`TryShow`, `TabMenuRow`) + `Model/TabPrefetch.cs` — the app-centric task-button menu. Tabs come from `ITabProvider` → `src/Bevel.Pal.MacOS/MacOSTabProvider.cs` (osascript dialects) / `GeckoTabEngine.cs` (AX) / `GeckoSessionStore.cs` / `TabFaviconStore.cs`, all keyed by `TabBrowserRegistry.cs`.
- `src/Bevel.Taskbar/TaskbarWindow.cs` (one window, primary display, bottom edge: `PositionAtPrimaryDisplayBottom`, `GetWorkAreaBand`) + `WorkAreaMitigator.cs` (Nudge) + `Components/BarGeometry.cs` (already per-bar).
- `src/Bevel.Taskbar/OnboardingWindow.axaml(.cs)` — the Taskbar Properties dialog (tabs General/Start/Buttons/Clock/Appearance/Tray/Behavior); every handler persists through `PersistAndApply`.
Target: one live-apply funnel with section diffs; stacks reconciled incrementally; tab activation verified, not positional; menus built from one model; bar geometry parametrised by (display, edge) so multi-monitor is "N windows", not a rewrite.

## Order

1. bevel-6asm — wrong-tab activation is a real wrong-action bug reachable today; unblocks a real tab cache.
2. bevel-2ip7 — one-line funnel fix + reconcile; unblocks dw5 and every stacks card.
3. bevel-5dn — makes the funnel cheap (section diff + debounced persist); every later setting rides on it.
4. bevel-jlbs — in progress, P2, daily-use Downloads stack (context menus, real file ops).
5. bevel-9elh — code shipped; only on-device QA left, close it.
6. bevel-l17f — favicons shipped; only the iTerm swatch remains, close it.
7. bevel-y3cl — URL enrichment shipped; pid carry + window identity remain (same file as 6asm's policy).
8. bevel-dw5 — stacks manager UI; needs 2ip7.
9. bevel-apm — P2 context-menu superset; phase 1 needs no new PAL verbs.
10. bevel-9w0h — Arc ranking; schema verified, pure parser.
11. bevel-s5x — per-app jump-list actions; small once apm's menu model exists.
12. bevel-h32 — tray denylist editor; needs proto + helper + core.
13. bevel-gcd — Task View / Search buttons (thumbnails already shipped); magnification split out.
14. bevel-9ue — edge position / auto-hide / centred; large layout work, prerequisite for tjr2.
15. bevel-hail — live Quit⇄Force Quit; cosmetic, large rewrite.
16. bevel-e5vz — spike/spec only.
17. bevel-tjr2 — epic; no children exist yet.
18. bevel-3qc — blocked on reference-hardware measurement; HUMAN.

## Cards

### bevel-6asm — Tab activation is positional, so a stale row activates the wrong tab
- **Meta:** P2 · bug · size M · AGENT+HUMAN-VERIFY
- **State today:** Confirmed. `MacOSTabProvider.ActivationScript` (`src/Bevel.Pal.MacOS/MacOSTabProvider.cs:223-249`) emits bare `select tab K of window id N` / `set active tab index of window N to K` / `set current tab to tab K`. `ITabProvider.ActivateAsync` doc (`Interfaces.cs:255`) wrongly promises "a silent no-op". Only the Gecko path verifies (`GeckoTabEngine.SelectTarget`, line 133).
- **Depends on:** none
- **Files:** `src/Bevel.Pal.MacOS/MacOSTabProvider.cs`, `src/Bevel.Pal.Abstractions/Interfaces.cs` (doc only), `tests/Bevel.Pal.MacOS.Tests/MacOSTabProviderTests.cs`
- **Decision:** (1) Change `ActivationScript(string bundleId, Dialect dialect, int windowRef, int tabIndex)` to wrap its body in `on run argv` and read `item 1 of argv` = wanted title, `item 2 of argv` = wanted url ("" when none). Title/url are foreign text and MUST travel as osascript arguments, never inside script source (injection). (2) Add `RunOsaScriptAsync(string script, IReadOnlyList<string> args, CancellationToken ct)`: appends `--` is NOT needed; add each arg to `psi.ArgumentList` after `-e script`. (3) Script policy, same as `SelectTarget`: if tab K of window N still has the wanted title (and url when the dialect has one) → select it; else collect every tab in EVERY window whose title (and url) match; exactly one → select it and raise its window; zero or 2+ → do nothing. Chromium/Safari window indexes are z-order and also go stale, hence the all-windows search. iTerm matches on `name of current session`.
- **Test first:** `MacOSTabProviderTests.Activation_scripts_take_title_and_url_as_arguments_never_inline` — for all three dialects assert the script contains `on run argv`, contains `item 1 of argv`, and that a title like `x" & (do shell script "id") & "` passed to the new arg path does not appear in the script text. Update existing `Safari_activation_sets_current_tab_chromium_sets_active_index` to the new shape.
- **Steps:**
  - [ ] Write the failing test above.
  - [ ] Rewrite the three `ActivationScript` arms per the policy; keep `activate` last and only on a successful select.
  - [ ] Add the args overload of `RunOsaScriptAsync`; `ActivateAsync` passes `tab.Title`, `tab.Url ?? ""`.
  - [ ] Fix the `ITabProvider.ActivateAsync` and `AppTab` doc comments to say "verified by title/url; ambiguous or vanished = no-op".
  - [ ] Leave `TaskbarView.TabPrefetchFreshness` (1.5 s) unchanged; file a new bead "Widen TabPrefetch freshness now that activation is verified".
- **Verify:** `dotnet test tests/Bevel.Pal.MacOS.Tests/Bevel.Pal.MacOS.Tests.csproj --filter "FullyQualifiedName~MacOSTabProviderTests"` → all pass. HUMAN: in Chrome and iTerm open the task-button menu, close a tab above the target with the menu open, click the row → the intended tab (not its neighbour) activates; with two identically-titled tabs nothing happens.
- **Done when:** tests green and the human check passes; then `bd close bevel-6asm --reason "Activation verifies title/url via osascript argv; ambiguous rows no-op"`

### bevel-2ip7 — StacksViewModel reads TaskbarStacks once at construction, never subscribes to settings.Changed
- **Meta:** P3 · bug · size S · AGENT
- **State today:** Confirmed. `TaskbarViewModel.cs:40-41` seeds `new StacksViewModel(settings?.Current.TaskbarStacks ...)`; `StacksViewModel` (`StackViewModel.cs:272-290`) has only a ctor and `Dispose`. `TaskbarView.ApplyLiveSettings` never touches stacks.
- **Depends on:** none
- **Files:** `src/Bevel.Taskbar/Model/StackViewModel.cs`, `src/Bevel.Taskbar/TaskbarView.axaml.cs`, `tests/Bevel.Taskbar.Tests/StackTests.cs`, `tests/Bevel.Taskbar.Tests/TaskbarViewModelSubscriptionOrderTests.cs` (comment only)
- **Decision:** Add `public void Reconcile(IReadOnlyList<string> folders)` to `StacksViewModel`; keep `appEnv`, the shared `IconLoader` and `thumbnails` as fields. Implement with `ObservableReconciler.Reconcile(Stacks, folders, keyOf: s => s.FolderPath, sourceKeyOf: f => f, create: f => new StackViewModel(f, _appEnv, _loader, new PreviewLoader(_loader, _thumbnails)), onRemove: (item, commit) => { item.Dispose(); commit(); })`. Call it from the funnel: add `_vm?.Stacks.Reconcile(s.TaskbarStacks);` in `ApplyLiveSettings` next to the `Tray.Configure` line (396). Do NOT add a `settings.Changed` subscription to the view-model (see cluster rule). Open-flyout-on-remove: remove immediately — the button leaves the visual tree and Avalonia closes its flyout; no special case.
- **Test first:** `StackTests.Reconcile_keeps_survivors_adds_new_removes_gone_and_follows_order` (`[AvaloniaFact]`) — build with `[a,b]`, capture the `a` instance, `Reconcile([c,a])`, assert `Stacks` paths are `[c,a]`, the `a` instance is reference-equal, and a second identical `Reconcile` raises no `CollectionChanged`.
- **Steps:**
  - [ ] Write the failing test.
  - [ ] Add fields + `Reconcile`; make the ctor call `Reconcile(folders.ToList())`.
  - [ ] Add the one line to `ApplyLiveSettings`.
  - [ ] Update the stale comment in `TaskbarViewModelSubscriptionOrderTests.cs:71-76` ("Stacks never changes again") — the asserted order `read, subscribe, read` must stay green.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~StackTests|FullyQualifiedName~TaskbarViewModelSubscriptionOrderTests"` → all pass
- **Done when:** a `TaskbarStacks` change reaching `ApplyLiveSettings` updates the bar without restart; then `bd close bevel-2ip7 --reason "StacksViewModel.Reconcile driven from ApplyLiveSettings"`

### bevel-5dn — Taskbar live-apply: separate cheap preview from debounced persist
- **Meta:** P3 · task · size M · AGENT
- **State today:** Confirmed. `ApplyLiveSettings` (`TaskbarView.axaml.cs:371-430`) unconditionally re-runs `Clock.Configure`, `ApplyStart`, `ApplyAppearance` (new brush), `Tray.Configure`, `ApplyComponentRegion` (a `Task.Run` each call) and `LayoutButtons`; only always-on-top, button size, badge detail and rows are diff-guarded. Slider handlers (`OnMinWidthChanged`, `PersistStartMenu`, `OnRowsChanged`, `PersistAppearance`, `PersistTray` in `OnboardingWindow.axaml.cs`) call `PersistAndApply` per integer step = one `UpdateAsync` (DB write or IPC patch) per step. Their equality guards read `_settings.Current`, which lags in a split shell.
- **Depends on:** bevel-2ip7 (adds a line to the same method)
- **Files:** `src/Bevel.Taskbar/TaskbarView.axaml.cs`, `src/Bevel.Taskbar/OnboardingWindow.axaml.cs`, `src/Bevel.Taskbar/ClockWidget.axaml.cs`, `tests/Bevel.Taskbar.Tests/LiveApplyDiffTests.cs` (new)
- **Decision:** (A) In `TaskbarView` add `private BevelSettings? _applied;`. Each section of `ApplyLiveSettings` runs only when one of its inputs differs from `_applied` (or `_applied` is null); end with `_applied = s.Clone();`. Sections: clock (4 fields), start (2), appearance (3), tray (2 + consolidate), component region (`TaskbarComponents` reference/sequence), stacks, width fields → `LayoutButtons` only if a width/label/grouping/sort/rows/size field changed. (B) In `OnboardingWindow` add `private BevelSettings? _draft;` and `private async Task PreviewThenPersist(Action<BevelSettings> mutate)`: `_draft ??= _settings.Current.Clone(); mutate(_draft); ApplyLive?.Invoke(_draft);` then (re)start a 250 ms one-shot `DispatcherTimer` whose tick sends ONE `_settings.UpdateAsync(s => s.CopyFrom(draft))`-equivalent built from the queued mutations and clears `_draft`. Use it in the five slider/textbox handlers only; checkbox/combo handlers keep `PersistAndApply`. Flush the timer in `OnApply`, `OnCancel` (before the revert) and `Closed`. Guards compare against `_draft ?? _settings.Current`.
- **Test first:** `LiveApplyDiffTests.Reapplying_an_identical_snapshot_reconfigures_nothing` — add `internal int ConfigureCount` to `ClockWidget` (new, incremented in `Configure`); build a headless `TaskbarView` the way `StartMenuSettingsRaceTests` does, call `ApplyLiveSettings(s)` twice, assert the count rose by at most 1; then change only `TaskbarOpacity` and assert the count did not rise.
- **Steps:**
  - [ ] Write the failing test.
  - [ ] Add `_applied` and section guards; keep the existing three guards.
  - [ ] Add `_draft`, `PreviewThenPersist`, timer flush; switch the five handlers.
  - [ ] Run the whole taskbar test project (settings race tests must stay green).
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~LiveApplyDiffTests|FullyQualifiedName~StartMenuSettingsRaceTests|FullyQualifiedName~RenderOnboardingTest"` → all pass
- **Done when:** dragging the opacity slider issues one settings write per pause and never rebuilds the clock; then `bd close bevel-5dn --reason "ApplyLiveSettings diffs sections; slider persistence debounced"`

### bevel-jlbs — Downloads stack UX: context menus, richer flyout, responsiveness + motion
- **Meta:** P2 · task · size L · AGENT+HUMAN-VERIFY
- **State today:** in_progress but none of the three scopes is in the tree. `StackFlyoutView.axaml` is the bare 4-wide grid + "Open folder" button: no header, timestamps, empty state, context menu, or Delete/F2 (`OnStackFileKeyDown` handles Enter/Space only). `StackViewModel.RefreshAsync` (line 82-91) does `Items.Clear()` + re-add every open (flicker; violates "no full rebuilds"). `MacOSFileOperations` (`src/Bevel.Pal.MacOS/MacOSPal.cs:54-62`) still throws `NotImplementedException` on all four methods and reports `NotYet.Unavailable`. `LocalTrash` is `internal` in `src/Bevel.Core/Vfs/LocalFsProvider.cs:443`; `Bevel.Pal.MacOS` does not reference `Bevel.Core`.
- **Depends on:** none (bevel-2ip7 touches the same file — land it first)
- **Files:** `src/Bevel.Pal.MacOS/MacOSPal.cs` (move class to `src/Bevel.Pal.MacOS/MacOSFileOperations.cs` (new)), `src/Bevel.Pal.MacOS/AppKitInterop.cs`, `src/Bevel.Taskbar/Model/StackViewModel.cs`, `src/Bevel.Taskbar/StackFlyoutView.axaml(.cs)`, `src/Bevel.Taskbar/Model/TaskbarViewModel.cs`, `src/Bevel.Taskbar/TaskbarView.axaml`, `tests/Bevel.Taskbar.Tests/StackTests.cs`, `tests/Bevel.Pal.MacOS.Tests/MacOSFileOperationsTests.cs` (new)
- **Decision:** (1) Real `MacOSFileOperations`: Copy/Move/Rename with `System.IO` inside `Task.Run`; `DeleteAsync(Trash)` via `NSFileManager trashItemAtURL:resultingItemURL:error:` (gives Finder "Put Back"; do not reuse `LocalTrash`, which is a raw move and lives in a project the PAL cannot reference), `Permanent` via `File.Delete`/`Directory.Delete(recursive)`. Declare any new selector helper in `AppKitInterop.cs` only (`InteropConventionTests`). `Capabilities` becomes `Available: true`. (2) `StackViewModel`/`StackFileViewModel` take `IFileOperations?` and `IFileOpener?` (both exist in DI) and expose commands `OpenWithCommand`, `RevealCommand` (`IFileOpener.RevealAsync`), `CopyPathCommand`, `DuplicateCommand` (`CopyAsync` into the same folder), `RenameCommand`, `TrashCommand`; stack-level `RefreshCommand`, `CleanUpCommand` (trash items older than 30 days — after a confirm dialog). (3) Refresh is a diff: `ObservableReconciler.Reconcile(Items, recent, keyOf: i => (i.FullPath, i.Stamp), ...)`; expose `Stamp` on `StackFileViewModel`. (4) Flyout: header row "Name — N items", relative-time second line per cell from `Stamp` (re-evaluated by one `DispatcherTimer` that runs only while the flyout is open), empty-state text, 120 ms opacity fade-in for the preview via `Transitions`. All colours via `DynamicResource` tokens (replace the hardcoded `White` foreground/background in the existing styles with `Bevel.Brush.HighlightText` / `Bevel.Brush.Window`; both tokens exist in `src/Bevel.Themes.Industrial1999/Tokens.axaml` / `SchemeAliases.axaml`).
- **Test first:** `StackTests.Refresh_keeps_unchanged_cells_and_only_adds_the_new_file` (`[AvaloniaFact]`: refresh, capture item instances, create one file, refresh, assert old instances reference-equal and count +1); `MacOSFileOperationsTests.Rename_and_permanent_delete_round_trip_in_a_temp_dir` (skip trash on non-macOS).
- **Steps:**
  - [ ] File ops: tests, then implement; register stays `CompositionRoot.cs:126`.
  - [ ] Diff refresh: test, then replace `Items.Clear()` loop.
  - [ ] Commands on the VMs + thread the two services through `TaskbarViewModel` → `StacksViewModel`.
  - [ ] `ContextFlyout` on the cell `Border` and on the stack button (`TaskbarView.axaml:350-372`); Delete → `TrashCommand`, F2 → inline rename `TextBox` in `OnStackFileKeyDown`.
  - [ ] Header, timestamps, empty state, fade; re-harvest `RenderStackGridTest` output into `site/shots/` and update `site/index.html` copy.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~StackTests|FullyQualifiedName~RenderStackGrid"` and `dotnet test tests/Bevel.Pal.MacOS.Tests/Bevel.Pal.MacOS.Tests.csproj --filter "FullyQualifiedName~MacOSFileOperationsTests"` → pass. HUMAN: on device, right-click a download → Move to Trash → file appears in Finder Trash with Put Back.
- **Done when:** all menu verbs act on real files and reopening the flyout does not flicker; then `bd close bevel-jlbs --reason "Stack context menus, real macOS file ops, diff refresh, header/timestamps"`

### bevel-9elh — Downloads stack: macOS-like grid with content previews
- **Meta:** P2 · feature · size S · AGENT+HUMAN-VERIFY
- **State today:** LIKELY DONE — grid + previews shipped in `fbbbfcf`/merge `514a16e` (`StackFlyoutView.axaml`, `Model/PreviewLoader.cs`, `Bevel.Pal.MacOS/MacOSThumbnailProvider.cs`, `IThumbnailProvider`); cache-key bugs fixed in `1bcfbb0` (bead note cites 946c65b). Tests exist: `StackTests`, `RenderStackGridTest`, `RenderStackGridRealPalTest`, `MacOSThumbnailProviderTests`. Every acceptance bullet is met in code. Only the on-device QA list from the notes is open.
- **Depends on:** none
- **Files:** none (verification only)
- **Decision:** Do not add scope here; richer UX belongs to bevel-jlbs. Close after the device QA.
- **Test first:** the existing suites named above.
- **Steps:**
  - [ ] Run the verify command.
  - [ ] HUMAN on a Retina Mac: open the Downloads stack with a HEIC, a RAW, a 100+ page PDF present → previews appear, no UI stall; cold `~/Downloads` with 16 previewable files → bar stays responsive; drag a cell to Finder; arrow keys move focus; flyout sits above the bar on 1-row and 2-row bars.
  - [ ] File one new bead per failed check; do not reopen this one.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~StackTests|FullyQualifiedName~RenderStackGrid"` → all pass
- **Done when:** QA list ticked; then `bd close bevel-9elh --reason "Grid + content previews shipped (fbbbfcf, 1bcfbb0); device QA done"`

### bevel-l17f — Tabs menu favicons: Chromium Favicons DB + iTerm session colors as row icons
- **Meta:** P2 · task · size S · AGENT+HUMAN-VERIFY
- **State today:** PARTLY DONE — Chromium AND Gecko favicons shipped in `8927980`: `TabFaviconStore.Enrich` (copy-then-query, memoised, `MinRecopyInterval`), `AppTab.IconPng`, `TaskButtonMenu.TabMenuRow.Icon`, `TabPrefetch` content-hash bitmap cache; tests `TabFaviconStoreTests`, `TaskButtonMenuTabsTests.Rows_carry_their_decoded_favicon_as_the_menu_icon`. Remaining: iTerm rows have no icon (`TabBrowserRegistry` iTerm entry has `FaviconRoot: null`; the iTerm script reads only `name of current session`).
- **Depends on:** bevel-6asm (same script file; land it first to avoid a merge)
- **Files:** `src/Bevel.Pal.Abstractions/Dtos.cs`, `src/Bevel.Pal.MacOS/MacOSTabProvider.cs`, `src/Bevel.Taskbar/TaskButtonMenu.cs`, `tests/Bevel.Pal.MacOS.Tests/MacOSTabProviderTests.cs`, `tests/Bevel.Taskbar.Tests/TaskButtonMenuTabsTests.cs`
- **Decision:** Add `string? Tint = null` (hex `#RRGGBB`) as the last parameter of `AppTab`. iTerm enumeration appends a 4th field: `background color of current session of t` (an AppleScript `{r,g,b}` list, 0-65535 each; UNVERIFIED: exact property name — check iTerm2's scripting dictionary with `sdef /Applications/iTerm.app | grep -i color` before coding; if absent, drop the field and close as shipped). `Parse` converts to hex. `TaskButtonMenu.Build` shows, when `row.Icon` is null and `Tab.Tint` parses, a code-drawn 12×12 `Border` with `CornerRadius=2`, `Background` = the tint, 1px `Bevel.Brush.ButtonShadow` border — vector, no bitmap. The tint is external content (like a favicon), so a literal colour is allowed here.
- **Test first:** `MacOSTabProviderTests.Parse_reads_an_iterm_session_colour_into_Tint` (row `"7␟1␟zsh␟65535, 0, 0"` → `Tint == "#FF0000"`); `TaskButtonMenuTabsTests.Rows_without_a_favicon_show_the_session_tint_swatch`.
- **Steps:**
  - [ ] Confirm the property with `sdef`; record the result in the bead notes.
  - [ ] Tests, then script + `Parse(raw, bundleId, hasUrl)` gains the colour column for `Dialect.ITerm`.
  - [ ] Swatch in `Build`.
- **Verify:** `dotnet test tests/Bevel.Pal.MacOS.Tests/Bevel.Pal.MacOS.Tests.csproj --filter "FullyQualifiedName~MacOSTabProviderTests"` and `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~TaskButtonMenuTabsTests"` → pass. HUMAN: iTerm with two differently-coloured profiles → two different swatches.
- **Done when:** iTerm rows carry a swatch (or the dictionary lacks the property and that is recorded); then `bd close bevel-l17f --reason "Chromium+Gecko favicons shipped 8927980; iTerm session swatch added"`

### bevel-y3cl — Gecko tab engine: stable window identity, pid carry, URL enrichment
- **Meta:** P3 · task · size M · AGENT+HUMAN-VERIFY
- **State today:** PARTLY DONE — item 3 (URL enrichment) is shipped: `GeckoSessionStore.TitleToUrl` + `MacOSTabProvider.EnrichAsync` lines 103-118, tests `GeckoSessionStoreTests`. Items 1 and 2 are open: `GetTabsCore` emits `WindowRef = (wi + 1)` (positional `AXWindows` index, `GeckoTabEngine.cs:65`); `ActivateCore` re-resolves `PidFor(tab.BundleId)` (line 93), which prefers whichever instance is active NOW.
- **Depends on:** bevel-l17f (both add an `AppTab` parameter; do them in this order)
- **Files:** `src/Bevel.Pal.Abstractions/Dtos.cs`, `src/Bevel.Pal.MacOS/GeckoTabEngine.cs`, `tests/Bevel.Pal.MacOS.Tests/GeckoTabEngineTests.cs`
- **Decision:** Add `int? Pid = null` and `string? WindowTitle = null` to `AppTab` (process-local record; `ITabProvider` never crosses IPC). Enumeration fills both (`StrAttr(window, "AXTitle")`). Activation: use `tab.Pid` when that pid is still in `runningApplicationsWithBundleIdentifier:` for the bundle, else fall back to `PidFor`. Window choice via new pure `internal static int? SelectWindow(IReadOnlyList<string> titles, int wantIndex, string? wantTitle)`: index still carries the title → that index; else a UNIQUE title match; else (null `wantTitle` only) the index; otherwise null = no-op. Public AX only — do not use the private `_AXUIElementGetWindow` (a missing private symbol must never break tab activation). `SelectTarget` still guards the tab inside the chosen window.
- **Test first:** `GeckoTabEngineTests.SelectWindow_follows_a_reordered_window_by_unique_title`, `SelectWindow_refuses_duplicate_window_titles_after_a_reorder`, `SelectWindow_keeps_the_index_when_the_title_still_matches`.
- **Steps:**
  - [ ] Tests, then `SelectWindow`.
  - [ ] `AppTab` fields; fill in `GetTabsCore` (pass `pid` down).
  - [ ] `ActivateCore`: pid validation helper `IsRunningInstance(string bundleId, int pid)` (new, next to `PidFor`), collect window titles first, then act on `SelectWindow`'s result.
- **Verify:** `dotnet test tests/Bevel.Pal.MacOS.Tests/Bevel.Pal.MacOS.Tests.csproj --filter "FullyQualifiedName~GeckoTabEngineTests"` → all pass. HUMAN: two Zen/Firefox windows, open the menu, click the other window (reorders AXWindows), pick a tab from the first → correct window and tab.
- **Done when:** tests green + human check; then `bd close bevel-y3cl --reason "Gecko tabs carry pid + window title; SelectWindow guards reorder; URL enrichment already shipped"`

### bevel-dw5 — Taskbar cust: stacks manager (add/remove/reorder folders)
- **Meta:** P3 · feature · size M · AGENT
- **State today:** Not started. `OnboardingWindow.axaml` has tabs General/Start/Buttons/Clock/Appearance/Tray/Behavior and no stacks UI; `TaskbarStacks` has one read site.
- **Depends on:** bevel-2ip7 (live rebuild), bevel-5dn (optional)
- **Files:** `src/Bevel.Taskbar/OnboardingWindow.axaml`, `src/Bevel.Taskbar/OnboardingWindow.axaml.cs`, `src/Bevel.Taskbar/Model/StackListEditor.cs` (new), `tests/Bevel.Taskbar.Tests/StackListEditorTests.cs` (new), `tests/Bevel.Taskbar.Tests/RenderOnboardingTest.cs`
- **Decision:** New `TabItem Header="Stacks"` after "Tray": a `ListBox x:Name="StacksList"` of folder paths plus buttons Add…, Remove, Move Up, Move Down (disabled when they cannot act — a disabled-with-reason button is wired, not hidden). Add… uses `StorageProvider.OpenFolderPickerAsync` (already async; no blocking). Pure logic in `public static class StackListEditor` with `string[] Add(string[] list, string path)` (trim trailing slash, reject duplicates by `StringComparison.Ordinal`), `Remove(string[] list, int index)`, `Move(string[] list, int index, int delta)`. Persist with `PersistAndApply(s => s.TaskbarStacks = next)`. Source of truth stays `TaskbarStacks` because that is what the bar renders today; `TaskbarComponents`' `run.bevel.stack` instances are NOT rewritten (nothing reads them yet). File a new bead: "When the Stack component renderer lands, make TaskbarComponents the single source and retire TaskbarStacks".
- **Test first:** `StackListEditorTests.Add_rejects_a_duplicate_and_normalises_a_trailing_slash`, `Move_clamps_at_both_ends`, `Remove_out_of_range_is_a_no_op`.
- **Steps:**
  - [ ] Tests + `StackListEditor`.
  - [ ] XAML tab + four handlers; fill the list in `LoadSettings`.
  - [ ] Confirm `OnCancel`'s `CopyFrom(baseline)` restores the list (it clones `TaskbarStacks`, `SettingsService.cs:1034`).
  - [ ] Extend `RenderOnboardingTest` to render the new tab; re-harvest the shot for `site/` if the page shows the dialog.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~StackListEditorTests|FullyQualifiedName~RenderOnboardingTest"` → pass
- **Done when:** adding/removing/reordering a folder in Properties changes the running bar immediately; then `bd close bevel-dw5 --reason "Stacks tab in Taskbar Properties; live via StacksViewModel.Reconcile"`

### bevel-apm — Context menus: cross-OS superset (Cascade/Tile + window verbs)
- **Meta:** P2 · feature · size L · AGENT+HUMAN-VERIFY
- **State today:** Bead text is stale about what shipped. The bar menu (`TaskbarView.axaml:84-92`) has Show the Desktop / Task Manager / Lock / Properties. The button menu is the app-centric `TaskButtonMenu.Build`: header, Windows ▸ (Activate/Minimize/Close per window), Tabs ▸, Quit/Force Quit. `TaskGroupViewModel.MinimizeAllCommand/RestoreAllCommand/CloseAllCommand` exist (`TaskGroupViewModel.cs:36-38`) but NO menu references them (dead commands), and no per-window Restore item exists although `TaskItemViewModel.RestoreCommand` does. `IWindowManager` has Activate/Minimize/Restore/Close/Reposition/CaptureWindow/TerminateApp only.
- **Depends on:** none for phase 1. Auto-hide / Position items wait for bevel-9ue; New Window for bevel-s5x.
- **Files:** `src/Bevel.Taskbar/TaskButtonMenu.cs`, `src/Bevel.Taskbar/Model/WindowArranger.cs` (new), `src/Bevel.Taskbar/Model/ShellModel.cs`, `src/Bevel.Taskbar/Model/TaskbarViewModel.cs`, `src/Bevel.Taskbar/TaskbarView.axaml`, `tests/Bevel.Taskbar.Tests/WindowArrangerTests.cs` (new), `tests/Bevel.Taskbar.Tests/TaskButtonMenuTabsTests.cs`
- **Decision:** This bead = phase 1, no new PAL verbs. (a) Group menu: after the Windows submenu add Minimize All / Restore All / Close All bound to the three existing commands (only when `dc is TaskGroupViewModel` with 2+ windows); per-window submenu gains Restore. Extend the private `MenuModel` record with those three commands. (b) Bar menu: Cascade Windows, Tile Windows Horizontally, Tile Windows Vertically. Pure geometry in `public static class WindowArranger`: `IReadOnlyList<PalRect> Cascade(PalRect area, int count)`, `TileHorizontally(PalRect area, int count)`, `TileVertically(PalRect area, int count)`. `area` = primary monitor rect minus the taskbar band (`TaskbarWindow.GetWorkAreaBand`), in the same coordinate space `WorkAreaMitigator` uses. `ShellModel` gets `Task ArrangeAsync(Func<PalRect,int,IReadOnlyList<PalRect>> layout, PalRect area)` that takes non-minimized, non-fullscreen, non-other-Space, non-app-presence windows from the projection and calls `RepositionAsync` per window off the UI thread, continuing past per-window failures. Phase 2 = new beads (file them): "Window verb: Maximize/Zoom (proto + helper AXZoomButton press)", "App verb: Hide app (NSRunningApplication hide)", "Show in Finder for a task button (needs bundle path on ForeignWindow)", "Pin/Unpin apps to the taskbar", "SPIKE: Move window to Space (private SkyLight only)". Move/Size keyboard modes are dropped: macOS has no equivalent and a mouse-less resize mode is not worth a helper verb.
- **Test first:** `WindowArrangerTests.Tile_vertically_splits_the_area_into_equal_columns_with_no_gap_or_overlap`, `Cascade_offsets_each_window_and_keeps_all_inside_the_area`, `Zero_windows_returns_empty`; `TaskButtonMenuTabsTests.Group_menu_offers_minimize_restore_close_all` (via `TaskButtonMenu.Build`).
- **Steps:**
  - [x] Arranger tests + implementation.
  - [x] `ShellModel.ArrangeAsync` + three commands on `TaskbarViewModel` (next to `ShowDesktopCommand`).
  - [x] Three `MenuItem`s in `Grid.ContextFlyout`.
  - [x] Group verbs + Restore in `TaskButtonMenu`.
  - [x] File the five phase-2 beads.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~WindowArrangerTests|FullyQualifiedName~TaskButtonMenuTabsTests"` → pass. HUMAN: with 3 windows open, Tile Vertically → three columns above the bar.
- **Done when:** phase-1 items work on device and phase-2 beads exist; then `bd close bevel-apm --reason "Cascade/Tile + group verbs shipped; remaining verbs split into beads"`

### bevel-9w0h — Arc tabs menu: prefer live/recent tabs over raw sidebar order (Space-aware)
- **Meta:** P2 · task · size M · AGENT+HUMAN-VERIFY
- **State today:** Not started (no `StorableSidebar` reference in `src/`). Arc goes through `Dialect.Chromium` and lists `every tab of window wi` in sidebar order; `TaskButtonMenu.MaxTabRows` is now 300 (the "25-row cap" in the bead is stale — the list scrolls), so the remaining problem is ORDER, not truncation. Schema checked on this machine (keys only): `sidebar.containers[]` → the element holding `items` and `spaces`; both are flat arrays alternating `"<id>"`, `{object}`. Item object: `id`, `parentID`, `childrenIds`, `data.tab.{savedTitle,savedURL,timeLastActiveAt}` (or `data.list` / `data.itemContainer`). Space object: `id`, `title`, `containerIDs`, `newContainerIDs` (alternating `{pinned}`/`{unpinned}` marker then container id).
- **Depends on:** bevel-6asm (ranked rows are activated by title/url, so row order no longer has to equal script index order)
- **Files:** `src/Bevel.Pal.MacOS/ArcSidebarStore.cs` (new), `src/Bevel.Pal.MacOS/MacOSTabProvider.cs`, `src/Bevel.Pal.Abstractions/Dtos.cs`, `src/Bevel.Taskbar/TaskButtonMenu.cs`, `tests/Bevel.Pal.MacOS.Tests/ArcSidebarStoreTests.cs` (new), `tests/fixtures/arc-sidebar.json` (new, hand-written synthetic — never a copy of a real profile)
- **Decision:** `internal static class ArcSidebarStore` with `internal static IReadOnlyDictionary<string, ArcTabInfo> ParseByUrl(byte[] json)` and `record ArcTabInfo(double LastActive, string? Space, bool Pinned)`; reader uses `System.Text.Json` `JsonDocument` (AOT-safe), tolerant of every missing key (returns empty, never throws). Walk each item's `parentID` chain up to a container id, map container → space + pinned via `newContainerIDs`. In `EnrichAsync`, only for bundle `company.thebrowser.Browser`, stable-sort tabs: `LastActive` descending (unknown last), keeping AppleScript order as tiebreak; set new `AppTab.Group` (`string? Group = null`) to the Space title. `TaskButtonMenu.Build` inserts a disabled header row + `Separator` whenever `Group` changes between consecutive rows. File read happens inside the existing `Task.Run` in `EnrichAsync` under the caller's budget token. Activation stays Apple Events.
- **Test first:** `ArcSidebarStoreTests.Parses_last_active_space_and_pinned_from_the_alternating_arrays`, `Malformed_or_empty_json_yields_an_empty_map`, `A_tab_without_timeLastActiveAt_sorts_last`.
- **Steps:**
  - [ ] Write the synthetic fixture (2 spaces, pinned + unpinned containers, one nested `list` folder).
  - [ ] Tests + parser.
  - [ ] Sort + `Group` in `EnrichAsync`; headers in `Build`.
- **Verify:** `dotnet test tests/Bevel.Pal.MacOS.Tests/Bevel.Pal.MacOS.Tests.csproj --filter "FullyQualifiedName~ArcSidebarStoreTests"` → pass. HUMAN: Arc's Tabs submenu lists the most recently used tabs first, grouped under Space names.
- **Done when:** tests green + human check; then `bd close bevel-9w0h --reason "Arc tabs ranked by timeLastActiveAt and grouped by Space from StorableSidebar.json"`

### bevel-s5x — Taskbar grouped-button jump-list: per-app custom actions (New window, etc.)
- **Meta:** P3 · task · size S · AGENT
- **State today:** Not started. `TaskButtonMenu.Build(object? dc, IReadOnlyList<TabMenuRow>? tabs, ITabProvider? tabProvider, Action? onActivateForeign)` has no extension point. `TaskbarView` already holds the launch surface the bead says is missing: `_openFolder` (`Action<VfsPath>`, wired to `App.OpenFilerAt`, `App.axaml.cs:363`). `TaskGroupViewModel.BundleId` and `TaskItemViewModel` bundle id exist.
- **Depends on:** bevel-apm (same `MenuModel`; land apm first)
- **Files:** `src/Bevel.Taskbar/TaskButtonMenu.cs`, `src/Bevel.Taskbar/TaskbarView.axaml.cs`, `tests/Bevel.Taskbar.Tests/TaskButtonMenuTabsTests.cs`
- **Decision:** Add `public sealed record TaskMenuAction(string Header, Action Invoke)` and an optional last parameter `Func<string?, IReadOnlyList<TaskMenuAction>>? appActions = null` to `Build` and `TryShow` (bundle id in, actions out). Items render directly under the header separator, followed by a `Separator`. `TaskbarView` supplies one provider method `AppActionsFor(string? bundleId)`: for `Bevel.Core.AppInfo.BundleId` return `New _Window` → `_openFolder(home)` where home is the user profile as a `VfsPath`; everything else returns empty. Key on bundle id, not `AppId` (`AppId` is a display name, `Dtos.cs:98-100`). Keep the provider in the view — no DI service for one entry.
- **Test first:** `TaskButtonMenuTabsTests.App_actions_appear_under_the_header_and_invoke` — `Build` with a provider returning one action; assert a `MenuItem` with that header exists before the Windows submenu and clicking it runs the callback; with a null provider the menu shape is unchanged.
- **Steps:**
  - [x] Test, then the parameter + rendering.
  - [x] `AppActionsFor` + pass it at `TaskbarView.axaml.cs:1018`.
  - [x] UNVERIFIED: that a `--role=filer` window reports bundle id `pl.ikari.bevel` to the helper in a packaged build — check with `BEVEL_DEBUG_TASKBAR=1` and note the result; in `dotnet run` dev mode it will not, which is acceptable.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~TaskButtonMenuTabsTests"` → pass
- **Done when:** right-clicking a Bevel Filer button offers New Window and it opens one; then `bd close bevel-s5x --reason "Per-app actions seam on TaskButtonMenu; Filer New Window wired"`

### bevel-h32 — Taskbar cust: tray denylist editor + live-capture toggle
- **Meta:** P3 · feature · size L · AGENT+HUMAN-VERIFY
- **State today:** Not started. Helper denylist is an immutable `let denyList` built from defaults + `BEVEL_TRAY_DENY` (`native/helper-macos/Sources/BevelHelper/TrayServiceImpl.swift:39-46`); `HelperProcessHost` passes only `BEVEL_HELPER_TOKEN` (line 101). Live capture is the self-test-gated `liveMirroringEnabled` (lines 78, 795). `TrayService` has `ListTrayItems`, `Changes`, `ForwardClick`, `SetConsolidation`. The Tray tab has two sliders + the consolidate checkbox.
- **Depends on:** none
- **Files:** `proto/bevel.helper.v1.proto`, `native/helper-macos/Sources/BevelHelper/TrayServiceImpl.swift`, `native/helper-macos/Tests/BevelHelperTests/TrayServiceTests.swift`, `src/Bevel.Pal.MacOS/MacOSSystemTrayHost.cs`, `src/Bevel.App/ShellCore/ShellCoreServer.cs`, `src/Bevel.Core/SettingsService.cs`, `src/Bevel.Taskbar/OnboardingWindow.axaml(.cs)`, `tests/Bevel.Core.Tests/` (settings round-trip test, new method)
- **Decision:** Live via helper RPC, not env + restart. New RPC `SetTrayPolicy(SetTrayPolicyRequest { repeated string deny = 1; bool live_capture = 2; }) returns (SetTrayPolicyReply)`. Helper: `denyList` becomes a lock-guarded `var` = built-in defaults + pushed list (env var still appended, for dev); `live_capture=false` forces `isLiveMirroringEnabled` false, `true` returns control to the self-test result. After a policy change the next `Changes` poll must emit removes for newly-denied items (reconcile, not append). Settings: `string[] TrayDenyList` (default empty) and `bool TrayLiveCapture` (default true) on `BevelSettings`, with `SetOrPrune`/`Get*`/`CopyFrom` lines beside `taskbarTrayIconSize`. Owner: the CORE pushes the policy — once when the helper becomes ready and again inside the `CoreCommandKind.ApplySettingsUpdate` case (`ShellCoreServer.cs:459`) when either key changed. No peer command and no taskbar-side subscription (core owns helper and settings). UI: in the Tray tab, a list + Add/Remove of substrings and a "Capture live icons" checkbox, persisted via `PersistAndApply`.
- **Test first:** Swift `TrayServiceTests.testSetTrayPolicyReplacesUserDenyListAndKeepsDefaults` (after the RPC, `isDenied(windowName:"Foo", ownerName:"")` is true for pushed "foo" and still true for default "siri"); C# settings test `TrayDenyList_round_trips_and_prunes_when_default`.
- **Steps:**
  - [ ] Proto + regenerate both sides.
  - [ ] Helper: mutable policy + test.
  - [ ] `MacOSSystemTrayHost.SetTrayPolicyAsync(IReadOnlyList<string> deny, bool liveCapture)` (new) modelled on the `SetConsolidationAsync` call at line 86.
  - [ ] Settings fields + round-trip test.
  - [ ] Core push at helper-ready and on settings apply.
  - [ ] Tray tab UI.
- **Verify:** `cd native/helper-macos && swift test --filter TrayServiceTests` → pass; `dotnet test tests/Bevel.Core.Tests/Bevel.Core.Tests.csproj --filter "FullyQualifiedName~TrayDenyList"` → pass. HUMAN (needs Screen Recording grant): add "dropbox" to the list → its tray icon disappears within a second, no restart.
- **Done when:** editing the list changes the mirrored tray live; then `bd close bevel-h32 --reason "Tray policy RPC + settings + editor; core pushes on change"`

### bevel-gcd — Taskbar cust: dock magnification, hover thumbnails, Task View/Search/Widgets buttons
- **Meta:** P3 · feature · size M · AGENT+HUMAN-VERIFY
- **State today:** PARTLY DONE — hover thumbnails shipped (`2dbdc4e`, then bevel-c04q): `WindowPreviewCache`, `TaskbarView.ShowTaskbarTooltip` (`TaskbarView.axaml.cs:875`), `PreviewFrame`/`PreviewImage` in `TooltipPopup`. Show Desktop shipped (`ShowDesktopButton`). No magnification, no Task View/Search/Widgets buttons. A search launch surface already exists: `_openSearch` (Start ▸ Search → Filer Find mode).
- **Depends on:** bevel-5dn (new settings ride the diffed funnel)
- **Files:** `src/Bevel.Core/SettingsService.cs`, `src/Bevel.Taskbar/TaskbarView.axaml(.cs)`, `src/Bevel.UI/Glyphs.cs`, `src/Bevel.Taskbar/OnboardingWindow.axaml(.cs)`, `tests/Bevel.Taskbar.Tests/RenderTaskbarTest.cs`
- **Decision:** Scope this bead to two launcher buttons; split the rest. Settings `bool TaskbarShowSearchButton`, `bool TaskbarShowTaskViewButton` (default false), checkboxes in the Behavior tab beside `ShowDesktopCheck`, applied in `ApplyLiveSettings` like `ShowDesktopButton.IsVisible`. Buttons sit right of Start, same theme `Button` style as the Start/ShowDesktop buttons, with code-drawn vector glyphs bound to theme brushes (magnifier; two overlapped window outlines). Search → `_openSearch?.Invoke()`. Task View → launch Mission Control through the existing `IAppEnvironment.LaunchAsync("/System/Applications/Mission Control.app")` (a real app bundle whose launch opens Mission Control — no synthetic key events; UNVERIFIED on this macOS version: run `open -a "Mission Control"` once by hand first). New beads to file: "Dock-style hover magnification of task buttons (RenderTransform scale + Transitions, metrics from theme tokens)" and "Widgets button — HUMAN: decide what a Bevel widget surface is before any button exists" (do not ship a button with nothing behind it).
- **Test first:** `RenderTaskbarTest` new method `Search_and_task_view_buttons_render_when_enabled` — headless render with both settings on; assert both buttons are visible and non-blank (pixel variance inside their bounds), and absent when off.
- **Steps:**
  - [ ] Settings fields + persistence lines.
  - [ ] Glyphs + buttons + click handlers + `AutomationProperties.Name`.
  - [ ] `ApplyLiveSettings` lines + Behavior-tab checkboxes.
  - [ ] Render under every theme (1999, 2001, Flat, Pastel); harvest one shot to `site/shots/` and update `site/index.html`.
  - [ ] File the two beads.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~RenderTaskbarTest"` → pass. HUMAN: Task View button opens Mission Control.
- **Done when:** both buttons work and the split beads exist; then `bd close bevel-gcd --reason "Thumbnails shipped earlier; Search + Task View buttons added; magnification/widgets split out"`

### bevel-9ue — Taskbar cust: edge position (top/bottom + vertical L/R) + auto-hide + centered alignment
- **Meta:** P3 · feature · size L · AGENT+HUMAN-VERIFY
- **State today:** Not started; bottom edge is hard-wired in four places: `TaskbarWindow.PositionAtPrimaryDisplayBottom` (`TaskbarWindow.cs:484`), `TaskbarGeometry.BottomLeft` / `TaskbarGeometry.WorkAreaBand`, `WorkAreaMitigator.MitigateOnceAsync` (shrinks a window's bottom to `band.Y`, line 246-247), and `Placement="Top"` popups in `TaskbarView.axaml` / `StartMenu.axaml`. No edge/auto-hide/alignment setting exists in `BevelSettings`.
- **Depends on:** bevel-5dn
- **Files:** `src/Bevel.Core/SettingsService.cs`, `src/Bevel.Taskbar/TaskbarGeometry.cs`, `src/Bevel.Taskbar/TaskbarWindow.cs`, `src/Bevel.Taskbar/WorkAreaMitigator.cs`, `src/Bevel.Taskbar/TaskbarView.axaml(.cs)`, `src/Bevel.Taskbar/StartMenu.axaml`, `src/Bevel.Taskbar/OnboardingWindow.axaml(.cs)`, `tests/Bevel.Taskbar.Tests/TaskbarGeometryTests.cs`, `tests/Bevel.Taskbar.Tests/WorkAreaMitigatorTests.cs`
- **Decision:** Do this bead as slice 1 (Top/Bottom) plus slice 2 (centred alignment); file new beads for "Taskbar auto-hide with edge-hover peek" and "Vertical taskbar (Left/Right)". Slice 1: `enum TaskbarEdge { Bottom, Top }` + `BevelSettings.TaskbarEdge` (default Bottom); reuse nothing from `DockEdge` (PAL type; Core must stay PAL-free). On macOS, Top means directly under the system menu bar (the screen's working-area top), never over it. Add `TaskbarGeometry.TopLeft(...)` and an `edge` parameter to `TaskbarGeometry.WorkAreaBand`; `TaskbarWindow.SetEdge(TaskbarEdge)` re-anchors and recomputes the band; `WorkAreaMitigator` branches on which side of the screen the band is: for a top band it moves the window's Y down to `band.Y + band.Height` and reduces Height by the same amount. Popups: bind placement to one view property `PopupPlacement` (Top for a bottom bar, Bottom for a top bar). Slice 2: `enum TaskbarAlignment { Left, Center }`; in `LayoutButtons` centre the strip only when total button width < available width, so shrink-to-fit still fills when crowded.
- **Test first:** `TaskbarGeometryTests.Top_edge_parks_the_bar_below_the_menu_bar_inset_at_scale_2`; `WorkAreaMitigatorTests.Top_band_pushes_an_overlapping_window_down_and_keeps_its_bottom_edge`.
- **Steps:**
  - [ ] Settings enums + persistence + a "Position" combo and "Alignment" combo in the Behavior tab.
  - [ ] Geometry tests + `TopLeft` / band edge.
  - [ ] `SetEdge` + funnel line in `ApplyLiveSettings` (guarded to a real change).
  - [ ] Mitigator top-band branch + test.
  - [ ] Popup placement property.
  - [ ] Centred alignment in `LayoutButtons` + a `ButtonLayoutTests` case.
  - [ ] File the two follow-up beads.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~TaskbarGeometryTests|FullyQualifiedName~WorkAreaMitigatorTests|FullyQualifiedName~ButtonLayoutTests"` → pass. HUMAN: switch to Top on device → bar under the menu bar, Start menu opens downward, a zoomed window is nudged below the bar.
- **Done when:** Top/Bottom and Left/Center are live-switchable; then `bd close bevel-9ue --reason "Top/bottom edge + centred alignment shipped; auto-hide and vertical split into beads"`

### bevel-hail — Taskbar menu: true live Quit⇄Force-Quit swap via in-tree Popup
- **Meta:** P3 · task · size L · AGENT+HUMAN-VERIFY
- **State today:** Not started; decide-at-open is in place (`TaskButtonMenu.cs:145-150`, `TaskbarNative.OptionKeyDown()`). One premise in the bead is out of date: since bevel-vk4n the bar takes key focus while a menu is open (`EnterMenuScope()` at `TaskbarView.axaml.cs:1024`, `TaskbarWindow.SetKeyFocusAllowed`), so modifier key events may now reach the window while the menu is up. The "open MenuFlyout never repaints" finding is unchanged and is also why Tabs are prefetched.
- **Depends on:** none (bevel-e5vz option (a) would reuse the host built here)
- **Files:** `src/Bevel.Taskbar/TaskMenuPopup.cs` (new), `src/Bevel.Taskbar/TaskButtonMenu.cs`, `src/Bevel.Taskbar/TaskbarView.axaml.cs`, `src/Bevel.Taskbar/TaskbarWindow.cs` (`TaskbarNative`), `tests/Bevel.Taskbar.Tests/TaskMenuPopupTests.cs` (new)
- **Decision:** Prove repaint before rewriting. Step 1 is a headless pixel test that hosts the SAME `MenuItem` list `Build` produces inside an in-tree `Popup` configured like `StartMenu.axaml`'s `MenuPopup` (line 15), changes one item's `Header`, ticks the render timer and compares `CaptureRenderedFrame()` before/after. If pixels change: build `TaskMenuPopup` (a `Popup` whose child is a themed `ContextMenu`-styled `ItemsControl` of those `MenuItem`s), keep `Build` returning the item list via a new `internal static IReadOnlyList<object> BuildItems(...)`, and swap the Quit item's `Header`/`Command` from a modifier callback. If pixels do NOT change: stop, record the result in the bead and close it as "won't do — decide-at-open stays". Modifier source: add `TaskbarNative.AddGlobalFlagsChangedMonitor(Action<ulong>)` (new, copy of `AddGlobalKeyDownMonitor` at line 800 with the flagsChanged event mask) plus the window's own `KeyDown`/`KeyUp` for the key-focused case; no polling timer. Submenus and arrow-key navigation must still work — that is the acceptance bar for the rewrite, not an optional extra.
- **Test first:** `TaskMenuPopupTests.An_open_in_tree_popup_repaints_when_an_item_header_changes` (the go/no-go probe above).
- **Steps:**
  - [ ] Write and run the probe; record the outcome in the bead notes.
  - [ ] (go) `BuildItems` + `TaskMenuPopup` + open/close/dismiss parity with the flyout (global mouse monitor, `_openTaskMenu`, key-focus scope).
  - [ ] (go) flagsChanged monitor + live swap; remove the decide-at-open comment block.
  - [ ] (go) keep `TaskButtonMenuTabsTests` green against `BuildItems`.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~TaskMenuPopupTests|FullyQualifiedName~TaskButtonMenuTabsTests"` → pass. HUMAN: open a task-button menu, then hold Option → item reads Force Quit; release → Quit.
- **Done when:** acceptance criterion met on device, or the probe failed and that is recorded; then `bd close bevel-hail --reason "<live swap shipped | probe showed no repaint — kept decide-at-open>"`

### bevel-e5vz — Explore: tab-level previews (CDP opt-in / extension) + window hover thumbnails beside tab list
- **Meta:** P3 · spike · size M · AGENT
- **State today:** Not started. Window-level capture exists (`IWindowManager.CaptureWindowAsync`, `WindowPreviewCache`, `TaskbarView.ShowTaskbarTooltip`). Two constraints the bead does not state: tabs attach to the APP, not a taskbar window (`AppTab` doc, `Dtos.cs:164-169`), so "the tab's window preview" needs a tab→window mapping that does not exist; and an open `MenuFlyout` never repaints, so nothing can appear beside the tab list after it opens unless the menu is re-hosted (bevel-hail).
- **Depends on:** bevel-hail (for option a's UI); bevel-y3cl (window title on Gecko tabs helps the mapping)
- **Files:** `docs/spike/2026-11-tab-previews.md` (new)
- **Decision:** Deliverable is the spike document only — no product code. It must answer, with measurements or citations: (a) can a scripting window (`WindowRef`) be matched to a `ForeignWindowId` by title for Chromium/Safari/iTerm/Gecko, and at what hit rate on a real session; if hail's popup lands, where the preview pane goes; (b) CDP: which browsers honour `--remote-debugging-port`, what a relaunch costs the user, the security exposure of an open local debug port, and the `Page.captureScreenshot` latency per background tab; (c) WebExtension + native messaging: manifest/permission set, store-review burden, what it adds for Firefox/Zen enumeration. End with a go/no-go per option and the beads to create for each "go".
- **Test first:** the document exists and each of (a)/(b)/(c) has a verdict line starting `GO:` or `NO-GO:`.
- **Steps:**
  - [ ] Measure (a) with a throwaway script against a live browser (not committed).
  - [ ] Research (b) and (c); cite sources.
  - [ ] Write the doc; create the follow-up beads it names.
- **Verify:** `grep -cE "^(GO|NO-GO):" docs/spike/2026-11-tab-previews.md` → `3`
- **Done when:** doc merged and follow-ups filed; then `bd close bevel-e5vz --reason "Spike written: docs/spike/2026-11-tab-previews.md"`

### bevel-tjr2 — EPIC: per-Space / multi-monitor taskbars
- **Meta:** P3 · epic · size L · AGENT (HUMAN picks the default mode in child 6)
- **State today:** No children exist and nothing is built. One bar: `TaskbarWindow` parks on `Screens.Primary` and joins all Spaces (`NSWindowCollectionBehaviorCanJoinAllSpaces`, `TaskbarWindow.cs:366`). Ready pieces: `BarGeometry` is already per-bar ("two displays may carry different components"); `ForeignWindow.IsOnOtherSpace` exists; the helper has `SpaceContext`. Gaps: `ForeignWindow` carries no display or Space id; `MacOSDesktopEnvironment.GetMonitorsAsync` invents ids as `m{i}` and never raises `MonitorsChanged` (declared at line 22, no invoke anywhere); `WorkAreaMitigator` takes one band; `TaskbarTheme` statics still hold bar metrics.
- **Depends on:** bevel-9ue (bar placement parametrised by edge is the same refactor as by display)
- **Files:** n/a (epic)
- **Decision:** Order below. Per-display bars come before per-Space bars: displays have public APIs, Spaces need private SkyLight symbols that the helper already treats as optional.
- **Children to create, in order:**
  1. "Show only current-Space windows (setting)" — filter in `TaskbarItemsProjector` on the existing `IsOnOtherSpace`; smallest user-visible step.
  2. "Stable monitor ids + MonitorsChanged on macOS" — `CGDirectDisplayID`-based `MonitorId`, raise the event on `applicationDidChangeScreenParameters`.
  3. "ForeignWindow carries DisplayId" — proto field + helper fill + core projection + delta on display change.
  4. "TaskbarWindow parametrised by screen" — replace `PositionAtPrimaryDisplayBottom` with a screen argument; one `TaskbarWindow` per display, created/removed on `MonitorsChanged`.
  5. "Per-bar window filter + multi-band WorkAreaMitigator" — each bar projects its display's windows; mitigator takes `Func<IReadOnlyList<PalRect>>`.
  6. "Multi-display mode setting" (HUMAN picks default: all windows on every bar / own-display only / primary only) + Properties UI.
  7. "Retire TaskbarTheme statics" — every reader uses `TaskbarWindow.Geometry`.
  8. "SPIKE: per-Space bars" — is a bar per Space (not CanJoinAllSpaces) viable, and can window→Space be read reliably.
- **Exit criterion:** with two displays attached, each shows a bar listing the windows chosen by the mode setting; unplugging a display removes its bar without a restart; per-Space behaviour has a written go/no-go.
- **Done when:** children 1-7 closed and 8 decided; then `bd close bevel-tjr2 --reason "Per-display bars shipped; per-Space decision recorded"`

### bevel-3qc — Work-area reservation (Dock-shim strict mode) + R9.4/R15 measurement
- **Meta:** P3 · feature · size L · HUMAN
- **State today:** Blocked/deferred by the 2026-07-12 decision; spike at `docs/spike/2026-07-12-dock-inset-measurement.md` (`89d9e4f`). Blocker (3) is cleared — bevel-0ml is CLOSED (multi-row bar shipped). Blockers (1) unreliable `visibleFrame` inset on the dev machine and (2) Dock-shim needs a visible Dock while Bevel auto-hides it, both stand. Contradiction with the "never ship a dead control" rule: `WorkAreaStrategy.DockShim` is selectable in Properties (`OnboardingWindow.axaml:90` `WASDockShim`, persisted at `OnboardingWindow.axaml.cs:325`) but no code anywhere reads `DockShim` (only the enum at `SettingsService.cs:1153`); `MacOSDesktopEnvironment.ReserveWorkAreaAsync` just logs (line 73-79).
- **Depends on:** reference hardware without BetterDisplay (owner)
- **Files:** `docs/spike/2026-07-12-dock-inset-measurement.md`, later `src/Bevel.Taskbar/TaskbarWindow.cs`, `src/Bevel.Pal.MacOS/MacOSDockController.cs`, `src/Bevel.Taskbar/OnboardingWindow.axaml(.cs)`
- **Decision:** Owner's call after one measurement. Recommendation: if the measurement is reliable, build DockShim (Dock visible at minimum size under the bar, bar height = inset rounded up to whole rows via `TaskbarTheme.HeightForRows`); if not, delete `WorkAreaStrategy.DockShim` and its radio (load path maps a stored `DockShim` to `Nudge`). Either outcome removes the dead radio. An AGENT can do the removal; only the measurement and the build/delete choice are HUMAN.
- **Test first:** the measurement itself: on a Mac with no virtual-display tool, Dock visible at bottom, `NSScreen.main.visibleFrame` vs `frame` gives a non-zero bottom inset that tracks the Dock size slider.
- **Steps:**
  - [ ] HUMAN: run the spike's measurement script on reference hardware; append numbers to the spike doc.
  - [ ] HUMAN: choose build or delete; write the choice in the bead.
  - [ ] AGENT (delete path): remove the enum member, the radio + note, the mapping in `OnWorkAreaChanged`; add a settings test `Stored_DockShim_loads_as_Nudge`.
  - [ ] AGENT (build path): file a fresh bead with the measured numbers; close this one.
- **Verify:** `grep -rn "DockShim" src --include='*.cs' --include='*.axaml'` → either zero hits (delete path) or a reader in `TaskbarWindow`/`MacOSDockController` (build path)
- **Done when:** the Properties dialog offers no strategy that does nothing; then `bd close bevel-3qc --reason "<DockShim removed | DockShim tracked in bevel-xxxx with measurements>"`
