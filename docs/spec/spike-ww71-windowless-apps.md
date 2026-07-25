# SPIKE bevel-ww71 — representing running-but-windowless apps in the taskbar

**Status:** spike complete · deliverable is understanding + a prototype detector + a recommendation (NOT the feature).
**Recommendation:** **Option B — app-presence buttons**, built on the AX signal, with Option C (`bevel-xrx6`) as the eventual home for a unified app-level model.

---

## 1. The problem

Bevel's taskbar is **window-centric**: one button per foreign top-level window. A macOS app can be
RUNNING with **zero windows** — you closed all its windows but didn't `Cmd-Q`; or a document app
(Preview/TextEdit) is between documents; or a media app (Music/TIDAL) is just playing. macOS shows a
running dot under the Dock icon and clicking it fires `applicationShouldHandleReopen` to make a
window. Today such an app has **no taskbar button** → it is invisible and unreachable from Bevel's bar.

## 2. How enumeration works today (and where an app/window distinction would live)

Pipeline (macOS), window-by-window end to end:

```
CGWindowList (.optionAll, layer 0)  ──┐
                                       ├─ describe(entry, axMap, frontmost)  →  Bevel_Helper_V1_TaskbarWindow
AX kAXWindows per PID (correlateAX) ──┘        (helper: WindowServiceImpl.swift)
        │                                            │  ListWindows (snapshot) + Changes (deltas), gRPC
        ▼                                            ▼
  ForeignWindow  ◄── Map(TaskbarWindow)  ── MacOSWindowManager  (Bevel.Pal.MacOS)
   (PAL DTO)                                         │  IWindowManager events: WindowOpened/Closed/Changed/ForegroundChanged
        ▼                                            ▼
  ShellModel.Windows  (ObservableCollection<TaskItemViewModel>)  ── one entry per window
        ▼
  TaskbarView / TaskGroupViewModel  (grouping by app already exists)
```

Key facts established by reading the code:

- **`WindowServiceImpl.describe()`** (`native/helper-macos/Sources/BevelHelper/WindowServiceImpl.swift`)
  is the single chokepoint. It already gates on **`NSRunningApplication(pid).activationPolicy == .regular`**
  (drops `.accessory` menu-bar agents and `.prohibited` daemons), reads `kAXMinimized`, drops
  zero-frame / no-title / offscreen-no-AX ghosts, and excludes Bevel's own shell chrome
  (`isShellChrome`, `isBevelTransient`, keyed off `parentPID`).
- **The unit of the entire pipeline is a `CGWindowID`.** `TaskbarWindow.windowID` is `String(cgID)`;
  `ForeignWindow.Id` is that same string; `TaskItemViewModel.Id` too. There is **no app-level entity
  anywhere** — an app with no CGWindow simply never enters the pipeline.
- The helper already knows about apps independently of windows: it holds per-PID `AXObserver`s and an
  **`NSWorkspace.didLaunchApplicationNotification`** hook (`startAXRunLoopThread`). So "an app launched
  / is running" is already an observable event inside the helper — it's just never turned into a
  taskbar entry when the app has no window.
- **`IAppEnvironment.GetRunningAppsAsync()` / `AppLaunched` / `AppTerminated`** already exist in the
  PAL (`Bevel.Pal.Abstractions/Interfaces.cs`) with a `RunningApp(AppId, DisplayName, ProcessId)` DTO —
  a ready-made seam for "running apps" that is currently unused by the taskbar.

**Where the distinction would live:** the natural insertion point is a new **app-presence stream in the
helper**, computed right next to `reconcile()` (it already enumerates PIDs and their AX windows every
500 ms), surfaced through the PAL as a distinct entry type, and merged in `ShellModel` alongside
`Windows`. Detail in §5.

## 3. The detector (prototype) and its output

`native/helper-macos/diagnostics/windowless-apps.swift` — standalone, `swift windowless-apps.swift`,
no helper rebuild. It computes: **regular-activation-policy running apps** MINUS **apps that own a real
window**. The load-bearing lesson it encodes:

> **CGWindowList `.optionAll` cannot answer this question. AX can.**
> `.optionAll` layer-0 reports 4–8 non-zero-frame windows *per app even for apps with no real window*
> (off-screen / other-Space / helper scaffolding). The authoritative signal — the exact one
> `describe()` already gates on — is **`AXUIElementCopyAttributeValue(app, kAXWindows)`**. The detector
> prints both columns so the gap is visible; the AX column is the truth. This is why the detector (and
> the feature) must live in the **helper**, which has Accessibility permission.

**Actual output on this machine (2026-07-26), 24 regular apps running:**

- **TRULY WINDOWLESS (AX 0 windows) — 13 of 24 (54%)**: Audio MIDI Setup, Cursor, Jump Desktop, Kimi,
  Music, Pinokio, Preview, Safari, Sensei, Sublime Text, TextEdit, TIDAL, Warp.
  (Every one showed CG `.optionAll` = 4–6 windows but AX = 0 — pure scaffolding.)
- **HIDDEN WINDOWS ONLY (AX>0, off-screen) — 2**: Finder (1 window, another Space), Chrome (1 minimized)
  → these already have a taskbar button and must **not** be duplicated.
