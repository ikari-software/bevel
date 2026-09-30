# Master Plan

Status: SYNTHESIS — Chapter 00 of the Bevel spec. This chapter is the **tiebreaker**: where sibling chapters disagree, the resolutions in §3 are normative and the disagreeing text is pending edit, not an alternative design. Read this first; then the per-chapter reading guide (§8).

---

## 1. Executive summary

**What we are building.** **Bevel** (name chosen 2026-07-04; identifiers `pl.ikari.bevel`, scheme `bevel://`, binary `Bevel.app`/`bevel.exe`, frozen at M0) is a cross-platform desktop shell replacement: a desktop with wallpaper and icon grid, a taskbar with window list, start menu, system tray and clock, and a faithful Windows-2000-Explorer-style file manager — all fully owner-drawn, themable to the pixel. The Windows 2000 Classic look is the default theme; Windows XP (Luna) and Windows 11 themes follow on the same engine. The product's differentiator is *era-faithfulness as behavior, not just pixels*: themes declare behavior profiles (no button grouping under Win2000, ever), and the shell captures and re-hosts the host OS's real system tray rather than faking one.

**Why C# / Avalonia.** One codebase renders every pixel identically on all three platforms via Skia; Avalonia's `ControlTheme` system is the right substrate for swappable, data-only theme packages, and Classic.Avalonia (MIT) gives us a debugged port of WPF's classic theme to fork. C# everywhere keeps a 1–2 person team in one language; native code (Swift on macOS, thin C shims on Linux) exists only where the platform demands it, isolated in crash-contained helper processes behind a Platform Abstraction Layer (PAL). Windows needs no native helper at all — its interop is stable in-proc P/Invoke/COM.

**Why macOS first.** Deliberately inverted difficulty ordering: macOS is the *hardest* platform (no supported shell-replacement story, TCC permission gates, no work-area API, tray capture requires the Ice-style ScreenCaptureKit technique) and it is the primary target, so the architecture is forged against the worst constraints first. The PAL is shaped by the hardest platform but must not tax the easiest (03 §10): async-everything with capability flags, while Windows backends complete synchronously. Windows (natively supported shell replacement via the Winlogon key, 14–20 ew) and Linux (X11 and Wayland co-equal first-class, ~32–48 ew) follow as ports that validate PAL portability rather than as redesigns.

**Plan of record.** macOS v1 in 6 milestones plus a test-infrastructure work item: **65–91 engineer-weeks** (P50–P80 incl. 15% reserve), ~10–13 calendar months with 2 engineers. v1 ships the Win2000 theme plus a swappability-proving stub theme; Luna targets v1.1 and Win11 v1.2 with self-produced asset work ($0, AI-assisted + open/CC) running in parallel during v1 (§3 R6). Every permission-gated feature has a defined degraded mode; the shell must be fully usable with zero TCC grants; every mechanism that makes us the shell has an automated, no-network escape hatch.

---

## 2. Decisions register

Every load-bearing decision, one row each. IDs refer to the owning chapter's requirement/decision numbering. Where a decision resolves a cross-chapter conflict, see §3.

### 2.1 Architecture & process model (owner: 01)

| Decision | Rationale | Ref |
|---|---|---|
| Monorepo, one .NET solution, native helpers as sibling sub-projects, NUKE build, Central Package Management | PAL + helper + contract-test changes must land atomically; C# build pipeline covers codesign/notarize/protoc | 01 ARCH-01..05, 09 ENG-001 |
| Two processes on macOS/Linux (Avalonia `Bevel.App` + native helper daemon); **single process on Windows** | Helper owns all TCC-gated/crash-prone OS work (AX, SCKit, CGEvent); Windows has no TCC analog and stable in-proc APIs | 01 §3, 03 D-W9 |
| IPC = gRPC over Unix domain sockets (named pipes for the contingency Windows helper only), protobuf contracts in `proto/`, snapshot+delta event streams, peer-credential + nonce auth | Streaming RPCs map to the event model; first-class C# and Swift codegen; XPC/raw protobuf/JSON-RPC rejected | 01 ARCH-06, IPC-01..05 |
| Pixel data (tray mirrors, previews) over a shared-memory seqlock frame ring, never gRPC | Base64-in-protobuf burns CPU; latest-wins semantics fit UI mirroring | 01 §4.4 |
| Mutual supervision: backoff restarts, degraded mode after crash-loop, helper-side reverse watchdog, **SAFE-01** no-network rescue script driven by a journaled mutations manifest | A shell that can't be cleanly removed is malware-shaped; escape hatch must not require our code to run | 01 SUP-01..07, SAFE-01; 02 Req 2.3–2.6 |
| PAL: capability-oriented async interfaces + `Capabilities` objects; analyzer bans `OperatingSystem.Is*` outside PAL; immutable record DTOs; mandatory contract-test suite vs Fake and real PALs | UI feature-detects, never platform-checks; contract tests are the portability guarantee (not empty stub projects) | 01 PAL-01..06, 09 ENG-051 |
| `Bevel.Pal.Fake` is a shipped product feature (scripted desktops, `--pal=fake`) | Powers headless UI tests, theming review tooling, reproducible repros on any OS | 01 DI-05 |
| macOS helper in Swift; Linux helper in C# (NativeAOT or CoreCLR); Windows helper = contingency harness only, empty in v1 | SCKit/AX/Apple Events are Swift-native; DBus/XLib are fine from C#; Windows interop is in-proc | 01 §3.4, 03 D-W9 |
| Settings: layered live-reload JSON (defaults → managed → user → session), System.Text.Json source-gen, schema versioning, unknown-key preservation | Users can git their config; Microsoft.Extensions.Configuration and native stores rejected as primary | 01 CFG-01..05 |
| Themes are data-only `.beveltheme` zip packages (AXAML + assets, no assemblies, `x:Class` rejected); first-party themes ship compiled AND as packages | Third-party pipeline exercised by our own themes; no plugin-level trust for themes | 01 THM-01..04 |
| CI asset-provenance gate + hash blocklist of Microsoft-original assets | IP constraint is enforced mechanically, not by policy document | 01 THM-04, 05 §6, 09 ENG-011 |
| Plugins deferred post-v1 but shaped now: collectible ALC vs curated `Bevel.Sdk`; out-of-proc for real sandboxing; zero third-party plugin support in v1 | Prevents architecture that would preclude plugins without paying for them now | 01 PLG-01..03 |
| Self-update: **Velopack on all platforms**; never auto-apply mid-session; helper supervises the restart-to-update swap; app bundle (incl. helpers) is the atomic update unit | One updater, delta updates, channels; Sparkle (mac-only) and MSIX pipelines rejected | 01 UPD-01..04, 09 §6 (§3 R7) |
| Logging Serilog + structured event IDs; **v1 crash reporting is fully offline** (local logs + user-exportable crash report; no network telemetry, no consent UI); opt-in scrubbed network reporting (Sentry — no user paths/titles/pixels) is a post-v1 addition; UI-hang watchdog | A shell sees everything; offline-first fits the privacy-conscious early audience; scrubbing rules (when added) are security-reviewed code | 01 LOG-01..05, 09 §7, §7.1 #3 |
| DI via Microsoft.Extensions.Hosting; PAL selected only at composition root; test pyramid on Avalonia.Headless + Fake PAL, real PALs on self-hosted runners | Feature modules never reference concrete PALs | 01 DI-01..06 |

### 2.2 macOS platform (owner: 02)

