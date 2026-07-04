# Engineering Plan

## Summary

This chapter turns the architecture (01-architecture.md), platform integration specs (02-macos-platform.md, 03-windows-platform.md, 04-linux-platform.md), and product surface (07-shell-ux.md, 06-file-manager.md, 05-theming.md, 08-os-interop.md) into an executable plan for a 1–2 person team. The plan is **risk-ordered**: the earliest milestones require zero TCC permissions and produce a demoable, screenshot-friendly artifact; the scariest platform work (AX window management, systray pixel mirroring, Apple Events compatibility) is isolated into its own milestones so a failure there never blocks the demoable core. macOS v1 is estimated at **65–91 engineer-weeks** across M0–M5 plus a dedicated test-infrastructure work item; Windows adds ~12–18 ew and Linux ~32–48 ew. Testing leans on Avalonia headless UI tests, PAL contract tests against fake platforms, and pixel-golden regression for themes; CI runs on GitHub Actions with self-hosted Apple-silicon macOS runners (funded from project start — this resolves 01-architecture.md open question 6 in the affirmative) and full signing/notarization in the release lane. A standing rapid-response process handles the inevitable "macOS point release broke tray mirroring" event with a target of a mitigating release within 72 hours.

Conventions used below:

- **ew** = engineer-week (one engineer, one week of focused work). Ranges are P50–P80.
- Requirements are numbered `ENG-nnn` and are normative ("MUST"/"SHOULD" per RFC 2119).
- Milestone acceptance criteria are testable statements; a milestone is *done* when every criterion passes on the reference environment listed for it.

---

## 1. Team model and cadence

- Team: 1–2 engineers. All estimates are single-engineer serial effort; with 2 engineers, milestones parallelize as noted (UI track vs. platform track), giving roughly a 1.6× wall-clock speedup, not 2×.
- **ENG-001** — The repo MUST be a single monorepo (`bevel/`) containing the C# solution, native helper sources (Swift/ObjC, C), theme asset pipeline, and packaging scripts. Rationale: PAL interface changes and helper protocol changes must land atomically. Rejected: separate helper repos (version-skew hell for an IPC protocol that will churn weekly early on).
- **ENG-002** — Trunk-based development: short-lived branches, PRs to `main`, `main` always releasable to the *previous* milestone's quality bar. Release branches (`release/v1.x`) cut only from M5 onward.
- **ENG-003** — Every PR that touches a `Pal.*` interface MUST update the fake-platform implementation and contract tests in the same PR (see §4.2).
- **ENG-004** — Milestone scope items that implement a decision made in another chapter MUST cite that chapter's decision ID (e.g., ARCH-06, UPD-01, D-W7), and the PR review checklist includes "cited decision IDs still say what this PR assumes". Rationale: this chapter drifted from 01/03 on IPC, updater, and Windows packaging before this rule existed; a citation makes the drift greppable. A CI doc-lint (regex: milestone bullets naming a mechanism owned elsewhere must contain a decision-ID token) SHOULD back this up.
- Cadence: weekly internal build; public builds start at M1 exit ("tech preview"), because M1 is deliberately safe to distribute (no permissions, no shell replacement). The product name **Bevel** is final (owner decision 2026-07-04), so the M1 preview ships publicly under it. Identifiers (bundle id `pl.ikari.bevel`, scheme `bevel://`, signing identity) are frozen at M0 exit.

## 2. Milestone plan — macOS v1

Ordering rationale: M1 front-loads *product* risk (does the themed shell look and feel right?) with zero *platform* risk. M2–M4 each retire exactly one hard platform bet, in decreasing order of "kills the product if impossible": work-area/window management (M2), tray mirroring (M3), Finder scripting compat (M4). Packaging (M5) is last because signing/notarization requirements are well understood and low-variance.

### M0 — Bootstrap (4–6 ew)

Foundation only; no user-visible product. (Re-estimated from 3–4 ew: the gRPC/grpc-swift stack per ARCH-06 is more setup work than a hand-rolled JSON-RPC skeleton, and the identifier freeze below adds a decision gate.)

