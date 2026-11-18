# Bevel Open-Beads Master Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan card by card. Steps inside each card use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Take every open bead (161 open or in progress on 2026-11-18) to closed, in order of value, without the executor having to make a design decision.

**Architecture:** "Retro looks, modern engine." The visuals stay Windows 2000/XP as if designed in 2026; the engine underneath is push-based, off the UI thread, virtualized, token-themed and measured. The ten rules in [Target architecture](#target-architecture) are the design; every card moves the code toward them.

**Tech stack:** C# / .NET 10 (SDK pinned in `global.json`), Avalonia 11.3.18 (+ Avalonia.Headless for tests), xUnit, gRPC over Unix domain sockets, Swift helper (`native/helper-macos`, swift-tools 6.0, macOS 15+), SQLite settings, BenchmarkDotNet, `bd` (beads) tracker.

**Spec:** the beads themselves (`bd show <id>`), `CODEMAP.md`, `CLAUDE.md`, `docs/spec/`, `docs/design/flat/`. Each card restates what it needs from them.

**Status of this document:** written from reading the tree at commit `51582c0`. Nothing was built or run while writing it. Claims a researcher could not confirm are marked `UNVERIFIED:` in the cards (33 of them). Every card therefore starts by checking its own premise.

---

## Start here: the executor loop

Repeat until `./progress.sh` prints `Next: none`.

1. Run `docs/superpowers/plans/2026-11-18-open-beads/progress.sh`. It prints progress per wave and the next card for an agent.
2. Open the card: the card file is named on that line; find the heading `### <bead-id>`.
3. Run `bd show <bead-id>`. If the bead is already closed, go to step 1.
4. **Check the premise.** Read the card's **State today** line and open the files it cites. If the code no longer matches (file missing, symbol renamed, behaviour already different), do not improvise: run `bd comments add <bead-id> "plan premise stale: <what you found>"`, park the card (see below), and go to step 1.
5. Check **Depends on**. If a listed bead is not closed, park this card with the reason `waits on <bead-id>` and go to step 1.
6. If the card's Meta says `HUMAN`, skip it. It belongs to the owner (see [Owner work](#owner-work)).
7. Claim it: `bd update <bead-id> --claim`. Create a branch off `main`: `git checkout main && git pull && git checkout -b <type>/<bead-id>-<short-name>`.
8. Do the card's steps in order. Write the test first and see it fail before writing the fix. Tick each `- [ ]` in the card file as you finish it and include the card file in your commits, so a later session can resume mid-card.
9. Run the card's **Verify** command. Then run the full suite: `dotnet build Bevel.sln -clp:ErrorsOnly && dotnet test Bevel.sln` (and `cd native/helper-macos && swift test` if you touched Swift).
10. If the card says to file new beads, file them with `bd create` and write the new ids under the card.
11. Commit (conventional-commit message, bead id in the subject). Open a PR only when the owner has asked for PRs in this session. Merge only when every CI check is green.
12. Close: use the exact `bd close` line from the card's **Done when**. For `AGENT+HUMAN-VERIFY` cards, do NOT close: run `bd comments add <bead-id> "agent work done on <branch>; owner check pending: <the manual check from the card>"` and park the card with the reason `owner check pending`.
13. Go to step 1.

If a step fails twice, stop work on that card: comment on the bead with the command and the full error, park the card with the reason `failed: <step>`, and go to step 1.

**Parking a card** means appending one line `<bead-id>|<reason>` to `parked.txt` in this folder and committing it. `progress.sh` skips parked cards, so the loop never offers the same card twice. To un-park, delete the line. Closing the bead in `bd` is still what counts as done.

## Global constraints

Every card's requirements implicitly include these.

- Never merge a PR with a failing CI check. "Pre-existing failure" is not an excuse; fix it first.
- Never add `Co-Authored-By: Claude`, `Claude-Session`, or "Generated with Claude Code" to a commit, PR or any output.
- Commit or push only when the owner asked. Batch pushes; do not push per commit.
- After a card's PR is merged, clean up: remove its git worktree (`git worktree remove <path>`), delete the local and remote branch, and run `git worktree prune`. Look at the worktree first (`git -C <path> status --short`); if it holds uncommitted work, stop and report instead of removing it.
- Prefer merge over rebase. macOS/BSD shell syntax (`sed -i ''`, no `grep -P`).
- Never `kill -9` Bevel. Stop it with `pkill -f "dist/Bevel.app/Contents/MacOS/Bevel$"` (SIGTERM to the launcher) or `./packaging/macos/bevel-recover.sh`.
- Never kill a process by port without identifying it first.
- Build env on the owner's machine: `export PATH=/usr/local/share/dotnet:$PATH DOTNET_ROOT=/usr/local/share/dotnet` (the Homebrew `dotnet` has the wrong SDK).
- Never block the Avalonia UI thread: no `.Result`, `.Wait()`, `.GetAwaiter().GetResult()`, file I/O, enumeration or icon render on it.
- Vector-only assets. Never disable antialiasing. Never hide a dead control: wire it or build the feature.
- Fix the root cause and every instance of a pattern, not one call site.
- Do not add `RequestedThemeVariant` / `ThemeVariantScope` overrides; the app pins Light.
- `Bevel.Core` must not reference Avalonia.
- Tests that mutate theme or resources carry `[Collection("TaskbarTheme")]`.
- Tests that touch files use a temp directory. Never run a destructive operation (Empty Trash, uninstall, delete, move) against the real home directory.
- When user-facing chrome changes, re-harvest the real screenshot into `site/shots/` from the `Render*` tests and update `CODEMAP.md` when entrypoints or contracts change.
- Task tracking is `bd` only. No TodoWrite, no markdown TODO lists outside this plan's card checkboxes.

## Target architecture

Ten rules. For each: what the code does today, and the cards that close the gap.

| # | Rule | Gap today | Closed by |
|---|---|---|---|
| 1 | **The UI thread never blocks.** No I/O, enumeration, icon render, process spawn or synchronous IPC in a handler or in startup. | `LocalFsProvider.EnumerateAsync` is synchronous on its caller, so every folder load, reload and tree expansion enumerates on the UI thread. `AppleEventBridge.Answer` blocks with `.GetAwaiter().GetResult()`. `ShellCoreEndpoint.ReadNonce` polls with `Thread.Sleep`. | `bevel-so27`, `bevel-odf`, `bevel-1jvr` |
| 2 | **Push, not poll.** Helper → core → peers is a delta stream. A (re)connect snapshot is a full reconcile: add, update AND remove. | Snapshots only add and update at every hop. Stale taskbar buttons are removed by a 2-second full re-list (`ShellModel.ReconcileLoopAsync`); the tray has no removal at all. | `bevel-ri8c`, `bevel-wheq`, `bevel-voqo`, `bevel-tnii`, then the follow-up beads those cards file to retire the poll |
| 3 | **One writer per shared resource.** Core owns the helper and `settings.db`. The **taskbar** is the single writer of the shared icon pool (`MmfBgraPool`), because the core is headless; every other role reads. | The pool is append-only with 512 slots and no eviction. Capability truth is duplicated in two proxies. | `bevel-web5`, `bevel-mhvu` |
| 4 | **Theme = tokens.** Every colour and size resolves from a theme token through `DynamicResource`, or rebuilds on one signal: `Bevel.UI.Glyphs.ThemeCacheInvalidated`. Nothing is resolved once at type-init. | Some recolour paths never raise the signal; others raise it before writing the new colours. Shared surfaces reference theme-scoped keys. Hardcoded hex remains. The CI token drift gate diffs a path that no longer exists. | `bevel-yfkw` (first), `bevel-5rel`, `bevel-yf7c`, `bevel-ayhn`, `bevel-lqot`, `bevel-quif`, `bevel-50hs` |
| 5 | **Every list is virtualized and updated incrementally.** | Only Filer's Details view virtualizes. Icon and list views measure every child. Start-menu population is O(n²). Stack refresh clears and re-adds. | `bevel-rzz` (first), `bevel-onyp`, `bevel-ujx8`, `bevel-jlbs`, `bevel-b8t` |
| 6 | **First frame from caches; everything else deferred and staggered after paint.** | `ShellModel.Start()` runs before the taskbar window is shown. The launcher spawns children before probing for an existing instance. | `bevel-ujx8`, `bevel-qf95`, `bevel-yg7h`, `bevel-9790` |
| 7 | **Performance is measured.** Benchmarks with budgets in `PerfBudgetTests`, not impressions. | Harness exists (`benchmarks/Bevel.Benchmarks`, `docs/perf.md`) but is not built in CI. | `bevel-zhmr` leftovers, `bevel-b8t`, `bevel-yd2n` |
| 8 | **AOT-friendly.** Compiled bindings, source-generated interop, no reflection bindings, no runtime AXAML loading. | Five reflection bindings remain. P/Invoke is hand-written. | `bevel-gww.9`, `bevel-2kdu`, `bevel-p5bx`, `bevel-gww.7` |
| 9 | **Tests are deterministic.** Headless tests tick the render timer and settle frames. | A stray touch of `Dispatcher.UIThread` between tests can poison one test (the Windows CI flake). Fourteen wall-clock sleeps in `Render*.cs`. CI never runs `swift test`. | `bevel-2nn2` (first card of the whole plan), `bevel-i145`, `bevel-tv19`, new beads listed in cards-A |
| 10 | **Honest surface.** No dead controls, no faked screenshots, security checks on every socket. | About a dozen Filer `MenuBar` items have no handler. `FlatFooterSearch` is a fake search box. The automation socket has file-permission auth only. | `bevel-4pgk`, `bevel-4vce`, `bevel-6sk6`, `bevel-wcqr`, new bead from cards-F |

### Key types the cards rely on (all verified to exist unless marked new)

- **Live settings:** `TaskbarView.ApplyLiveSettings(BevelSettings)` is the single live-apply funnel. New settings add one diff-guarded line there. Do not add per-view-model subscriptions to `ISettingsService.Changed`.
- **Theme invalidation:** `Bevel.UI.Glyphs.InvalidateThemeCache()` raises `Glyphs.ThemeCacheInvalidated`. `ThemeService` is static and has no changed event; live applies run in `App.axaml.cs`.
- **Snapshot reconcile (new):** `Bevel.Pal.Abstractions.SnapshotReconcile.Absent<T>(known, snapshot, keyOf)`, created by `bevel-ri8c`.
- **Search (new):** `Bevel.Core.Search.ISearchProvider`, `SearchSession`, `SearchRanker`, created by `bevel-6sk6`.
- **Socket auth:** `Bevel.ShellCore.Ipc.Handshake` (`BuildHelloPayload` / `ValidateHello`).
- **Filer spawn:** `IFilerSpawner` → `LauncherFilerSpawner` → `LauncherControl.TrySpawnFiler`; `FilerSupervisor` owns the processes.
- **Updates:** `IUpdateSource` is bound to `NoUpdateSource` in `CompositionRoot.AddUpdateChecking`.

### Theme names: beads versus code

Older beads use names the code no longer uses.

| Bead says | Code today |
|---|---|
| Win2000 | `src/Bevel.Themes.Industrial1999` (theme id `industrial1999`) |
| Luna | `src/Bevel.Themes.Blue2001` + `Blue2001VariantService` |
| Flat / Whistler watercolor (the look in `docs/design/flat/flat-theme-spec.md`) | theme id `pastel` (`src/Bevel.Themes.Pastel`) |
| (cobalt Whistler skin) | theme id `flat` (`src/Bevel.Themes.Flat`) + `FlatVariantService` |

## Rulings that override individual cards

The cards were written by eight researchers in parallel. Where they disagree, this section wins.

1. **Snapshot reconcile has one helper and no proto change.** `bevel-ri8c` creates `Bevel.Pal.Abstractions.SnapshotReconcile.Absent` and fixes the taskbar-side tray hop. `bevel-wheq` fixes the taskbar-side window hop. `bevel-voqo` fixes only the core-side tray hop (`MacOSSystemTrayHost`). The core-side window hop is the follow-up bead `bevel-wheq` files. The `SNAPSHOT_END` proto marker proposed in cards-A is withdrawn. The affected cards carry an override line.
2. **The icon pool writer stays the taskbar.** Do not move it to the core.
3. **`bevel-ayhn` owns the CI token drift gate fix** (`ci.yml:187`). The matching "new bead" in cards-A is not to be filed.
4. **`AppTab` field additions land in this order:** `bevel-6asm`, `bevel-l17f`, `bevel-y3cl`, `bevel-9w0h`. One at a time; each rebases on the previous.
5. **Files several cards edit are edited one card at a time,** never in parallel branches: `ShellCoreSystemTrayHost.cs`, `StartMenu.axaml(.cs)`, `TaskbarView.axaml.cs` (`ApplyLiveSettings`), `ItemView.axaml(.cs)`, `SettingsService.cs`, `.github/workflows/ci.yml`.
6. **`bevel-ioxx` (real file icons) waits for `bevel-rzz` (virtualized panel) and `bevel-yfkw` (theme signal).**
7. **Anything that changes a wire format or a persisted name** (`CoreCommand`/`CoreResponse` in `bevel-alb`, the enum rename in `bevel-zvo0`, new settings keys) must read the old form too, and adds a test that an old value still loads.

## Order of work

`progress.sh` follows this order. Waves are value order; inside a wave the order respects dependencies. "State found" is what the researcher saw in the code on 2026-11-18.

### W0 — Tracker truth: close what is already done

| # | Bead | Title | Pri | Size | Who | State found | Card file |
|---|---|---|---|---|---|---|---|
| 1 | `bevel-zhmr` | Perf benchmark harness for frequent taskbar/shell operations | P2 | S | agent | likely done | [A](cards-A-test-reliability.md) |
| 2 | `bevel-3gad` | Shared icon pool has no writer — the cross-process cache is inert | P1 | S | agent | likely done | [B](cards-B-core-ipc-startup.md) |
| 3 | `bevel-lej1` | [bug] Shell starts unskinned (Win2000) and the theme changes under it mid-session | P2 | S | agent | likely done | [B](cards-B-core-ipc-startup.md) |
| 4 | `bevel-5no9` | Version switch relaunches the whole shell 2-3 times instead of once | P2 | S | agent | likely done | [B](cards-B-core-ipc-startup.md) |
| 5 | `bevel-nsk` | Configure CI secrets for signing + notarization | P2 | S | agent | duplicate of bevel-9po | [C](cards-C-packaging-security.md) |
| 6 | `bevel-ayf` | M5: publish DMG hosting + first GitHub release (v0.1.0 tech preview) | P3 | S | agent | likely done | [C](cards-C-packaging-security.md) |
| 7 | `bevel-rkxb` | Online updates for Bevel (check, download, apply new versions) | P2 | S | agent | duplicate of bevel-ym0 | [C](cards-C-packaging-security.md) |
| 8 | `bevel-i9iu` | AddressBar's folder icon uses hardcoded hex, not theme tokens | P3 | S | agent | likely done | [D](cards-D-theming.md) |
| 9 | `bevel-8lb` | Luna theme: port vector style guide into Bevel.Themes.Luna ControlThemes | P2 | S | agent | likely done | [D](cards-D-theming.md) |
| 10 | `bevel-ww71` | SPIKE: represent running-but-windowless apps in the taskbar | P2 | S | agent | likely done | [E1](cards-E1-start-menu-search.md) |
| 11 | `bevel-m3.1` | M3-A: Tray discovery + limited-mode rendering (bevel-m3) | P2 | S | agent | likely done | [G](cards-G-tray-interop-windows.md) |
| 12 | `bevel-m2.6` | M3: mirror macOS menu-bar NSStatusItems into tray + autohide LRU + iStat denylist | P2 | S | agent | duplicate of bevel-m3.1 | [G](cards-G-tray-interop-windows.md) |
| 13 | `bevel-sd6n` | macOS 26: ~25 status items expose no capturable pixels in ANY window (limited-mode fallbac | P2 | S | agent | likely done | [G](cards-G-tray-interop-windows.md) |

### W1 — Green CI: make every merge trustworthy

| # | Bead | Title | Pri | Size | Who | State found | Card file |
|---|---|---|---|---|---|---|---|
| 1 | `bevel-2nn2` | PushFrame PlatformNotSupportedException in headless tests | P1 | S | agent | open | [A](cards-A-test-reliability.md) |
| 2 | `bevel-i145` | Luna render tests flake on shared headless Application state | P3 | M | agent | open | [A](cards-A-test-reliability.md) |
| 3 | `bevel-ayhn` | Luna tokens are hand-mirrored and not drift-checked (ThemeGen only covers Win2000) | P3 | M | agent | open | [D](cards-D-theming.md) |
| 4 | `bevel-tv19` | RenderFlatAndPastelComparisonTest dirties 5 checked-in PNGs on every run | P3 | S | agent | partly done | [A](cards-A-test-reliability.md) |

### W2 — Daily-use correctness

| # | Bead | Title | Pri | Size | Who | State found | Card file |
|---|---|---|---|---|---|---|---|
| 1 | `bevel-so27` | Filer enumeration still runs ON THE UI THREAD (syscall count fixed; threading blocked on c | P2 | L | agent | partly done | [B](cards-B-core-ipc-startup.md) |
| 2 | `bevel-ri8c` | Tray reconnect snapshot only upserts -- ghost icons survive a core restart | P2 | M | agent | open | [B](cards-B-core-ipc-startup.md) |
| 3 | `bevel-wheq` | Window-list reconnect snapshot only upserts -- possible stale taskbar buttons after core r | P2 | S | agent | open | [B](cards-B-core-ipc-startup.md) |
| 4 | `bevel-voqo` | Tray live-mirror icons go stale / don't rebuild | P2 | M | agent, owner verifies | partly done | [G](cards-G-tray-interop-windows.md) |
| 5 | `bevel-tnii` | Redundant 2s full-ListWindows poll re-ships every window icon on top of the delta stream | P2 | M | agent | partly done | [B](cards-B-core-ipc-startup.md) |
| 6 | `bevel-9790` | Probe the launcher control socket BESIDE the instance lock, before spawning anything | P2 | S | agent | open | [B](cards-B-core-ipc-startup.md) |
| 7 | `bevel-u2x6` | Drags out of Filer carry no files — dropping on Finder/Mail/browser does nothing | P1 | S | agent, owner verifies | partly done | [B](cards-B-core-ipc-startup.md) |
| 8 | `bevel-hsvx` | Start-menu folder opens bypass the parked Filer and spawn unsupervised | P1 | S | agent | partly done | [B](cards-B-core-ipc-startup.md) |
| 9 | `bevel-yfkw` | Glyphs.Icon() direct consumers never rebuild on live theme switch | P1 | M | agent | open | [D](cards-D-theming.md) |
| 10 | `bevel-5rel` | ItemView static selection brushes resolved once at type-init, never invalidated | P1 | S | agent | partly done | [D](cards-D-theming.md) |
| 11 | `bevel-yf7c` | Filer Toolbar.ApplyIcons() rasterized once in the ctor, never re-applied on live theme swi | P2 | S | agent | open | [D](cards-D-theming.md) |
| 12 | `bevel-6asm` | Tab activation is positional, so a stale row activates the wrong tab | P2 | M | agent, owner verifies | open | [E2](cards-E2-taskbar-customization.md) |
| 13 | `bevel-2ip7` | StacksViewModel reads TaskbarStacks once at construction, never subscribes to settings.Cha | P3 | S | agent | open | [E2](cards-E2-taskbar-customization.md) |
| 14 | `bevel-5dn` | Taskbar live-apply: separate cheap preview from debounced persist | P3 | M | agent | open | [E2](cards-E2-taskbar-customization.md) |
| 15 | `bevel-glnc` | ShellInstanceLock: IOException can escape, and RecordPid leaks the lock stream | P3 | S | agent | open | [B](cards-B-core-ipc-startup.md) |
| 16 | `bevel-soon` | CFNumberCreate declares CFNumberType as int, but it is CFIndex (pointer-sized) | P3 | S | agent | open | [G](cards-G-tray-interop-windows.md) |

### W3 — Shippable: release, update, uninstall

| # | Bead | Title | Pri | Size | Who | State found | Card file |
|---|---|---|---|---|---|---|---|
| 1 | `bevel-9po` | M5: activate release-macos.yml — configure the 6 GitHub 'release' environment secrets | P2 | S | owner | open | [C](cards-C-packaging-security.md) |
| 2 | `bevel-m5` | M5 — Polish + packaging = mac v1 | P2 | L | agent, owner verifies | open | [C](cards-C-packaging-security.md) |
| 3 | `bevel-ka6c` | Apphost links /opt/homebrew brotli — breaks portability of signed builds | P2 | S | agent, owner verifies | open | [C](cards-C-packaging-security.md) |
| 4 | `bevel-wcqr` | Automation control socket has file-perm-only auth (no nonce), unlike helper/shell-core | P2 | M | agent | open | [C](cards-C-packaging-security.md) |
| 5 | `bevel-ym0` | M5: Velopack auto-update integration (UPD-01) | P2 | L | agent, owner verifies | open | [C](cards-C-packaging-security.md) |
| 6 | `bevel-q3l` | M5: uninstaller / --uninstall (ENG-040) | P2 | M | agent, owner verifies | open | [C](cards-C-packaging-security.md) |
| 7 | `bevel-tak` | M5: local crash logging + export (offline, ENG s7) | P3 | M | agent | partly done | [C](cards-C-packaging-security.md) |
| 8 | `bevel-ct7` | M5: Settings app — full v1 surface | P3 | S | agent | partly done | [C](cards-C-packaging-security.md) |
| 9 | `bevel-gsz` | M5: Homebrew cask (brew install --cask bevel) | P2 | S | agent, owner verifies | open | [C](cards-C-packaging-security.md) |
| 10 | `bevel-cp2` | M5: user guide + known-issues docs | P3 | M | agent | open | [C](cards-C-packaging-security.md) |
| 11 | `bevel-l3o` | M1: per-capability RPC auth scoping for helper services (ScriptingService/InputService) | P2 | S | agent | partly done | [C](cards-C-packaging-security.md) |

### W4 — Snappy engine: lists, startup, icons

| # | Bead | Title | Pri | Size | Who | State found | Card file |
|---|---|---|---|---|---|---|---|
| 1 | `bevel-rzz` | File manager: type-to-select smooth-scroll + virtualized list with up/down buffer | P2 | L | agent, owner verifies | partly done | [F](cards-F-filer.md) |
| 2 | `bevel-onyp` | Filer: virtualize wrap-panel + O(n^2) InsertSorted + watcher-vs-search + Open-With wire-up | P2 | L | agent | partly done | [F](cards-F-filer.md) |
| 3 | `bevel-ujx8` | Taskbar: tray seed/stream ordering + StartMenu O(n^2) startup + All-Programs flyout rebuil | P3 | M | agent | open | [B](cards-B-core-ipc-startup.md) |
| 4 | `bevel-qf95` | Startup: stagger the taskbar post-paint installed-app enumeration + icon rendering | P3 | M | agent, owner verifies | open | [B](cards-B-core-ipc-startup.md) |
| 5 | `bevel-web5` | Shared icon pool has no eviction; a full pool silently stops sharing | P2 | M | agent | open | [B](cards-B-core-ipc-startup.md) |
| 6 | `bevel-yg7h` | Startup: defer the helper ScreenCaptureKit/AX tray self-test off the first-paint window | P2 | M | agent, owner verifies | open | [B](cards-B-core-ipc-startup.md) |
| 7 | `bevel-ioxx` | No real thumbnails or OS file icons (IconResolver is a stub) | P2 | L | agent, owner verifies | open | [F](cards-F-filer.md) |
| 8 | `bevel-m3.2.1` | M3-B.1: throttled live-capture refresh cadence (Req 5.4) (bevel-m3.2) | P3 | M | agent, owner verifies | partly done | [G](cards-G-tray-interop-windows.md) |
| 9 | `bevel-umgr` | Stream-resubscribe test seams for MacOSWindowManager + MacOSSystemTrayHost | P2 | M | agent | partly done | [A](cards-A-test-reliability.md) |
| 10 | `bevel-gww.9` | AOT: convert 3 code-behind reflection Bindings to compiled/manual (StartMenu icon, ItemVie | P3 | M | agent | open | [B](cards-B-core-ipc-startup.md) |
| 11 | `bevel-hprc` | [design] Launcher self-update on RestartAll (version-stamped handshake, re-exec) | P3 | M | agent | partly done | [B](cards-B-core-ipc-startup.md) |
| 12 | `bevel-f4g5` | Version-skew message needs to be readable and offer actions | P2 | M | agent, owner verifies | open | [B](cards-B-core-ipc-startup.md) |
| 13 | `bevel-mhvu` | ShellCoreSystemTrayHost duplicates tray capabilities instead of forwarding the core's | P2 | S | agent | open | [B](cards-B-core-ipc-startup.md) |
| 14 | `bevel-b8t` | M5: 100k-entry file-manager perf validation (06 perf) | P3 | M | agent, owner verifies | open | [F](cards-F-filer.md) |
| 15 | `bevel-yd2n` | PROFILE FIRST: colourway switch does ~140 individual resource writes | P3 | M | agent | open | [D](cards-D-theming.md) |

### W5 — Daily features: search, stacks, Filer basics, tabs

| # | Bead | Title | Pri | Size | Who | State found | Card file |
|---|---|---|---|---|---|---|---|
| 1 | `bevel-6sk6` | Start-menu inline search: results panel (apps, files, tabs) — the search foundation | P2 | L | agent | partly done | [E1](cards-E1-start-menu-search.md) |
| 2 | `bevel-vtc6` | Start-menu search over tabs: type-to-jump across every ITabProvider app | P2 | M | agent, owner verifies | open | [E1](cards-E1-start-menu-search.md) |
| 3 | `bevel-pr4l` | StartMenu.axaml.cs grew 38% absorbing the search UI — extract the search strip | P3 | M | agent | open | [E1](cards-E1-start-menu-search.md) |
| 4 | `bevel-qb19` | Taskbar buttons need an attention state | P2 | M | agent, owner verifies | open | [E1](cards-E1-start-menu-search.md) |
| 5 | `bevel-4vce` | Start menu: unify shell-management actions + fix Luna 'Log Off' mislabel | P2 | M | agent, owner verifies | partly done | [E1](cards-E1-start-menu-search.md) |
| 6 | `bevel-xrx6` | Frequency-based app suggestions: exclude-open filter + OS usage seeding | P2 | L | agent, owner verifies | open | [E1](cards-E1-start-menu-search.md) |
| 7 | `bevel-jt30` | Setting: list windows on other Spaces (default on) | P2 | S | agent | open | [E1](cards-E1-start-menu-search.md) |
| 8 | `bevel-psro` | Reuse TypeToFind in tree / Start menu / FolderPicker / taskbar / desktop grid | P2 | M | agent | open | [E1](cards-E1-start-menu-search.md) |
| 9 | `bevel-jlbs` | Downloads stack UX: context menus, richer flyout, responsiveness + motion | P2 | L | agent, owner verifies | open | [E2](cards-E2-taskbar-customization.md) |
| 10 | `bevel-dw5` | Taskbar cust: stacks manager (add/remove/reorder folders) | P3 | M | agent | open | [E2](cards-E2-taskbar-customization.md) |
| 11 | `bevel-apm` | Context menus: cross-OS superset (Cascade/Tile + window verbs) | P2 | L | agent, owner verifies | open | [E2](cards-E2-taskbar-customization.md) |
| 12 | `bevel-ze3o` | Sort order and view mode are not persisted per-folder | P3 | M | agent | open | [F](cards-F-filer.md) |
| 13 | `bevel-4pgk` | Wire Favorites end-to-end | P2 | M | agent | open | [F](cards-F-filer.md) |
| 14 | `bevel-cq7g` | Folders browsed INSIDE the Trash switch to file:// and regain normal caps | P3 | S | agent | open | [F](cards-F-filer.md) |
| 15 | `bevel-j905` | Empty Trash + Restore/Put Back UI for the Trash view | P2 | M | agent, owner verifies | open | [F](cards-F-filer.md) |
| 16 | `bevel-lkib` | net:// Network tree node is a dead stub (silent no-op) | P3 | S | agent | open | [F](cards-F-filer.md) |
| 17 | `bevel-l17f` | Tabs menu favicons: Chromium Favicons DB + iTerm session colors as row icons | P2 | S | agent, owner verifies | partly done | [E2](cards-E2-taskbar-customization.md) |
| 18 | `bevel-y3cl` | Gecko tab engine: stable window identity, pid carry, URL enrichment | P3 | M | agent, owner verifies | partly done | [E2](cards-E2-taskbar-customization.md) |
| 19 | `bevel-9w0h` | Arc tabs menu: prefer live/recent tabs over raw sidebar order (Space-aware) | P2 | M | agent, owner verifies | open | [E2](cards-E2-taskbar-customization.md) |
| 20 | `bevel-s5x` | Taskbar grouped-button jump-list: per-app custom actions (New window, etc.) | P3 | S | agent | open | [E2](cards-E2-taskbar-customization.md) |

### W6 — Tray (M3) and Apple Events (M4) to their exit criteria

| # | Bead | Title | Pri | Size | Who | State found | Card file |
|---|---|---|---|---|---|---|---|
| 1 | `bevel-7hf4` | Hide mirrored icons from the macOS menu bar when Bevel owns them (Bartender-style) | P2 | M | agent, owner verifies | partly done | [G](cards-G-tray-interop-windows.md) |
| 2 | `bevel-m3` | M3 — Systray mirroring (Ice technique) | P2 | L | agent, owner verifies | open | [G](cards-G-tray-interop-windows.md) |
| 3 | `bevel-whz` | Wire AppleScript 'open'/odoc folder-open through IShellAutomation (INT-6) | P2 | M | agent, owner verifies | open | [G](cards-G-tray-interop-windows.md) |
| 4 | `bevel-odf` | Synchronous AE get/count can freeze the UI thread on huge enumeration | P2 | L | agent, owner verifies | partly done | [G](cards-G-tray-interop-windows.md) |
| 5 | `bevel-4qr` | M4-E: AppleScript compat corpus harness (~40 snippets, exit criteria) | P2 | M | agent, owner verifies | partly done | [G](cards-G-tray-interop-windows.md) |
| 6 | `bevel-m4` | M4 — Apple Events tier 1 | P2 | L | agent, owner verifies | open | [G](cards-G-tray-interop-windows.md) |
| 7 | `bevel-ye27` | macOS 26: resolve real names for Control-Center-owned tray items (most show as generic 'It | P2 | M | agent, owner verifies | partly done | [G](cards-G-tray-interop-windows.md) |
| 8 | `bevel-sdss` | IPermissionBroker.PermissionChanged is declared but never raised anywhere -- dead live-upd | P3 | M | agent | open | [G](cards-G-tray-interop-windows.md) |
| 9 | `bevel-h32` | Taskbar cust: tray denylist editor + live-capture toggle | P3 | L | agent, owner verifies | open | [E2](cards-E2-taskbar-customization.md) |
| 10 | `bevel-vs10` | Apple Events: add a 'launch' verb (INT-3 parity with bevelctl/bevel://) | P3 | M | agent, owner verifies | open | [G](cards-G-tray-interop-windows.md) |
| 11 | `bevel-aogp` | Remove dead trayGlyphLayout sizing code + its test (native-size rendering replaced it) | P3 | S | agent | open | [G](cards-G-tray-interop-windows.md) |
| 12 | `bevel-tzx` | Share one ScreenCaptureKit capture helper (tray vs window) | P3 | S | agent | partly done | [G](cards-G-tray-interop-windows.md) |

### W7 — Theme platform

| # | Bead | Title | Pri | Size | Who | State found | Card file |
|---|---|---|---|---|---|---|---|
| 1 | `bevel-rqcp` | Author Flat watercolor SVG spec and align runtime | P1 | S | agent, owner verifies | likely done | [D](cards-D-theming.md) |
| 2 | `bevel-q455` | Implement the Flat (Whistler watercolor) theme from the design spec | P2 | S | agent, owner verifies | likely done | [D](cards-D-theming.md) |
| 3 | `bevel-lqot` | Promote Luna.Brush.StartMenu*/TaskButton*/CaptionButton* to Bevel.Brush.* | P2 | M | agent | open | [D](cards-D-theming.md) |
| 4 | `bevel-quif` | Luna ControlThemes have hardcoded hex that ignores the colour variant | P3 | S | agent | open | [D](cards-D-theming.md) |
| 5 | `bevel-dob` | Theme engine: per-theme control-template sets (unlocks XP/Luna/Aero chrome) | P2 | L | agent | partly done | [D](cards-D-theming.md) |
| 6 | `bevel-7cq` | Luna theme: remaining ControlThemes (caption, scrollbar, progress, tabs, checkbox/radio, m | P2 | M | agent | partly done | [D](cards-D-theming.md) |
| 7 | `bevel-ii93` | EPIC: Watercolor Flat theme (Whistler Watercolor) | P2 | M | agent, owner verifies | partly done | [D](cards-D-theming.md) |
| 8 | `bevel-safz` | Dead AXAML class: Classes="taskbar-start" matches no selector | P3 | S | agent | open | [D](cards-D-theming.md) |
| 9 | `bevel-zvo0` | InfoPaneStyle enum still carries vendor names (Win2000/WinXP/Win9x) | P3 | S | agent, owner verifies | open | [D](cards-D-theming.md) |
| 10 | `bevel-50hs` | Tokenize straggler hardcoded colours/sizes | P2 | M | agent | open | [D](cards-D-theming.md) |
| 11 | `bevel-q91m` | Arbitrary accent-colour derivation (free picker) | P2 | M | agent | open | [D](cards-D-theming.md) |
| 12 | `bevel-fdo5` | EPIC: Derivation engine → derived dark mode | P2 | L | agent, owner verifies | open | [D](cards-D-theming.md) |
| 13 | `bevel-tknt` | The .beveltheme package pipeline / third-party themes are unimplemented | P2 | L | agent, owner verifies | open | [D](cards-D-theming.md) |
| 14 | `bevel-ti3` | M5: Win2000 theme completion across every control (pixel-golden) | P2 | L | agent, owner verifies | open | [D](cards-D-theming.md) |
| 15 | `bevel-ztdt` | Theme Studio: fold caption colour/height into Save | P3 | M | agent | open | [D](cards-D-theming.md) |
| 16 | `bevel-ekcv` | Luna menus: render mac ⌘-glyph accelerators (not "Cmd+X") | P3 | S | agent | open | [D](cards-D-theming.md) |

### W8 — Hardening and hygiene

| # | Bead | Title | Pri | Size | Who | State found | Card file |
|---|---|---|---|---|---|---|---|
| 1 | `bevel-osp` | Verify helper code signature / team-id before launch (defense-in-depth) | P3 | S | agent, owner verifies | open | [C](cards-C-packaging-security.md) |
| 2 | `bevel-kyf` | Helper: verify peer UID/credentials on the UDS connection (defense-in-depth) | P3 | S | agent | open | [C](cards-C-packaging-security.md) |
| 3 | `bevel-9ucj` | Automation channel: harden composite WindowRef bit-budget + pid-reuse edge | P3 | S | agent | open | [C](cards-C-packaging-security.md) |
| 4 | `bevel-4x0` | bevel:// implements only 2 of 8 automation verbs; 'set' unreachable from CLI/URL | P3 | S | agent | open | [C](cards-C-packaging-security.md) |
| 5 | `bevel-2kdu` | Migrate macOS P/Invoke to source-generated [LibraryImport] | P3 | L | agent | open | [G](cards-G-tray-interop-windows.md) |
| 6 | `bevel-p5bx` | Windows P/Invoke has the same silo duplication macOS had | P3 | L | agent, owner verifies | open | [G](cards-G-tray-interop-windows.md) |
| 7 | `bevel-gw2` | Factor shared gRPC helper-client base (WindowManager + SystemTrayHost) | P3 | S | agent | open | [B](cards-B-core-ipc-startup.md) |
| 8 | `bevel-alb` | ShellCore CoreCommand/CoreResponse: replace optional-field grab-bag with typed payloads | P3 | L | agent | open | [B](cards-B-core-ipc-startup.md) |
| 9 | `bevel-1nxe` | Migrate drag-drop to Avalonia DataTransfer API (retire IDataObject) | P3 | M | agent, owner verifies | open | [B](cards-B-core-ipc-startup.md) |
| 10 | `bevel-1jvr` | Startup: replace ShellCoreEndpoint.ReadNonce 5s Thread.Sleep poll with async wait (latent  | P4 | S | agent | open | [B](cards-B-core-ipc-startup.md) |
| 11 | `bevel-6tpi` | Nothing asserts popup PLACEMENT | P2 | M | agent | open | [A](cards-A-test-reliability.md) |
| 12 | `bevel-hr5k` | Debug traces are lost when the shell is launched the way users launch it | P2 | M | agent, owner verifies | partly done | [A](cards-A-test-reliability.md) |
| 13 | `bevel-hs1j` | Spawner tests need an AX-granted host to actually run | P3 | M | agent, owner verifies | open | [A](cards-A-test-reliability.md) |
| 14 | `bevel-79r` | Harden flaky WindowSpawner close enumeration test | P2 | S | agent, owner verifies | open | [A](cards-A-test-reliability.md) |
| 15 | `bevel-2tlp` | MacOSTabProvider: seam + tests for the osascript runner | P3 | S | agent | open | [A](cards-A-test-reliability.md) |
| 16 | `bevel-adbo` | Landing-page shots are all 1x — harvest them at 2x for HiDPI | P2 | M | agent | partly done | [A](cards-A-test-reliability.md) |
| 17 | `bevel-lgpn` | Prove why hero-blue2001.png is not byte-reproducible across machines | P3 | S | agent, owner verifies | open | [A](cards-A-test-reliability.md) |
| 18 | `bevel-gww.7` | P7: NativeAOT publish (the per-process memory payoff) | P2 | L | owner | partly done | [B](cards-B-core-ipc-startup.md) |
| 19 | `bevel-gww` | Multi-process shell: --role split + shell-core owner + shared caches + AOT | P2 | L | agent | open | [B](cards-B-core-ipc-startup.md) |
| 20 | `bevel-0ai` | Taskbar cust follow-ups: stacks live-apply, transparency click-through, large-PNG frame ca | P3 | M | agent | open | [E1](cards-E1-start-menu-search.md) |

### W9 — Big bets, research and parked decisions

| # | Bead | Title | Pri | Size | Who | State found | Card file |
|---|---|---|---|---|---|---|---|
| 1 | `bevel-9ue` | Taskbar cust: edge position (top/bottom + vertical L/R) + auto-hide + centered alignment | P3 | L | agent, owner verifies | open | [E2](cards-E2-taskbar-customization.md) |
| 2 | `bevel-gcd` | Taskbar cust: dock magnification, hover thumbnails, Task View/Search/Widgets buttons | P3 | M | agent, owner verifies | partly done | [E2](cards-E2-taskbar-customization.md) |
| 3 | `bevel-hail` | Taskbar menu: true live Quit⇄Force-Quit swap via in-tree Popup | P3 | L | agent, owner verifies | open | [E2](cards-E2-taskbar-customization.md) |
| 4 | `bevel-e5vz` | Explore: tab-level previews (CDP opt-in / extension) + window hover thumbnails beside tab  | P3 | M | agent | open | [E2](cards-E2-taskbar-customization.md) |
| 5 | `bevel-c11a` | Address bar is a plain textbox — no breadcrumbs/autocomplete/history | P3 | L | agent | open | [F](cards-F-filer.md) |
| 6 | `bevel-o6cc` | EPIC: Filer Bar system (Folders/Search/History/Favorites) | P2 | L | agent | partly done | [F](cards-F-filer.md) |
| 7 | `bevel-tjr2` | EPIC: per-Space / multi-monitor taskbars | P3 | L | owner | open | [E2](cards-E2-taskbar-customization.md) |
| 8 | `bevel-lkw1` | EPIC: Theme lineage — Win10 flat, KDE Breeze, Aero, Win11 | P2 | L | agent, owner verifies | open | [D](cards-D-theming.md) |
| 9 | `bevel-te20` | Git-repo theme install (.beveltheme from user-supplied git URL) | P2 | M | owner | open | [D](cards-D-theming.md) |
| 10 | `bevel-2t7u` | Windows: mirror third-party notification-area icons into the tray | P2 | L | owner | open | [G](cards-G-tray-interop-windows.md) |
| 11 | `bevel-m3.3.1` | Tray click-forwarding: custom status items unresponsive on macOS 26 (bevel-m3.3) | P3 | L | agent, owner verifies | open | [G](cards-G-tray-interop-windows.md) |
| 12 | `bevel-spike-tier3` | SPIKE: tier-3 headless-Finder mirroring (measure window-escape rate) | P3 | L | owner | open | [G](cards-G-tray-interop-windows.md) |
| 13 | `bevel-3qc` | Work-area reservation (Dock-shim strict mode) + R9.4/R15 measurement | P3 | L | owner | open | [E2](cards-E2-taskbar-customization.md) |
| 14 | `bevel-assets` | Self-produce Win2000 core asset set ($0; AI-assisted + open/CC/OFL) | P2 | L | owner | partly done | [D](cards-D-theming.md) |
| 15 | `bevel-gye` | RTL: set FlowDirection from OS locale at startup + verify bevel light source vs RTL Win200 | P4 | M | agent, owner verifies | open | [D](cards-D-theming.md) |
| 16 | `bevel-zr4` | Second-source the WinDaisy-derived named color schemes (Brick, Desert, Eggplant, etc.) in  | P4 | M | owner | open | [D](cards-D-theming.md) |
| 17 | `bevel-legal-branding` | Parked counsel asks: trade-dress + .beveltheme licensing (Bevel Desktop decided) | P2 | S | owner | open | [C](cards-C-packaging-security.md) |
| 18 | `bevel-7c6m` | Mac App Store is blocked by entitlements, not licence — pick real channels | P3 | S | owner | open | [C](cards-C-packaging-security.md) |
| 19 | `bevel-m3.6` | M3-F: Menu-bar reclaim (consent-gated, feature-flagged) (bevel-m3) | P2 | S | owner | open | [G](cards-G-tray-interop-windows.md) |
| 20 | `bevel-spike-shadow` | SPIKE: bundle-id shadowing of com.apple.finder (documents failure mode; never ships) | P3 | S | owner | open | [G](cards-G-tray-interop-windows.md) |

### H — Owner device sessions (any time; not on the agent path)

| # | Bead | Title | Pri | Size | Who | State found | Card file |
|---|---|---|---|---|---|---|---|
| 1 | `bevel-cezo` | Start menu: type-to-search while open | P1 | S | owner | likely done | [E1](cards-E1-start-menu-search.md) |
| 2 | `bevel-ijln` | Taskbar: app unread/badge counts (Slack-style) | P2 | S | owner | likely done | [E1](cards-E1-start-menu-search.md) |
| 3 | `bevel-yv2m` | macOS Zoom ignores Bevel taskbar height | P2 | S | owner | likely done | [E1](cards-E1-start-menu-search.md) |
| 4 | `bevel-au94` | Disable or remappable click-again-to-minimize on task buttons | P2 | S | owner | likely done | [E1](cards-E1-start-menu-search.md) |
| 5 | `bevel-c54t` | Taskbar: Win10/11-style big icons mode | P2 | S | agent, owner verifies | likely done | [E1](cards-E1-start-menu-search.md) |
| 6 | `bevel-xpfl` | Two-row taskbar: tray icons taller than available row height | P2 | S | owner | likely done | [E1](cards-E1-start-menu-search.md) |
| 7 | `bevel-9elh` | Downloads stack: macOS-like grid with content previews | P2 | S | agent, owner verifies | likely done | [E2](cards-E2-taskbar-customization.md) |
| 8 | `bevel-szw4` | Space switch: first taskbar click visibly flips other-display Spaces | P1 | S | owner | partly done | [E1](cards-E1-start-menu-search.md) |
| 9 | `bevel-yduf` | [bug] Tray mirrors the right items but fails to render their icons | P2 | S | agent, owner verifies | likely done | [G](cards-G-tray-interop-windows.md) |
| 10 | `bevel-qcd9` | Tray self-test never re-verifies live capture once Screen Recording is granted mid-session | P2 | S | agent, owner verifies | likely done | [G](cards-G-tray-interop-windows.md) |
| 11 | `bevel-tned` | On-device QA: tray adaptive tinting + multi-row/stacked-date | P2 | S | owner | open | [G](cards-G-tray-interop-windows.md) |
| 12 | `bevel-7qdi` | Intermittent: tray icons render too small (not reproduced) | P3 | S | agent, owner verifies | open | [G](cards-G-tray-interop-windows.md) |
| 13 | `bevel-injy` | Quit leaves the Windows taskbar hidden (no restore / no crash-heal) | P1 | S | agent, owner verifies | likely done | [G](cards-G-tray-interop-windows.md) |
| 14 | `bevel-h0sr` | Windows taskbar sits too high and ignores display-size changes | P2 | S | agent, owner verifies | likely done | [G](cards-G-tray-interop-windows.md) |

## Owner work

### Tracker edits to approve before starting

These decouple packaging from the tray and Apple Events milestones (the chain is milestone ordering, not a code dependency) and record the real packaging order. Nothing has been changed in the tracker.

```bash
bd dep remove bevel-m5 bevel-m4
bd dep remove bevel-m4 bevel-m3
bd dep add bevel-ym0 bevel-9po
bd dep add bevel-ym0 bevel-ka6c
bd dep add bevel-gsz bevel-ka6c
bd dep add bevel-gsz bevel-q3l
bd dep add bevel-cp2 bevel-q3l
bd dep add bevel-cp2 bevel-tak
bd dep add bevel-ct7 bevel-tak
```

### Decisions the cards made that the owner may want to reverse

| Bead | Decision in the card |
|---|---|
| `bevel-tknt` | `.beveltheme` v1 carries tokens only, no runtime AXAML, to stay AOT-safe. |
| `bevel-fdo5` | Dark mode is a derived token set, not Avalonia's `ThemeVariant.Dark`. |
| `bevel-tnii` | Fix on the core side (strip unchanged icons from the poll reply) instead of adding a proto `include_icons` flag. |
| `bevel-4x0` | No full verb parity for `bevel://`, because the URL scheme is reachable from the web. |
| `bevel-4pgk` | Delete the `Links` and `Media` Favorites items instead of wiring them. |
| `bevel-ka6c` | Add an `otool` gate that fails the build on a Homebrew link; do not bundle brotli. |
| `bevel-psro` | Drop two of the five surfaces: the Start menu (a filter by design) and the desktop grid (does not exist). |
| `bevel-vs10` | Cut from the M4 exit gate. |
| `bevel-m3.6` | Cut: superseded by Strategy C. |
| `bevel-spike-shadow` | Cancel: no outcome changes the never-ship decision. |
| `bevel-szw4` | Bead text is stale: the code deliberately does not warp the cursor back. Owner decides whether to close. |

### Things only the owner can do

- **Release secrets** (`bevel-9po`): five of the six `release` environment secrets are missing. The card has the checklist. An agent can do the two lane fixes first (runner image, `LSMinimumSystemVersion`).
- **Apple Events routing check** (`bevel-m4`): confirm an event addressed to "Bevel" reaches the taskbar process. About 15 minutes; the card has the steps.
- **Windows shell-replacement mode** (`bevel-2t7u`): product decision; the tray mirror cannot work alongside Explorer.
- **NativeAOT publish** (`bevel-gww.7`): needs the signing identity, notarization and a native-dependency decision.
- **Device sessions** (wave H): one macOS session with Screen Recording and Accessibility granted, one Windows session. Each card lists its manual check.

Cards marked owner-only:

| Bead | Title | Card file |
|---|---|---|
| `bevel-gww.7` | P7: NativeAOT publish (the per-process memory payoff) | [B](cards-B-core-ipc-startup.md) |
| `bevel-9po` | M5: activate release-macos.yml — configure the 6 GitHub 'release' environment secrets | [C](cards-C-packaging-security.md) |
| `bevel-7c6m` | Mac App Store is blocked by entitlements, not licence — pick real channels | [C](cards-C-packaging-security.md) |
| `bevel-legal-branding` | Parked counsel asks: trade-dress + .beveltheme licensing (Bevel Desktop decided) | [C](cards-C-packaging-security.md) |
| `bevel-te20` | Git-repo theme install (.beveltheme from user-supplied git URL) | [D](cards-D-theming.md) |
| `bevel-assets` | Self-produce Win2000 core asset set ($0; AI-assisted + open/CC/OFL) | [D](cards-D-theming.md) |
| `bevel-zr4` | Second-source the WinDaisy-derived named color schemes (Brick, Desert, Eggplant, etc.) in  | [D](cards-D-theming.md) |
| `bevel-cezo` | Start menu: type-to-search while open | [E1](cards-E1-start-menu-search.md) |
| `bevel-szw4` | Space switch: first taskbar click visibly flips other-display Spaces | [E1](cards-E1-start-menu-search.md) |
| `bevel-ijln` | Taskbar: app unread/badge counts (Slack-style) | [E1](cards-E1-start-menu-search.md) |
| `bevel-yv2m` | macOS Zoom ignores Bevel taskbar height | [E1](cards-E1-start-menu-search.md) |
| `bevel-au94` | Disable or remappable click-again-to-minimize on task buttons | [E1](cards-E1-start-menu-search.md) |
| `bevel-xpfl` | Two-row taskbar: tray icons taller than available row height | [E1](cards-E1-start-menu-search.md) |
| `bevel-tjr2` | EPIC: per-Space / multi-monitor taskbars | [E2](cards-E2-taskbar-customization.md) |
| `bevel-3qc` | Work-area reservation (Dock-shim strict mode) + R9.4/R15 measurement | [E2](cards-E2-taskbar-customization.md) |
| `bevel-tned` | On-device QA: tray adaptive tinting + multi-row/stacked-date | [G](cards-G-tray-interop-windows.md) |
| `bevel-2t7u` | Windows: mirror third-party notification-area icons into the tray | [G](cards-G-tray-interop-windows.md) |
| `bevel-m3.6` | M3-F: Menu-bar reclaim (consent-gated, feature-flagged) (bevel-m3) | [G](cards-G-tray-interop-windows.md) |
| `bevel-spike-tier3` | SPIKE: tier-3 headless-Finder mirroring (measure window-escape rate) | [G](cards-G-tray-interop-windows.md) |
| `bevel-spike-shadow` | SPIKE: bundle-id shadowing of com.apple.finder (documents failure mode; never ships) | [G](cards-G-tray-interop-windows.md) |

### Parked (deferred in the tracker; not in the order of work)

- `bevel-f4st` — licence validation design (fail-open).
- `bevel-kbg9` — licensing tiers.
- `bevel-l4fg` — move Filer and widgets to the private repo. No card in this plan adds a new App-to-Filer link; `bevel-ioxx`, `bevel-ze3o` and `bevel-4pgk` add small Filer-facing surface, noted in their cards.

## Review focus

Failure modes no single card's tests cover, most likely first.

1. **A card's premise went stale.** The code moved after `51582c0`. Expected: the executor notices at step 4 of the loop and comments on the bead instead of forcing the card's steps onto different code.
2. **Green in headless tests, wrong on a real machine.** TCC grants, Spaces, multiple displays and macOS 26 capture limits do not exist in the test host. Expected: `AGENT+HUMAN-VERIFY` cards stay open until the owner's check passes.
3. **Two cards edit the same file at once.** Expected: ruling 5 is followed; conflicts are merged, never rebased away.
4. **A wire or persisted format changes under running processes or old settings.** Bevel's processes can briefly run different versions during an update. Expected: ruling 7; old values still load, and a version-skewed peer gets a Hold dialog, not a crash.
5. **A destructive operation runs against real data.** Empty Trash (`bevel-j905`), the uninstaller (`bevel-q3l`), real file operations (`bevel-jlbs`). Expected: confirm dialog in the product, temp directories in every test, and no manual run against the home directory by an agent.

## Files in this plan

- `README.md` — this file.
- `waves.txt` — the order as data: `wave|bead|who|size|card file|title`.
- `progress.sh` — prints progress per wave and the next card, reading status from `bd`.
- `parked.txt` — cards the loop should skip for now: `bead|reason`. Starts empty.
- `cards-*.md` — one card per bead, grouped by area. Each area file opens with a summary of that area's architecture and its own order.