| Decision | Rationale | Ref |
|---|---|---|
| Default mode is **Coexist**: `com.apple.finder CreateDesktop=false` + `killall Finder` — Finder alive but headless; loginwindow `Finder` key replacement is an opt-in, heavily-warned, best-effort Advanced toggle | The key is unreliable-to-inert on Sonoma+; CreateDesktop=false gives 95% of the win, zero SIP fights, preserves NSWorkspace/AE interop | 02 §2.1–2.2 |
| Reversibility manifest (`mutations.json`), standalone `restore-finder.sh`, Safe-Mode boot hotkey, uninstall never leaves a black screen | Recovery is safety-critical QA scope | 02 Req 2.3–2.6 |
| All TCC-sensitive/crash-prone work in separately-signed `BevelHelper.app`; safe AppKit calls (window level, collection behavior) in-proc via `IMacOSTopLevelPlatformHandle` | Stable helper signing identity keeps TCC grants durable across app updates | 02 Req 1.1, 7.4, 11.2 |
| Desktop/taskbar via NSWindow levels + collection behaviors; **panel-capable Avalonia backend workstream** (upstream or vendored fork) budgeted in M2 for non-activating surfaces | Stock Avalonia.Native can't produce `NSPanel` non-activating semantics; this is a real fork commitment, not a shim | 02 Req 3.1–3.6 |
| Taskbar window list: CGWindowList enumeration + AXUIElement control, correlated by PID+frame+z-order; AXObserver notifications, poll backstop; app-level degraded mode without Accessibility | No shared window id between the APIs; degradation is mandatory (ENG-020) | 02 §4 |
| Tray mirroring = Ice playbook: CGWindowList discovery → per-window SCContentFilter capture → AX-press-first click forwarding with CGEvent fallback → consent-gated menu-bar reclaim (system auto-hide setting + ⌘-drag item relocation). Feature-flagged behind a per-OS-build **self-test gate**; static-icon limited mode without Screen Recording | The signature feature and the biggest ABI fragility; shipped promise is "duplicate tray that becomes canonical with consent" | 02 §5, Req 5.10, 10.1 |
| Tray forwarding latency: two-tier **≤150 ms** (bar visible) / **≤500 ms** (hidden-bar reveal) | Reveal animation physically busts a flat 120 ms budget (supersedes 07 R-TR-2, §3 R3) | 02 §5.5 |
| Work area: default = Dock auto-hidden on a side edge + AX window nudging; visible-Dock shim (taller bar) is opt-in strict mode; no private SkyLight APIs | An auto-hidden Dock reserves nothing; the shim inset never matches the themed bar height — physics, not preference | 02 §9 (§3 R2) |
| Zero-permission operation is a design invariant; guided TCC onboarding with live grant polling and deep links | A desktop must never be un-bootable because a checkbox is off | 02 Req 7.1–7.3 |
| macOS 14 (Sonoma) floor; universal arm64+x86_64; Developer ID + notarization outside MAS; hardened runtime with .NET JIT entitlements; no sandbox | SCK per-window capture and status-item semantics require 14+; MAS is incompatible with our entitlements | 02 §10–11 |
| Primary-display-only taskbar in v1; one shared desktop across Spaces | Win2000-authentic; per-Space parity and multi-taskbar are open post-v1 questions | 02 Req 8.1, §3.3 |

### 2.3 Windows platform (owner: 03)

| Decision | Rationale | Ref |
|---|---|---|
| Shell replacement via **HKCU** Winlogon `Shell` only, never HKLM | Per-user blast radius; no UAC; another admin account always boots Explorer | D-W1 |
| Pre-Avalonia crash circuit breaker: 3 starts in 5 min → delete key, respawn explorer.exe, raw Win32 MessageBox | Crash-loop at logon is a bricked session; runs in the first ~50 lines of Main | D-W2 |
| Full `Shell_TrayWnd`/`TrayNotifyWnd` wire-protocol fidelity: WM_COPYDATA appbar server, all NOTIFYICONDATA generations, 32/64-bit senders, NOTIFYICON_VERSION_4; evaluate vendoring Cairo's ManagedShell | 25 years of tray callers must keep working; the structs are undocumented-but-frozen with OSS prior art | D-W3, REQ-W10..18 |
| Window tracking: RegisterShellHookWindow + SetWinEventHook; UIA enrichment only | That's Explorer's own feed, pre-filtered for taskbar eligibility | D-W4 |
| Virtual desktops: public `IVirtualDesktopManager` + cloak-event tricks + synthesized Win+Ctrl+arrow; no `IVirtualDesktopManagerInternal` in v1 | Per-build vtable GUIDs violate the reliability bar | D-W5 |
| **Companion mode is the default install**; replacement is explicit opt-in with logoff | Zero-risk wedge; tray capture only in shell mode (REQ-W18) | D-W6 |
| WiX MSI per-user installer, Authenticode-signed; MSIX rejected | Registry virtualization + Winlogon launch semantics are incompatible with shell replacement | D-W7, REQ-W22 |
| win-x64 + win-arm64 first-class, no x86; 32-bit compat is a tray-message data-format issue only | Windows 11 has no 32-bit SKU | D-W8 |
| **No native helper process on Windows** — all interop in-proc in `Bevel.Pal.Windows`; helper harness kept as documented contingency | No TCC analog, no isolation benefit, and IPC would slow the hottest path; normative over 01's earlier sketch | D-W9, §10.1 (§3 R1) |

### 2.4 Linux platform (owner: 04)

| Decision | Rationale | Ref |
|---|---|---|
| **X11-first** for Linux v1 via EWMH (DESKTOP/DOCK types, `_NET_WM_STRUT_PARTIAL`, client-message control); Wayland is a later milestone gated on a layer-shell spike (Sway + KWin) | Avalonia's Linux backend is X11; XWayland panels are broken panels; EWMH is fully specified for third-party shells | LNX-01, LNX-11 |
| Bevel is a **pager, not a window manager**: requires an EWMH host WM; optional Bevel Session bundles Openbox (X11) / labwc (Wayland) | Writing a WM/compositor is a product in itself | LNX-07, LNX-12 |
| **GNOME-on-Wayland unsupported as host** (no layer-shell, no foreign-toplevel); no extension workarounds | Mutter rejected layer-shell upstream; extension injection is a maintenance tarpit | LNX-10 |
| Wayland strategy: extend/fork Avalonia's Wayland backend with layer-shell surface roles (gtk-layer-shell precedent) | A native "layer host" helper would reimplement half a windowing backend over IPC | 04 §3.2 |
| Tray: native C# `org.kde.StatusNotifierWatcher` owner + SNI host + `com.canonical.dbusmenu` client (Tmds.DBus.Protocol); XEmbed fallback in-proc with X-error containment; snixembed rejected as dependency; D-Bus name policy = queue + `NameOwnerChanged`, guided disable-the-incumbent (no forced steal exists) | D-Bus name semantics forbid takeover unless the owner allows replacement | LNX-17..22 |
| File manager: `xdg-mime` default for `inode/directory`, `org.freedesktop.FileManager1` ownership (opt-in service install), native C# freedesktop Trash implementation with Nautilus interop tests, GIO P/Invoke as an *optional* virtual tier | Pure freedesktop standards; GVfs absence must not break local browsing | LNX-23..27 |
| Session: `/usr/share/xsessions` entry + systemd user units with `Restart=on-failure` and an `OnFailure` recovery unit; coexist with GNOME/KDE at display-manager level | Crash → recovery mode, never a black screen | LNX-13..16 |
| Packaging native-only (.deb/.rpm/AUR/tarball); **no Flatpak/Snap for the shell** (session files, DBus names, XEmbed, xdg-mime defaults all break in sandbox) | Recorded once so the question stays answered | LNX-30..31 |