Scope:
- Repo layout per 01-architecture.md: `src/Bevel.App`, `src/Bevel.Pal.Abstractions`, `src/Bevel.Pal.MacOS`, `src/Bevel.Pal.Fake`, `native/macos/BevelHelper` (Swift), `proto/`, `themes/`, `tests/`.
- .NET 9 + Avalonia 11.x solution builds and runs an empty owner-drawn window on macOS (arm64 + x64).
- Classic.Avalonia vendored as a git submodule or source-forked (decision: **fork into `themes/classic-avalonia/`**, MIT license preserved; rationale: we will diverge heavily for pixel fidelity and asset replacement per 05-theming.md; rejected: NuGet dependency — too slow an upstreaming loop for a core dependency).
- Helper process skeleton: Swift executable, launchd-managed, **gRPC over Unix domain socket per 01-architecture.md ARCH-06** (grpc-swift server in the helper, `Grpc.Net.Client` over UDS in C#, protobuf contracts in `proto/`), with handshake, protocol-version negotiation, heartbeat, and crash-restart supervision from the C# side. The shared-memory frame ring (01 §4.4) is **not** M0 scope — nothing needs it before tray mirroring; it lands in M3.
- **Identifier freeze (M0 exit gate)**: the frozen identifiers are bundle id `pl.ikari.bevel`, URL scheme `bevel://`, and the macOS Developer ID signing identity (`Developer ID Application: Cezar Pokorski (4TP7TPH2K6)`, individual account — no D-U-N-S/org); all are decided and frozen before anything is shared outside the team. Rationale: TCC grants are keyed to bundle id + signing identity (02 Req 7.4/11.6), 08 Risk 7 requires the scheme frozen before any public build, and 01 ARCH-01 wants the rename before v0.2. The product name **Bevel** is now settled (owner decision 2026-07-04), so name and identifiers land together.
- CI skeleton (see §5): build + unit tests on `macos-15` (arm64) and `ubuntu-24.04` (compile-only for PAL abstractions).

Acceptance criteria:
1. `dotnet build -c Release` produces a runnable app bundle on macOS 14/15.
2. Helper crash (kill -9) is detected within 2 s and helper is restarted; C# side re-establishes the gRPC session without app restart; a server-streaming echo RPC survives the restart cycle.
3. CI green on PR within 15 min.
4. Bundle id (`pl.ikari.bevel`), URL scheme (`bevel://`), and signing identity are recorded as frozen in 01-architecture.md; all project templates/plists use them (no `pl.ikari.bevel` placeholders remain).

### M-INFRA — Test infrastructure (3–5 ew, parallel to M1)

Previously implicit and unowned; made explicit because M2–M4 acceptance criteria depend on it and a 1–2 person team will otherwise discover it mid-M2. This also answers 01-architecture.md open question 6: **yes, self-hosted macOS CI is funded at project start.**

Scope:
- Self-hosted Apple-silicon Mac mini runner(s) + Tart VM matrix wiring into `nightly.yml` (§4.4, §5).
- TCC-provisioned VM base images per §4.3 (SIP-disabled images with scripted `TCC.db` inserts), regenerated per macOS version, plus the TCC-schema canary test.
- `WindowZoo.app` / `TrayZoo.app` rig apps and the AppleScript corpus runner skeleton (§4.3) — built here, consumed by M2–M4.

Acceptance criteria:
1. Nightly matrix runs the fake-PAL suite plus a trivial real-helper rig test on macOS 14 and 15 Tart images, unattended.
2. A rig test that requires Accessibility and one that requires Screen Recording both pass on the provisioned images with no manual prompt-clicking.

### M1 — Desktop + themed file manager, zero permissions (12–16 ew)

The demo milestone. **ENG-010** — M1 MUST require no TCC permissions beyond default file access (no Accessibility, no Screen Recording, no Full Disk Access) and MUST NOT replace or fight the real Finder/Dock. It runs as a normal `.app`.

M1 is an explicit **MVP cut of 06-file-manager.md**, not all of it — the full chapter is 20+ ew on its own and would sink the schedule of the milestone that gates the tech preview. The cut is a decision, not an accident; deferred items are named below.

Scope:
- Desktop window: full-screen borderless Avalonia window at desktop level (`kCGDesktopWindowLevel + 1` via NSWindow level, `collectionBehavior = .canJoinAllSpaces | .stationary`), wallpaper rendering, icon grid bound to `~/Desktop` via FileSystemWatcher-backed PAL VFS (06-file-manager.md).
- File manager, M1-MVP providers only: local `file`, `computer` virtual root, and `trash` — ZipProvider (FM-120) and network mounts deferred (see backlog below). Tree pane + list/details/icons views, classic editable address bar (flat ComboBox per **FM-030 — no breadcrumbs**; an earlier draft of this bullet said "breadcrumb/edit toggle", which FM-030 explicitly forbids), navigation history, rename/copy/move/delete with undo, context menus, drag-and-drop within the app, keyboard model per 06-file-manager.md.
- Custom virtualizing wrap panel (FM-180, 06 risk 1) including free placement for the desktop grid — stays in M1 because desktop icon views need it, built against a synthetic large-directory harness. The M1 perf bar is **10k entries** (see acceptance 3); the 100k/250 ms target from 06 §perf is validated in M5, not here.
- Win2000 theme complete for every control the file manager and desktop use (05-theming.md): classic title bars (owner-drawn since we're borderless), 3D borders, menu bar, toolbar, statusbar, scrollbars, listview/treeview. Theme switching infrastructure live with Win2000 + a stub second theme to prove swappability.
- Icon/asset pipeline: recreated icon set (16/32/48 px, classic palette) for the **controls and file types M1 actually shows** (~60–80 icons, not the full ~150-icon set) with build-time validation that **zero** Microsoft-original bitmaps are present (hash denylist of known MS assets; ENG-011: release build MUST fail if a denylisted hash appears).
- macOS niceties that need no permissions: open-with via `NSWorkspace.shared.open`, Quick Look preview via `QLPreviewPanel` (helper-hosted), Trash via `NSFileManager.trashItem`.

**M1-deferred backlog** (each item lands in the named later milestone; none may silently re-enter M1):
- → M5: ZipProvider read-only browsing (FM-120); Send To menu (FM-083); properties-dialog disk pie chart; remainder of the recreated icon set; 100k-entry perf targets (06 §perf) validated against the panel built here.
- → v1.x: search (06 §7, incl. Spotlight merge); thumbnails view + helper-hosted QuickLook thumbnail pipeline (06 §6.3); Explorer Bar panes beyond Folders.

Acceptance criteria:
1. Fresh macOS 15 VM, no permission prompts at any point: app launches to a Win2000 desktop with wallpaper and `~/Desktop` icons.
2. File manager passes the headless UI test suite (≥ 90% of **M1-scope** interactions from 06-file-manager.md covered) and the pixel-golden suite for Win2000 at 100% and 200% scale.
3. Copy of a 2 GB folder tree shows progress dialog, is cancellable, and survives 10k-file directories with < 100 ms UI stalls (measured by dispatcher-latency probe).
4. A 3-minute demo script (open drives, browse, rename, drag to folder, change wallpaper) runs without crash 20/20 times.
5. Distributable zip of the app runs on a colleague's machine with only the standard Gatekeeper prompt (ad-hoc/developer-ID signed, not yet notarized).

Parallelization: engineer A on file manager logic + VFS, engineer B on theme + desktop rendering.

### M2 — Taskbar + AX window management (10–14 ew)

Retires the "can we be a taskbar on macOS at all" risk. First milestone needing **Accessibility** permission.

Scope:
- Taskbar window: bottom edge, always-on-top-of-desktop but below normal windows during overlap-mitigation mode; start button + start menu (app launcher from `/Applications` via `NSMetadataQuery`/LaunchServices), running-window list, clock, layout per 07-shell-ux.md.
- AX integration in the Swift helper: window enumeration (`CGWindowListCopyWindowInfo` for z-order/geometry + `AXUIElement` per-app for titles, minimize/raise/close actions), app activation (`NSRunningApplication.activate`), window focus/minimize/restore from taskbar clicks, live title updates via `AXObserver` notifications (`kAXTitleChangedNotification`, `kAXWindowCreatedNotification`, `kAXUIElementDestroyedNotification`).
- Work-area strategy per 02-macos-platform.md: Dock-as-space-reservation shim (position Dock on same edge, auto-hide our bar in sync) **or** AX-based overlap mitigation (nudge windows out of the bar's strip). Decision recorded there; M2 implements the primary strategy plus the fallback behind a setting.
- TCC onboarding flow: first-run wizard that deep-links `x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility`, detects grant via `AXIsProcessTrusted()` polling, and degrades gracefully (taskbar shows launcher + clock but no window list) when denied. **ENG-020** — Every permission-gated feature MUST have a defined degraded mode; the app MUST never dead-end on a denied permission.
- Minimal settings window (pulled into M2 per earlier review): permissions status/onboarding surface plus basic taskbar and startup toggles; the full Settings app (theme picker, permissions dashboard) still lands in M5.
- Multi-monitor: one taskbar on primary display for v1 (per-display bars deferred; recorded in 07-shell-ux.md).

Acceptance criteria:
1. With Accessibility granted: taskbar lists windows of ≥ 95% of top-30 Mac apps (test matrix incl. Safari, Chrome, Finder, Terminal, iTerm2, VS Code, Slack, Electron apps); click focuses, second click minimizes; titles update within 500 ms.
2. With Accessibility denied: launcher/start menu/clock fully functional; a single non-nagging affordance explains what's missing.
3. Work-area: dragging a Finder/TextEdit window "behind" the bar triggers the specced mitigation; maximized windows do not underlap the bar on the primary strategy.
4. Helper survives an AX-heavy stress test (open/close 200 windows scripted) without leaking `AXObserver`s (RSS growth < 10 MB over the run).
5. Integration rig (see §4.3) green on macOS 15 (locally or on the M-INFRA runners, whichever exists first). If the M-INFRA runner fleet has slipped, the full 14/15/current-beta matrix becomes an M3 exit criterion instead of blocking M2.

### M3 — Systray mirroring (10–15 ew)

The hardest single feature on macOS; isolated so slippage doesn't block M4/M5. Needs **Screen Recording** permission. (Re-estimated from 9–13 ew: the shared-memory frame ring was previously in no milestone's scope; it is 1–2 ew and lives here because nothing earlier needs it.)

Scope (per 02-macos-platform.md, Ice-derived technique):
- **Shared-memory frame ring per 01-architecture.md §4.4** (1–2 ew): memory-mapped ring of BGRA frame slots with per-slot seqlock, Swift writer + C# reader, gRPC control channel carrying `FrameRingInfo`/`FrameSignal`, torn-frame rejection tests. This is the transport for all captured tray pixels below; gRPC itself never carries frame data.
- Helper: enumerate menu-bar status items via `CGWindowListCopyWindowInfo` filtered to window layer `kCGStatusWindowLevel` / owner `SystemUIServer`-adjacent status windows; per-item pixel capture via **ScreenCaptureKit** (`SCContentFilter` on the status-item window, `SCStreamConfiguration` at 2×, event-driven refresh with a low-frequency fallback timer).
- Click forwarding: reposition/expose strategy — synthesize a click at the item's real menu-bar location via `CGEvent(mouseEventSource:)` posted with `CGEventTapLocation.cghidEventTap`, with menu-bar auto-unhide choreography when the native bar is hidden.
- Native menu bar auto-hide management (`defaults write NSGlobalDomain _HIHideMenuBar -bool true` equivalent via AppKit presentation options for our own management scope; system-wide hiding requires the user toggle in System Settings — the onboarding flow automates what's possible and instructs the rest).
- Tray UI in taskbar: icon strip + overflow flyout, hover tooltips (best-effort from AX title of the status item's owning app), left/right-click forwarding.
- **ENG-030** — Tray mirroring MUST be independently disableable at runtime via a **local** kill switch (a setting/config toggle; see §7) without restarting the shell. (v1 has no remote config; a remote-flag driver is a post-v1/v2 addition.)

Acceptance criteria:
1. Reference tray set (Dropbox, 1Password, Docker Desktop, Tailscale, Bartender-style test app we ship) renders at correct scale with < 1 s icon-update latency.
2. Left-click and right-click forwarding opens the correct native menu for 9/10 reference items; failures degrade to "reveal native menu bar" fallback.
3. Screen Recording denied → tray area shows a "connect tray" affordance; rest of shell unaffected (ENG-020).
4. 24-hour soak: capture pipeline ≤ 3% of one core average, no unbounded memory growth.
5. Kill switch verified: disabling tears down SCStream and event taps within 1 s.

### M4 — Apple Events tier 1 (7–10 ew)

Scope (per 08-os-interop.md, tier 1 only — own scripting suite mirroring Finder terminology):
- `NSAppleEventManager`-registered handlers in the app (via helper or direct AppKit interop) implementing the Finder-compatible core: `open`, `reveal`, `get selection`, `make new folder`, `delete`, `duplicate`, `exists`, window/desktop object model subset; SDEF (`Bevel.sdef`) mirroring Finder terminology for the implemented subset.
- `NSWorkspace` entry points: respond to `NSWorkspace.shared.activateFileViewerSelecting(_:)` / `selectFile(_:inFileViewerRootedAtPath:)` when we are the registered file-viewer (registration mechanics in 02-macos-platform.md; the loginwindow `Finder` defaults key experiment is explicitly **out of M4** and lives in the research track).
- Compatibility harness: a corpus of ~40 real-world `tell application "Finder"` snippets (collected from Alfred workflows, Hammerspoon configs, app "Reveal in Finder" implementations) executed against the shell with expected outcomes.

Acceptance criteria:
1. ≥ 30/40 corpus scripts behave correctly when retargeted at the shell's bundle id (tier-1 goal; bundle-id shadowing is a separate research spike, timeboxed 1 ew, outcome recorded in 08-os-interop.md).
2. "Reveal in Finder" from Safari downloads list and VS Code opens a shell file-manager window with the item selected, when the shell is set as handler.
3. `osascript -e 'tell app "Bevel" to reveal POSIX file "/tmp/x"'` round-trips in < 300 ms.

### M5 — Polish, packaging, v1 release (8–11 ew)

(Re-scoped per the staged theme-delivery decision (2026-07-04): Luna (XP) and Windows 11 theme completion move to post-v1 (v1.1/v1.2), so M5 ships only the v1 **Win2000** theme + hardened theming engine while still absorbing the M1-deferred file-manager items — ZipProvider, Send To, disk pie chart, remaining icons, 100k perf validation — netting **8–11 ew**. The difference vs. the prior 10–13 ew estimate is banked as reserve to hold the published 65–91 ew macOS v1 envelope.)

Scope:
- **Win2000** theme completion for v1 across every control (theme infra already proven in M1; this is asset + template volume work) plus theming-engine hardening to the pixel-golden bar. **Luna (XP) ships in v1.1 and Windows 11 in v1.2** per the staged theme-delivery decision — neither is v1 scope.
- M1-deferred file-manager items scheduled here: ZipProvider (FM-120), Send To (FM-083), properties disk pie chart, remainder of the recreated icon set, and validation of the 100k-entry perf targets from 06 §perf against the M1 virtualizing panel.
- Settings app (theme picker, taskbar options, startup behavior, permissions dashboard) — extends the minimal M2 settings window.
- Managed-config hook (CFG-03) shipped in v1 (owner decision): a read-only managed/policy configuration source (e.g., MDM-delivered defaults) feeds the settings + flag system; small add, kept in v1 rather than deferred.
- Login-item installation (`SMAppService.mainApp.register()`), Dock/menu-bar coexistence presets ("full shell" vs "companion" modes per 02-macos-platform.md).
- Local crash logging + export wired end-to-end (§7, offline in v1) and privacy policy (no first-run telemetry consent — nothing leaves the machine).
- Signing, notarization, DMG, Homebrew cask, **Velopack updates per 01-architecture.md UPD-01** (§6).
- Performance pass: cold start < 2 s to interactive desktop on M1-class hardware; memory < 350 MB steady-state with tray mirroring on.
- Docs: user guide, known-issues, uninstaller (**ENG-040** — the app MUST ship an uninstaller/`--uninstall` flow that removes login items, launchd plists, and restores Dock/menu-bar settings; a shell replacement that can't cleanly leave is a support disaster).

Acceptance criteria:
1. Notarized DMG installs and passes Gatekeeper on a fresh macOS 14 VM with no dev tools.
2. The Win2000 theme passes the pixel-golden suite at 100/150/200% scale; theme switch at runtime < 1 s without restart (Luna/Win11 themes and their goldens ship in v1.1/v1.2).
3. Update from (n−1) build via Velopack works, including helper re-registration, launchd job re-registration (ENG-070), and TCC-grant survival (bundle id + signing identity unchanged, verified by the post-update permission self-check per 02 Req 11.6).
4. Crash-free sessions ≥ 99% across a 2-week beta with ≥ 50 external users.
5. Uninstall leaves no login items, no launchd jobs, no TCC-visible residue beyond the standard entries.

### macOS v1 estimate roll-up

| Milestone | Scope headline | P50 (ew) | P80 (ew) | Permissions introduced |
|---|---|---|---|---|
| M0 | Bootstrap, PAL, gRPC helper IPC (ARCH-06), identifier freeze, CI | 4 | 6 | none |
| M-INFRA | Runners, Tart matrix, TCC-provisioned images, rig apps | 3 | 5 | n/a (CI) |
| M1 | Desktop + themed file manager (MVP cut) | 12 | 16 | none |
| M2 | Taskbar + AX window mgmt | 10 | 14 | Accessibility |
| M3 | Systray mirroring + frame ring | 10 | 15 | Screen Recording |
| M4 | Apple Events tier 1 | 7 | 10 | Automation (per-target) |
| M5 | Polish + packaging + Win2000 theme completion + M1 deferrals | 8 | 11 | none |
| **Total** | **macOS v1** | **54** | **77** | |
| + reserve | Integration debt, macOS beta churn, banked M5 theme re-scope | 11 | 14 | |
| **Plan of record** | | **65** | **91** | |

With 2 engineers: ~10–13 calendar months to macOS v1 (M-INFRA parallelizes fully with M1).

### Windows track (after mac v1) — 12–18 ew

Windows is the easiest platform (native shell-replacement support) and validates PAL portability.

- **W1 (3–5 ew)** — Port desktop + file manager: `Bevel.Pal.Windows` (Win32 via CsWin32 source generators), IFileOperation-backed file ops, desktop window parented under `Progman`/`WorkerW`. Acceptance: M1 criteria on Windows 11 22H2+ (Windows 10 dropped — see support policy).
- **W2 (5–7 ew)** — Taskbar + tray takeover: `SHAppBarMessage` (`ABM_NEW`/`ABM_SETPOS`) work-area reservation; tray via creating our own `Shell_TrayWnd`-class window and broadcasting `TaskbarCreated` (`RegisterWindowMessage("TaskbarCreated")`) so apps re-register `Shell_NotifyIcon` icons with us; window list via `EVENT_OBJECT_*` WinEvents + `EnumWindows`. **Companion mode (alongside Explorer) is the default install on Windows; full shell replacement is opt-in.** Acceptance: companion/coexistence mode alongside Explorer AND full mode.
- **W3 (4–6 ew)** — Full shell replacement (opt-in; companion is the default per W2) + packaging: per-user `HKCU\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\Shell` (with documented recovery: Ctrl+Alt+Del → Task Manager → run `explorer.exe`; installer writes a restore script), **WiX MSI per 03-windows-platform.md D-W7** with a winget manifest pointing at the MSI (MSIX rejected there — its container cannot write the Winlogon key), code signing with a **separate OV/EV Authenticode cert obtained at Windows-track start** per 03 REQ-W22 (distinct from the macOS individual Developer ID; Azure Trusted Signing eligibility reconciled in 03). Acceptance: log in to our shell, work a day, revert cleanly.

### Linux track — 32–48 ew

Position (recorded here, detailed in 04-linux-platform.md): **X11 and Wayland are co-equal first-class backends at launch (owner directive 2026-07-04).** X11 lands in L1; the funded `Bevel.Pal.Linux.Wayland` backend (wlr-layer-shell for the taskbar/desktop layer + `zwlr_foreign_toplevel` for the window list, targeting wlroots compositors and KDE — both implement `zwlr_layer_shell_v1`) lands in L2. **GNOME/Mutter Wayland is explicitly an unsupported host** (GNOME Shell exposes no layer-shell; being the compositor is out of scope for v1 — rejected as a product-defining detour). Wayland-first-class adds ~8–14 ew over the prior X11-lead estimate; the exact split is firmed at track kickoff. 

- **L1 (8–11 ew)** — X11: desktop (`_NET_WM_WINDOW_TYPE_DESKTOP`), taskbar (`_NET_WM_WINDOW_TYPE_DOCK` + `_NET_WM_STRUT_PARTIAL`), window list via `_NET_CLIENT_LIST` root-property tracking, file manager port (GIO/xdg-trash semantics).
- **L2 (15–24 ew)** — First-class Wayland backend (`Bevel.Pal.Linux.Wayland`): wlr-layer-shell taskbar/desktop, `zwlr_foreign_toplevel` window list, tray via StatusNotifierItem: watcher on DBus (`org.kde.StatusNotifierWatcher`), items via `org.kde.StatusNotifierItem` + `com.canonical.dbusmenu`; XEmbed fallback tray on X11. (Grown from 7–10 ew: Wayland is now co-equal first-class, not an X11 follow-on.)
- **L3 (9–13 ew)** — Session integration (`.desktop` X-session / wayland-session files), packaging: `.deb`, `.rpm`, AUR; **no Flatpak for the shell itself** (sandbox is incompatible with session ownership; rejected), Flatpak considered later for the file manager alone.

## 3. Research track (continuous, timeboxed)

Spikes that must not live on the critical path. Each is timeboxed and produces a written outcome in the relevant chapter:

| Spike | Timebox | Feeds |
|---|---|---|
| Bundle-id shadowing of `com.apple.finder` (SIP/LaunchServices behavior) | 1 ew | 08-os-interop.md |
| loginwindow `Finder` defaults key (`defaults write com.apple.loginwindow Finder <path>`) viability on current macOS — **pulled onto the plan into M1** (owner directive 2026-07-04): v1's only user is the author, so the replacement path is dogfooded early on the author's own machine, de-risking the north-star full-replace goal ahead of any public toggle | 0.5 ew (scheduled in M1) | 02-macos-platform.md, 00 §7.1 #5 |
| Headless real-Finder mirroring (tier 3) | 1 ew | 08-os-interop.md |
| macOS 26/next-beta ScreenCaptureKit changes | 0.5 ew per beta cycle | §7 rapid response |

## 4. Testing strategy

Test pyramid: unit (xunit) → headless UI → PAL contract → per-platform integration rig → VM matrix → manual demo scripts.

### 4.1 Avalonia headless UI tests

- **ENG-050** — All view-model logic and all UI interaction flows specced in 06/07 MUST be covered by `Avalonia.Headless.XUnit` tests (`[AvaloniaFact]`/`[AvaloniaTheory]`) running the real control tree with the real theme, no display server. These run on Linux CI runners (cheap, fast) even for mac-targeted UI.
- Headless keyboard/mouse simulation (`Window.KeyPressQwerty`, `MouseDown`/`MouseUp` helpers) drives flows like rename-in-place, address-bar editing (FM-030 ComboBox), marquee selection.

### 4.2 PAL contract tests

Every PAL interface ships a reusable contract-test suite executed against **every** implementation — fake and real:

```csharp
// tests/Bevel.Pal.ContractTests/WindowSystemContract.cs
public abstract class WindowSystemContract
{
    protected abstract IPalWindowSystem CreateSut();   // FakeWindowSystem | MacWindowSystem(rig)

    [Fact] public async Task WindowOpened_RaisesEvent_WithStableId() { /* ... */ }
    [Fact] public async Task Focus_ThenEnumerate_ReportsFocusedFirstInZOrder() { /* ... */ }
    [Fact] public async Task Ids_SurviveTitleChange() { /* ... */ }
}
public sealed class FakeWindowSystemContract : WindowSystemContract { /* runs everywhere */ }
public sealed class MacWindowSystemContract : WindowSystemContract { /* runs on mac rig only */ }
```

- `Bevel.Pal.Fake` is a first-class product: deterministic, scriptable (spawn fake windows/tray items/file systems), used by headless UI tests so the *whole shell* runs end-to-end on a Linux CI box with zero OS integration.
- **ENG-051** — A PAL interface without a contract suite MUST NOT be merged.

### 4.3 Per-platform integration rigs

- **macOS rig**: a test-host app suite (`tests/rigs/macos/`) containing (a) `WindowZoo.app` — scripted spawner of NSWindows with controllable titles/levels, (b) `TrayZoo.app` — spawns NSStatusItems with animated icons and menus, (c) AppleScript corpus runner. Rig tests exercise the real Swift helper against these zoo apps; pass/fail asserted from C# over the normal PAL. Runs on self-hosted mac runners and the local VM matrix; built and budgeted in M-INFRA.
- **macOS rig TCC provisioning** (corrected — an earlier draft said "MDM profile or `tccutil`-prepared images", which does not work: `tccutil` can only *reset* permissions, never grant, and MDM PPPC profiles cannot grant Screen Recording at all — Apple restricts PPPC's `ScreenCapture` service to deny/standard-user-approval, and Screen Recording is exactly what the M3 rig needs):
  - VM base images are **SIP-disabled Tart images** provisioned by scripted SQLite inserts into the user and system `TCC.db` (Accessibility, Screen Recording, Full Disk Access, Apple Events), with `csreq` code-requirement blobs generated from the frozen signing identity (M0 exit gate). Images are regenerated per macOS version because the TCC schema churns across releases.
  - MDM/PPPC profiles are used only for what PPPC can actually grant (Accessibility, Full Disk Access, Apple Events) on any SIP-*enabled* images we keep for release-realism checks; those images cannot run Screen Recording rig tests.
  - **ENG-054** — The nightly matrix MUST include a TCC-schema canary: on every new macOS (beta) image, a probe validates that the scripted `TCC.db` inserts still produce effective grants (helper's own permission self-check API) and fails loudly before rig results are trusted. Rationale: silent schema drift would make the M3 acceptance criteria and the §7 playbook's beta lane meaningless.
  - Dependency note: the M3 acceptance criteria and the 72 h playbook (§7) assume this rig runs unattended; M-INFRA acceptance 2 verifies that before M2 relies on it.
- **Windows rig**: Win32 zoo (tray via `Shell_NotifyIcon`, windows via CreateWindow) + Explorer-takeover/restore harness.
- **Linux rig**: Xvfb + openbox for X11 tests; `sway --headless` (wlroots) for layer-shell tests; `libdbus` SNI zoo.

### 4.4 VM matrix

| Platform | Versions in matrix | Host |
|---|---|---|
| macOS | 14 (Sonoma), 15 (Sequoia), current release, current beta | Tart VMs on self-hosted Apple-silicon Mac mini |
| Windows | 11 22H2 / 23H2 / 24H2 (Windows 10 dropped) | GitHub-hosted + local Hyper-V |
| Linux | Ubuntu LTS (X11+Wayland), Fedora current (Wayland), Arch (rolling) | GitHub-hosted + QEMU |

**ENG-052** — The macOS *beta* channel MUST be in the nightly matrix from M3 onward; a red beta lane pages the rapid-response process (§7), not the PR author.

### 4.5 Pixel regression for themes

- Golden-image tests render key surfaces (title bar states, button states, menu open, listview details header, start menu, taskbar) via headless Skia render-to-bitmap at 100%/150%/200% scale, per theme.
- **ENG-053** — Win2000 goldens use **zero-tolerance** byte comparison (the theme's whole point is pixel exactness); XP/Win11 goldens allow per-channel Δ ≤ 1 to absorb gradient dithering. Goldens stored via Git LFS; an approved-update flow (`dotnet run --project tools/GoldenApprove`) regenerates with a reviewable image diff artifact in the PR.

### 4.6 Manual/exploratory

- Scripted demo runs (M1 criterion 4 style) before every public build.
- Dogfooding requirement from M2: at least one engineer runs the shell as their daily driver in "companion" mode; from M5, in full mode.

## 5. CI/CD — GitHub Actions

Pipelines (`.github/workflows/`):

| Workflow | Trigger | Runners | Contents |
|---|---|---|---|
| `pr.yml` | PR | `ubuntu-24.04`, `macos-15` (arm64) | build, unit, headless UI, fake-PAL contract, pixel goldens, asset-hash denylist check (ENG-011) |
| `nightly.yml` | cron | self-hosted mac (Tart matrix), `windows-2025` | integration rigs, VM matrix, soak subset, beta-macOS lane |
| `release.yml` | tag `v*` | `macos-15`, `windows-2025`, `ubuntu-24.04` | full test pass → sign → notarize → package → draft GitHub Release + update feeds |

macOS signing/notarization in `release.yml`:
- Developer ID Application + Developer ID Installer certs for the individual identity `Developer ID Application: Cezar Pokorski (4TP7TPH2K6)` (no D-U-N-S/org) stored as base64 `.p12` in GitHub Environments secrets (`SIGNING_CERT_P12`, `SIGNING_CERT_PASSWORD`), imported into an ephemeral keychain per run; notarization uses the `bevel` notarytool keychain profile.
- `codesign --deep` is NOT used (**ENG-060**: each nested component — app, Swift helper, Velopack update binaries — is signed explicitly, innermost-first, with hardened runtime `--options runtime` and per-target entitlements; `--deep` breaks helper entitlements).
- Notarization: `xcrun notarytool submit Bevel.dmg --keychain-profile bevel --wait` then `xcrun stapler staple`. App Store Connect API key (`AC_API_KEY_*` secrets) rather than Apple-ID password auth.
- **ENG-061** — Release artifacts MUST be reproducible from a tag: version injected from the tag, no runner-local state; SBOM (CycloneDX via `dotnet CycloneDX`) attached to every release.

Self-hosted runner policy: self-hosted mac runners run **only** nightly/release workflows from `main`/tags, never fork PRs (secret-exfiltration hygiene).

## 6. Distribution

The updater is **Velopack on all platforms, per 01-architecture.md UPD-01** (one framework, delta updates, `stable`/`beta` channels, staged rollouts; Sparkle was rejected there as mac-only with an ObjC integration burden). An earlier draft of this section specced Sparkle 2 on macOS and MSIX-via-winget on Windows; both contradicted the owning chapters (UPD-01, D-W7) and are withdrawn. Editorial follow-ups in sibling chapters: 02 Req 11.6's "Sparkle-style or custom updater" should be amended to name Velopack, and 03 open question 5 ("settle the updater in 09") is hereby settled and should be deleted there.

| Platform | Primary | Secondary | Updates |
|---|---|---|---|
| macOS | Notarized DMG (drag-to-Applications) | Homebrew cask (`brew install --cask bevel`) | Velopack (UPD-01): signed release feed, `stable`/`beta` channels, staged rollouts; helper migration hooks + launchd re-registration (ENG-070); shell-safe swap flow per 01 UPD-03 (never mid-session; helper supervises the swap, SAFE-01 escape hatch) |
| Windows | **WiX MSI per 03 D-W7** (per-user, HKCU strategy); winget manifest points at the MSI | Signed `.exe` bootstrapper mirror of the same MSI | Velopack (UPD-01) for in-app updates; `winget upgrade` follows the MSI feed. MSIX is rejected outright per D-W7 (container cannot write the Winlogon key) — there is no MSIX companion-mode SKU |
| Linux | `.deb` + `.rpm` from CI, apt/rpm repo hosted on Cloudflare R2 | AUR package | distro package manager; in-app update check only notifies |

- **ENG-070** — The macOS updater MUST be able to update the Swift helper and re-register launchd jobs atomically; a version-skewed app/helper pair MUST refuse to run in mixed mode (protocol version gate from M0's handshake).
- **ENG-071** — Homebrew cask ships at M5 GA, not before (cask review requires stable versioned URLs).

## 7. Crash reporting and rapid response

- Crash/error telemetry: **fully offline in v1** (owner decision 2026-07-04). Crashes and errors are written to **local crash logs** (managed side + the Swift helper, tagged `component:helper`) with an on-demand **export** flow (bundle logs for the user to attach to a report); **no network reporting, no backend, no consent UI** — nothing leaves the machine. The reporting layer is structured to swap in a network sink (e.g., Sentry via sentry-dotnet / sentry-cocoa) **post-v1** without changing call sites. Rejected: shipping any network crash uploader in v1.
- **ENG-080** — Helper crashes MUST be captured even when the main app is healthy (the supervisor writes the helper's crash envelope to the local crash log on restart; the export flow includes it).
- Kill switches / config: **v1 has no remote anything** (owner decision 2026-07-04 — *v1's user base is the author*, so there is nobody to push a flag to). ENG-030's runtime kill switch is driven **locally** — a setting/config value the author toggles on their own machine; a subsystem broken by a macOS point release (e.g. tray mirroring) is disabled by the author directly, not over the wire. The flags layer is *structured* so a signed, inbound-only config manifest (per-feature, per-OS-build gates, riding the Velopack feed, no user data) can be added **when v1 opens to real users (v2)** — but nothing fetches it in v1. The only network touch in v1 is the Velopack update check itself. Rejected for v1: remote flags, remote-config SaaS.

### The "macOS update broke systray mirroring" playbook

Trigger: red nightly beta lane (ENG-052), a cluster of user-submitted crash-log exports tagged `component:helper` on a new `ProductBuildVersion`, or user reports. (v1 telemetry is offline, so detection is nightly-matrix- and user-report-driven, not server-side.)

1. **T+0 h** — Acknowledge: pin a GitHub issue; flags manifest updated to disable the affected feature on the affected OS build (users degrade gracefully per ENG-020 within ≤ 6 h poll).
2. **T+24 h** — Diagnose on the beta VM (kept perpetually current in the Tart matrix); classify: capture API change (ScreenCaptureKit), window-enumeration change (CGWindowList metadata), event-synthesis/TCC change.
3. **T+72 h** — Target: mitigating release (fix or permanent degraded mode) through the full release pipeline; Velopack staged rollout 10% → 100% over 48 h. (The 72 h target is only credible while the M-INFRA rig — beta VM + TCC canary, §4.3 — is green; a red canary lane extends the target and is called out in the pinned issue.)
4. Postmortem note appended to 02-macos-platform.md's fragility ledger.

**ENG-081** — Every macOS developer-beta cycle (WWDC June, then each beta), the nightly matrix picks up the new beta within 2 weeks of seed availability.

## 8. Versioning and support policy

- **SemVer** on the marketing version: MAJOR = platform additions/breaking settings changes, MINOR = features, PATCH = fixes. Pre-1.0 tech previews are `0.x`.
- IPC protocol between app and helpers has its own integer version, negotiated at handshake (M0); app and helper from the same release always match (ENG-070).
- OS support: **macOS current and two prior majors** (at v1: 14/15/current); **Windows 11 22H2+ only** (Windows 10 dropped); Linux: current Ubuntu LTS + Fedora current + Arch rolling (X11 and Wayland both first-class; GNOME/Mutter Wayland unsupported).
- Release support: latest MINOR gets fixes; previous MINOR gets security/data-loss fixes for 90 days. No LTS pre-2.0.
- Theme packages carry a `themeFormatVersion`; the shell loads themes with `formatVersion <= supported`, and format bumps are MINOR at most once per release train (protects third-party themes per 05-theming.md).

## Risks

Top 10, scored L×I (likelihood, impact) on 1–5:

| # | Risk | L | I | Mitigation | Owner milestone |
|---|---|---|---|---|---|
| 1 | macOS point/major release breaks tray mirroring (SCK, CGWindowList, event-tap changes) | 5 | 4 | Beta lane in nightly matrix (ENG-052); **local** kill switch (ENG-030); degraded mode (ENG-020); 72 h playbook (§7; v1 detection is first-hand — author is the only user) | M3+ |
| 1b | TCC.db schema churn breaks the SIP-disabled rig images → beta lane silently untrustworthy | 4 | 3 | ENG-054 schema canary; per-macOS-version image regeneration budgeted in M-INFRA; PPPC-provisioned SIP-enabled images as partial fallback (no Screen Recording lane) | M-INFRA+ |
| 2 | AX window management too unreliable across apps (Electron, Java, games) → taskbar feels broken | 3 | 5 | M2 top-30-app matrix as hard acceptance gate; CGWindowList fallback for enumeration; ship "companion mode" as honest default | M2 |
| 3 | No work-area reservation on macOS → windows underlap the bar, feels amateur | 4 | 4 | Dock-shim primary + AX nudge fallback both specced in 02-macos-platform.md; auto-hide as user escape hatch | M2 |
| 4 | Apple Events compat underdelivers (tier 2/3 blocked by SIP/LaunchServices) | 4 | 3 | Tier 1 (own suite) is the committed scope; tiers 2–3 are timeboxed research spikes, never on the critical path | M4 |
| 5 | Win2000 pixel fidelity takes far longer than estimated (Classic.Avalonia gaps for listview/treeview/menus) | 3 | 3 | Fork early (M0); pixel-golden suite makes the gap measurable; M1 budget already assumes heavy theme work | M1 |
| 6 | IP/legal challenge over lookalike assets or "Windows" trade dress | 2 | 5 | Recreated assets only + build-time hash denylist (ENG-011); metric-compatible free fonts; no MS marks in product name/branding; legal review before M5 GA | M5 |
| 7 | TCC permission friction kills onboarding (3 scary prompts) | 4 | 3 | Staged permissions (one per milestone-feature, ENG-020 degraded modes); permissions dashboard in Settings; M1 tech preview needs zero prompts | M2–M5 |
| 8 | 1–2 person team: bus factor and estimate risk (P80 ≈ 91 ew) | 3 | 4 | Milestones independently shippable; M1 alone is a viable "themed file manager + desktop" product (MVP cut keeps it so); 15% reserve budgeted; M1-deferred backlog is the first thing to slip further, by design | all |
| 9 | Avalonia gaps (native menu interop, accessibility tree, per-monitor DPI edge cases) | 3 | 3 | Pin + vendor-patch policy (fork with upstream PRs); helper-side native fallbacks for menus; a11y audit scheduled in M5 | M1+ |
| 10 | GNOME/Mutter Wayland makes the Linux taskbar impossible for a large user segment | 4 | 2 | X11 and Wayland (wlr-layer-shell + foreign-toplevel, wlroots/KDE) both first-class; GNOME/Mutter host unsupported and documented; Linux is last in rollout | L1–L3 |

## Open questions

1. **Product marketing name** — *Resolved (2026-07-04): the product is **Bevel**.* Identifiers are frozen at M0 exit (`pl.ikari.bevel`, `bevel://`, individual Developer ID); the Velopack feed URL, cask token (`bevel`), `Bevel.sdef` app display name, and MSI/winget identity all key off it.
2. **Distribution identity** — *Resolved (2026-07-04): macOS ships under the existing **individual** Developer ID `Developer ID Application: Cezar Pokorski (4TP7TPH2K6)` (no D-U-N-S/org; notarytool profile `bevel`). Windows uses a **separate OV/EV Authenticode cert obtained at Windows-track start** (03 REQ-W22; Azure Trusted Signing eligibility reconciled in 03).*
3. **Telemetry stance** — *Resolved (2026-07-04): v1 is **fully offline** — local crash logs + on-demand export, no network, no consent UI. Network crash reporting (e.g., Sentry) is a post-v1 option; §7 and the privacy policy reflect the offline stance.*
4. **M1 public tech preview** — *Resolved (2026-07-04): public at M1 exit.* The blocking condition (final marketing name) is met — the name is **Bevel** — and bundle id/scheme/signing identity are frozen at M0, so the public preview ships under the final name.
5. **Windows 10 support** — *Resolved (2026-07-04): **dropped**.* The Windows track targets **Windows 11 22H2+ only** (~2 ew saved; MSFT consumer EOL passed Oct 2025).
6. **Asset production budget** — *Resolved (2026-07-04): **$0 cash.** Assets are self-produced by the author using AI generation tools (incl. the author's existing graphics-AI subscriptions) plus open-licensed source material — **open licenses only, Creative Commons (CC0/CC-BY) and SIL OFL preferred** (see 05-theming.md §5–§6). No designer is commissioned for v1–v2; revisit only if the project grows past a solo effort. Estimates already assume the author produces assets.*
