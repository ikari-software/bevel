> Part of the [open-beads master plan](README.md). Read README.md first: it sets the rules, the order, and the rulings that override individual cards.
> Written 2026-11-18 from reading the tree at `51582c0`. Nothing here was built or run. `UNVERIFIED:` marks a claim the researcher could not confirm — check it before relying on it.

## Cluster E1: Start menu, search and taskbar behaviour

Owners today. `src/Bevel.Taskbar/StartMenu.axaml(.cs)` (771 lines) is the Start menu view for BOTH layouts
(`ClassicLayout` + `Blue2001Layout`) inside one `Popup` (`MenuPopup` → `PopupRoot` → `ItemsMenu`).
`Model/ShellModel.cs` owns `Programs`, `FrequentPrograms` (`RecomputeFrequent`, :532), `Windows`, focus
(`ApplyExclusiveFocus`) and the 2 s `ReconcileLoopAsync` that also polls `IAppBadgeSource`. `StartMenuViewModel`
is a 30-line projection. Task buttons are `TaskItemViewModel`/`TaskGroupViewModel` rendered by `TaskbarView`.

Search today (bevel-cezo, shipped): `Model/StartMenuFilter.cs` holds the query and a static `Rank<T>` (prefix >
word-start > substring). `StartMenu.ApplySearch` (:400) ranks `ShellModel.Programs` synchronously on the UI
thread and re-points the classic `ProgramsItem` cascade / Luna `Blue2001Pinned` at `_searchResults`. Apps only,
no provider seam, no sections, no cancellation. `Bevel.Core.Input.TypeToFind` is a different thing (jump
matcher, 700 ms idle reset) and stays separate. `ITabProvider` (`Bevel.Pal.Abstractions/Interfaces.cs:241`)
exists and is cached by `Model/TabPrefetch.cs`. There is NO `ISearchProvider` anywhere.

Target (ONE search architecture, decided in bevel-6sk6): `Bevel.Core/Search/` gets `ISearchProvider`,
`SearchHit`, `SearchRanker` (the ONE ranking function, moved out of `StartMenuFilter`) and `SearchSession`
(per-keystroke CancellationTokenSource + generation counter; drops late results). Providers publish whole
section lists through a callback; only the in-memory apps provider runs inline, everything else runs on the
thread pool and is marshalled back as one small batch. Sections are fixed-order (Programs, Files, Tabs), never
interleaved. Budgets: apps same frame (rank of 500 apps <= 2 ms), tabs first paint from cache then refresh
<= 1500 ms, files 150 ms debounce + 400 ms budget. `TypeToFind` reuse (bevel-psro) is for list JUMP surfaces only.

Code that contradicts the north star (found while reading): `StartMenu.axaml:315-325` `FlatFooterSearch` is a
dead fake search box with hardcoded `White` / `#68799A` / `#8A8598`; `StartMenu.axaml.cs:628-629` classic
"Log Off…" / "Shut Down…" only `Close()` (dead controls); `MacOSShellSession.LogOutAsync` throws
`NotImplementedException` (`MacOSPal.cs:49`); `ShellModel` polls badges every 2 s (no public push exists);
`TrayServiceImpl.swift` uses blocking `usleep` at :144, :225, :228, :488; `FolderPickerDialog.axaml:20`
`Background="White"`; `Bevel.Desktop/DesktopView.axaml` is a 14-line placeholder with `#3A6EA5`.

## Order

1. bevel-6sk6 — the search foundation; vtc6, pr4l and the search half of psro all hang off its seam.
2. bevel-cezo — P1, already shipped; needs only on-device QA + site shot, then close (do after 6sk6 so the shot shows sections).
3. bevel-szw4 — P1 in_progress; code is in, owner must verify on the two-display rig; bead text is stale.
4. bevel-vtc6 — Tabs section; the headline feature the foundation exists for.
5. bevel-pr4l — shrink StartMenu.axaml.cs once 6sk6 has moved the results into their own view.
6. bevel-qb19 — attention state; daily-use pain (Dock bounces off-screen, Bevel silent).
7. bevel-ijln — badges shipped (2d01f71); verify on device and close, feeds qb19.
8. bevel-4vce — two dead menu items + mislabelled footer; small, user-visible honesty fix.
9. bevel-xrx6 — frequent column quality (exclude-open + OS seeding).
10. bevel-jt30 — one settings toggle over an existing flag.
11. bevel-psro — TypeToFind in tree / folder picker / taskbar; Start menu and desktop grid drop out.
12. bevel-yv2m — shipped; on-device QA then close.
13. bevel-au94 — shipped; on-device QA then close.
14. bevel-c54t — shipped; on-device QA + one missing tray test, then close.
15. bevel-xpfl — shipped; on-device QA then close.
16. bevel-ww71 — spike done and feature shipped; close now.
17. bevel-0ai — four small leftovers, P3.

## Cards