### 2.5 Theming (owner: 05)

| Decision | Rationale | Ref |
|---|---|---|
| Engine = Avalonia `ControlTheme`s (structure) + semantic resource keys `Bevel.Color/Metric/Font.*` (palette/numbers), layered base → theme → variant → user overrides | Structural differences need templates; recolor variants are dictionary swaps | ENG-01..04 |
| **Staged theme delivery**: v1 = Win2000 Classic + a stub inherits-win2000 second theme (proves swap/install/rollback + one structural template); Luna (Blue only) v1.1; Win11 v1.2; self-produced asset work ($0, AI + open/CC) starts during v1 | Full three-theme v1 sinks the schedule; engine (package format, metrics, behavior profiles) is v1 scope regardless | 05 Summary, PKG-03 (§3 R6) |
| Live theme switch rebuilds window content against retained view-models, atomic rollback, <500 ms target | Avalonia ControlTheme invalidation is unreliable for full re-templating | ENG-05..07, THM-03 |
| DPI: hybrid — vector chrome with device-pixel snapping (`devicePx = max(1, round(logical×scale))`), pixel-art icons integer-scaled nearest-neighbor | Pure integer scaling blurs text; pure vector kills the pixel grid that *is* the aesthetic | DPI-01..05 |
| Metrics are data in `theme.json`; a source generator emits both the C# `ThemeMetrics` record and `Bevel.Metric.*` AXAML resources | C# and AXAML cannot disagree | MET-01 |
| Fonts: self-produced OFL "Bevel Sans" for Classic/Luna **in v1** (Wine Tahoma LGPL only as an open stopgap, renamed "Bevel Tahoma"); Selawik + Fluent UI System Icons for Win11; aliased text at 1×, AA at retina. License posture decided: **open only, CC/OFL preferred** | Metric-compatible open substitutes; no Microsoft font files ever ship; license no longer counsel-gated (only trade dress is) | 05 §5, FNT-01..04 |
| **Fork Classic.Avalonia** into `Bevel.Themes.Win2000` (don't depend-and-override, don't rewrite); upstream generic pieces | Resource-key refactor, metrics extraction, and DPI snapping are invasive | 05 §11 |
| Legal line: zero copied Microsoft bytes (CI gate + hash blocklist); lookalike layout defensible (*Apple v. Microsoft*, *Lotus v. Borland*); trade-dress mitigations: no MS marks, original start logo, non-Microsoft user-facing names | Copyright is the bright line; trade dress is the live risk needing counsel review | 05 §7, LEG-01 |
| Win2000 spec is normative to the pixel: registry-default palette, DrawEdge bevel algebra via `ClassicBorderDecorator`, classic color schemes incl. high-contrast (doubles as a11y story) | Zero-tolerance pixel goldens require an exact target | 05 §8, W2K-01..03 |

### 2.6 File manager (owner: 06)

| Decision | Rationale | Ref |
|---|---|---|
| All browsing on a VFS (`IVfsProvider`/`IVfsNode`); v1 providers: local FS, My Computer analog, Trash, mounted network shares, **read-only ZIP** | Archive write and other formats post-v1; zip-read is near-free and proves the VFS | FM-120, §2 |
| Win2000-authentic dual-mode window (folder vs Explore), full menu/toolbar/status anatomy, editable path ComboBox address bar — **no breadcrumbs** | Breadcrumbs are XP/Vista-era | FM-001, FM-030 |
| Native context-menu extensions (IContextMenu, Finder Sync, Nautilus scripts) out of scope v1; verbs + Open With + Send To only | Each is a huge compat surface with in-proc crash risk | FM-082 |
| Per-folder view state (and desktop icon layout) in a local DB keyed by VFS path hash — never desktop.ini/.DS_Store sidecars | No droppings in user folders | FM-050, 07 R-DK-5 |
| File ops run in the managed shell process, FIFO per target volume, parallel across volumes; macOS copies via `copyfile(3)` `COPYFILE_ALL` | xattr/resource-fork/quarantine fidelity is Finder-compat-critical | FM-130..136 |
| Trash put-back is **asymmetric on macOS**: sidecar origin map for our own trashing; Finder-trashed items show unknown origin, Restore disabled (canonical statement; 08 §4.1 mirrors it) | Apple exposes no put-back API | FM-110 |
| Thumbnails generated in the crash-isolated native helper (QLThumbnailGenerator / IShellItemImageFactory / freedesktop thumbnailers), 512 MB shared disk cache | Image decoders are crash- and exploit-prone | FM-163..164 |
| Icons: theme semantic-key pack first, native fallback second, per-theme `nativeIconFallback: allow|pixelate|deny` (Win2000 pixelates) | Modern icons must not shatter the period illusion | FM-160..161 |
| Search v1 = streaming VFS walk merged with Spotlight on macOS; no custom content index | Don't duplicate the OS | FM-170..171 |
| Perf: streamed enumeration, custom virtualizing wrap panel (ours — Avalonia lacks one), 100k-entry directory first paint < 250 ms | The panel is on the critical path from M1 | FM-180, 06 §8 |

### 2.7 Shell UX (owner: 07)

| Decision | Rationale | Ref |
|---|---|---|
| Era-faithfulness via theme-declared `BehaviorProfile` flags; UX never special-cases theme identity; per-flag user overrides in user config | Behavioral authenticity is the differentiator; grouping under a Win2000 skin reads as fake | R-ERA-1..3 |
| Win2000 profile: no grouping ever, no tray overflow, cascading Programs, balloon tips; XP adds grouping + chevron; W11 centered icon-only + flyout — XP/W11 behaviors ship **with their themes** (v1.1/v1.2 per §3 R6) | Profile flags exist in v1; no shipped theme sets them until the theme lands | 07 §1 |
| Start menu Documents/Recommended from **our own MRU only**; no sharedfilelist parsing; Personalized Menus cut from v1 | Fragile undocumented formats; universally hated feature | 07 §3 |
| macOS start hotkey default `⌥Esc` + `Ctrl+Esc`; **never remap ⌘**; avoid Spotlight/input-source collisions | Host muscle memory is sacred | R-SM-3, R-KB-2 |
| Date/Time dialog is a faithful recreation but **read-only**, deep-linking OS settings for changes | Setting system time needs privilege escalation on every platform | R-CL-2 |
| Desktop layout in our own `desktop-layout.json` keyed by (monitorSetHash, resolution); new macOS shortcuts are Finder aliases (bookmark data), not symlinks | Aliases survive moves and interop with real Finder | R-DK-5, §5.3 |
| Shell renders notifications **only for shell-originated events**; no notification-host role in v1 (Linux `org.freedesktop.Notifications` ownership is a post-v1 flag, see open Q) | macOS can't participate in interception; halving the surface | 07 §8 |
| We do NOT replace Alt-Tab/⌘Tab, Mission Control, Spotlight, Notification Center, lock/login; coexistence table is the FAQ source | Boundary honesty; each seam has documented mechanics | 07 §10, 08 §4 |
| Accessibility: Avalonia UIA/NSAccessibility tree, named automation nodes (mirrored tray icons carry native titles), VoiceOver/Narrator smoke passes as release gates; Linux AT-SPI gap accepted for v1 | An owner-drawn shell must not be a screen-reader black hole | R-AX-1..4 |
| Drag-and-drop matrix with era-authentic Windows modifier semantics on all hosts; native pasteboard bridging incl. macOS file promises (spike scheduled) | Browsers drag promises; missing this breaks the desktop | 07 §6, R-DND-2 |

### 2.8 OS interop (owner: 08)

