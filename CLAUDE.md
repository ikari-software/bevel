# Project Instructions for AI Agents

This file provides instructions and context for AI coding agents working on this project.

<!-- BEGIN BEADS INTEGRATION v:1 profile:minimal hash:6cd5cc61 -->
## Beads Issue Tracker

This project uses **bd (beads)** for issue tracking. Run `bd prime` to see full workflow context and commands.

### Quick Reference

```bash
bd ready              # Find available work
bd show <id>          # View issue details
bd update <id> --claim  # Claim work
bd close <id>         # Complete work
```

### Rules

- Use `bd` for ALL task tracking — do NOT use TodoWrite, TaskCreate, or markdown TODO lists
- Run `bd prime` for detailed command reference and session close protocol
- Use `bd remember` for persistent knowledge — do NOT use MEMORY.md files

**Architecture in one line:** issues live in a local Dolt DB; sync uses `refs/dolt/data` on your git remote; `.beads/issues.jsonl` is a passive export. See https://github.com/gastownhall/beads/blob/main/docs/SYNC_CONCEPTS.md for details and anti-patterns.

## Agent Context Profiles

The managed Beads block is task-tracking guidance, not permission to override repository, user, or orchestrator instructions.

- **Conservative (default)**: Use `bd` for task tracking. Do not run git commits, git pushes, or Dolt remote sync unless explicitly asked. At handoff, report changed files, validation, and suggested next commands.
- **Minimal**: Keep tool instruction files as pointers to `bd prime`; use the same conservative git policy unless active instructions say otherwise.
- **Team-maintainer**: Only when the repository explicitly opts in, agents may close beads, run quality gates, commit, and push as part of session close. A current "do not commit" or "do not push" instruction still wins.

## Session Completion

This protocol applies when ending a Beads implementation workflow. It is subordinate to explicit user, repository, and orchestrator instructions.

1. **File issues for remaining work** - Create beads for anything that needs follow-up
2. **Run quality gates** (if code changed) - Tests, linters, builds
3. **Update issue status** - Close finished work, update in-progress items
4. **Handle git/sync by active profile**:
   ```bash
   # Conservative/minimal/default: report status and proposed commands; wait for approval.
   git status

   # Team-maintainer opt-in only, unless current instructions forbid it:
   git pull --rebase
   git push
   git status
   ```
5. **Hand off** - Summarize changes, validation, issue status, and any blocked sync/commit/push step

**Critical rules:**
- Explicit user or orchestrator instructions override this Beads block.
- Do not commit or push without clear authority from the active profile or the current user request.
- If a required sync or push is blocked, stop and report the exact command and error.
<!-- END BEADS INTEGRATION -->


## Build & Test

.NET 10 SDK (pinned in `global.json`), macOS-first. Common commands:

```bash
dotnet build Bevel.sln -clp:ErrorsOnly          # build everything
dotnet test  Bevel.sln                          # full test suite (xUnit + Avalonia.Headless)
dotnet test  tests/Bevel.FileManager.Tests/Bevel.FileManager.Tests.csproj   # one project

# Run the shell for dev (Fake PAL — safe, no helper/AX prompts). Split is the ONLY mode: an
# argument-less launch boots --role=launcher, which spawns core + taskbar (+ filer per window):
dotnet run --project src/Bevel.App -- --pal=fake
dotnet run --project src/Bevel.App -- --role=launcher            # explicit; same as no --role
dotnet run --project src/Bevel.App -- --role=filer --open-path ~/Documents   # a single surface

# Package + code-sign a dev .app (re-run after each build so TCC grants stick — ad-hoc
# cdhash churn otherwise forces re-granting Accessibility/Screen Recording every rebuild):
./packaging/macos/build-app.sh && ./packaging/macos/dev-sign.sh

# Stop a running dev shell — ASK IT KINDLY. Never kill -9.
# Bevel hides the macOS Dock while it runs and restores it from AppDomain.ProcessExit
# (MacOSDockController), which SIGTERM reaches and SIGKILL does not. kill -9 therefore leaves the
# user's Dock hidden until the NEXT clean start notices and self-heals. Signal the LAUNCHER (the
# argument-less process) — it drives the clean shutdown of core, taskbar and helper; signalling a
# child instead just gets it respawned by the supervisor.
pkill -f "dist/Bevel.app/Contents/MacOS/Bevel$"      # SIGTERM, no -9
# Best of all, when the UI is alive: Start > Turn Off.
# If it is genuinely wedged and -9 is unavoidable, restore the Dock by hand afterwards:
#   defaults write com.apple.dock autohide -bool false && killall Dock
```

Benchmarks live in `benchmarks/Bevel.Benchmarks` (BenchmarkDotNet; **not** in `Bevel.sln` —
build/run explicitly). See `docs/perf.md`.

## Architecture Overview

**Multi-process shell (the ONLY mode).** An argument-less launch boots `--role=launcher`: a **launcher**
supervises a headless **core** process and a **taskbar** process (and a **desktop** process only when
shown from the Start menu — off by default, bevel-gdie/dwhy), and each Filer window spawns as its own
`--role=filer` process (`--open-path`). There is **no single-process mode** — `role=all` was removed
(bevel-dwhy). The **core** owns the Swift helper + `settings.db` and serves both to the peer processes over
the shell-core IPC (bevel-6nve); peers never open the DB or the helper directly. `RoleProcessSupervisor`
(in `Bevel.App/Supervision`) owns spawn/restart (and runtime add/remove for the desktop); SIGTERM handlers
must set `ctx.Cancel` or children orphan.