### bevel-6sk6 — Start-menu inline search: results panel (apps, files, tabs) — the search foundation
- **Meta:** P2 · task · size L · AGENT
- **State today:** PARTLY DONE — apps-only filter shipped by bevel-cezo: `StartMenuFilter.Rank` (`src/Bevel.Taskbar/Model/StartMenuFilter.cs:68`), `StartMenu.ApplySearch` (`StartMenu.axaml.cs:400`), strips `ClassicSearchStrip` / `Blue2001SearchStrip`. No provider seam, no sections, no async, no cancellation. `FlatFooterSearch` (`StartMenu.axaml:315`) is a dead fake box.
- **Depends on:** none
- **Files:** `src/Bevel.Core/Search/ISearchProvider.cs` (new), `src/Bevel.Core/Search/SearchHit.cs` (new), `src/Bevel.Core/Search/SearchRanker.cs` (new), `src/Bevel.Core/Search/SearchSession.cs` (new), `src/Bevel.Taskbar/Model/AppsSearchProvider.cs` (new), `src/Bevel.Taskbar/StartSearchResultsView.axaml` + `.axaml.cs` (new), `src/Bevel.Taskbar/Model/StartMenuFilter.cs`, `src/Bevel.Taskbar/StartMenu.axaml(.cs)`, `tests/Bevel.Core.Tests/SearchSessionTests.cs` (new), `tests/Bevel.Taskbar.Tests/StartMenuSearchTests.cs`, `tests/Bevel.Taskbar.Tests/PerfBudgetTests.cs`
- **Decision:** (1) Seam, in `Bevel.Core.Search` (no Avalonia, ARCH-02): `public interface ISearchProvider { string SectionId {get;} string SectionTitle {get;} int Order {get;} bool RunsInline {get;} TimeSpan Debounce {get;} TimeSpan Budget {get;} Task QueryAsync(string query, int maxResults, Action<IReadOnlyList<SearchHit>> publish, CancellationToken ct); }`. `publish` REPLACES the section and may be called more than once (cache first, fresh later). `public sealed record SearchHit(string Title, string? Subtitle, int Rank, Func<Task> ActivateAsync, object? Tag = null);`. (2) Ranking lives in ONE place: `public static class SearchRanker { public static int RankOf(string text, string query); }` — move the body of `StartMenuFilter.RankOf` + `IsWordBoundary` there; `StartMenuFilter.Rank` calls it. Sections show in `Order`; inside a section sort by `Rank` then provider order; never interleave sections. (3) `public sealed class SearchSession(IReadOnlyList<ISearchProvider> providers, Action<Action> post)` with `void SetQuery(string query)`, `void Clear()`, `event Action<string, IReadOnlyList<SearchHit>>? SectionChanged`, `SearchHit? TopHit`. `SetQuery` cancels+disposes the previous `CancellationTokenSource`, increments `_generation`, calls `RunsInline` providers synchronously, and for the rest does `Task.Run` after `Task.Delay(Debounce, ct)` with `cts.CancelAfter(Budget)`; every publish is wrapped to check the generation and go through `post`. (4) `AppsSearchProvider` (`RunsInline = true`, Order 0, title "Programs") ranks `ShellModel.Programs` — inline because that collection is UI-thread-owned. (5) UI: one `StartSearchResultsView` (virtualized `ListBox`, header rows per section, all brushes `DynamicResource Bevel.Brush.*`) shown in place of `ItemsMenu` (classic) and `Blue2001Pinned` (Luna) while `IsSearchActive`; this replaces the cascade re-pointing in `ShowClassicResults`. Delete `FlatFooterSearch`. Files section is NOT in this card (see last step).
- **Test first:** `tests/Bevel.Core.Tests/SearchSessionTests.cs` (new): `A_newer_query_cancels_the_older_one_and_drops_its_late_results`, `Inline_providers_publish_before_SetQuery_returns`, `A_provider_over_budget_is_cancelled_and_its_section_stays_empty`, `Sections_come_back_in_provider_order`. Plus `PerfBudgetTests.Perf_search_rank_500_apps_under_budget` (new; <= 2 ms).
- **Steps:**
  - [ ] Add the four `Bevel.Core/Search` files exactly as in Decision; `SearchSessionTests` green using fake providers and `post = a => a()`.
  - [ ] Move ranking into `SearchRanker`; `StartMenuSearchTests.Ranking_puts_name_prefixes_first_then_word_starts_then_substrings` still green.
  - [ ] Add `AppsSearchProvider`; hit `ActivateAsync` executes `ProgramItemViewModel.LaunchCommand`, `Tag` is the VM (icon via `EnsureIcon`).
  - [ ] Build `StartSearchResultsView`; in `StartMenu` create one `SearchSession` with `post = a => Dispatcher.UIThread.Post(a)`, call `SetQuery(_filter.Query)` from `ApplySearch`, `Clear()` from `ClearSearch`; Enter activates `TopHit` (keep `EnterShouldLaunchTopMatch`).
  - [ ] Update the layout-specific assertions in `StartMenuSearchTests` to read the results view; keep all 14 behaviours.
  - [ ] Delete `FlatFooterSearch` markup; re-harvest `RenderStartMenuSearchTest` shots into `site/shots/`.
  - [ ] Report for the planner (do not run `bd create`): new bead "Start search: Files section — move `SearchService` from `Bevel.FileManager/FileOperations` to `Bevel.Core/Vfs`, `FilesSearchProvider` over Documents/Desktop/Downloads, MaxDepth 2, MaxResults 20, Debounce 150 ms, Budget 400 ms"; new bead "Start search: Settings section".
- **Verify:** `dotnet test tests/Bevel.Core.Tests/Bevel.Core.Tests.csproj --filter "FullyQualifiedName~SearchSession"` and `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~StartMenuSearch|FullyQualifiedName~Perf_search"` → all pass.
- **Done when:** typing in either layout shows a "Programs" section from `AppsSearchProvider` through `SearchSession`, no code path re-points `ProgramsItem.ItemsSource` at results; then `bd close bevel-6sk6 --reason "ISearchProvider/SearchSession seam + sectioned results view; files/settings split into follow-up beads"`

### bevel-cezo — Start menu: type-to-search while open
- **Meta:** P1 · bug · size S · HUMAN
- **State today:** LIKELY DONE — handlers on `PopupRoot` and `ItemsMenu` (`StartMenu.axaml.cs:312-319`), `OnMenuTextInput` :336, `OnMenuKeyDown` :345, two-step Escape in `TaskbarView.OnTaskbarKeyDown` (`TaskbarView.axaml.cs:1307-1313`), 14 tests in `tests/Bevel.Taskbar.Tests/StartMenuSearchTests.cs` + `RenderStartMenuSearchTest.cs`; feature commit 370003d (the 9532d4d hash in the bead notes no longer resolves). Only on-device QA remains.
- **Depends on:** bevel-6sk6 (so QA and the site shot are done once, on the final results view)
- **Files:** `site/index.html`, `site/shots/` (screenshot only)
- **Decision:** No code. One filter mechanism for both layouts stays; `TypeToFind` is deliberately not used here.
- **Test first:** Manual: packaged app (`./packaging/macos/build-app.sh && ./packaging/macos/dev-sign.sh`), open Start, type `fi`.
- **Steps:**
  - [ ] HUMAN: type with the menu open, press Down into the results, keep typing → the query must keep growing (nested-popup routing).
  - [ ] HUMAN: with the menu open and no query, press `p` and `h` → they must filter, not trigger the `_Programs` / `_Help` mnemonics.
  - [ ] If either fails, file a new bug with the exact key sequence; do not close.
  - [ ] Harvest the search render into `site/shots/` and reference it from `site/index.html`.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~StartMenuSearch"` → all pass.
- **Done when:** both manual checks pass on a real macOS run; then `bd close bevel-cezo --reason "type-to-search shipped (370003d), on-device routing + mnemonic checks passed"`

