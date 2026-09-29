# Explorer Supervision + Pre-Warm (bevel-t48y) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Explorer windows become supervised launcher children — spawned via a launcher-control verb, torn down on Quit, restarted on RestartAll, with visible failure and captured stderr — plus ONE parked pre-warmed `--role=explorer` instance so "open folder" drops from a cold process start to a frame.

**Architecture:** The launcher owns a new `ExplorerSupervisor` (a dynamic, instance-keyed sibling to `RoleProcessSupervisor`'s one-process-per-role set) and a `SpawnExplorer` control verb with a path payload. `SpawningShellSurface` routes supervised opens to the verb (parked-handoff fast path, cold-spawn fallback) and keeps today's in-process `Program.SpawnExplorer` only for unsupervised dev runs. The parked explorer serves a `Show` request on its existing per-pid `ExplorerControlServer`.

**Tech Stack:** C# / .NET 10, xUnit, `Bevel.ShellCore.Ipc` UDS transports, conventional commits.

**Spec:** `bevel-t48y` (this plan is the design record) + `bevel-ncrh` (the first-open symptom this fixes) + `CODEMAP.md` → Coupling / Entrypoints (launcher supervision, `LauncherControl` verbs, `ExplorerControlEndpoint` rendezvous).

---

## Mandatory classification (csharp-refactoring gate)

This bead is a **feature**, not a behavior-preserving refactor: supervision, teardown-on-Quit, restart-on-RestartAll, and pre-warm are new observable behavior. Exactly ONE task in this plan (Task 1) is a behavior-preserving structural operation, and is governed by the `csharp-refactoring` skill; every other task is feature work under the repo's normal TDD workflow. The skill's stop-handoff does not abort this plan because the plan was classified before any edit.

## Global Constraints

- **Never block the Avalonia UI thread.** The taskbar's open/reveal path must not `.Wait()` on the launcher verb — the existing `LauncherControl.TrySend` pattern (bounded, off-thread, `Task.Run`-detached) is the template.
- **Bevel.Core must not reference Avalonia** (ARCH-02). No new files go there.
- **macOS TCC attribution:** children spawn DIRECTLY from the launcher process (like core/taskbar today). Never route explorer spawns through `/bin/sh`/`nohup` — the bead names detached-spawn as the attribution breaker. Relaunch-through-LaunchServices applies only to the launcher itself (`Program.Relaunch`), unchanged.
- **Never `kill -9` a Bevel process; SIGTERM the launcher** so the Dock restore + child teardown run (repo memory `never-kill-9-bevel-it-owns-the-dock`).
- **Conventional-commit messages; NEVER add Claude/Anthropic attribution trailers** (CLAUDE.md absolute rule).
- **Full suite green at every commit:** `dotnet test Bevel.sln`. Baseline measured 2026-09-30: **1195 passed, 0 failed, 2 skipped**.
- **End-of-turn:** `dotnet build Bevel.sln -clp:ErrorsOnly`, and if the dist changed: `build-app.sh && dev-sign.sh` → SIGTERM launcher → `open ~/src/shell/dist/Bevel.app` **as one cycle** (rebuild-while-running puts the old launcher into a VersionSkew Hold — repo memory `dist-rebuild-skews-live-launcher`).
- macOS/BSD shell syntax. Task tracking via `bd` (claim `bevel-t48y` before starting).

## Review Focus

Five things this feature could get wrong that per-task tests won't obviously catch:

1. **TCC identity of the parked explorer.** The parked instance spawns at launcher boot and lives hidden; if its spawn path ever detaches (shell/nohup), the *first* window it opens silently loses Accessibility grants. Task 2's launcher-side spawn must reuse `Program.CreateRoleStartInfo`'s direct-start shape, and Task 5 verifies live from a Finder-launched shell.
2. **Restart policy vs. the user-hidden rule.** `bevel-gdie` established that a surface the user closed must NOT be auto-respawned. An explorer that exited because its window closed is a *normal* close; one that exited with the window up is a *crash*. Task 3's supervisor must distinguish them (close-ack vs. unexpected exit) or closed windows resurrect / crashed ones never return.
3. **Park-handoff races.** Two rapid opens: the second must not receive a parked instance mid-handoff, and the keep-1 invariant must not leak a second parked process. Task 3's handoff takes the parked slot under its lock before showing.
4. **Heartbeat key collisions.** `RoleHeartbeatStore` is keyed by role; N explorers would clobber each other's `status=ready`. Task 3 writes instance-keyed heartbeats (`explorer-<pid>`) — or skips explorer heartbeats entirely and relies on supervisor liveness; pick ONE and keep `ShellHealthMonitor` from alerting on it.
5. **Launcher verb protocol back-compat.** `LauncherControl` verbs are single bytes with one-byte replies; `SpawnExplorer` adds a *payload*. The reply contract for OLD verbs must not change — old cores/taskbars dialing a new launcher and vice versa keep working (version skew window during self-update).

## Current State (measured 2026-09-30)

- `Program.SpawnExplorer` (Program.cs ~line 462): filters this process's argv, appends `--role=explorer --open-path=… [--search] [--select=…]`, builds a `ProcessStartInfo` via `CreateRestartStartInfo`, and fire-and-forgets `Process.Start`. Failure = one `Console.Error` line; nothing supervises the child; RestartAll and Quit don't know it exists. TCC: `CreateDetachedMacOSStartInfo` — **verify at Task 1** whether it still routes via `/bin/sh`+nohup (the bead's claim; line numbers have drifted) — either way the supervision rework removes the detach.
- `RoleProcessSupervisor`: fixed ordered children (core, taskbar, [+desktop runtime]) + `SpawnRoleAsync`/`CloseRoleAsync` keyed by **role** — one process per role. Crash monitor, backoff, cooldown, and `ShellHealthMonitor` integration all key on `ShellRole`. Explorers need **N instances per role** — that is why this is a sibling supervisor, not a reuse.
- `LauncherControl`: verbs 1–6 (`RestartAll`…`QueryDesktop`), 1-byte wire + 1-byte ack; child side `TrySend`/`QueryDesktopRunning` (bounded, off-thread). Launcher loop lives in `Program.RunLauncher` (~line 280–315).
- `SpawningShellSurface` (`IShellSurface` impl in the taskbar) → `IExplorerSpawner` → today `Program.SpawnExplorer`. This is THE seam Task 2 rewires — no Start-menu/automation code changes beyond the spawner.
- `ExplorerControlServer` (`explorer-<pid>.sock`, rendezvous dir env-published via `ShellCoreAppEnvironment`): per-pid control channel the taskbar already dials (`TaskbarExplorerControlClient`). The parked instance's `Show` request rides this existing server + protocol.
- `bevel-ncrh` context: the "first open does nothing" symptom is cold-start latency (single-file self-extract + dyld + Avalonia/DI + the settings dial). Pre-warm deletes the latency; supervision deletes the invisible-failure class. The automation-socket contention found during its investigation is OUT of scope here (separate fragility, own bead if wanted).

## Non-Goals

- Explorer crash-restart carrying window *state* (scroll/selection) — restart at last-open-path only.
- Multi-display park placement — the parked window shows wherever `TaskbarWindow`-independent `FileManagerWindow` placement puts it.
- The automation-socket fixed-path fragility (`bevel-ncrh` finding).
- Changing `bevelctl`/`bevel://` verbs — they land in `SpawningShellSurface` like Start-menu opens.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Bevel.App/Program.cs` | `CreateRoleStartInfo` generalized to extra child args (Task 1); `SpawnExplorer` becomes the unsupervised fallback; `--park` arg parsing. |
| `src/Bevel.App/Supervision/ExplorerSupervisor.cs` (new) | Dynamic explorer child set: spawn/park/handoff, close-vs-crash policy, teardown, RestartAll participation, instance heartbeats. |
| `src/Bevel.App/Supervision/LauncherControl.cs` | Verb `SpawnExplorer` + payload encode/decode + `TrySpawnExplorer` child API (bounded, off-thread). |
| `src/Bevel.App/Supervision/LauncherControlProtocol.cs` (new, only if encoding outgrows a small helper) | Payload framing for the new verb — keep verbs 1–6 byte-identical. |
| `src/Bevel.App/SpawningShellSurface.cs` | `IExplorerSpawner` implementation swap: supervised → verb; unsupervised → `Program.SpawnExplorer`. |
| `src/Bevel.App/ShellCore/ExplorerControlEndpoint.cs` + `Bevel.App`'s `ExplorerProtocol` | `Show` request (open path + search + select) for the parked instance. |
| `src/Bevel.App/Program.cs` launcher loop | Verb dispatch → `ExplorerSupervisor`; boot-time park spawn; Quit teardown; RestartAll fan-out. |
| `tests/Bevel.Taskbar.Tests/ExplorerSupervisorTests.cs` (new) | Supervisor lifecycle tests over a fake `IRoleProcess`. |
| existing `RoleProcessSupervisorTests`, `ExplorerControlChannelTests`, `SpawningShellSurface`-related tests | Extended, not replaced. |

---

## Task 1 — Refactor: one spawn-command builder (skill-governed, behavior-preserving)

**Classification: behavior-preserving refactor — govern with `csharp-refactoring`.** If any sub-step turns out to change observable behavior (e.g. removing the detach), split it out and defer it to Task 2's feature work.

- [ ] Record baseline: `dotnet test Bevel.sln` (expect 1195 passed / 2 skipped).
- [ ] Verify what `CreateDetachedMacOSStartInfo` actually does today (read the body; the bead's `/bin/sh`+nohup claim has drifted line numbers). Record the finding on the bead — Task 2 depends on it.
- [ ] Generalize `Program.CreateRoleStartInfo(role, launcherArgs, env)` to accept optional extra child args (`--open-path=…`, `--search`, `--select=…`) — binding-aware update of its callers (`RoleProcessSupervisor`'s spawn path); no textual find/replace.
- [ ] Extract `Program.BuildExplorerArgs(launcherArgs, openPath, search, selectPath)` as the ONE argv builder; make `SpawnExplorer` use it (today's inline filtering moves there verbatim).
- [ ] New unit test: `BuildExplorerArgs` + the generalized `CreateRoleStartInfo` produce the exact argv/env today's `SpawnExplorer` builds (assert against literal expected args).
- [ ] `dotnet build Bevel.sln -clp:ErrorsOnly` + `dotnet test Bevel.sln` — same pass count.
- [ ] Commit: `refactor(app): one spawn-command builder for role and explorer children (bevel-t48y)`

## Task 2 — `SpawnExplorer` launcher verb (feature)

- [ ] Write failing test: `LauncherControl` round-trips the new verb with payload (encode open-path + search flag + select-path; existing transport tests are the pattern — `ExplorerControlChannelTests`).
- [ ] Implement verb `SpawnExplorer = 7`: child-side `TrySpawnExplorer(openPath, search, selectPath)` (bounded off-thread send, bool ack) + launcher-side decode. **Verbs 1–6 stay byte-identical.**
- [ ] Wire `SpawningShellSurface`'s spawner: `LauncherControl.IsSupervised` → `TrySpawnExplorer`; else → `Program.SpawnExplorer` (today's behavior, unchanged for unsupervised dev).
- [ ] Tests: spawner routing (supervised sends the verb, unsupervised spawns in-process) — extend the existing `SpawningShellSurface` fake-spawner test.
- [ ] Full suite + build green. Commit: `feat(launcher): SpawnExplorer control verb (bevel-t48y)`

## Task 3 — `ExplorerSupervisor` (feature, launcher side)

- [ ] Write failing tests first (fake `IRoleProcess`, the `RoleProcessSupervisorTests` pattern):
  - cold open spawns a supervised explorer child with the requested args and instance key;
  - Quit/teardown SIGTERMs every explorer child and de-supervises;
  - RestartAll respawns every live explorer at its **last-open-path**, and re-parks the parked slot;
  - **crash** (exit without close-ack) respawns at last path with backoff; **normal close** (close-ack received) never respawns (the `bevel-gdie` user-hidden rule);
  - stderr of each child is captured to `RestartDiag`/a per-child file (the `bevel-peer-process-crashes-an-unhandled-exception` lesson: the only managed stack for a dying peer is its stderr).
- [ ] Implement `ExplorerSupervisor`: instance-keyed children (pid), spawn via Task 1's generalized `CreateRoleStartInfo`, last-open-path tracking, close-vs-crash distinction, heartbeat decision (instance-keyed `explorer-<pid>` OR supervisor-only liveness — one choice, documented; `ShellHealthMonitor` must not Hold on explorer churn).
- [ ] Launcher loop: verb 7 → `ExplorerSupervisor.OpenAsync` (parked handoff when available — Task 4 — else cold spawn); ack `true` after spawn acceptance, `false` + `RestartDiag` on failure (the visibility fix).
- [ ] Boot: spawn ONE parked instance shortly after core+taskbar are ready (not on the first-paint critical path).
- [ ] Full suite + build green. Commit: `feat(launcher): supervised explorer children — teardown, restart, visible failure (bevel-t48y)`

## Task 4 — Parked explorer + `Show` handoff (feature)

- [ ] Write failing test: `ExplorerControlServer` handles a `Show` request (open path / search / select) — extend `ExplorerControlChannelTests`.
- [ ] `--park` parsing (Program/RoleSelection): explorer builds its window hidden, starts its control server, reports parked-ready, and waits. No taskbar/startup work happens on the taskbar's first-paint path (it's a separate process — the never-block rule is about its own UI thread).
- [ ] Handoff: launcher (or the parked explorer's own server handler) shows the window at the requested path; the slot is marked busy **under the supervisor's lock before** the show request is sent (rapid-open race); after handoff the supervisor immediately spawns a replacement parked instance (keep-1 invariant).
- [ ] A parked instance that never hands off stays alive across RestartAll **only as a fresh re-park** (do not re-show a stale window at an old path).
- [ ] Full suite + build green. Commit: `feat(explorer): parked pre-warmed explorer + Show handoff (bevel-t48y)`

## Task 5 — Wire-up, live validation, close-out

- [ ] End-to-end check on `--pal=fake` first (`dotnet run --project src/Bevel.App -- --pal=fake`), then the real cycle: `build-app.sh && dev-sign.sh` → SIGTERM launcher → `open ~/src/shell/dist/Bevel.app`.
- [ ] **Live checklist (needs the user / a Finder-launched shell):**
  - Start ▸ a place opens a window in ~a frame (parked handoff); a *second* open right after also opens (replacement park won the race or cold path covered it);
  - first-open-of-session opens (the `bevel-ncrh` symptom is gone);
  - Quit tears down explorers (no orphan `--role=explorer` processes; `pgrep -fl "dist/Bevel.app"` empty);
  - RestartAll restarts explorers at their paths on the new binary (no version-skew Hold);
  - a force-crashed explorer comes back at its path; a normally-closed one does not;
  - the opened window keeps working after TCC-gated operations (attribution survived).
- [ ] CODEMAP update: Entrypoints (explorer role + park), Coupling (supervision now covers explorers), Local Commands unchanged.
- [ ] `bd close bevel-t48y --reason=…`; comment `bevel-ncrh` with the outcome (close it if the symptom is confirmed gone live).
- [ ] Final: full suite, commit, push (with the user's blessing), dist cycle.