| Decision | Rationale | Ref |
|---|---|---|
| macOS Apple Events: **Tier 1 ships in v1** (own sdef mirroring Finder terminology, byte-identical class/verb names + 4-char codes); **Tier 2 (bundle-id shadowing) never ships** in any release build (2-week timeboxed experiment); **Tier 3 (headless-Finder mirroring) is post-v1, opt-in, default OFF** | Tier 1 is achievable and durable; Tier 2 is one point-release from breaking and poisons system paths; Tier 3 is racy (window flash) and gated on a spike's measured escape rate | INT-5 (§3 R5) |
| Apple Events **terminate in Bevel.app's own process**; the object-specifier resolver (name/index access, restricted `whose`, POSIX coercions) is hand-written C# over the command model; sdef is dictionary metadata only | AEs are delivered on the receiving process's Mach port — a nested helper cannot receive them; Cocoa Scripting can't resolve into a non-KVC object graph from any process | 08 §2.1.2 (§3 R4) |
| Measurable compat bar: 100% of six core verbs' single-statement forms; ≥30/40 of the v1 corpus after mechanical `tell` retargeting; post-v1 campaign grows corpus to ≥300 + top-30-app AE survey | Compat must be a number, not a vibe | 08 §2.1.2, 09 M4 |
| Linux: own `org.freedesktop.FileManager1` (shell mode: fight for it; app mode: acquire-only-if-free), `xdg-mime` `inode/directory` + `trash` scheme; portals covered transitively | The reveal standard used by every browser/Electron app | INT-7..8 |
| Windows: HKCU-only registration with snapshot/restore; never touch Directory/Folder default verbs or shadow explorer.exe — except one sanctioned default-verb override gated on being the registered Winlogon shell, with crash-loop auto-restore | Highest-blast-radius write we make anywhere; treated accordingly | INT-9, 08 §2.3 |
| All automation surfaces (`bevelctl` CLI, `shell://` scheme, AppleScript, DBus `…Shell1`, post-v1 COM) route through one canonical `IShellAutomation` command model | One seam to test; behavioral identity across surfaces | INT-3 |
| `shell://` handles non-destructive verbs only | Kills the web-page confirmation-fatigue attack class outright | INT-11 |
| No emulation of Explorer's `Shell.Application` progid or Finder's full dictionary; never inject code into third-party processes | Empirically common subsets only; injection is off the table for notarization and trust | 08 §3.4, Q3 |
| Every degraded permission state renders an explanatory affordance; `bevelctl doctor` probes the degradation matrix live; all registrations reversible via `bevelctl register/unregister` | INT-4, INT-13 | |

### 2.9 Engineering plan (owner: 09)

| Decision | Rationale | Ref |
|---|---|---|
| Risk-ordered milestones: M1 needs zero TCC permissions and is independently demoable; M2–M4 each retire exactly one hard platform bet (AX window mgmt, tray mirroring, Finder scripting) | A failure in the scary work never blocks the demoable core | 09 §2 |
| macOS v1 = 65–91 ew incl. 15% reserve; Windows 14–20 ew; Linux 24–34 ew after mac v1 | P50–P80 with a 1–2 person team | 09 roll-up |
| Self-hosted Apple-silicon CI funded from project start (M-INFRA): Tart VM matrix incl. macOS beta lane, SIP-disabled TCC-provisioned images with schema canary, zoo-app rigs | Resolves 01 open Q6 in the affirmative; M2–M4 acceptance depends on it | 09 M-INFRA, ENG-052/054 |
| Identifier freeze at **M0 exit**: bundle id, URL scheme, signing identity (marketing name may lag) | TCC grants key on bundle id + signing identity; scheme rename breaks integrators | 09 M0 |
| Win2000 pixel goldens are zero-tolerance byte-exact; XP/W11 allow Δ≤1 | Pixel exactness is the theme's whole point | ENG-053 |
| Per-component codesigning innermost-first, hardened runtime, never `codesign --deep`; notarytool + stapler; SBOM per release | `--deep` breaks helper entitlements | ENG-060..061 |
| **v1: local kill switch only** — tray mirroring disableable at runtime via a local setting (v1's user base is the author; no remote flags); signed inbound-only remote flags are a v2 (real-users) addition | "macOS update broke tray mirroring" playbook: author disables locally, mitigating rebuild ≤72 h; remote feature-off (≤6 h) applies once there are remote users | ENG-030, 09 §7 |
| Dogfooding from M2 (companion mode), full mode from M5; scripted demo runs gate every public build | 09 §4.6 | |
| SemVer; macOS current+2 majors; previous MINOR gets security fixes 90 days; IPC protocol independently versioned, negotiated at handshake | 09 §8 | |

---

## 3. Cross-chapter resolutions (this section is the tiebreaker)

Residual inconsistencies found during synthesis, resolved here. "Stale text" = files that still disagree and need mechanical edits; until edited, this section overrides them.

**R1 — Windows helper: none.** 03 D-W9 is normative; 01 has already been reconciled (contingency-only `helper-windows`, in-proc PAL split table). No remaining conflict; keep the empty contingency harness documented.

**R2 — macOS work-area/Dock strategy.** 02 §9 is the single normative source: default = Dock auto-hidden on a side edge + AX window nudging; visible-Dock shim is an opt-in strict mode (taller bar, covered Dock); never both. **Stale text:** 07 §2.1 bullet ("Dock shim — Dock kept visible, tucked") and 07 §10 Dock row ("Kept visible … never auto-hidden by default"), 08 §4.1 Dock row ("Default: Dock auto-hide + Shell taskbar reserves via shim" — self-contradictory), 09 M2 ("auto-hide our bar in sync"). 07's own consistency note already flags 08/09; apply the same fix to 07 §10's row, which currently inverts 02's default.

**R3 — Tray click-forwarding latency.** Two-tier ≤150 ms / ≤500 ms per 02 §5.5. **Stale text:** 07 R-TR-2's flat 120 ms budget (02 explicitly retires it; 07 not yet edited).

**R4 — Where Apple Events terminate.** **08 §2.1.2 wins:** inbound AEs terminate in Bevel.app's own process (Mach-port delivery makes helper termination impossible for events addressed to us), the sdef binds to Bevel.app's Info.plist, and the object-specifier resolver is hand-written C#. **Stale text:** 02 Req 6.3 (AEs terminate in BevelHelper), 02 Req 11.1 (sdef in the helper bundle, helper as LaunchServices scripting target), and 02 Open Q8 (the addressing question dissolves — Bevel.app receives its own events). What *stays* in the helper: outbound Tier-3 AE *sends* to Finder (post-v1) and the `com.apple.security.automation.apple-events` entitlement for them. Consequence acknowledged: the resolver runs without crash isolation — mitigations (pure managed code, exception-wrapping to `errAEEventNotHandled`, corpus-as-fuzz-seed) per 08 Risk 8; the `AEFlattenDesc`-forwarding escape hatch is retained on paper.

**R5 — Tier 3 timing.** **08 INT-5 + 09 win:** Tier 3 is post-v1, opt-in, default OFF, gated on the 1-ew spike's measured window-escape rate; v1 budgets the spike only. **Stale text:** 02 §6.4/decision summary ("v1 ships … Tier 3 as an opt-in advanced toggle") — amend to post-v1.

**R6 — Theme delivery staging.** **05 wins:** v1 ships Win2000 + the stub theme; Luna v1.1 (Blue only); Win11 v1.2; self-produced asset work parallel to v1. **Stale text:** 07 R-ERA-3 ("all three themes ship at v1 per 05-theming.md" — 05 says otherwise; XP/W11 behaviors "delivered in M5") and 09 M5 scope/estimate ("XP (Luna) and Windows 11 theme completion", 10–13 ew). M5 sheds the Luna/Win11 template+asset volume; re-estimate M5 at ~8–11 ew and bank the difference as reserve — keep 65–91 ew as the published envelope. XP/W11 `BehaviorProfile` flags still land in the v1 engine, unset by any shipped theme.

**R7 — Updater.** Velopack everywhere (01 UPD-01; 09 §6 concurs and withdraws its earlier Sparkle/MSIX draft). **Stale text:** 02 Req 11.6 ("Sparkle-style or custom updater") — name Velopack; 03 open question 5 — settled, delete.

**R8 — loginwindow `Finder` key.** The registered "be the shell" state on macOS is **Coexist** (CreateDesktop=false + login items); the loginwindow key is an experimental lever inside the Advanced toggle, pending the 0.5-ew viability spike. 01 §9/SAFE-01 already reconciled. **Stale text:** 08 §2.1.4 still presents `defaults write com.apple.loginwindow Finder` as the login-shell mechanism without the unreliable-to-inert caveat — add the cross-reference to 02 §2.1.

**R9 — v1 solution trim.** Create `Bevel.Pal.Windows`, `Bevel.Pal.Linux`, `Bevel.Themes.Luna`, `Bevel.Themes.Win11`, and `native/helper-linux` **when their tracks start**, not at M0. The portability guarantee is `Bevel.Pal.Abstractions` + the contract-test suite, not empty stubs. 01 §1's layout is the *target-state* repo shape, not the M0 checklist. (`native/helper-windows` stays as an empty documented contingency per 01 open Q7 — costs nothing.)

**R10 — Settings and audio are unowned dependencies; now owned.** A **minimal settings window** (plain toggle list) is pulled into M2 scope — M2 needs the reserve-space strategy picker (02 Req 9.5) and permission affordances; M3 needs "reduce capture" and the tray kill switch (ENG-030); 07's start menu references "Control Panel (our settings app)" from M2. M5 keeps the *polished* settings app. Additionally, 05 §6.4's theme sounds are "played via the PAL audio service" but 01 §2.2 defines no such interface: add a minimal `IAudioPlayback` (play WAV, respect mute) to 01 §2.2, or explicitly cut theme sounds from v1 — default position: add the interface, it is trivially fakeable.

**R11 — PAL interface consolidation.** 01 §2.2 is the single definition point. The deltas exposed by 07 Q7 must be absorbed into 01: `IWindowManager` gains active-window snapshot, `RestoreAsync`, `DemandsAttention`, `AppId` grouping key, Space/virtual-desktop identity; `IAppEnvironment` gains live install/uninstall watching, `ProgramsTree`, category metadata. All other chapters' interface sketches (02 `IWindowService`, 04 `IWindowTracker`/`IWorkAreaReserver`, 06 `IAppRegistry`, 08 `IShellAutomation` is canonical-in-08-but-registered-in-01's-DI, …) carry the "illustrative" marker 07 already adopted.