### bevel-szw4 — Space switch: first taskbar click visibly flips other-display Spaces
- **Meta:** P1 · bug · size S · HUMAN
- **State today:** PARTLY DONE and the bead text is STALE. `SkyLight.switchToSpace(ofWindow:)` (`native/helper-macos/Sources/BevelHelper/SkyLight.swift:176`) parks the pointer on the window centre via `parkPointer(atCocoa:)` BEFORE Hide/Show/SetCurrent and deliberately does NOT warp back: its doc comment (:170-174) records that restoring the cursor in the same turn cancels the Dock's paint, and that a synthetic click is not sent because Jump/RDP would eat it. Commit 7574e64. The bead's "warp cursor back" acceptance contradicts what was measured.
- **Depends on:** none
- **Files:** none (verification); `tools/eval/space_activate_eval.py`, `tools/eval/HelperActivateJump` exist for the eval
- **Decision:** Accept "pointer follows focus" (cursor ends on the target display) as the shipped behaviour; rewrite the acceptance to "first click visibly switches; cursor lands on the switched display". Restoring the cursor is only allowed later via a delayed restore proven by the eval — that is the owner's call, not the executor's.
- **Test first:** `python3 tools/eval/space_activate_eval.py` on the two-display machine with a Jump session on the left display.
- **Steps:**
  - [ ] HUMAN: with the current helper in `dist/`, click a Jump-session task button on the primary bar once → the other display must visibly change Space on the first click.
  - [ ] HUMAN: decide whether the parked cursor is acceptable. If yes, close. If no, file a new bead "delayed cursor restore after Space paint" (do not reopen the same-turn restore).
- **Verify:** `cd native/helper-macos && swift test --filter WindowPolicyTests` → pass (ActivationPlan steps unchanged).
- **Done when:** first-click flip confirmed on device; then `bd close bevel-szw4 --reason "first click flips (park pointer, 7574e64); same-turn cursor restore proven to cancel the paint"`

### bevel-vtc6 — Start-menu search over tabs: type-to-jump across every ITabProvider app
- **Meta:** P2 · task · size M · AGENT+HUMAN-VERIFY
- **State today:** Not started. `ITabProvider` (`src/Bevel.Pal.Abstractions/Interfaces.cs:241`: `SupportsApp`, `GetTabsAsync`, `ActivateAsync`), `AppTab` (`Dtos.cs:176`), and the cache `TabPrefetch.Warm(string? bundleId)` (`src/Bevel.Taskbar/Model/TabPrefetch.cs:72`, budget/freshness 1500 ms at `TaskbarView.axaml.cs:1072`) all exist. `FakeTabProvider` (`src/Bevel.Pal.Fake/FakePal.cs:92`) has 3 fixture tabs for app `fake.browser`.
- **Depends on:** bevel-6sk6
- **Files:** `src/Bevel.Taskbar/Model/TabsSearchProvider.cs` (new), `src/Bevel.Taskbar/StartMenu.axaml.cs`, `src/Bevel.Taskbar/TaskbarView.axaml.cs` (:584 construction of `StartMenu`, :1060 `EnsureTabPrefetch`), `tests/Bevel.Taskbar.Tests/TabsSearchProviderTests.cs` (new)
- **Decision:** `internal sealed class TabsSearchProvider : ISearchProvider` with ctor `(Func<IReadOnlyList<string>> runningBundleIds, Func<string, Task<IReadOnlyList<TaskButtonMenu.TabMenuRow>>?> warm, ITabProvider tabs)`. Reuse `TabPrefetch` through `warm` (one cache owner, favicons already decoded) — do NOT call `GetTabsAsync` directly. `RunsInline=false`, `Order=2`, title "Tabs", `Debounce=0`, `Budget=1500 ms`. `QueryAsync`: publish immediately from the rows of the last completed warm (instant first paint), then await all `warm(bundleId)` in parallel and publish again. Match `SearchRanker.RankOf` on `Title`, falling back to `Url` with rank+3; cap 8 rows. The bead's "~200 ms" is wrong for real apps (an osascript spawn is 100-650 ms per `TabPrefetch` docs), hence cache-first. Activation must not use a stale row (`AppTab` refs are positional): `ActivateAsync` re-warms that bundle, finds the row with the same `Url` (else same `Title`), and calls `ITabProvider.ActivateAsync(freshRow.Tab)`; no match = no-op. Call `warm` for every running supported app from `StartMenu.OpenAsync` so the cache is hot before the first keystroke.
- **Test first:** `TabsSearchProviderTests`: `Publishes_cached_rows_first_then_fresh_rows`, `Matches_title_before_url`, `Activation_re_resolves_the_tab_by_url_and_ignores_a_stale_index`, `Unsupported_apps_are_never_queried` (assert `SupportsApp` gate via a counting fake like `TabPrefetchTests.CountingTabs`).
- **Steps:**
  - [ ] Write the provider + tests above against a fake `warm`.
  - [ ] Pass `EnsureTabPrefetch` and a running-bundle-id getter (`ShellModel.Windows` → `TaskItemViewModel.BundleId`, distinct) into `StartMenu`; add the provider to the `SearchSession` list.
  - [ ] Render favicon from `TabMenuRow.Icon` via `SearchHit.Tag`.
  - [ ] HUMAN-VERIFY: first search after install triggers one macOS Automation consent prompt per browser; denial must just yield an empty Tabs section.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~TabsSearchProvider"` → all pass; `dotnet run --project src/Bevel.App -- --pal=fake`, type `docs` → a "Tabs" section lists "Docs".
- **Done when:** a tab typed by title is activated by Enter under `--pal=fake` and on a real browser; then `bd close bevel-vtc6 --reason "Tabs section via TabsSearchProvider over TabPrefetch; activation re-resolves by URL"`

### bevel-pr4l — StartMenu.axaml.cs grew 38% absorbing the search UI — extract the search strip
- **Meta:** P3 · task · size M · AGENT
- **State today:** Still true: `StartMenu.axaml.cs` is 771 lines, search block is :277-472; strips are inline at `StartMenu.axaml:68-103` (`ClassicSearchStrip`) and :285-305 (`Blue2001SearchStrip`). bevel-6sk6 moves the RESULTS out; this card moves the strip + key wiring.
- **Depends on:** bevel-6sk6
- **Files:** `src/Bevel.Taskbar/StartSearchStrip.axaml` + `.axaml.cs` (new), `src/Bevel.Taskbar/StartMenuSearchInput.cs` (new), `src/Bevel.Taskbar/StartMenu.axaml(.cs)`, `tests/Bevel.Taskbar.Tests/StartMenuSearchTests.cs`
- **Decision:** One `StartSearchStrip` UserControl with styled properties `Query` (string), `CountText` (string) and a `Classes` switch `classic` / `luna` for the two frames (keep nested `Border`s, NOT `ClassicBorderDecorator` — see the comment at `StartMenu.axaml:62-67`). One `internal sealed class StartMenuSearchInput` owning `StartMenuFilter`, `AlreadySeen`, `OnMenuTextInput`, `OnMenuKeyDown`, with `void Attach(params Interactive[] hosts)` and events `QueryChanged`, `Cleared`, `EnterPressed(object? source)`. `StartMenu` calls `Attach(PopupRoot, ItemsMenu)` — both hosts, KeyDown tunnel + TextInput bubble, `handledEventsToo: true`, exactly as `WireTypeToSearch` does today. Public surface of `StartMenu` (`IsSearchActive`, `SearchQuery`, `SearchResults`, `ClearSearchIfActive`, `EnterShouldLaunchTopMatch`) stays, forwarding.
- **Test first:** existing `StartMenuSearchTests.Search_listens_on_the_popups_own_root_not_on_the_usercontrol` must stay green unchanged; add `Search_input_is_attached_to_both_the_popup_root_and_the_inner_menu` (asserts both hosts receive the handler).
- **Steps:**
  - [ ] Create `StartMenuSearchInput`, move the five members, keep per-event de-dup.
  - [ ] Create `StartSearchStrip`, replace both inline strips with it.
  - [ ] Run the search tests; no assertion may be weakened.
  - [ ] Check `wc -l src/Bevel.Taskbar/StartMenu.axaml.cs` <= 600.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~StartMenuSearch"` → all pass.
