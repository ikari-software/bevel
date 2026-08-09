# Bevel — Codemap

A navigational map of the repository: where things live and what to touch for a given change.
For the *why* behind the architecture, see [`../CLAUDE.md`](../CLAUDE.md); for deep design notes,
see [`design/`](./design), [`spec/`](./spec), and [`plans/`](./plans).

> **⚠️ Keep this in sync.** When you add a project, move a subsystem, or change an entry point,
> update the tables below. And when a change is **user-facing** (a new feature, theme, or skin),
> also refresh the **landing page** — see [Landing site](#landing-site-siteindexhtml) at the bottom.

---

## Top-level layout

| Dir | What's in it |
|---|---|
| `src/` | All .NET projects (the shell itself). |
| `native/` | Swift `BevelHelper` (window/tray enumeration) + AppleEvent probes. |
| `proto/` | gRPC `.proto` contracts (helper client ↔ helper, taskbar ↔ core). |
| `tests/` | xUnit + Avalonia.Headless test projects (`compat/`, `rigs/` are support). |
| `packaging/macos/` | `.app` build → sign → notarize → DMG scripts + `Info.plist`, entitlements, `Bevel.icns`. |
| `branding/` | `bevel-icon.svg` — the app-icon vector source (feeds `packaging/macos/Bevel.icns`). |
| `site/` | The landing page (`index.html`) + real screenshots (`shots/`). |
| `docs/` | `design/`, `spec/`, `plans/`, `reference/`, `spike/`, `perf.md`, this file. |
| `benchmarks/` | BenchmarkDotNet (`Bevel.Benchmarks`) — **not** in `Bevel.sln`; build/run explicitly. |
| `assets/`, `third_party/`, `tools/`, `refs/`, `dist/` | Static assets, vendored code, dev tooling, git refs helpers, build output. |

---

## .NET projects (`src/`)

**Composition & entry**

| Project | Role | Start here |
|---|---|---|
| `Bevel.App` | Composition root: DI wiring, `--role`/`--pal` parsing, entry point, window factories. | `Program.cs`, `CompositionRoot.cs`, `RoleSelection.cs`, `PalSelection.cs`, `Supervision/`, `ShellCore/` |
| `bevelctl` | CLI that drives the shell over the automation socket (`bevel://` verbs). | — |

**Domain & platform**

| Project | Role | Notes |
|---|---|---|
| `Bevel.Core` | Domain, services, settings, VFS. | **Must not reference Avalonia** (ARCH-02). `SettingsService` = SQLite blob at `~/.config/bevel/settings.db`. |
| `Bevel.Pal.Abstractions` | Capability-oriented platform abstraction interfaces. | |
| `Bevel.Pal.MacOS` | Real macOS PAL; spawns/manages the Swift helper (`HelperProcessHost`). | Needs TCC grants. |
| `Bevel.Pal.Fake` | Deterministic in-memory PAL (`--pal=fake`). | What most tests + safe dev runs use. |
| `Bevel.Interop` | ObjC interop + the automation control socket (`bevel://` / CLI verbs). | |
| `Bevel.Ipc` / `Bevel.ShellCore.Ipc` | gRPC-over-Unix-domain-socket transports. | Per-session HMAC nonce auth; socket dir 0700. Contracts in `proto/`. |

**UI & windows**

| Project | Role | Notes |
|---|---|---|
| `Bevel.UI` | Shared Avalonia chrome + theming services. | `BevelWindow` (client-drawn Win2000 chrome), `Glyphs`, `ThemeService`, `LunaVariantService`, `ColorSchemeService`. |
| `Bevel.Themes.Win2000` | Default theme (aliases Classic.Avalonia via `BasedOn`). | |
| `Bevel.Themes.Luna` | Glossy Luna (XP) ControlThemes. Colours: Blue / Silver / Black / Purple × Gloss/Hybrid/Matte. | Flat/Whistler is spec-only (`docs/design/flat/`). |
| `Bevel.FileManager` | Explorer window + `Components/` (ItemView, InfoPane, address bar…) + VFS UI. | |
| `Bevel.Taskbar` | Taskbar, Start menu, tray, and the background `ShellModel`. | |
| `Bevel.Desktop` | The desktop surface window. | |
| `Bevel.IconPreview` | Dev tool for eyeballing self-drawn icons. | |

**Two runtime recolor engines** (`LunaVariantService`, `ColorSchemeService`) *override* static theme
tokens and must be `Clear()`'d symmetrically when switching away.

---

## Native helper (`native/helper-macos/Sources/BevelHelper/`)

Swift agent (`.accessory`) that enumerates windows/tray over gRPC. Needs its **own** TCC grants
(Accessibility + Screen Recording), keyed to its signature.

| File | Role |
|---|---|
| `BevelHelper.swift` | `@main`: arg parse, NSApplication agent + **TCC prompts**, service wiring. |
| `WindowServiceImpl.swift` | Window enumeration (AX + CGWindowList; drops non-`.regular` apps). |
| `TrayServiceImpl.swift` | Tray mirroring via ScreenCaptureKit; `[BEVEL-TRAY]` self-test → stderr. |
| `SupervisionServiceImpl.swift` / `ReverseWatchdog.swift` | Lifecycle: parent-death detection, clean exit. |
| `AuthInterceptor.swift` | HMAC nonce auth on the gRPC channel. |

Diagnostics go to **stderr only** (never to disk) — captured by the parent via `HelperProcessHost`.

---

## Tests (`tests/`)

Standard xUnit + Avalonia.Headless (`UseSkia()` + `UseHeadlessDrawing=false` → real pixels via
`CaptureRenderedFrame()`). Theme-mutating classes share `[Collection("TaskbarTheme")]`.

**Screenshot harvesting** — the `Render*` tests dump real rendered PNGs to env-var paths; used to
build the landing-page screenshots (no mockups). See the beads memory `site-real-screenshots`, e.g.
`BEVEL_LUNA_VARIANTS_DIR=… dotnet test tests/Bevel.Taskbar.Tests --filter FullyQualifiedName~RenderLunaVariantsTest`.

---

## Packaging (`packaging/macos/`)

Pipeline: `build-app.sh` → `sign-app.sh` (`TIMESTAMP=1` for notarization) → `notarize-app.sh`
→ `make-dmg.sh`. `dev-sign.sh` is the fast local grant-preserving sign. `build-app.sh` bundles the
Swift helper and copies `Bevel.icns` if present. See `../CLAUDE.md` → Build & Test for commands.

---

## Landing site (`site/index.html`)

Single self-contained page (fonts inlined as woff2 data-URIs). Every visual is a **real render**
harvested from the shell's own headless tests — hero (Luna desktop composite), the "See it running"
gallery (Win2000 Start menu + Folder-Options triptych), and the Themes cards (Win2000 dialog + the
four real Luna colourways). Screenshots live in `site/shots/`. Flat/Whistler is an honest
"renderer in progress" placeholder (it has no renderer yet). **Not** published to GitHub Pages —
public branding is gated on counsel (bead `bevel-legal-branding`).

> **Update the page as the project grows.** When a user-facing feature, theme, colourway, or skin
> lands, refresh `site/index.html` to match — re-harvest the relevant `Render*` screenshot rather
> than hand-drawing one, drop it in `site/shots/`, and keep the copy honest (no faked shots for
> things that don't exist yet).