- **ON-SCREEN — 9**: AyuGram, Beeper, Blender, Claude, Firefox, iTerm2, qBittorrent, Thunderbird, Zen.

## 4. Frequency assessment

**This is a real, daily, high-frequency situation, not an edge case.** At a routine moment, **more than
half** the running Dock apps were windowless. The population is exactly the apps people leave running
all day and dip into occasionally:

- **Media / background**: Music, TIDAL, Sensei, Pinokio — run for hours with no window.
- **Document apps between documents**: Preview, TextEdit — the canonical `reopen` case.
- **Browsers/editors with all windows closed but not quit**: Safari, Chrome, Cursor, Sublime, Warp.
- **Utilities opened once**: Audio MIDI Setup, Jump Desktop.

Under Bevel-as-shell (no macOS Dock, or the Dock auto-hidden via `IDockController`, bevel-3kz), these 13
apps are **completely unreachable** — the user can't even see they're running. That is a materially worse
experience than the OS default, which argues for acting.

## 5. Recommendation — Option B, with a path to C

**Do B now; design it as the first slice of C.** B is the smallest change that removes the "invisible &
unreachable" cliff; C (`bevel-xrx6`) is where a unified pinned/running/window-group app entity belongs,
and B's app-presence entity is a natural down payment on it. Do **not** do A (do-nothing) — the frequency
evidence is too strong. Do **not** jump straight to the full C model — it entails pinning, usage seeding,
and a per-platform `IUsageProvider`, which is a much larger surface than removing this specific cliff.

### Wiring sketch (helper → PAL → taskbar VM)

1. **Helper — new app-presence signal** (`WindowServiceImpl`, alongside `reconcile()`):
   - Each poll, compute `regularApps = NSWorkspace.runningApplications where activationPolicy == .regular`,
     minus `getpid()`/`parentPID`, minus any PID present in the kept-window set (`windowStore` values'
     PIDs) — i.e. the AX-authoritative "has a real window" set the poll already builds.
   - The remainder = windowless-running apps. Emit them on the existing **Changes** stream as a new
     `WindowChange` variant (add `kind = APP_PRESENCE` and an `app` message: bundle id, name, icon PNG,
     pid), or a sibling stream. Reuse `didLaunch`/`didTerminate`/`didActivate` hooks for instant deltas;
     the 500 ms poll is the backstop, exactly as for windows.
   - Key the entry by **bundle id** (stable across the app's window lifecycle), not a CGWindowID.
2. **PAL** (`Bevel.Pal.Abstractions`): add an `AppPresence(AppId, DisplayName, IconPng, Pid)` DTO and
   either extend `IWindowManager` with `AppPresenceAppeared/Disappeared` events or (cleaner) surface it
   through the already-existing `IAppEnvironment.AppLaunched/AppTerminated` + a
   `GetWindowlessRunningAppsAsync()`. `MacOSWindowManager.Map` gets a sibling `MapPresence`.
3. **Taskbar VM** (`ShellModel`): add a parallel `ObservableCollection` (or a common
   `ITaskbarItem` the projector already references) for app-presence buttons, reconciled the same way
   `Windows` is. Click handler calls `ActivateAsync` — the helper's `activateWindow` already falls back
   to `NSRunningApplication.activate()` for apps with no window AX element (the Apple Music path), which
   is exactly `applicationShouldHandleReopen`. Render dimmer, icon-only, no title (per the bead).

### Edge cases (these shape the design)

- **Activation policy filter**: only `.regular`. `.accessory` menu-bar agents are excluded — several of
  them already appear in Bevel's **systray mirror** (`TrayServiceImpl`), so surfacing them here too would
  double-represent them. The helper's `describe()` and `enqueueAXEvent` already apply this exact gate;
  reuse it so the two paths can't drift.
- **Exclude Bevel's own processes**: filter `getpid()` and `parentPID` (the split shell runs taskbar/
  desktop as separate `.accessory` processes, already handled by the policy filter, but the headless
  core is `parentPID` and must be excluded explicitly).
- **Merge transition (the critical one)**: when a windowless app opens a window, its window flows in as a
  normal `TaskbarWindow` **and** it drops out of the windowless set in the same poll. Because the app
  button is keyed by **bundle id** and the window button by **CGWindowID**, naive handling shows both for
  one frame → a duplicate. Design rule: **the projector must treat an app-presence entry as suppressed
  whenever a real window button exists for the same bundle id.** Since grouping-by-app already exists
  (`TaskGroupViewModel`/`TaskbarGrouping`), the clean model is: the app-presence entry is the
  *zero-window state of that app's group* — when the group gains its first window, the same visual slot
  becomes the window button; when the last window closes but the app stays running, it reverts to the
  presence state. That makes the transition an in-place update of one group, not an add+remove race.
- **Reverse transition (last window closed, app still running)**: symmetric — on the poll where the app
  leaves `windowStore` but is still in `runningApplications`, it re-enters the windowless set. The
  bundle-id-keyed group survives, so the button stays put instead of vanishing then reappearing.
- **AX permission**: the detector proved the signal needs Accessibility; the helper already has it, so
  no new permission is required.

## 6. Files

- `native/helper-macos/diagnostics/windowless-apps.swift` — the prototype detector (standalone).
- This document.
No production code was changed (spike).
