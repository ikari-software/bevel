# OS Interop & Compatibility Services

## Summary

This chapter specifies the compatibility layer that makes the host OS and third-party applications treat Bevel as *the* file manager and desktop shell. It covers three directions of traffic:

1. **Inbound compatibility** — the per-platform entry points other apps use to say "reveal this file", "open this folder", or "script the file manager", and how Bevel registers for, intercepts, or mirrors each one. On macOS this means the Apple Events tier strategy (Tier 1: own scripting suite → Tier 2: bundle-id shadowing → Tier 3: headless Finder mirroring; tier numbering shared with `02-macos-platform.md` §6, execution details there) plus the `NSWorkspace` reveal path. **v1 ships Tier 1 only**; Tier 3 is a post-v1 opt-in compat mode. On Linux it is `org.freedesktop.FileManager1` on DBus plus `xdg-mime` registration for `inode/directory`. On Windows it is folder association and shell-verb registration, a minefield we enter deliberately and narrowly.
2. **Outbound automation** — Bevel's own scriptable surface: a canonical command model in C#, exposed as a CLI (`bevelctl`), a `bevel://` URL scheme, and per-platform native automation (AppleScript sdef on macOS, DBus interface on Linux, COM/`IDispatch` later on Windows).
3. **The boundary** — a signed-off list of OS features we intentionally leave native, the seams users will notice at each boundary, mitigations for each, and a degradation matrix stating exactly what works when a given permission or registration is missing.

Positions taken here: we ship a Finder-terminology-compatible scripting suite as the primary macOS compat mechanism and treat bundle-id shadowing as a time-boxed experiment, not a plan of record; on Linux we claim `org.freedesktop.FileManager1` unconditionally when running as shell; on Windows we register per-user (`HKCU`) only and never touch the `Directory`/`Folder` progid default verbs.

Cross-references: process/IPC architecture in `01-architecture.md`, macOS platform mechanics in `02-macos-platform.md`, Windows platform mechanics in `03-windows-platform.md`, Linux platform mechanics in `04-linux-platform.md`, file manager behavior in `06-file-manager.md`, permissions onboarding UX in `07-shell-ux.md`.

---

## 1. Scope, definitions, and the compat contract

**Definition — "reveal":** open a file-manager window showing the parent container of a target item, with the item selected. This is the single most common cross-app integration ("Show in Finder", "Reveal in Explorer", download managers, IDEs, chat apps). Getting reveal right on every platform is the P0 of this chapter.

**Definition — "compat entry point":** any OS-provided or de-facto-standard mechanism a third-party app uses to reach the file manager without knowing which file manager is installed.

**The compat contract (numbered requirements use the `INT-` prefix):**

- **INT-1:** Every compat entry point listed in §2 MUST resolve to a Bevel file-manager window when Bevel is running as the active shell, or degrade per the matrix in §5 when it is not.
- **INT-2:** Reveal latency from entry-point invocation to a visible, selected item MUST be < 500 ms p95 on reference hardware (window may reuse an existing view per `06-file-manager.md` window-reuse rules).
- **INT-3:** Every inbound entry point maps to the same internal command model (§3.1). No entry point may bypass it — this keeps CLI, URL scheme, DBus, and Apple Events behaviorally identical and testable through one seam.
- **INT-4:** All registration/deregistration actions MUST be reversible and performed by the `bevelctl register`/`bevelctl unregister` verbs so that uninstall restores the OS to its prior state (see `07-shell-ux.md`).

---

## 2. Inbound compatibility: being "the file manager"

### 2.1 macOS

macOS has **no supported "default file manager" API**. Apps reach Finder three ways, in descending frequency:

| # | Mechanism | Typical caller | How it routes |
|---|-----------|----------------|---------------|
| 1 | `NSWorkspace.shared.activateFileViewerSelecting(_:)` / `selectFile(_:inFileViewerRootedAtPath:)` | Native apps ("Show in Finder") | Private path from AppKit → Finder; effectively hardcoded to `com.apple.finder` |
| 2 | Apple Events to `com.apple.finder` (`tell application "Finder"`) | AppleScript, automation tools, Electron apps via `osascript`, installers | AESend targeted by bundle id |
| 3 | `open -R <path>` / `LSOpenCFURLRef` on a folder URL | CLIs, scripts | Launch Services → handler for `public.folder`; folder-open (not reveal) *is* interceptable via LS |

Only #3 is cleanly claimable: Bevel registers as a handler for `public.folder` / `public.volume` document types in its `Info.plist` and calls `LSSetDefaultRoleHandlerForContentType(kUTTypeFolder, kLSRolesViewer, ourBundleID)` at registration time. #1 and #2 route by the literal bundle id `com.apple.finder`, which SIP prevents us from owning honestly. Hence the tier strategy.

#### 2.1.1 Apple Events tier strategy (mechanics in `02-macos-platform.md`; surface specced here)