**R12 — Cross-reference hygiene (resolved 2026-07-04).** The consistency sweep corrected the known-bad internal references: 06's stale desktop/taskbar-UX citations now point to 07-shell-ux.md, and 01 §1's repo-layout and pillar comments now carry the correct chapter numbers (desktop/taskbar → 07, file manager → 06, theme/assets → 05, macOS tray/Apple-Events/TCC → 02, Linux PAL → 04). A docs-CI link check that every relative `.md` reference resolves to an existing file remains a standing guard (fits 09 ENG-004's doc-lint).

**R13 — Trash put-back.** Already reconciled on disk: 08 §4.1's boundary row now matches FM-110's asymmetry. FM-110 remains the canonical statement; keep 08 as a cross-reference.

**R14 — Taskbar height nit.** 05 §3 (the metrics single source of truth) says Win2000 = 28 px; 02 §9 narrates "the themed taskbar's 30 logical px". Cosmetic; fix 02's prose during the R12 audit. All code reads `Bevel.Metric.TaskbarHeight`.

---

## 4. Architecture overview

```
                ┌──────────────────────────────────────────────────────────────┐
                │                    Bevel.App  (C# / .NET 9 / Avalonia)        │
                │                                                              │
                │  Bevel.Desktop     Bevel.Taskbar      Bevel.FileManager       │
                │  (icons+wallpaper) (list/start/tray)  (VFS + Filer UI)     │
                │  ────────────────────────────────────────────────────────    │
                │  Bevel.UI (chrome primitives)   Bevel.Themes.* (Win2000+stub)│
                │  Bevel.Core (settings, supervision, file-ops, MRU, undo)     │
                │  Bevel.Interop (IShellAutomation command model, bevelctl)    │
                │  ────────────────────────────────────────────────────────    │
                │        Bevel.Pal.Abstractions  (capability interfaces)       │
                │   ┌──────────────┬───────────────┬───────────────────────┐   │
                │   │ Pal.MacOS    │ Pal.Windows*  │ Pal.Linux*  │ Pal.Fake │   │
                │   └──────┬───────┴───────┬───────┴──────┬──────┴─────────┘   │
                └──────────┼───────────────┼──────────────┼───────────────────┘
                           │               │(in-proc      │
             gRPC over UDS │ + shared-mem  │ P/Invoke/COM,│ gRPC over UDS
             frame ring    │   (pixels)    │ no helper —  │ (Linux track)
                           ▼               │ D-W9)        ▼
                ┌────────────────────┐     │      ┌────────────────────┐
                │ BevelHelper.app    │     │      │ shell-helper       │
                │ (Swift, signed,    │     │      │ (C#, DBus/XEmbed   │
                │  crash-isolated)   │     │      │  escape hatch only)│
                │ AX · SCKit · CGEvent│    │      └────────────────────┘
                │ tray capture ·      │    │
                │ Tier-3 AE sends     │    │   * created when the platform
                └────────────────────┘     │     track starts (R9)
                                           ▼
                              Winlogon AutoRestartShell +
                              pre-Avalonia crash circuit breaker
```

**Bevel.App** is the single Avalonia UI process that owns every pixel: desktop, taskbar, start menu, tray UI, file manager, popups. It hosts the composition root (Microsoft.Extensions.Hosting), the settings system, theme engine, supervision logic, and — per R4 — the inbound Apple Events handlers and object-specifier resolver on macOS. It never references a platform API directly.

**Bevel.Pal.Abstractions** is the contract between product and platform: narrow, capability-oriented async interfaces (`IWindowManager`, `ISystemTrayHost`, `IDesktopEnvironment`, `IShellSession`, `IFileOperations`, `IIconProvider`, `IAppEnvironment`, `IPermissionBroker`, `IAudioPlayback` per R10). Every implementation — including `Bevel.Pal.Fake`, which is a shipped feature powering headless tests and demo mode — must pass one shared contract-test suite. Interfaces are shaped by the hardest platform (async, capability flags like `TrayCapability.Authoritative` vs `.Mirrored`) without taxing the easiest.

**BevelHelper.app (macOS)** is a separately signed Swift daemon holding everything TCC-gated or crash-prone: AX window enumeration/control, ScreenCaptureKit tray capture, CGEvent synthesis, menu-bar reclaim choreography, and (post-v1) outbound Tier-3 Apple Events to the hidden Finder. A helper crash costs ~2 s of tray mirroring, never the desktop. TCC grants attach to its stable signing identity, surviving app updates. It also runs the reverse watchdog: if Bevel.App dies while registered as shell, the helper relaunches it and, on final failure, fires the SAFE-01 escape hatch (restore Finder's desktop from the mutations journal).

**IPC** is gRPC over Unix domain sockets with protobuf contracts in `proto/`: unary calls with deadlines, one server-streaming RPC per event domain that always opens with a snapshot then deltas (reconnect = resubscribe, never corruption), peer-credential + nonce auth, no TCP ever. Pixel streams bypass gRPC via a shared-memory ring of seqlocked BGRA frame slots; gRPC carries only `FrameRingInfo`/`FrameSignal` control messages.

**Windows** runs Bevel.App alone: `Shell_TrayWnd` wire protocol, appbar server, shell-hook window tracking, and work-area calls are all in-proc P/Invoke/COM in `Bevel.Pal.Windows`. Containment comes from Winlogon `AutoRestartShell` plus a crash circuit breaker that runs before Avalonia initializes and restores Explorer after 3 failed starts in 5 minutes.

**Linux** uses in-proc C# for almost everything (EWMH via XCB on a dedicated X connection, SNI/dbusmenu via Tmds.DBus.Protocol, GIO P/Invoke); a native helper exists only as an escape hatch if XEmbed hosting proves unstable in-process. The shell is an EWMH pager under a host WM; the optional Bevel Session bundles Openbox/labwc.

**Themes** are data-only `.beveltheme` packages (control themes + semantic resources + provenance-gated assets). First-party themes compile into assemblies for startup speed but round-trip through the package pipeline in CI so the third-party path stays honest.

---

## 5. Roadmap

### 5.1 macOS v1 (plan of record: 65–91 ew incl. 15% reserve; ~10–13 months with 2 engineers)

| Milestone | Scope headline | ew (P50–P80) | Permissions | Key acceptance criteria |
|---|---|---|---|---|
| **M0 — Bootstrap** | Repo per 01 (trimmed per R9), Avalonia app boots on macOS arm64+x64, Classic.Avalonia forked, Swift helper skeleton with gRPC/UDS handshake + supervision, CI skeleton, **identifier freeze** (bundle id, scheme, signing identity) | 4–6 | none | Helper kill -9 → detected ≤2 s, session re-established without app restart; no placeholder identifiers remain |
| **M-INFRA — Test infra** (parallel to M1) | Self-hosted Apple-silicon runners, Tart VM matrix, SIP-disabled TCC-provisioned images + schema canary, WindowZoo/TrayZoo rigs, AppleScript corpus runner | 3–5 | n/a | Nightly matrix runs unattended on macOS 14+15 images; Accessibility and Screen Recording rig tests pass with no manual prompt-clicking |
| **M1 — Desktop + themed file manager** (the demo milestone) | Desktop window + wallpaper + icon grid; file manager MVP (local/computer/trash providers, tree + views, ops with undo, no-breadcrumbs address bar); custom virtualizing wrap panel; Win2000 theme complete for all M1 controls + stub second theme proving swap; ~60–80 recreated icons; hash-denylist gate live | 12–16 | **none** (ENG-010) | Fresh VM, zero permission prompts → Win2000 desktop; pixel goldens at 100%/200%; 2 GB copy cancellable, <100 ms UI stalls at 10k files; 3-min demo 20/20 crash-free |
| **M2 — Taskbar + AX window management** | Taskbar + start menu + clock; AX enumeration/control in helper; Dock-auto-hide + AX-nudge work-area strategy (+ opt-in shim per R2); TCC onboarding wizard; **minimal settings window (R10)**; panel-backend spike (02 Req 3.6) decided | 10–14 | Accessibility | Window list correct for ≥95% of top-30 apps; click focus/minimize; denied-permission mode fully usable; 200-window AX stress leak-free |
| **M3 — Systray mirroring** | Shared-memory frame ring; SCK per-item capture; click forwarding with reveal choreography; menu-bar reclaim consent flow; tray UI; runtime kill switch (ENG-030) | 10–15 | Screen Recording | Reference tray set renders <1 s latency; 9/10 click-forwarding; denied mode static icons; 24 h soak ≤3% core; kill switch tears down ≤1 s |
| **M4 — Apple Events Tier 1** | In-app NSAppleEventManager handlers + hand-written C# resolver (R4); Bevel.sdef; ~40-snippet corpus harness; `public.folder` LS handler | 7–10 | Automation (per-target) | 6 core verbs 100%; ≥30/40 corpus after retargeting; reveal round-trip <300 ms |
| **M5 — Polish + packaging + v1** | M1-deferred items (Zip, Send To, disk pie, remaining icons, 100k perf validation); polished settings app; login-item install + coexistence presets; crash reporting; signing/notarization/DMG/cask; Velopack updates; uninstaller (ENG-040). *(Luna/Win11 themes removed per R6)* | ~8–11 (was 10–13) | none | Notarized DMG on fresh VM; update n−1→n preserves TCC grants; ≥99% crash-free over 2-week/50-user beta; uninstall leaves no residue |

Research track (timeboxed, off the critical path): bundle-id shadowing (1 ew, never ships), Tier-3 mirroring spike (1 ew), per-beta SCK check (0.5 ew/cycle). **The loginwindow key viability spike (0.5 ew) is pulled onto the plan into M1** (owner directive 2026-07-04): since v1's only user is the author, the replacement path is dogfooded early on the author's own machine.

### 5.2 Windows track (after mac v1) — 14–20 ew

| Milestone | Scope | ew | Acceptance |
|---|---|---|---|
| **W1** | `Bevel.Pal.Windows` (CsWin32), desktop + file manager port, IFileOperation ops | 4–6 | M1 criteria on Windows 11 |
| **W2** | Taskbar appbar client/server, `Shell_TrayWnd` takeover + `TaskbarCreated`, shell-hook window tracking | 5–7 | Companion mode AND full mode both work |
| **W3** | HKCU shell replacement + circuit breaker, WiX MSI + winget, Authenticode | 5–7 | Log in to our shell, work a day, revert cleanly |

### 5.3 Linux track — 24–34 ew

| Milestone | Scope | ew | Acceptance |
|---|---|---|---|
| **L1** | X11: EWMH desktop/dock/struts, `_NET_CLIENT_LIST` taskbar, FM port (trash spec, FileManager1, xdg-mime) | 8–11 | LNX exit matrix on Openbox/Xfwm/KWin/Marco/i3 |
| **L2** | SNI watcher/host + dbusmenu, XEmbed fallback; Wayland layer-shell **gated on the LNX-11 spike** | 7–10 | Tray zoo passes; Plasma name-queue behavior verified |
| **L3** | Session entries + systemd units + recovery mode; .deb/.rpm/AUR packaging | 9–13 | GDM/SDDM/LightDM boot <5 s to desktop; crash → recovery, never black screen |

GNOME-on-Wayland: unsupported as host at Linux launch, documented (LNX-10).

---

## 6. Consolidated risk register (top 10)

Scored L×I (1–5). Merged from all chapters; owner = where mitigation lives.

| # | Risk | L | I | Mitigation | Owner |
|---|---|---|---|---|---|
| 1 | **macOS release breaks tray mirroring** (SCK filters, status-item layers, auto-hide semantics, Tahoe "Liquid Glass") — the signature feature is the biggest ABI fragility | 5 | 4 | Helper crash isolation; per-OS-build self-test gate + limited mode (02 5.10); signed-manifest kill switch ≤6 h; beta lane in nightly matrix; 72 h playbook; Ice's tracker as canary | 02 §5, 09 §7 |
| 2 | **AX window management unreliable across apps** (Electron/Java quirks, heuristic CGWindowList↔AX correlation) → taskbar feels broken | 3 | 5 | Top-30-app matrix as hard M2 gate; app-level degraded mode; per-app quirk table; companion mode as honest fallback | 02 §4, 09 M2 |
| 3 | **No work-area API on macOS**: AX nudging jitters, the Dock shim inflates the bar — every option is a tax | 4 | 4 | Three explicit user-selectable strategies with inline tradeoffs (02 Req 9.5); Req 9.4 measurements before shipping strict mode | 02 §9 |
| 4 | **Trade-dress/IP exposure** — mitigations are engineering-side only; counsel review of assets, names, marketing has not happened; late renames/redraws possible | 2 | 5 | Provenance gate + hash denylist; no MS marks; original start logo; metric-compatible free fonts; **legal review scheduled before M5 GA** | 05 §7, 09 risk 6 |
| 5 | **Avalonia platform gaps are real fork commitments**: non-activating NSPanel backend (02 Req 3.6), macOS file-promise DnD, Wayland layer-shell backend, Linux AT-SPI absent | 3 | 4 | M2 panel spike with a defined shippability call on the fallback; DnD spike; upstream-first policy with vendored patches; AT-SPI accepted v1 gap (Linux ships last) | 02/04/07, 09 risk 9 |
| 6 | **TCC friction**: three scary prompts, plus Sequoia's periodic Screen-Recording re-consent could make live tray previews impractical | 4 | 3 | Staged permissions (one per milestone); zero-permission invariant; capture throttling; fallback = static-icon default with live opt-in (02 open Q7) | 02 §7, 09 risk 7 |
| 7 | **1–2 person team / estimate risk** (P80 ≈ 91 ew; two-language Swift+C# tax on every mac tray/AX feature) | 3 | 4 | Independently shippable milestones (M1 alone is a viable product); 15% reserve; M1-deferred backlog slips first by design; batch proto churn | 09 risk 8, 01 risk 3 |
| 8 | **Apple Events compat underdelivers**: Tier 1's hand-written resolver is unbudgeted-in-detail scope in the UI process; Tier 2/3 are blocked-by-design | 4 | 3 | Resolver estimate re-check (08 Q2) with `whose`-filter cut as pressure valve; exception-wrapping + corpus fuzzing; tiers 2/3 are spikes, never critical path | 08 §2.1.2, 09 M4 |
| 9 | **Windows tray-protocol fidelity + Defender/SmartScreen flagging** (Winlogon write is a classic persistence TTP; subtle NOTIFYICONDATA offset bugs silently drop icons) | 3 | 3 | Vendor ManagedShell marshaling; tray-app zoo matrix; Authenticode + Defender submission; HKCU-only; companion-default install | 03 risks 1/3 |
| 10 | **Linux display-server squeeze**: Avalonia Wayland backend never materializes upstream while X11 relevance decays | 3 | 3 | X11-first buys time; LNX-11 spike gates all Wayland work; wlroots/KWin only at first Wayland release; monitor distro Xorg-removal annually | 04 risks, 09 risk 10 |

Honorable mentions tracked in-chapter: TCC.db schema churn breaking CI rig images (09 1b, canaried); Win2000 pixel-fidelity cost vs Classic.Avalonia gaps (09 risk 5, measured by goldens); crash-report scrubbing as a trust incident (01 risk 7); FileManager1/watcher D-Bus name flakiness in Linux companion mode (04).

---

## 7. Consolidated open questions

### 7.1 Owner decisions — RESOLVED 2026-07-04

All thirteen owner-decision questions were resolved in an interactive session on 2026-07-04. Recorded here as the authoritative answers; downstream chapters inherit them.

| # | Question | Decision | Notes / consequences |
|---|---|---|---|
| 1 | Product name → identifiers | **Bevel** — `pl.ikari.bevel`, `bevel://`, `Bevel.app`/`bevel.exe` | Low trademark risk (generic word, no MS overlap). Freeze at M0. Propagates to sdef app name, Velopack feed, cask token, DBus name, MSI identity, start-menu banner. |
| 2 | Distribution / signing identity | **Reuse existing individual Developer ID** — `Developer ID Application: Cezar Pokorski (4TP7TPH2K6)` | Already in keychain; konCePCja's `tools/sign_and_notarize.sh` is the template (new notarytool profile `bevel`). No D-U-N-S needed (individual enrollment). Trade-dress exposure is personal — revisit an entity only on commercial traction. Windows Authenticode = separate cert at Windows-track start. |
| 3 | Telemetry stance | **Fully offline v1** — local crash logs + export button; no network telemetry; no consent UI; **no remote kill-switch/config either** (v1's user base is the author) | Unhandled AE verbs logged locally only. v1 is single-user (author dogfooding), so the runtime kill switch is a **local** toggle and rapid-response is first-hand; remote flags + aggregate reporting arrive when v1 opens to real users (v2). Simplifies PAL logging/crash interfaces. |
| 4 | Asset production | **$0 cash — self-produced (AI-assisted + open/CC), staggered by ship date** | No paid commissioning. Author produces assets with AI generation tools (incl. existing graphics-AI subscriptions) + **open-licensed source, Creative Commons (CC0/CC-BY) and SIL OFL preferred**. Win2000 core (~80–120 icons + Bevel Sans font + core sounds) during v1; Luna mid-v1 (v1.1); Win11 after v1 (v1.2). Start mark (BevelMark) shipped 2026-09-30; residual cube-mark clearance parked. Font/asset *license* posture decided (open/CC/OFL). |
| 5 | "Replace Finder" in v1 | **Coexist-only in v1; gated opt-in replace toggle in a later v1.x; full replace-by-default is the north star for the mature/productionized release** | v1 carries ~zero brick risk. The gated toggle ships only after the loginwindow spike proves recovery (Safe-boot restore, defaults-key auto-restore, Finder-relaunch watchdog) is bulletproof — the viability spike is **scheduled into M1** (the author self-tests replacement early, since v1 is single-user). |
| 6 | Windows install default | **Companion by default; replacement a prominent opt-in** | Mirrors macOS posture. Both modes exist. Revisit default once macOS replace-rollout data is in. |
| 7 | Windows 10 support | **Windows 11 22H2+ only** | Saves ~2 ew; no legacy API guards. Revisit only if early Windows demand skews Win10-heavy. |
| 8 | Update feed & rollout | **GitHub Releases + Sparkle/Velopack appcast for beta; CDN + %-staged rollout approaching 1.0** | Zero infra now; cohort-by-download staging (no tracking) consistent with #3. |
| 9 | Theming / branding legal | **Owner posture set 2026-09-30** — product **Bevel Desktop**; theme names + Start mark shipped; trade dress accepted as redesign + deep customisability (not a build gate). **No hosted theme gallery**; install from **git repos**. Theme-package licensing / install disclaimers / provenance enforcement = **TBD** (blurry). Parked counsel asks: `docs/legal/open-questions-parked.md` | Engine + first-party chrome proceed. Asset/font licenses remain open-only (#4). Do not invent a trust marketplace. |
| 10 | Injection red line | **Held — never inject into third-party processes**, not even opt-in | Reveals route via Apple Events + Finder suppression; exotic paths soft-fail. Keeps notarization/library-validation clean. "For now" — revisited only if a genuinely safe justification appears (unlikely). |
| 11a | File-manager extensions default | **Follow the OS setting** | Native feel; no `.pdf.exe` spoofing footgun by default. Toggles both ways. |
| 11b | macOS "My Documents" → | **`~` (home folder)** | Treated as a launchpad. Needed for onboarding design — now unblocked. |
| 11c | Win2000 Appearance dialog in v1 | **Presets only** | Full per-element editor deferred; engine stays per-element capable. |
| 11d | Show-Desktop = minimize-all (macOS) | **Yes** (auto-decided) | Authentic, low-risk, via PAL window interface. |
| 11e | Anachronistic secondary taskbar | **Off by default, opt-in available** (auto-decided) | Under 2000/XP profiles. |
| 12 | Linux scope | **Display-server stance SET (revised 2026-07-04): X11 and Wayland are co-equal first-class targets.** Remaining Linux details (systemd floor, notifications ownership, Plasma funding level, bundled-WM, Flatpak funnel) still deferred to track kickoff. | Wayland scope = wlroots family (Sway/Hyprland/river/Wayfire) + KWin/Plasma via `zwlr_layer_shell_v1` + `zwlr_foreign_toplevel_management_v1`. **GNOME/Mutter unsupported as a host** (refuses both protocols — hard constraint, not a gap). Cost: a real `Bevel.Pal.Linux.Wayland` backend (Avalonia has none; XWayland can't do layer surfaces) is now a **funded up-front line item**, not an X11-gated follow-on — bumps the Linux estimate (was 24–34 ew; re-estimate at track kickoff, expect +8–14 ew for the Wayland backend). Owning the whole Wayland session by bundling a compositor = later "Bevel Session" phase, out of v-Linux scope. Mitigation from prior stance retained: Fake PAL platform + design review keeps abstractions display-server-neutral. |
| 13 | MDM / managed-config (CFG-03) | **Keep in v1** | Bets managed fleets (kiosks, labs, retro installs, enterprise) are an early audience. Adds profile-parsing + policy-precedence work to v1 scope — fold small ew bump into M2/config. |

**Scope deltas (propagated 2026-07-04 via consistency sweep):** product name Shell→Bevel applied across all chapters (`Bevel.*` namespaces, `.beveltheme`, `bevelctl`, `pl.ikari.bevel`); macOS signing pinned to the existing individual Developer ID (Cezar Pokorski, 4TP7TPH2K6, no D-U-N-S); v1 telemetry offline-first; 03 companion-default + Win11-22H2+-only settled; CFG-03 kept in v1; Linux X11+Wayland co-equal first-class (Wayland backend +8–14 ew). **Residual reconciliation (done):** PAL namespace unified to the canonical `Bevel.Pal.*` (per 01 + this register/R9); the 03/04 draft `Bevel.Platform.<OS>` usages were corrected to `Bevel.Pal.Windows` and `Bevel.Pal.Linux[.X11/.Wayland]`. **Branding (2026-09-30):** product **Bevel Desktop**; theme display names + BevelMark shipped; trade-dress + git-only themes decided; licensing TBD — see `docs/legal/open-questions-parked.md`.

### 7.2 Needs a spike (timeboxed, written outcome required)

1. **loginwindow `Finder` key viability** on current macOS (0.5 ew) — **scheduled into M1** (owner directive 2026-07-04; the author dogfoods replacement early, since v1 is single-user) → decides question 5 above and the Advanced toggle's contents.
2. **Bundle-id shadowing** (1 ew, hard cap) → documented failure mode; never ships regardless of outcome. (08 Tier 2)
3. **Tier-3 headless-Finder mirroring** (1 ew): measure window-escape rate and flash duration; gates the post-v1 4–6 ew productization. (08 §2.1.1)
4. **Panel-capable Avalonia.Native backend** (M2): upstream a non-activating NSPanel option vs vendored fork; if neither lands, is the activate-then-refocus fallback shippable? (02 Q9/Req 3.6)
5. **macOS file-promise drag-and-drop** in Avalonia (early): if missing, the helper owns an `NSDraggingDestination` overlay. (07 R-DND-2)
6. **Dock inset measurements** on macOS 14/15 (02 Req 9.4): prerequisite for the strict shim mode; decides whether strict mode appears in settings at all (02 Q6).
7. **AE resolver re-estimate** (08 Q2): does M4's 7–10 ew hold for a from-scratch object-specifier resolver? Pressure valve = cut `whose` filters, not the corpus bar. Includes Script Editor dictionary/4-char-code collision validation (08 risk 3).
8. **Sequoia Screen-Recording re-consent field data** (02 Q7): if too intrusive, flip the default to static-icon tray with live preview opt-in.
9. **Dock-AX bounce observation** (07 Q3): can `DemandsAttention` be sourced from Dock AX without private API? Upgrades R-TB-8 from documented-unsupported.
10. **macOS per-Space wallpaper strategy** (02 Q1): confirm one-shared-desktop is acceptable for launch or scope per-Space parity.
11. **Windows track spikes** (defer until W1): `IShellWindows`/`SHOpenFolderAndSelectItems` emulation depth (03 Q1); `Shell.Application` progid squat hazard (08 Q4); `UserNotificationListener` toast remediation without package identity (03 risk 4); per-build `IVirtualDesktopManagerInternal` adapter worth (03 Q4).
12. **Wayland protocol pick** at milestone time: `zwlr_foreign_toplevel_management_v1` vs `ext_foreign_toplevel_*` (04 Q5); desktop DnD viability on layer-shell background surfaces (04 Q6); per-DE takeover-recipe CI (04 Q7).
13. **Shortcuts/App Intents scope** (08 Q5): worth the native surface in v1 or milestone 2.

---

## 8. Reading guide

| Chapter | One line |
|---|---|
| **00-master-plan.md** | This file — executive summary, decisions register, tiebreaking resolutions, roadmap, risks, open questions. |
| **01-architecture.md** | Solution/process architecture: monorepo, PAL contracts, two-process model, gRPC+shared-memory IPC, supervision & escape hatches, settings, themes-as-data, logging, Velopack updates, DI/testability. |
| **02-macos-platform.md** | The hard platform: Finder coexistence, desktop-level NSWindows, AX taskbar, Ice-style tray mirroring, TCC onboarding, Dock/work-area strategy, packaging/notarization, macOS 14 floor. |
| **03-windows-platform.md** | The easy platform: HKCU Winlogon replacement + circuit breaker, `Shell_TrayWnd`/appbar wire protocol, shell-hook window tracking, companion-default install, WiX MSI, no native helper (D-W9). |
| **04-linux-platform.md** | X11-first EWMH pager, Wayland-later via layer-shell (GNOME unsupported), native SNI/dbusmenu tray, freedesktop FM integration, session units, native-only packaging. |
| **05-theming.md** | Theme engine (ControlThemes + semantic keys + metrics-as-data), `.beveltheme` format, hybrid DPI strategy, font/asset/legal plan, pixel-exact Win2000 spec, staged Luna/Win11, Classic.Avalonia fork. |
| **06-file-manager.md** | Win2000 Explorer clone: window anatomy and FM-xxx requirements, VFS + five providers, file-operations engine with undo, change watching, icon/thumbnail pipeline, search, 100k-entry performance bar. |
| **07-shell-ux.md** | Observable behavior of desktop/taskbar/start/tray across themes and platforms: BehaviorProfile era-faithfulness, DnD matrix, multi-monitor, notifications scope, keyboard/accessibility, coexistence contract. |
| **08-os-interop.md** | Inbound compat (Apple Events tiers, FileManager1, Windows registration hazards), outbound automation (`IShellAutomation`, bevelctl, shell://), boundary document, degradation matrix + `bevelctl doctor`. |
| **09-engineering-plan.md** | Risk-ordered milestones M0–M5 + platform tracks with estimates, testing pyramid (contract tests, rigs, TCC-provisioned VM matrix, pixel goldens), CI/CD, distribution, kill switches, 72 h playbook, support policy. |