- **Done when:** search markup and input wiring live in their own files, line count target met, tests green; then `bd close bevel-pr4l --reason "search strip + input wiring extracted; both attachment points preserved"`

### bevel-qb19 — Taskbar buttons need an attention state
- **Meta:** P2 · task · size M · AGENT+HUMAN-VERIFY
- **State today:** Nothing exists: no `Attention`/`DemandsAttention` symbol in `src/` or `proto/`. Badges exist: `IAppBadgeSource.GetBadgesAsync` polled in `ShellModel.ReconcileLoopAsync` (`ShellModel.cs:190-204`) → `ApplyBadges` :424 → `TaskItemViewModel.ApplyBadge` (:252 region). No reduced-motion setting exists.
- **Depends on:** none blocking. Advisory: bevel-ijln (owner device check of the same badge source) — do the agent work without waiting for it
- **Files:** `src/Bevel.Taskbar/Model/TaskItemViewModel.cs`, `src/Bevel.Taskbar/Model/TaskGroupViewModel.cs`, `src/Bevel.Taskbar/Model/ShellModel.cs`, `src/Bevel.Taskbar/TaskbarView.axaml`, `src/Bevel.Core/SettingsService.cs`, `src/Bevel.Taskbar/OnboardingWindow.axaml(.cs)`, `tests/Bevel.Taskbar.Tests/TaskAttentionTests.cs` (new), `tests/Bevel.Taskbar.Tests/RenderTaskAttentionTest.cs` (new)
- **Decision:** v1 signal = "the app's badge label appeared or changed while the app is not focused" — it is already sourced, public-API, and testable through `FakeAppBadgeSource`. UNVERIFIED: whether the Dock AX tree exposes the bounce from `NSApplication.requestUserAttention`; that is a HUMAN spike on device (last step), not a blocker. Add `bool NeedsAttention` on `TaskItemViewModel` and `TaskGroupViewModel`, set in `ShellModel.ApplyBadges` when the label differs from the previous one and `!IsFocused`, cleared in `ApplyExclusiveFocus` when that button becomes focused. Visual: a light sweep left-to-right, 3 passes of 900 ms, then a steady tint (bounded, like classic 3 flashes + stay lit) — a `LinearGradientBrush` overlay `Border` in both task-button templates, colours from `Bevel.Brush.Highlight` at reduced opacity (works on Win2000 grey and Luna gloss). Setting `TaskbarAttentionEffect { Sweep, Tint, Off }`, json key `taskbarAttentionEffect`, default `Sweep`; `Tint` is the reduced-motion choice.
- **Test first:** `TaskAttentionTests`: `A_badge_change_on_an_unfocused_app_raises_attention`, `Focusing_the_window_clears_attention`, `A_badge_change_on_the_focused_app_does_not_raise_attention`, `Off_setting_never_raises_attention`. `RenderTaskAttentionTest.Render_attention_state(theme)` for both skins, ticking the render timer to the steady tint (no bare sleeps), `[Collection("TaskbarTheme")]`.
- **Steps:**
  - [ ] Add the property + ShellModel transitions; unit tests green.
  - [ ] Add the setting (SetOrPrune + parse + property + enum, additive like `TaskbarReclickMinimize` at `SettingsService.cs:549/718/949/1105`) and a combo under Settings > Taskbar > Window buttons.
  - [ ] Add the overlay + bounded animation to both templates in `TaskbarView.axaml`, bound to `NeedsAttention`; never assign `IsChecked` from code.
  - [ ] Render tests for both skins.
  - [ ] HUMAN spike: on device, make an app call `requestUserAttention` and dump its `AXApplicationDockItem` attributes; if a usable attribute exists, file a follow-up bead to add it as a second trigger in `MacOSAppBadgeSource`.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~TaskAttention"` → all pass.
- **Done when:** an unfocused app whose badge changes sweeps 3 times then stays tinted, clears on activation, and can be turned off; then `bd close bevel-qb19 --reason "attention state from badge change; bounded sweep; setting taskbarAttentionEffect"`

### bevel-ijln — Taskbar: app unread/badge counts (Slack-style)
- **Meta:** P2 · feature · size S · HUMAN
- **State today:** LIKELY DONE — commit 2d01f71 / merge 5bea58c. `IAppBadgeSource` (`src/Bevel.Pal.Abstractions/AppBadges.cs:43`), `MacOSAppBadgeSource` reads the Dock's `AXApplicationDockItem` / `AXStatusLabel` (:117-120), `FakeAppBadgeSource`, `Model/TaskBadges.cs`, pill `src/Bevel.Taskbar/TaskBadge.axaml`, tests `tests/Bevel.Taskbar.Tests/TaskBadgeTests.cs`. Inline pill before the title (option 2) is the shipped UX. It is a 2 s poll: no public push exists (documented exception to "push, not poll").
- **Depends on:** none
- **Files:** none
- **Decision:** Do not rebuild. Icon-overlay layout (option 1) is NOT part of this bead; file it separately only if the owner asks.
- **Test first:** Manual on device with Accessibility granted to the shell.
- **Steps:**
  - [ ] HUMAN: with Slack or Messages showing a Dock badge, confirm the same number appears as a pill before the title on its task button within ~2 s, in both skins.
  - [ ] If it shows: close. If not: `grep FATAL "$TMPDIR/bevel-restart.log"` and file a bug with the app's bundle id.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~TaskBadge"` → all pass.
- **Done when:** a real badge is seen on a task button; then `bd close bevel-ijln --reason "badge pill shipped in 2d01f71, verified on device against <app>"`