- **Tier 1 (plan of record): own scripting suite mirroring Finder terminology.** Bevel's app bundle ships an `.sdef` whose class and command names are byte-identical to Finder's for the supported subset (same terminology strings, same four-char codes where safe — Finder's suite codes `fndr`/`core` are not trademarked and AE codes are shared across apps by design). Result: any script or app that does `tell application "Bevel"` — or `tell application id "pl.ikari.bevel"` — gets Finder-compatible behavior, and scripts can be ported by changing one word. We additionally ship a **script shim**: an optional `osascript` translation layer (a launchd-registered helper observing `NSAppleEventManager` within our own process only — no system hooking) is *not* possible for events addressed to Finder; instead we provide `bevelctl translate-script <file.scpt>` which rewrites `tell application "Finder"` targets. Honest limitation: unmodified third-party binaries that send AEs to `com.apple.finder` will still reach the real Finder.
- **Tier 2 (time-boxed experiment, 2 engineering weeks max, feature-flagged off):** bundle-id shadowing — registering a helper app whose `CFBundleIdentifier` is `com.apple.finder` in a user-domain LS database position that outranks `/System/Library/CoreServices/Finder.app`. Expected outcome: Launch Services refuses or Gatekeeper/AMFI flags it (platform-binary identifiers are protected); we document the failure mode and close the experiment. **Decision:** we do NOT ship Tier 2 in any release build even if it works, because it is one macOS point-release away from breaking and likely violates notarization expectations. Rationale recorded; rejected as plan of record.
- **Tier 3 (post-v1, opt-in compat mode for stubborn apps): headless real-Finder mirroring.** Real Finder stays running but windowless (we close its windows via AE and suppress its Desktop with `defaults write com.apple.finder CreateDesktop false`). A watcher in the macOS native helper (see `01-architecture.md`, helper process model) subscribes to Finder window creation via the Accessibility API (`AXObserver` on Finder's PID, `kAXWindowCreatedNotification`). When a third-party app's reveal lands in real Finder and a window appears, the helper reads the window's target via AX (`AXDocument`/title heuristics + a follow-up AE `get target of Finder window 1`), closes the Finder window, and re-issues the reveal into Bevel through the command model. Seam: a Finder window may flash for ~100–300 ms. Mitigation: none reliable; we set Finder windows' AX-observed close as fast as possible and document the flash. Tier 3 requires Accessibility + Automation TCC grants (§5). **Status:** post-v1. `02-macos-platform.md` §6.4 and its Decision summary spec this as the opt-in "High-fidelity Finder automation" advanced toggle, and `09-engineering-plan.md` budgets only a 1-ew research spike for it inside the v1 cycle; productization is its own post-v1 milestone (estimate 4–6 ew, gated on the spike's measured window-escape rate and flash duration) with the Automation-TCC onboarding flow scoped into it.
- **INT-5:** Tier 1 MUST ship in v1. Tier 3 MUST NOT ship in v1; when it ships post-v1 it is an **opt-in** "Finder compatibility mode" toggle, default OFF, per `02-macos-platform.md` §6.4 (default-state ownership: 02, Open Q4 there). Tier 2 MUST NOT ship in any release.

#### 2.1.2 The scripting suite surface (Tier 1 sdef)

Object model subset (classes, mirrored Finder terminology):

| Class | Key properties supported | Notes |
|-------|--------------------------|-------|
| `application` | `home`, `desktop`, `startup disk`, `trash`, `version`, `frontmost`, `insertion location`, `selection` (r/w) | `selection` maps to active file-manager view selection |
| `Finder window` (we expose the same class name via terminology alias `window`) | `target` (r/w), `bounds`, `index`, `current view` (icon/list/column→details), `toolbar visible`, `sidebar width` | `column view` maps to details view in v1; documented deviation |
| `item` (abstract) | `name`, `displayed name`, `name extension`, `container`, `URL`, `kind`, `size`, `creation date`, `modification date`, `label index`, `comment`, `position` (desktop only) | |
| `file` / `document file` / `application file` / `alias file` | inherits `item`; `file type`, `version` | `alias file` covers symlinks + Finder aliases (resolved via `CFURLCreateBookmarkData` APIs) |
| `folder` / `disk` / `desktop-object` / `trash-object` | inherits `container`; `entire contents` **not** supported in v1 (unbounded recursion) | |

Command set (verbs):

| Verb | Semantics | v1 |
|------|-----------|----|
| `reveal` | Select item(s) in a window showing the container | ✅ |
| `open` | Open folder in window, or launch file's default app via `NSWorkspace` | ✅ |
| `select` | Set window/desktop selection | ✅ |
| `make` | `make new folder at <container> with properties {name:…}`; also `make new Finder window to <target>` | ✅ |
| `delete` | Move to Trash (`NSFileManager.trashItem(at:resultingItemURL:)`) | ✅ |
| `duplicate` | Copy within/into container, Finder naming rules ("`name copy`") | ✅ |
| `move` | Move item(s) to container | ✅ |
| `exists`, `count`, `get`/`set` on listed properties | Standard AE object-model plumbing | ✅ |
| `eject`, `empty` (trash), `sort`, `clean up`, `update`, `print` | — | ❌ v1, error `errAEEventNotHandled` with a descriptive message |

Object-specifier resolution MUST support: name and index access into `windows`, `folders`, `files`, `items`, `disks`; `whose` filters on `name`/`name extension`/`kind` only (full filter algebra deferred); POSIX-path coercions (`POSIX file`, `as alias`, `as «class furl»`).

**Coverage target (measurable, sized to M4's 7–10 ew budget in `09-engineering-plan.md`):** the v1 corpus is the **~40-snippet** harness specced in 09 M4 (real-world `tell application "Finder"` snippets from Alfred workflows, Hammerspoon configs, and app "Reveal in Finder" implementations), checked into `tests/compat/applescript-corpus/`. v1 exit criteria: **100%** of the six core verbs' single-statement forms pass; **≥30/40** corpus scripts behave correctly after mechanical `tell` retargeting (matches 09 M4 acceptance criterion 1). Scripts using System Events UI scripting of Finder, `entire contents`, or Finder-window chrome manipulation are out of scope and counted as expected failures. **Post-v1 quality campaign** (not an M4 exit criterion): grow the corpus to ≥300 scripts harvested from GitHub code search and macscripter/Script Debugger forum archives, survey app-emitted Apple Events from the 30 most-downloaded Mac apps offering "Reveal in Finder", and target ≥80% of app-emitted AEs / ≥50% of the general corpus.

Implementation: two hard constraints shape this. First, **Apple Events terminate in Bevel.app's own process, full stop** — AEs addressed to `tell application "Bevel"` / our bundle id are delivered on the AE Mach port of the receiving app's process, so a separately bundled helper can never receive them, and `OSAScriptingDefinition`/`NSAppleScriptEnabled` in Bevel.app's `Info.plist` bind the dictionary to Bevel.app. Second, the **standard Cocoa Scripting machinery is off the table regardless of host**: `NSScriptSuiteRegistry`/`NSScriptCommand` object-specifier resolution assumes a KVC-scriptable Cocoa object graph, which an Avalonia/C# core does not have. The sdef is therefore terminology/dictionary metadata (for Script Editor, Script Debugger, and AE code mapping), not executable machinery. **Decision:** inbound handlers are installed in the Bevel.app process via `NSAppleEventManager` through the AppKit shim (consistent with `00-master-plan.md` R4 and `02-macos-platform.md` Req 6.3), and the object-specifier resolver — name/index element access, the restricted `whose` algebra above, POSIX-path coercions — is **hand-written in C#** against the command model (§3.1). The separately bundled native helper receives **no** inbound Apple Events; its only AE role is *outbound* Tier-3 sends (post-v1) — querying and closing real-Finder windows (§2.1.1). This is real, owned scope: a from-scratch AE object-model resolver with no crash isolation. Mitigations for the isolation loss: the resolver is pure managed code over the command model (no unmanaged state to corrupt), is fuzzed against the script corpus, and every handler is wrapped so a resolver fault returns `errAEEventNotHandled` with a descriptive error rather than propagating. Rejected alternative: receive AEs in Bevel.app and forward flattened descriptors (`AEFlattenDesc`/`AEUnflattenDesc`) over IPC to the Swift helper for resolution — this buys nothing, because the helper would still have to hand-roll the same resolver (Cocoa Scripting cannot resolve into our object graph from any process), while adding a reply-routing protocol and an IPC round-trip per event; retained only as an escape hatch if in-proc resolution proves unstable in practice.

#### 2.1.3 NSWorkspace reveal path

`activateFileViewerSelecting(_:)` cannot be redirected. Mitigation stack, in order:

1. *(post-v1, opt-in)* Tier 3 mirroring catches the resulting Finder window (§2.1.1). In v1, a reveal that lands in real Finder opens a real Finder window — documented seam (§4.1).
2. We evangelize/document a direct integration: apps that check `NSWorkspace.shared.urlForApplication(toOpen: URL(fileURLWithPath:"/", isDirectory:true))` and use `open -R`-equivalent LS calls will reach us via the `public.folder` handler.
3. `bevel://reveal?path=` (§3.3) as the recommended explicit integration for third parties.

- **INT-6:** Bevel MUST register as viewer for `public.folder` and `public.volume` UTIs and handle Launch Services `odoc`/`GURL` open events for them.

#### 2.1.4 Becoming the login shell

Covered operationally in `02-macos-platform.md`; the interop-relevant key is `defaults write com.apple.loginwindow Finder /Applications/Bevel.app` (per-user), making loginwindow launch us instead of Finder at login. Coexist mode (`02-macos-platform.md` §2.2) keeps real Finder alive-but-hidden regardless; Tier 3 (post-v1) builds on that hidden Finder. `bevelctl register --login-shell` and `--restore-finder` own both directions (INT-4).

### 2.2 Linux

#### 2.2.1 `org.freedesktop.FileManager1`

The freedesktop File Manager Interface is the reveal mechanism used by browsers (Firefox/Chromium "Open containing folder"), Electron's `shell.showItemInFolder`, and the XDG `OpenURI` portal backend.

- Bus: session bus, well-known name **`org.freedesktop.FileManager1`**, object path **`/org/freedesktop/FileManager1`**, interface **`org.freedesktop.FileManager1`**.
- Methods: `ShowFolders(as uris, s startup_id)`, `ShowItems(as uris, s startup_id)` (reveal), `ShowItemProperties(as uris, s startup_id)`.
- **INT-7:** Bevel MUST own this name whenever it runs (shell mode *or* file-manager-only mode), acquiring with `DBUS_NAME_FLAG_ALLOW_REPLACEMENT | DBUS_NAME_FLAG_REPLACE_EXISTING` so we win against a lingering Nautilus and cede gracefully on exit. DBus activation file `org.freedesktop.FileManager1.service` (in `$XDG_DATA_DIRS/dbus-1/services/`) points at `bevel --gapplication-service`-style headless start so reveals work before first window. `startup_id` is honored for startup-notification/`xdg-activation` focus-stealing tokens (X11 `_NET_STARTUP_ID`, Wayland `xdg_activation_v1`).
- Implementation: C# via Tmds.DBus.Protocol in the core process (no native helper needed; DBus is safe managed territory).

#### 2.2.2 Desktop entry + MIME registration

- Ship `pl.ikari.bevel.filemanager.desktop` with `MimeType=inode/directory;x-scheme-handler/file;` — registered via `xdg-mime default pl.ikari.bevel.filemanager.desktop inode/directory`. This routes `xdg-open ~/Documents`, GIO `AppInfo` folder launches, and most "Open folder" buttons.
- Also register `x-scheme-handler/trash` and handle `trash:///` URIs (GIO trash spec) so "Open trash" from other apps works; computer:/// and network:/// are **not** claimed in v1 (GVFS-specific, low traffic).
- **INT-8:** `bevelctl register --linux-default` performs both `xdg-mime` calls and name acquisition setup; `--unregister` restores the previously recorded default (queried first via `xdg-mime query default inode/directory` and stored).

#### 2.2.3 Portals

Flatpak'd apps reveal via the `org.freedesktop.portal.OpenURI.OpenDirectory` portal, whose backend (xdg-desktop-portal-gtk/-kde) calls FileManager1 — so INT-7 covers it transitively. When Bevel later *is* the Wayland session (see compositor position in `04-linux-platform.md`), we ship our own `xdg-desktop-portal-bevel` backend; out of scope for v1.

### 2.3 Windows

Explorer replacement is registry-supported (`HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon\Shell` = `Bevel.exe`, falling back to `HKLM` machine-wide) — details in `03-windows-platform.md`. The interop hazards are all in *file-manager association*:

**Hazard inventory (things we deliberately do NOT do):**

| Temptation | Why it breaks the system |
|------------|--------------------------|
| Changing default verb on `HKCR\Folder\shell` or `HKCR\Directory\shell` | Hundreds of installers/apps `ShellExecute` folder paths expecting Explorer semantics; the default `open` verb uses `DelegateExecute` CLSID `{11dbb47c-a525-400b-9e80-a54615a090c0}` (ExecuteFolder) and replacing it breaks Explorer windows, some file dialogs, and `shell:` moniker resolution when the user switches back |
| Shadowing `explorer.exe` (App Paths or image-file-execution) | Apps invoke `C:\Windows\explorer.exe /select,<path>` by absolute path; IFEO hijack of explorer.exe is malware-signature territory and breaks the desktop when Bevel isn't running |
| Taking `Drive` progid | Same blast radius as `Directory` |

**What we DO (all per-user, HKCU):**

1. Add a *non-default* verb: `HKCU\Software\Classes\Directory\shell\openinbevel` (`"Open in Bevel"`, `command` = `"...\Bevel.exe" --open "%1"`), and the same under `Folder\shell` and `Drive\shell`. Users get a context-menu entry inside real Explorer without hijacking defaults.
2. When Bevel **is** the registered Winlogon shell (Explorer not running), we handle folder activation natively: `ShellExecute(open, <folder>)` resolves through the default verb whose `DelegateExecute` fails without Explorer's broker and falls back to the command line; we install, *only while registered as shell and only in HKCU*, `Directory\shell\open\command` = Bevel.exe with `DelegateExecute` cleared — and restore the original values on unregister (values snapshotted per INT-4). This is the one sanctioned default-verb touch, gated on shell registration.
3. `SHOpenFolderAndSelectItems` (the Win32 reveal API) works by talking to the live `IShellWindows` (CLSID `{9BA05972-F6A8-11CF-A442-00A0C90A8F39}`) registry of open Explorer windows. When Explorer is absent, shell32 launches a new Explorer window. **Decision (v-Windows):** Bevel's Windows build registers an out-of-proc `IShellWindows` replacement in the ROT the way Explorer-alternative projects (e.g., Open-Shell research, cascadia-era litter) have prototyped, so `SHOpenFolderAndSelectItems` resolves to us; if this proves unstable it degrades to hazard-free behavior: real Explorer opens as a *file-manager app* (perfectly functional non-shell process). Windows is post-v1; this is a design position, not a v1 commitment.

- **INT-9:** All Windows registration is per-user (HKCU) and snapshot-restored on unregister. No HKLM writes except optional machine-wide shell registration by explicit admin action.

---

## 3. Outbound automation: scripting Bevel

### 3.1 Canonical command model

Every automation surface (CLI, URL scheme, AE helper, DBus, COM) converges on one C# service in the core process:

```csharp
namespace Bevel.Interop;

public interface IShellAutomation   // registered in DI; single-threaded dispatch onto UI scheduler
{
    Task<RevealResult> RevealAsync(IReadOnlyList<BevelPath> items, RevealOptions opts, CancellationToken ct);
    Task<WindowRef>    OpenAsync(BevelPath containerOrFile, OpenOptions opts, CancellationToken ct);
    Task               SelectAsync(WindowRef window, IReadOnlyList<BevelPath> items, CancellationToken ct);
    Task<BevelPath>    MakeAsync(ContainerRef parent, NewItemKind kind, string? name, CancellationToken ct);
    Task               DeleteAsync(IReadOnlyList<BevelPath> items, DeleteMode mode /*Trash|Permanent*/, CancellationToken ct);
    Task<IReadOnlyList<BevelPath>> DuplicateAsync(IReadOnlyList<BevelPath> items, ContainerRef? target, CancellationToken ct);
    Task<BevelStateSnapshot> QueryAsync(AutomationQuery query, CancellationToken ct);   // windows, selection, properties
    Task SetAsync(AutomationTarget target, AutomationProperty prop, Variant value, CancellationToken ct);
}
```

`BevelPath` is the virtual-path type from `06-file-manager.md` (wraps file URLs plus virtual locations like `trash:`, `desktop:`, `computer:`). Native helpers reach this interface over the IPC transport defined in `01-architecture.md`; in-proc surfaces call it directly.

- **INT-10:** Automation calls are authorization-scoped: local same-user callers only (CLI socket/pipe is user-permission 0600; URL scheme invocations that perform *destructive* verbs — `delete`, `set` — require a user confirmation prompt unless invoked from an allow-listed app).

### 3.2 CLI — `bevelctl`

Single self-contained binary (also `bevel` symlink subcommands), talking to the core over the local IPC socket. Verb surface (v1):

```
bevelctl reveal <path>... [--new-window]
bevelctl open <path> [--view icons|list|details]
bevelctl select <path>...
bevelctl mkdir|delete|duplicate|move ...
bevelctl query windows|selection|version [--json]
bevelctl theme get|set <id>                     # see 05-theming.md
bevelctl register|unregister [--login-shell|--linux-default|--windows-shell] [--status]
bevelctl permissions status [--json]            # feeds §5 matrix, used by onboarding (07-…)
bevelctl translate-script <file>                # macOS: retarget Finder AppleScripts (§2.1.1)
bevelctl doctor                                 # runs the degradation matrix live, prints failures
```

Exit codes: `0` ok, `2` bad args, `3` shell not running (verbs that require it), `4` permission missing (prints which, and the grant command), `5` target not found. `--json` on every query verb for machine consumption.

### 3.3 URL scheme — `bevel://`

Registered per platform (macOS `CFBundleURLTypes`; Linux `x-scheme-handler/bevel` in the desktop entry; Windows `HKCU\Software\Classes\bevel\shell\open\command`).

Grammar:

```
bevel://reveal?path=<url-encoded-posix-path>[&path=...]
bevel://open?path=<path>[&view=details]
bevel://search?query=<q>&scope=<path>            # opens file manager search (05-…)
bevel://settings[/<page>]                        # Bevel settings UI
bevel://theme?set=<theme-id>
```

- **INT-11:** `bevel://` handles only non-destructive verbs (reveal/open/search/settings/theme). Destructive verbs are CLI/native-automation only. Rationale: URL schemes are invocable by any web page; we refuse the entire class of confirmation-fatigue attacks rather than prompt.
- All `path` parameters are canonicalized and must resolve inside the user-visible namespace; `file://` UNC/remote translation per `06-file-manager.md`.

### 3.4 Per-platform native automation of Bevel

| Platform | Surface | Contents |
|----------|---------|----------|
| macOS | AppleScript/JXA via the sdef in §2.1.2 — the *same* suite serves Finder compat and first-party scripting; plus Shortcuts actions (App Intents in the native helper) for Reveal/Open/New Folder in a later milestone | `tell application "Bevel" to reveal POSIX file "/tmp/x"` |
| Linux | DBus interface **`pl.ikari.Bevel1`** at `/pl/ikari/Bevel1`, methods mirroring `IShellAutomation` (`Reveal(as,a{sv})`, `Open`, `Query`, …), signals `WindowOpened`, `SelectionChanged`; introspectable, documented XML shipped in `/usr/share/dbus-1/interfaces/` | `busctl --user call pl.ikari.Bevel1 ...` |
| Windows | Post-v1: COM local server exposing `Shell.Application`-*inspired* (not identical — we do not squat Explorer's `Shell.Application` progid) `BevelApp.Automation` dual interface for VBScript/PowerShell | `New-Object -ComObject BevelApp.Automation` |

**Decision:** we do not attempt to emulate Explorer's `Shell.Application` COM object or Finder's *entire* dictionary; compat targets are the empirically common subsets. Rejected alternative: full-surface emulation — unbounded effort, and the long tail is used by system-management tooling that should talk to the real OS anyway.

---

## 4. Boundary document: what stays native

Explicit non-goals, the visible seams they create, and mitigations. This table is the reference for support/FAQ and for `07-shell-ux.md` messaging.

### 4.1 macOS

| Native feature (kept) | Seam the user notices | Mitigation |
|---|---|---|
| **Open/Save dialogs (`NSOpenPanel`/`NSSavePanel`)** in every third-party app | Dialogs look like macOS, not the active Bevel theme; sidebar favorites are Finder's, not Bevel's | Keep Bevel favorites synced *into* Finder sidebar favorites (`sfltool` / SharedFileList API) so the dialog sidebar matches; document as known seam |
| **Spotlight** | Spotlight "Show in Finder" result action opens real Finder in v1 (and post-v1 whenever Tier 3 is off) | Bevel's own search (05) as primary path; Tier 3 mirroring once it ships (post-v1, opt-in) |
| **Mission Control / Spaces / window management** | Our desktop/taskbar are windows *within* Spaces; Mission Control shows them as such | None planned; documented |
| **Dock** (used as work-area reservation shim per `02-macos-platform.md`) | Dock is visible (or auto-hidden) alongside Bevel taskbar | Default: Dock auto-hide + Bevel taskbar reserves via shim; user chooses in settings |
| **Notification Center, Control Center** | Slide-overs use macOS styling | None; tray capture (04) covers menu-bar extras only |
| **Quick Look** | Preview panel is native-styled | We *invoke* it (`QLPreviewPanel` via helper) from Bevel selection with Space, same as Finder — feature works, styling is native |
| **AirDrop, iCloud Drive UI, Continuity** | AirDrop browser is Finder's; iCloud sync status icons limited | Reveal iCloud paths fine (they're real dirs); AirDrop via share sheet (`NSSharingService`) invoked from Bevel context menu |
| **Trash *behavior*** | Restore is asymmetric (`06-file-manager.md` FM-110): Finder's "Put Back" metadata is **not publicly readable**, so items trashed by Finder or other apps show "unknown" origin in Bevel with Restore disabled; items trashed *by Bevel* restore to origin via our own sidecar map (and remain Put Back-able in Finder, since we trash via `trashItem`) | Clear "Original location unknown" UI (06 Risk 3) + FAQ entry; no full-fidelity claim — Apple provides no API for Finder's put-back metadata |
| **Desktop icons on secondary spaces full-screen apps** | Bevel desktop window layering differs from Finder's desktop in edge cases | Tracked in `02-macos-platform.md` |

### 4.2 Linux

| Native (kept) | Seam | Mitigation |
|---|---|---|
| GTK/Qt file chooser dialogs | Themed by GTK/Qt, not Bevel | Ship optional GTK theme + Kvantum/Qt theme approximating active Bevel theme (stretch goal, `05-theming.md`) |
| NetworkManager/polkit agents/IBus | Native dialogs | We *launch* standard agents (e.g. `polkit-gnome-authentication-agent-1`) as session children so auth prompts still appear |
| Display/power settings | We don't ship a control center in v1 | Launch `gnome-control-center`/etc. from our start menu |

### 4.3 Windows (post-v1 positions)

| Native (kept) | Seam | Mitigation |
|---|---|---|
| Common file dialogs (`IFileOpenDialog`) | Native styling; "This PC" namespace is Explorer's | None; namespace is shell32's, works without Explorer process |
| UWP surfaces (Action Center, volume flyout) | Some require Explorer/ShellExperienceHost | Launch ShellExperienceHost-dependent surfaces best-effort; document gaps |
| Win+X / system hotkey handlers owned by Explorer | Missing when Explorer absent | Bevel reimplements the common ones (Win+E → Bevel file manager) |

- **INT-12:** Every seam in §4 tables MUST have a corresponding FAQ entry and, where a mitigation is a setting, a settings-UI toggle.

---

## 5. Degradation matrix

What works with each permission/registration absent. "FM" = file manager. Enforcement: `bevelctl doctor` runs these as live probes; onboarding (07) drives grants from the same data.

### 5.1 macOS

| Missing grant/registration | Still works | Broken/degraded | In-product mitigation |
|---|---|---|---|
| **Accessibility (TCC)** | FM, desktop, own windows, taskbar *list of own knowledge via CGWindowList (names only)* | Tier 3 Finder mirroring (post-v1); window management actions on other apps (minimize/focus from taskbar); menu-bar auto-hide coordination | Taskbar falls back to activate-only (via `NSRunningApplication.activate`); banner linking to System Settings pane |
| **Screen Recording (TCC)** | Everything except tray pixel capture | Systray icon *images* (04) — items listed by title only with generic glyphs | Text-only tray mode; re-prompt flow |
| **Automation → Finder (TCC)** | Tier 1; FM fully | Tier 3 (post-v1; can't close/query Finder windows) | Detect `AEDeterminePermissionToAutomateTarget` result; disable compat mode toggle with explanation |
| **Full Disk Access** | Home-dir browsing | Browsing Mail/Safari containers, Time Machine metadata, others' TCC-protected dirs | Standard per-folder TCC prompts still grant piecemeal; FDA offered in onboarding |
| **`loginwindow Finder` key not set (not login shell)** | Everything as a regular app: FM, our windows, tray capture, Tier 1 (and Tier 3 when enabled, post-v1) | Auto-start at login as shell; real Finder owns desktop icons & default desktop | "App mode": Bevel desktop window optional-off; explicit `register --login-shell` upsell |
| **`public.folder` LS handler not default** | Reveal via FileManager1-equivalents (`bevel://`, CLI, AEs) | `open <dir>` from Terminal & LS folder opens go to Finder | `bevelctl register` fixes; doctor flags |

### 5.2 Linux

| Missing | Still works | Broken/degraded | Mitigation |
|---|---|---|---|
| `org.freedesktop.FileManager1` name (lost race or DBus down) | FM by direct launch | Browser/Electron "show in folder" goes elsewhere or fails | Retry acquisition with REPLACE_EXISTING; doctor probe |
| `inode/directory` default | Reveal via FileManager1 (browsers) still hits us | `xdg-open <dir>` opens old FM | `bevelctl register --linux-default` |
| Not session shell (running inside GNOME/KDE) | FM + FileManager1 + tray SNI host may conflict with existing tray | Desktop icons/taskbar off by default (host DE owns them) | "App mode" autodetect: if `XDG_CURRENT_DESKTOP` ≠ shell, start FM-only |
| No layer-shell (non-wlroots Wayland, e.g. GNOME Mutter) | FM, everything app-mode | Taskbar/desktop as proper shell surfaces | Per `04-linux-platform.md` position (X11 first; Wayland shell only on layer-shell compositors or bundled compositor) |

### 5.3 Windows

| Missing | Still works | Broken/degraded | Mitigation |
|---|---|---|---|
| Winlogon `Shell` not set | Everything as app incl. FM + `openinbevel` verbs | Tray takeover contested with Explorer; taskbar duplicates Explorer's | App mode: hide taskbar by default |
| Running alongside Explorer | FM, theming | `TaskbarCreated` tray takeover causes icon re-registration churn | Tray capture off unless shell-registered (04) |

- **INT-13:** No missing permission may crash a feature; every degraded state in §5 MUST render an affordance (banner/toggle/doctor entry) explaining the cause and grant path.

---

## Risks

1. **Tier 3 flash-and-mirror is inherently racy.** AX notification latency vs. Finder window paint means visible flashes; worst case, a Finder window escapes mirroring (AXObserver dropped notification) and the user sees two file managers. This raciness is a core reason Tier 3 is **post-v1 and opt-in** (INT-5): the 1-ew research spike (`09-engineering-plan.md` §3) must measure escape rate and flash duration before the post-v1 productization milestone is committed. Design mitigations when it ships: watchdog re-scan of Finder windows every 2 s while compat mode is on; kill-switch toggle. Residual risk deferred along with the feature.
2. **macOS point releases can silently change AE/AX/TCC behavior.** The whole §2.1 stack needs a per-OS-release compat test pass (CI on multiple macOS versions, see `09-engineering-plan.md`). Apple could also start requiring entitlements for `NSAppleEventManager`-received events' semantics changes — low likelihood, high impact.
3. **AE four-char-code collisions.** Reusing Finder's terminology codes in our sdef could confuse tools that hardcode `typeApplicationBundleID`+code pairs, or trip App Review if we ever distribute via MAS (we won't; direct distribution + notarization only — decision recorded in `09-engineering-plan.md`). Needs an early spike validating that Script Editor/Script Debugger handle our dictionary cleanly.
4. **Windows default-verb touch (§2.3 item 2) is the highest-blast-radius write we make anywhere.** A crash-loop while shell-registered with the verb overridden could leave a user unable to open folders. Mitigation: the registration snapshot lives in a plain `.reg` restore file + `bevelctl unregister` is callable from our safe-mode boot (01) and from a plain cmd.exe; also auto-restore on clean uninstall and on 3 consecutive startup crashes.
5. **FileManager1 name contention** with the host DE's file manager in "app mode" can ping-pong (`REPLACE_EXISTING` both ways). Mitigation: in app mode we acquire without `REPLACE_EXISTING` and only take the name if free; only shell mode fights for it.
6. **Corpus-based compat target may over-index on hobbyist scripts** and under-represent commercial apps' AE usage (which we can't easily harvest) — doubly so now that the v1 corpus is ~40 snippets (§2.1.2). Mitigation: in v1, log (locally, opt-in) AE codes that reach our own suite unhandled; when Tier 3 ships post-v1, extend the instrumentation to unhandled AE codes reaching Finder. Both feed the post-v1 corpus growth campaign.
7. **URL-scheme freeze**: the product name (**Bevel**) and its URL scheme (**`bevel://`**) are decided (2026-07-04 owner decision), which also clears the old `shell://`-vs-Windows-`shell:`-moniker collision. Residual risk is only a *late* post-decision rename: shipping then renaming a scheme breaks integrators, so `bevel://` is frozen as of this decision and MUST NOT change before or after public beta.
8. **The AE object-specifier resolver lives in the UI process with no crash isolation.** Because Apple Events can only be delivered to Bevel.app itself (§2.1.2), a malformed or adversarial object specifier is parsed in the same process as the user's desktop. Mitigation: the resolver is pure managed code (worst realistic failure is an exception, not memory corruption), all handlers are exception-wrapped to `errAEEventNotHandled`, and the corpus doubles as a fuzz seed set. The `AEFlattenDesc`-forwarding escape hatch (§2.1.2) exists if this proves wrong in practice.

## Open questions

1. **Reverse-DNS identifiers.** The product name (**Bevel**) and URL scheme (**`bevel://`**) are decided and frozen (2026-07-04; Risk 7). Still open is the reverse-DNS *identifier* derivation: the bundle id (`pl.ikari.bevel` used as placeholder throughout), the DBus name `pl.ikari.Bevel1`, and the `pl.ikari.bevel.filemanager.desktop` entry have **not** yet been re-cut to the final name. Owner to confirm the final reverse-DNS strings before beta; whatever is chosen must be stable at launch (a later change is a breaking change for integrators the same way a scheme rename would be).
2. **Does M4's 7–10 ew hold for a hand-written object-specifier resolver?** §2.1.2 now makes explicit that the resolver (element access, restricted `whose` filters, coercions) is from-scratch C# with no Cocoa Scripting assist. 09's M4 estimate predates that framing; owner should re-estimate. If it doesn't fit, the pressure valve is cutting `whose` filter support from v1 (element name/index access covers the six core verbs and the app-emitted reveal/open/select traffic), not growing the corpus bar. *(The former Q2 — Tier 3 default state — is decided as opt-in/default-OFF per INT-5; any future default-ON revisit is owned by `02-macos-platform.md` Open Q4.)*
3. **`NSWorkspace.activateFileViewerSelecting` interception — DECIDED (held, 2026-07-04): never inject.** A private-API route (posing/`__NSWorkspaceFileViewer` styles) would mean dyld interposing in *other apps'* processes; this is a firm red line, **not** an open question and **not** an opt-in power-user mode. Bevel NEVER injects code into third-party processes to intercept `NSWorkspace` reveals. Reveals are routed exclusively via the Apple Events suite (§2.1.2) plus Finder suppression/mirroring (Tier 3, post-v1, opt-in), and any exotic `NSWorkspace` path that cannot be caught that way **soft-fails** to a real Finder window (documented seam, §2.1.3 / §4.1) rather than being chased with injection.
4. **Do we squat Explorer's `Shell.Application` COM progid on Windows** when running as registered shell (would make thousands of scripts work) or stay with `BevelApp.Automation` (spec's current safe position)? Needs a dedicated hazard spike when Windows work starts.
5. **Shortcuts/App Intents scope for v1 macOS** — the AE suite covers power users; is a Shortcuts action set (Reveal, Open, New Folder) worth the native-helper surface in v1, or milestone 2?
6. **Logging of unhandled/unknown AE verbs — DECIDED (2026-07-04): local-only.** Telemetry is fully offline in v1, so the Risk 6 mitigation logs unhandled AE codes reaching our own suite (and, post-v1, reaching Finder) to an **on-device** log only — nothing is ever sent off-device. Remaining sign-off is narrow: whether that local log is on by default or opt-in, given it captures user-automation verb content; default is opt-in until owner decides otherwise. No off-device transmission is on the table in v1.
