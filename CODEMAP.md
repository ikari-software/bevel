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
- **Task-button Tabs submenu = Apple Events + AX** (bevel-a40b/bevel-osad/bevel-l17f). `ITabProvider` (`Bevel.Pal.Abstractions`) → `Bevel.Pal.MacOS/MacOSTabProvider.cs`, an `/usr/bin/osascript` child per query (RS/US-framed rows; iTerm2 / Chromium-family / Safari dialects; failures collapse to empty by contract). The Gecko family (Zen/Firefox) has NO AppleScript tab dictionary — `GeckoTabEngine.cs` serves it in-proc via accessibility (`AXTabButton` walk + `AXPress`; systemwide AX messaging timeout; press-safety policy in `SelectTarget` refuses ambiguous stale rows), and `GeckoSessionStore.cs` (mozLz4 session store) backfills tab urls by title. Favicons: `TabFaviconStore.cs` reads the browsers' own on-disk caches (Chromium `Favicons` / Gecko `favicons.sqlite`) — sharp edge: the live DBs are UNREACHABLE through both SQLite (Firefox holds locking_mode=EXCLUSIVE) and managed file APIs (.NET's macOS flock contends with SQLite's fcntl locks), so the store raw-syscall-copies then queries. Registered direct in every role like `IFileOpener`. `TaskbarView.PrefetchTabsAsync` fetches + decodes icons off-thread BEFORE `TaskButtonMenu.TryShow` (an open MenuFlyout never repaints) under a 1.5 s budget; the submenu height-caps at 480px and scrolls via the Classic template's own menu scroll viewer. Sharp edge: keep the batched `every tab of window wi` property reads — per-tab reads cost ~24 ms each and measured 8.9 s on a 182-tab sidebar (a pinning test guards this).
- **Window-chrome geometry is token-driven**, not hand-tuned in the template: the Win2000 border bands (2px bevel · 2px sizing-frame face · 18px caption · 1px separator) come from `theme.json` metrics (`CaptionMargin`, `WindowContentInset`, `CornerRadius`) → `Tokens.axaml` / `LunaTokens.axaml` via `tools/ThemeGen`, and are **pixel-guarded** by `tests/Bevel.Taskbar.Tests/RenderWin2000BorderTest`. Vendored window template: `third_party/classic-avalonia/.../Styles/Window.axaml`; caption buttons: `.../Styles/CaptionButtons.axaml`.
- **Explorer left pane is resizable + persistent.** `LeftSplitter` drag (code-behind pointer-capture in `FileManagerWindow.axaml.cs`, `OnSplitter*`) resizes column 0; the width (`ExplorerLeftPaneWidth`) and the Folders-vs-info toggle (`ExplorerFoldersOpen`) persist via `SettingsService` and restore in `SetSettingsService`. `Components/InfoPane` owns four independently selectable era presets (Win2000/Me Web View, XP Common Tasks, Vista/7 navigation, Win95/NT4 minimal); `FileManagerWindow.UpdateInfoPane` supplies their shared live places, commands, and selection details. Luna's pane aliases preset-local fallback brushes to `Luna.Brush.InfoPane*`; `LunaVariantService.TransformInfoPane` derives each semantic role from the configured source color plus the active base hue and role-specific S/L treatment. Sharp edge: `ApplyFolderOptions` re-lists the directory **only** when a listing-affecting input (hidden files / extension hiding) changed — layout-only settings writes (pane width, toggle, info-pane style) fan out through `App`'s `settings.Changed` but must NOT reset sibling windows' items/scroll/selection. **Don't re-add an unconditional `ReloadWithCurrentOptions()` there.**
- **Type-ahead is a shared service.** `src/Bevel.Core/Input/TypeToFind.cs` (pure, clock-injected, generic) is the one correct type-ahead matcher — prefix accumulation, single-char cycling, forward wrap, `OrdinalIgnoreCase` — fed from real `TextInput` so digits + accents work. `ItemView` uses it (`OnTextInput` → `MoveTo`). The folder tree / Start menu / folder picker / taskbar / desktop grid should **reuse it**, not re-derive a `Key`-decoding copy (that path had a dead digit map, backwards wrap, and no cycling — bevel-p3v3).
- **Accessibility has two non-obvious seams.** (1) The Explorer file list exposes screen-reader semantics through `Components/ItemViewAutomation.cs`: each row's visual root is wrapped in a layout-transparent `ItemRow : Decorator` carrying an `ItemRowAutomationPeer` (ListItem + `ISelectionItemProvider` mirroring `ItemViewModel.IsSelected`), and the container is `ItemList : ItemsControl` (List role; `StyleKeyOverride => typeof(ItemsControl)` so it still finds the ControlTheme and realizes rows). Sharp edge: **don't rewrite the `ItemsControl` to a `ListBox`** for a11y — it would break the marquee, rect-based `Hit()`, and the `VirtualizingWrapPanel`; the Decorator+peer is deliberately inert to layout (`Hit()`/`Marquee()` measure the `ContentPresenter`). (2) The taskbar uses **menu-scoped key focus** (bevel-vk4n): it is non-key at idle and `TaskbarWindow.SetKeyFocusAllowed(bool)` flips `canBecomeKeyWindow` true **only** while an owned menu/popover is open (depth-counted `EnterMenuScope`/`ExitMenuScope` in `TaskbarView`), capturing + re-activating the prior app so focus returns. **Never restore the unconditional `SetCanBecomeKeyWindow(false)`-only path or set it `true` at idle** — the bar must not steal focus. From-idle `Ctrl/Option+Esc` rides a native global keyDown monitor that needs the Input Monitoring TCC grant (`taskbar-idle-hotkey-needs-input-monitoring`).
- Flat/Whistler is **spec-only** (`docs/design/flat/`) — no renderer yet; don't look for one.
- Landing page `site/index.html` mirrors real UI (screenshots harvested from `Render*` tests). Refresh it when user-facing features land — see CLAUDE.md → Codemap & Landing Site.
- Verification gap: the `check-codemaps` script referenced by `FORMAT.md` isn't wired in this repo yet — check path references by hand.
