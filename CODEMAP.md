# Bevel Code Map

For shared format rules, see `docs/code-maps/FORMAT.md`. Inventory: `docs/code-maps/INVENTORY.md`.

## Snapshot
- Themable desktop-shell replacement (Windows 2000/XP reimagined "as if designed in 2026"), macOS-first.
- .NET 10 + Avalonia — ~16 projects under `src/` — plus a Swift helper (`native/helper-macos`) over gRPC/UDS.
- Multi-process: shipped `.app` boots `--role=all`; `--role=launcher` splits into launcher → core + taskbar, and each Explorer window is its own `--role=explorer` process.

## Start Here
- `src/Bevel.App/Program.cs` — process entry; `--role` / `--pal` parsing dispatches to the right surface.
- `src/Bevel.App/CompositionRoot.cs` — the single place services are wired (DI). Change wiring here, not per-project.
- `src/Bevel.App/Supervision/RoleProcessSupervisor.cs` — spawns/restarts child roles.
- `src/Bevel.UI/` — shared chrome (`BevelWindow`) + theming. Live theme colour lives here, **not in XAML** (see Notes).
- `src/Bevel.Taskbar/Model/ShellModel.cs` — background model owning the window/app/tray observable collections.
- `native/helper-macos/Sources/BevelHelper/` — window/tray enumeration; a separate TCC identity (see Coupling).

## Entrypoints
- Roles: `--role=all|launcher|core|taskbar|explorer` (+ `--open-path`). PAL: `--pal=fake|macos`. Parsed in `Program.cs` + `RoleSelection.cs` + `PalSelection.cs`.
- `bevelctl` binary + `bevel://` URL verbs → automation socket, owned by `Bevel.Interop`.
- Packaging: `packaging/macos/{build-app,sign-app,notarize-app,make-dmg}.sh` (+ `dev-sign.sh` for fast local signing).

## Contracts And Data
- gRPC contracts are **source-of-truth in `proto/`** — verify message shapes there, don't infer from a call site. Transports: `Bevel.Ipc` + `Bevel.ShellCore.Ipc` (UDS, per-session HMAC nonce auth, socket dir 0700).
- Settings: `Bevel.Core/SettingsService.cs` — one JSON blob row in SQLite at `~/.config/bevel/settings.db`, polled every 750 ms. **`settings.json` is a passive export — editing it does nothing; change settings via `SettingsService`.**
- Domain / VFS / services: `Bevel.Core` — **must not reference Avalonia** (ARCH-02).
- PAL: interfaces in `Bevel.Pal.Abstractions`; `Bevel.Pal.MacOS` (real) vs `Bevel.Pal.Fake` (deterministic; what most tests + `--pal=fake` use).

## Coupling
- `Bevel.Pal.MacOS/HelperProcessHost.cs` launches the Swift helper; `ResolveHelperBinary` locates the binary (bundle or dev-build). The helper (`pl.ikari.bevel.helper`) is a **separate TCC entity** needing its OWN Accessibility + Screen Recording grants; it prompts on launch; its diagnostics go to **stderr only**.
- Icons render off-thread, cached, shared across processes via a memory-mapped BGRA pool (`MmfBgraPool`) — not re-rendered per process.

## Local Commands
- Build `dotnet build Bevel.sln -clp:ErrorsOnly` · Test `dotnet test Bevel.sln`.
- Safe dev run: `dotnet run --project src/Bevel.App -- --pal=fake` (Fake PAL, no helper/TCC prompts).
- Dev `.app`: `./packaging/macos/build-app.sh && ./packaging/macos/dev-sign.sh`.
- Benchmarks: `benchmarks/Bevel.Benchmarks` — **not** in `Bevel.sln`; build/run explicitly.

## Notes
- **Live theme colour = two runtime engines, not XAML.** `LunaVariantService` (`src/Bevel.UI/Luna/LunaVariant.cs`; Luna colours Blue/Silver/Black/Purple × gloss) and `ColorSchemeService` (`src/Bevel.UI/ColorSchemeService.cs`; Win2000 schemes) OVERRIDE static tokens and must be `Clear()`'d symmetrically when switching away. Editing the static `.axaml` alone won't change the running look.
- **Never block the Avalonia UI thread** — no `.Result`/`.Wait()`; do heavy work off-thread, marshal the cheap result back.
- App pins `RequestedThemeVariant="Light"`; don't add variant/scope overrides (a Dark leak washes out popups).
- A Popup only opens if its host is attached to a visual tree with a TopLevel — a VM-only `IsOpen=true` can pass a unit test while nothing ever shows (`avalonia-popup-needs-visual-tree`).
- Flat/Whistler is **spec-only** (`docs/design/flat/`) — no renderer yet; don't look for one.
- Landing page `site/index.html` mirrors real UI (screenshots harvested from `Render*` tests). Refresh it when user-facing features land — see CLAUDE.md → Codemap & Landing Site.
- Verification gap: the `check-codemaps` script referenced by `FORMAT.md` isn't wired in this repo yet — check path references by hand.