**Projects:**
- `Bevel.Core` — domain, services, settings, VFS. **Must not reference Avalonia** (ARCH-02).
- `Bevel.App` — composition root: DI wiring, `--role`/`--pal` arg parsing, entry point, window factories.
- `Bevel.UI` — shared Avalonia chrome: `BevelWindow` (inherits Classic.Avalonia `ClassicWindow` for
  client-drawn Win2000 chrome), `Glyphs` (self-drawn vector file icons), and the theming services
  `ThemeService` / `LunaVariantService` / `ColorSchemeService`.
- `Bevel.Themes.Win2000` (default; aliases Classic.Avalonia via `BasedOn`) and `Bevel.Themes.Luna`
  (glossy vector ControlThemes). Flat/Whistler is spec-only so far (`docs/design/flat/`).
- `Bevel.FileManager` — Filer window + `Components/` (ItemView, InfoPane, address bar, …) + VFS UI.
- `Bevel.Taskbar` — taskbar, Start menu, tray, and the background `ShellModel` (owns window/app/tray
  observable collections, updated off-thread).
- `Bevel.Desktop` — the desktop surface window.
- `Bevel.Ipc` / `Bevel.ShellCore.Ipc` — gRPC-over-Unix-domain-socket transports (helper client;
  taskbar↔core). Per-session HMAC **nonce** auth; socket dir hardened to 0700.
- `Bevel.Pal.Abstractions` / `Bevel.Pal.MacOS` / `Bevel.Pal.Fake` — capability-oriented platform
  abstraction layer (`--pal=fake` is the deterministic in-memory impl used by most tests).
- `Bevel.Interop` — ObjC interop + the automation control socket (`bevel://` / CLI verbs).
- `native/helper-macos/` — Swift helper: window/tray enumeration (AX + CGWindowList + ScreenCaptureKit)
  exposed over gRPC-swift. Needs its **own** TCC grant (keyed by binary path).

**Settings** persist as a single-row JSON blob in SQLite at `~/.config/bevel/settings.db`
(`SettingsService`); only the core opens it and pushes snapshots to peers over IPC — a peer paints its
first frame from `peer-settings-cache.json` (its write-through copy of the last snapshot) so it never
waits on the socket (`settings.json` is a passive export). **Icons** render off-thread, cached, and are shared across processes via a
memory-mapped BGRA pool (`MmfBgraPool`).

## Codemap & Landing Site

- **Codemap:** [`CODEMAP.md`](CODEMAP.md) is the repo navigation map (roles, boundaries, where theming /
  settings / IPC / the helper live). Format rules + per-package convention: [`docs/code-maps/FORMAT.md`](docs/code-maps/FORMAT.md);
  index: [`docs/code-maps/INVENTORY.md`](docs/code-maps/INVENTORY.md). These are *navigation maps, not architecture
  docs* — spend lines where grep fails (ownership, sibling disambiguation, not-this trails, exact symbols).
  Update per the map's own checklist when you change entrypoints, contracts, or sharp edges.
- **Landing page:** `site/index.html` is a self-contained page whose visuals are all **real renders**
  harvested from the `Render*` tests (beads memory `site-real-screenshots`). **When a user-facing
  feature, theme, colourway, or skin lands, update the page to match** — re-harvest the screenshot into
  `site/shots/` rather than hand-drawing one, and keep the copy honest (no faked shots for things that
  don't exist yet). Canonical host: **https://bevel.ikari.software/** (Deno Deploy project `bevel-site`);
  alias `https://ikari.software/bevel-desktop` 301s there. Also serves `/robots.txt`, `/agents.txt`, and
  `/updates/` (Velopack static feed stubs).

## Conventions & Patterns

- **NEVER block the Avalonia UI thread** (non-negotiable). No `.Result`/`.Wait()`/`.GetAwaiter().GetResult()`
  on the UI thread, no inline icon render / enumeration / file I/O in handlers or startup. Do heavy work
  off-thread and marshal only the cheap result back (`Dispatcher.UIThread.Post/InvokeAsync`).
- **Vector-only assets** — SVG or code-drawn geometry bound to theme tokens, never bitmaps. **Never
  disable antialiasing**, even for "authenticity".
- **"Fidelity = colours + feel + function," not pixel-perfect.** The north star is "Win2000 as if
  designed in 2026"; tasteful cross-era extensions (XP/macOS niceties) are welcome, rendered in the skin.
- App pins `RequestedThemeVariant="Light"` — do not add `RequestedThemeVariant`/`ThemeVariantScope`
  overrides (popups follow `Application.ActualThemeVariant`; a Dark leak washes out menus).
- Two runtime recolor engines (`LunaVariantService`, `ColorSchemeService`) **override** static theme
  tokens and must be `Clear()`'d symmetrically when switching away.
- **Task tracking is `bd` (beads), not TodoWrite/markdown.** Run `bd prime`. Persistent knowledge via
  `bd remember`.
- **Tests:** xUnit + Avalonia.Headless (`UseSkia()` + `UseHeadlessDrawing=false` yields real pixels via
  `CaptureRenderedFrame()`). Theme/resource-mutating test classes share `[Collection("TaskbarTheme")]`
  so they don't collide on the `Application.Current` singleton.
- macOS/BSD shell syntax; conventional-commit messages.