### bevel-4vce — Start menu: unify shell-management actions + fix Luna 'Log Off' mislabel
- **Meta:** P2 · task · size M · AGENT+HUMAN-VERIFY
- **State today:** PARTLY DONE — f45348f added honest "Restart Bevel"/"Quit Bevel" rows to Luna (`StartMenu.axaml:541-571` → `OnRestartClick` / `OnQuitClick`). Remaining lies: Luna footer `OnBlue2001LogOffClick` → `_restart()` and `OnBlue2001TurnOffClick` → `_quit()` (`StartMenu.axaml.cs:706-707`); classic `OnLogOffClick` / `OnShutDownClick` only `Close()` (:628-629) — dead controls. `IShellSession.LogOutAsync(LogoutKind kind)` exists (`Interfaces.cs:105`, `LogoutKind { LogOut, Restart, Shutdown, Lock }`) but `MacOSShellSession.LogOutAsync` throws `NotImplementedException` (`src/Bevel.Pal.MacOS/MacOSPal.cs:49`); Windows implements it (`WindowsShellSession.cs`).
- **Depends on:** none
- **Files:** `src/Bevel.Taskbar/StartMenu.axaml.cs`, `src/Bevel.Taskbar/ShutDownDialog.cs` (new, code-built like `RunDialog.cs`), `src/Bevel.Pal.MacOS/MacOSPal.cs`, `src/Bevel.Taskbar/TaskbarView.axaml.cs` (:584), `src/Bevel.App/App.axaml.cs` (:355-372 wiring), `tests/Bevel.Taskbar.Tests/StartMenuTests.cs`
- **Decision:** Keep the footer wording and glyphs (owner's "footer unchanged" choice) and make the words true. "Log Off" in BOTH layouts → `IShellSession.LogOutAsync(LogoutKind.LogOut)` (the OS shows its own confirmation). "Shut Down…" (classic) and "Turn Off" (Luna) → open `ShutDownDialog`, the Win2000 "What do you want the computer to do?" list: Quit Bevel (default), Restart Bevel, Lock, Log off, Restart computer, Shut down. Bevel entries call the existing `_quit` / `_restart`; OS entries call `LogOutAsync`. Add ctor parameter `Func<LogoutKind, Task>? session = null` to `StartMenu`; when null the OS entries are disabled (visible, not hidden). macOS `LogOutAsync`: send the loginwindow Apple Event (`kAELogOut`/`kAERestart`/`kAEShutDown` — these show the system confirm) and for `Lock` UNVERIFIED: pick the public mechanism during implementation; run off the UI thread.
- **Test first:** `StartMenuTests`: `Luna_log_off_calls_session_logout_not_restart`, `Luna_turn_off_opens_the_shut_down_dialog`, `Classic_log_off_and_shut_down_are_wired` (recording fake for `session`, `_restart` counter stays 0).
- **Steps:**
  - [ ] Add the `session` seam and rewire the four handlers; tests green.
  - [ ] Build `ShutDownDialog` on `BevelWindow` with theme tokens only.
  - [ ] Implement `MacOSShellSession.LogOutAsync`; wire `session:` from `IShellSession` in `App.axaml.cs`.
  - [ ] HUMAN-VERIFY on device: Log Off shows the macOS confirmation and Cancel leaves Bevel running with the Dock state intact.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~StartMenuTests"` → all pass; `grep -n "OnBlue2001LogOffClick" src/Bevel.Taskbar/StartMenu.axaml.cs` shows no `_restart`.
- **Done when:** no Start-menu item does something other than its label in either theme; then `bd close bevel-4vce --reason "Log Off = OS logout, Turn Off/Shut Down = ShutDownDialog; Restart/Quit Bevel explicit in both themes"`

### bevel-xrx6 — Frequency-based app suggestions: exclude-open filter + OS usage seeding
- **Meta:** P2 · feature · size L · AGENT+HUMAN-VERIFY
- **State today:** Neither part exists. `ShellModel.RecomputeFrequent` (`ShellModel.cs:532-560`) ranks newly-added then `ProgramUsageStore.LaunchCount`, does a full `FrequentPrograms.Clear()` + re-add, and is called only on `Programs.CollectionChanged`, `FrequentCap` set, and a launch. `ProgramUsageStore` has `LaunchCount`, `FirstSeen`, `MarkSeen`, `RecordLaunch`, `Set`, `BeginBatch`. No `IUsageProvider`, no `TaskbarStartMenuFrequentExcludeOpen` / `TaskbarSeedUsageFromOS` settings. `InstalledApp.AppId` vs `TaskItemViewModel.BundleId`/`AppId` key match is UNVERIFIED — check what `MacOSAppEnvironment` puts in `InstalledApp.AppId` before writing the comparison.
- **Depends on:** none
- **Files:** `src/Bevel.Taskbar/Model/ShellModel.cs`, `src/Bevel.Taskbar/Model/ProgramUsageStore.cs`, `src/Bevel.Pal.Abstractions/Interfaces.cs`, `src/Bevel.Pal.Abstractions/Dtos.cs`, `src/Bevel.Pal.MacOS/MacOSUsageProvider.cs` (new), `src/Bevel.Pal.Fake/FakePal.cs`, `src/Bevel.App/CompositionRoot.cs`, `src/Bevel.App/App.axaml.cs` (:309, :381), `src/Bevel.Core/SettingsService.cs`, `src/Bevel.Taskbar/OnboardingWindow.axaml(.cs)`, `tests/Bevel.Taskbar.Tests/ProgramCurationTests.cs`
- **Decision:** Part A: `public bool FrequentExcludeOpen { get; set; }` on `ShellModel` (pushed from `App.axaml.cs` beside `FrequentCap`); in `RecomputeFrequent` filter the `frequent` sequence (not `newly`) by "no item in `Windows` has the same app id", BEFORE `Take(cap)` so backfill is automatic. Recompute when the SET of open app ids changes (compare a cached `HashSet<string>` in the window add/remove paths; do not recompute on title changes). Replace Clear+re-add with a diff via the existing `ObservableReconciler` so the column does not flicker (north star 5). Setting `TaskbarStartMenuFrequentExcludeOpen`, key `taskbarStartMenuFrequentExcludeOpen`, default true. Part B: `public interface IUsageProvider { ValueTask<IReadOnlyList<AppUsageRecord>> GetUsageAsync(CancellationToken ct = default); }` and `public sealed record AppUsageRecord(string AppId, DateTime? LastUsedUtc, int? Count);` plus `NullUsageProvider`. macOS impl: `mdfind`/`mdls` child process reading `kMDItemLastUsedDate` for the already-enumerated app paths, off-thread, once per profile (flag stored in the usage file), started after `MarkProgramsLoaded`. Seed rule: `ProgramUsageStore.SeedIfHigher(string appId, int seedCount)` (new) = max(seed, observed); with no count, map recency rank to synthetic counts (most recent 10 apps → 10..1). Setting `TaskbarSeedUsageFromOS`, default true. No `knowledgeC.db`. Windows/Linux get `NullUsageProvider` (file follow-up beads for UserAssist / Zeitgeist).
- **Test first:** `ProgramCurationTests`: `Open_apps_are_excluded_and_the_column_backfills_to_the_cap`, `Exclude_open_off_keeps_raw_frequency_order`, `Closing_the_last_window_returns_the_app_to_the_column`, `Seed_never_lowers_an_observed_count`, `Seeding_runs_once_per_profile` (fake `IUsageProvider`).
- **Steps:**
  - [ ] Part A model + setting + tests, in one commit.
  - [ ] Settings UI row under the existing frequent-count slider (`FrequentCountSlider`).
  - [ ] Part B seam + Null/Fake providers + store method + tests.
  - [ ] `MacOSUsageProvider` + DI registration in all three PAL blocks of `CompositionRoot`.
  - [ ] HUMAN-VERIFY: fresh `~/.config/bevel` profile on macOS shows recently used apps in the Luna left column on first open.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~ProgramCuration"` → all pass.
- **Done when:** both acceptance parts hold with tests; then `bd close bevel-xrx6 --reason "exclude-open with backfill + IUsageProvider seeding (macOS Spotlight last-used)"`

### bevel-jt30 — Setting: list windows on other Spaces (default on)
- **Meta:** P2 · feature · size S · AGENT
- **State today:** Other-Space windows are listed unconditionally. The flag already reaches .NET: helper sets `win.isOnOtherSpace` (`WindowServiceImpl.swift:733`, computed against `SkyLight.currentSpaceIDs()` which is a SET, so it is already display-aware), proto field 12, `ForeignWindow.IsOnOtherSpace` (`Dtos.cs:111`), mapped at `MacOSWindowManager.cs:310`. No setting exists; `TaskItemViewModel` does not carry the flag (`Update` at :231 copies only Title/IsMinimized).
- **Depends on:** none
- **Files:** `src/Bevel.Core/SettingsService.cs`, `src/Bevel.Taskbar/Model/TaskItemViewModel.cs`, `src/Bevel.Taskbar/Model/TaskbarItemsProjector.cs`, `src/Bevel.Taskbar/Model/TaskbarViewModel.cs`, `src/Bevel.Taskbar/TaskbarView.axaml.cs` (`ApplyLiveSettings` :371), `src/Bevel.Taskbar/OnboardingWindow.axaml(.cs)`, `tests/Bevel.Taskbar.Tests/ShellModelTests.cs`
- **Decision:** Filter in the taskbar PROJECTION, not in the helper: no proto/helper change, the helper keeps sending everything (activation and `WorkAreaMitigator` still need other-Space windows), and the toggle applies live. Add `bool IsOnOtherSpace` to `TaskItemViewModel` (set in ctor and `Update`), `TaskbarItemsProjector.SetShowOtherSpaces(bool show)` next to `SetSort` (:56), forwarded by `TaskbarViewModel`, called from `ApplyLiveSettings` beside `_vm?.SetSort(...)`. Setting `TaskbarShowOtherSpaceWindows`, key `taskbarShowOtherSpaceWindows`, default true. A windowless app-presence button (`Id` starting `app:`) is never filtered.
- **Test first:** `tests/Bevel.Taskbar.Tests/TaskbarGroupingTests.cs` or a new `OtherSpaceFilterTests.cs` (new): `Other_space_windows_are_listed_by_default`, `Turning_the_setting_off_drops_other_space_windows_and_keeps_current_ones`, `A_window_that_moves_to_the_current_space_reappears` (drive `Update` with `IsOnOtherSpace=false`).
- **Steps:**
  - [x] Add the VM property and keep it current in `Update`.
  - [x] Add the projector filter + tests.
  - [x] Add the setting (same four additive edits as `WindowlessAppsLast`, `SettingsService.cs:551/722/957`) and a checkbox in Settings > Taskbar.
  - [x] Wire `ApplyLiveSettings` and the startup mirror block at `TaskbarView.axaml.cs:173-190`.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~OtherSpace"` → all pass.
- **Done when:** the toggle hides/shows other-Space buttons live with no restart; then `bd close bevel-jt30 --reason "taskbarShowOtherSpaceWindows (default on), filtered in TaskbarItemsProjector"`

### bevel-psro — Reuse TypeToFind in tree / Start menu / FolderPicker / taskbar / desktop grid
- **Meta:** P2 · task · size M · AGENT
- **State today:** Only `ItemView` uses it (`src/Bevel.FileManager/Components/ItemView.axaml.cs:107`, `OnTextInput` :903). Of the five listed surfaces: Start menu is DONE DIFFERENTLY on purpose (filter, bevel-cezo/6sk6 — do not add `TypeToFind` there); desktop grid DOES NOT EXIST (`src/Bevel.Desktop/DesktopView.axaml` is a 14-line placeholder). Real remaining surfaces: the Filer folder tree (`TreeView x:Name="TreeView"`, `FileManagerWindow.axaml:25`, nodes built by `CreateTreeNode` at `FileManagerWindow.axaml.cs:581`), `FolderPickerDialog` (`TreeView`, `Components/FolderPickerDialog.axaml:20`), and the task-button strip in `TaskbarView`.
- **Depends on:** none (independent of bevel-6sk6: jump, not search)
- **Files:** `src/Bevel.FileManager/FileManagerWindow.axaml.cs`, `src/Bevel.FileManager/Components/FolderPickerDialog.axaml.cs`, `src/Bevel.Taskbar/TaskbarView.axaml.cs`, `src/Bevel.FileManager/Components/TreeTypeAhead.cs` (new), `tests/Bevel.FileManager.Tests/TreeTypeAheadTests.cs` (new)
- **Decision:** Scope = three surfaces, one commit each. For both trees add one shared helper `internal static class TreeTypeAhead { public static void Attach(TreeView tree, TypeToFind matcher); }` that on `TextInputEvent` flattens the VISIBLE (expanded) `TreeViewItem`s in display order, calls `matcher.Match(text, visible, static i => i.Header?.ToString() ?? "", currentIndex)` and sets `tree.SelectedItem` + `BringIntoView()`. Construct `TypeToFind` with a `Stopwatch`-based clock as `ItemView` does. Taskbar: handle `TextInputEvent` on the strip only while the bar is in menu-scoped key focus (`EnterMenuScope`, `TaskbarView.axaml.cs:1321`), match over the realized task items' titles and move keyboard focus; do nothing when a Start menu is open (its filter owns typing). Never decode `Key` enums.
- **Test first:** `TreeTypeAheadTests`: `Typing_a_letter_selects_the_next_visible_node_with_that_prefix`, `Repeating_the_letter_cycles`, `Collapsed_children_are_not_matched`, `Digits_and_accented_characters_match`.
- **Steps:**
  - [x] `TreeTypeAhead` + tests; attach in `FileManagerWindow` (one commit).
  - [x] Attach in `FolderPickerDialog` (one commit); while there replace `Background="White"` with `{DynamicResource Bevel.Brush.Window}`.
  - [x] Taskbar strip handler + a test in `tests/Bevel.Taskbar.Tests/TaskbarAccessibilityTests.cs` (one commit).
  - [x] Update the CODEMAP "Type-ahead is a shared service" note: Start menu = filter, desktop grid = not built.
- **Verify:** `dotnet test tests/Bevel.FileManager.Tests/Bevel.FileManager.Tests.csproj --filter "FullyQualifiedName~TreeTypeAhead"` → all pass; `grep -rn "TypeToFind" src --include='*.cs' | grep -v /obj/` lists FileManagerWindow, FolderPickerDialog (via TreeTypeAhead) and TaskbarView.
- **Done when:** the three surfaces jump on typing; then `bd close bevel-psro --reason "TypeToFind wired to folder tree, folder picker, task strip; Start menu uses the search filter; desktop grid not built yet"`

### bevel-yv2m — macOS Zoom ignores Bevel taskbar height
- **Meta:** P2 · bug · size S · HUMAN
- **State today:** LIKELY DONE — `src/Bevel.Taskbar/WorkAreaMitigator.cs` now rate-limits on the offending frame and runs a 400 ms verify pass; `FakeWindowManager.RepositionAsync` applies moves; tests in `tests/Bevel.Taskbar.Tests/WorkAreaMitigatorTests.cs`. The mitigator reads `IProjectedWindowSource` (no helper hop). "No overlap ever" is not achievable on macOS (visibleFrame is system-owned); the real criterion is "corrected in < 1 s".
- **Depends on:** none
- **Files:** none
- **Decision:** Accept "corrected within 1 s" as the acceptance; a true reservation is a separate Dock-shim bead the owner may file.
- **Test first:** Manual QA list in the bead notes (7 items).
- **Steps:**
  - [ ] HUMAN: packaged build; green-button Zoom Finder, Safari, TextEdit → each snaps above the bar in under a second; zoom/unzoom/zoom within 2 s also corrects.
  - [ ] HUMAN: repeat with Dock auto-hide off and with a 2-row / Big bar; confirm Bevel's own bar never moves.
  - [ ] Any failure → new bug with the app and the frame from `$TMPDIR/bevel-restart.log`.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~WorkAreaMitigator"` → all pass.
- **Done when:** QA items 2-7 pass; then `bd close bevel-yv2m --reason "Nudge corrects zoom < 1s (frame-keyed back-off + verify pass); reservation impossible on macOS by design"`

### bevel-au94 — Disable or remappable click-again-to-minimize on task buttons
- **Meta:** P2 · feature · size S · HUMAN
- **State today:** LIKELY DONE — enum `TaskbarReclickMinimize { Click, OptionClick, Never }` (`src/Bevel.Core/SettingsService.cs:1105`, default `Click` :949), `Model/TaskButtonClickPolicy.cs`, live apply at `TaskbarView.axaml.cs:391`, combo `ReclickMinimizeCombo` (`OnboardingWindow.axaml:237`), `tests/Bevel.Taskbar.Tests/TaskButtonReclickTests.cs`, `docs/design/taskbar-reclick-minimize.md`. Its blocker bevel-hx63 is CLOSED.
- **Depends on:** none (bevel-hx63 closed)
- **Files:** none
- **Decision:** Default stays `Click`. No freshness probe is added unless the sticky-focus case reproduces.
- **Test first:** Manual on device.
- **Steps:**
  - [ ] HUMAN: set Re-click = Option-click; plain click raises, Option+click minimizes; set Never → re-click never minimizes.
  - [ ] HUMAN: open Spotlight / a full-screen app and check no task button stays pressed. If one does, file the follow-up described in `docs/design/taskbar-reclick-minimize.md`.
  - [ ] Windows `AltKeyDown` is untested on this machine — note it in the close reason, do not block.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~TaskButtonReclick"` → 14 pass.
- **Done when:** the three modes behave on a real bar; then `bd close bevel-au94 --reason "taskbarReclickMinimize shipped; on-device OK; Windows Alt path unverified"`

### bevel-c54t — Taskbar: Win10/11-style big icons mode
- **Meta:** P2 · feature · size S · AGENT+HUMAN-VERIFY
- **State today:** LIKELY DONE — `TaskbarButtonSize.Big`, `TaskbarTheme.TaskIconSize`, `ApplyTaskIconMetric` (`TaskbarView.axaml.cs:106`, called at :427), token `Bevel.Metric.TaskbarIconSize`, `tests/Bevel.Taskbar.Tests/RenderBigIconsTaskbarTest.cs`. Task icons come from `ForeignWindow.IconPng` (helper renders 64 px), not `IconLoader`. One test gap: tray cap under Big x Rows=2 is unasserted.
- **Depends on:** none
- **Files:** `tests/Bevel.Taskbar.Tests/TrayIconHeightCapTests.cs`, `site/index.html`, `site/shots/`
- **Decision:** Close the test gap here; leave the helper icon at 64 px unless the owner sees softness on a 3x display (then a new bead: 128 px vs the gRPC message cap).
- **Test first:** `TrayIconHeightCapTests.Big_tier_two_rows_caps_tray_icons_to_the_row_budget` (new): under `TaskbarTheme.Configure(TaskbarButtonSize.Big)` assert `TrayViewModel.MaxIconHeight(2) == (TaskbarTheme.HeightForRows(2) - 6) / 2` and an over-tall item is scaled to it.
- **Steps:**
  - [ ] Add the test (keep `[Collection("TaskbarTheme")]`, restore the tier in `Dispose`).
  - [ ] HUMAN: Big icons on a real bar, rows 1 and 2; check glyph sharpness and the 16 px Start logo; file a bead if the logo looks undersized.
  - [ ] Harvest `RenderBigIconsTaskbarTest` output (`BEVEL_BIGICONS_TASKBAR_OUT`) into `site/shots/` and reference it in `site/index.html`.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~TrayIconHeightCap|FullyQualifiedName~BigIcons"` → all pass.
- **Done when:** new test green and on-device look accepted; then `bd close bevel-c54t --reason "Big tier shipped; tray cap under Big asserted; site shot harvested"`

### bevel-xpfl — Two-row taskbar: tray icons taller than available row height
- **Meta:** P2 · bug · size S · HUMAN
- **State today:** LIKELY DONE — `TrayViewModel.MaxIconHeight(rows)` + `TrayItemViewModel.SetMaxHeight` (`src/Bevel.Taskbar/Model/TrayViewModel.cs`), 5 tests in `tests/Bevel.Taskbar.Tests/TrayIconHeightCapTests.cs`. Cap derives from `TaskbarTheme.HeightForRows`, so it survived bevel-c54t.
- **Depends on:** none
- **Files:** none
- **Decision:** No code. The clamp stays at the size source, not in `TrayRowsPanel.MeasureOverride`.
- **Test first:** Manual on the reporting (notched) machine.
- **Steps:**
  - [ ] HUMAN: set rows = 2, tray icon size slider to max → no icon crosses the tray well's border.
  - [ ] If one does, capture its native height from the helper log and file a bug.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~TrayIconHeightCap"` → all pass.
- **Done when:** visual check passes; then `bd close bevel-xpfl --reason "per-row tray cap from live bar metrics; verified at Rows=2 on device"`

### bevel-ww71 — SPIKE: represent running-but-windowless apps in the taskbar
- **Meta:** P2 · spike · size S · AGENT
- **State today:** LIKELY DONE — spike commit 46d755e (`docs/spec/spike-ww71-windowless-apps.md`, 13/24 apps windowless), feature shipped in 4a6a323: app-presence buttons keyed by bundle id (`TaskItemViewModel.BundleId`, ids prefixed `app:` at `TaskItemViewModel.cs:49`), `TerminateAppAsync`, tests `tests/Bevel.Taskbar.Tests/AppPresenceTests.cs`, sort option `WindowlessAppsLast`. Every acceptance clause (measure, decide, option B works, clean merge) is met.
- **Depends on:** none (related: bevel-xrx6)
- **Files:** none
- **Decision:** Close. Option C (pinned + running model) is a new feature bead, not this spike.
- **Test first:** existing `AppPresenceTests`.
- **Steps:**
  - [ ] Confirm `ls docs/spec/spike-ww71-windowless-apps.md` exists and `git log --oneline -1 4a6a323` resolves.
  - [ ] Close.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~AppPresence"` → all pass.
- **Done when:** checks above pass; then `bd close bevel-ww71 --reason "spike done (46d755e), option B shipped (4a6a323)"`

### bevel-0ai — Taskbar cust follow-ups: stacks live-apply, transparency click-through, large-PNG frame cap, clickViaCGEvent usleep
- **Meta:** P3 · task · size M · AGENT
- **State today:** (1) STILL TRUE: `StacksViewModel` is built once from `settings.Current.TaskbarStacks` (`Model/TaskbarViewModel.cs:40`); `ApplyLiveSettings` never touches it and `ApplyComponentRegion` composes an empty list on purpose (`TaskbarView.axaml.cs:269-288`). (2) UNVERIFIED: `ReapplyMetrics` sets window `MinHeight/MaxHeight` (`TaskbarWindow.cs:168-172`) and `RootGrid.Height` is set later in `ApplyRowLayout` (`TaskbarView.axaml.cs:1091`) — two steps, gap not measured. (3) STILL TRUE: `captureWindowThumbnail` (`WindowServiceImpl.swift:485`) takes caller dims unclamped; caps are 32 MiB (`Bevel.ShellCore.Ipc/Framing.cs:56`) and 16 MiB on the helper gRPC client (`Bevel.Ipc/HelperClient.cs:61`). (4) STILL TRUE and wider than stated: blocking `usleep` in `TrayServiceImpl.swift` at :144, :225, :228 and :488 (`clickViaCGEvent`).
- **Depends on:** none
- **Files:** `src/Bevel.Taskbar/Model/StackViewModel.cs`, `src/Bevel.Taskbar/Model/TaskbarViewModel.cs`, `src/Bevel.Taskbar/TaskbarView.axaml.cs`, `src/Bevel.Taskbar/TaskbarWindow.cs`, `native/helper-macos/Sources/BevelHelper/WindowServiceImpl.swift`, `native/helper-macos/Sources/BevelHelper/TrayServiceImpl.swift`, `tests/Bevel.Taskbar.Tests/StackTests.cs`, `native/helper-macos/Tests/` (`WindowServiceTests.swift`, `TrayServiceTests.swift`)
- **Decision:** (1) Add `public void SetFolders(IEnumerable<string> folders)` to `StacksViewModel` that diffs by path (add/remove/reorder in place, dispose removed `StackViewModel`s) and call it from `ApplyLiveSettings` via `TaskbarViewModel`. (2) Set `RootGrid.Height` in the same synchronous call as the window height: have `ReapplyMetrics` raise its existing rows/metrics callback before returning, so both change in one layout pass; pin with a headless test that after a tier change `RootGrid.Height == window.MaxHeight`. (3) Clamp in the helper: `min(maxW, 1024)`, `min(maxH, 1024)` at the top of `captureWindowThumbnail`. (4) Make all four sites async: `clickViaCGEvent`, `revealParkAndClick` and the move helper become `async` and use `try? await Task.sleep(nanoseconds:)`; fix every instance, not just :488.
- **Test first:** `StackTests.Changing_the_stack_list_applies_live_without_rebuilding_unchanged_stacks`; `TaskbarWindowGeometryTests.Content_height_matches_window_height_after_a_live_size_change`; Swift `WindowServiceTests` `testThumbnailRequestIsClampedTo1024`; `grep -n usleep native/helper-macos/Sources` → no matches.
- **Steps:**
  - [ ] Item 1 + test (one commit).
  - [ ] Item 2: write the test first; if it already passes, record "not reproducible, guarded by test" and keep the test.
  - [ ] Item 3 + Swift test.
  - [ ] Item 4: convert the four sleeps, keep the same durations; run the tray Swift tests.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~StackTests|FullyQualifiedName~TaskbarWindowGeometry"` → pass; `cd native/helper-macos && swift test --filter TrayServiceTests` → pass; `grep -rn usleep native/helper-macos/Sources` → empty.
- **Done when:** all four items are fixed or pinned by a test; then `bd close bevel-0ai --reason "stacks live-apply, height set atomically, thumbnail dims clamped, usleep removed from helper"`
