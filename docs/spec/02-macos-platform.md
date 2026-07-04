# macOS Platform Integration

## Summary

macOS is the v1 target and the hardest platform this project touches: Apple gives no supported "replace the shell" story, so every pillar (desktop-level windows, a taskbar that manages other apps' windows, a system tray that mirrors native menu-bar items, Finder interop) is built on techniques that live one AppKit revision away from breaking. This chapter specifies how the C# shell — Avalonia over the Avalonia.Native backend — reaches down to AppKit/CoreGraphics/ScreenCaptureKit through a crash-isolated Swift/ObjC helper process (`BevelHelper.app`, see [01-architecture.md](01-architecture.md) for the PAL/IPC contract), and it takes firm positions on the fragile parts.

Headline decisions:
- **We do NOT replace `Finder.app` via the loginwindow `Finder` defaults key in v1.** v1 is **Coexist-only**: we ship as an ordinary login-item agent that presents desktop + taskbar + tray on top of a *hidden* Finder (`CreateDesktop=false`, Finder alive). The true takeover — the loginwindow `Finder`-key replacement — is a **gated opt-in "Advanced: Replace Finder" toggle that ships only in a later v1.x**, and only *after* the loginwindow viability spike (§2) proves the recovery paths hold; full replace-by-default is the long-term north star, not v1. Rationale below (§2).
- **Systray mirroring follows the Ice playbook** ([jordanbaird/Ice](https://github.com/jordanbaird/Ice)): `CGWindowListCopyWindowInfo` to discover status-item windows, ScreenCaptureKit to capture their pixels, and `CGEvent` synthesis to forward clicks. Reclaiming the native strip uses the two mechanisms that actually exist: the user-consented system-wide menu-bar auto-hide setting, and Ice's real item-relocation technique (synthesized ⌘-drag) for individual third-party items (§5.4). Without user consent to either, our tray is a duplicate, not a replacement. macOS 14.0 (Sonoma) is our floor because SCK's per-window capture and the modern status-item window semantics require it.
- **Finder Apple Events compat ships in tiers**, and v1 only guarantees **Tier 1** (our own scripting suite mirroring Finder terminology) plus `NSWorkspace` reveal. Bundle-id shadowing (Tier 2) and headless-Finder mirroring (Tier 3) are experiments, off by default.
- **No work-area reservation API exists on macOS.** Default: Dock auto-hidden (consented) + AX-based overlap mitigation ("window nudging"); a visible minimum-size Dock as a reservation shim is an opt-in strict mode with documented downsides (§9). An auto-hidden Dock reserves nothing, so "shim" and "auto-hide" are mutually exclusive — we do not fight `NSScreen.visibleFrame`.

All native calls cross the IPC boundary defined in [01-architecture.md](01-architecture.md) (gRPC over Unix domain sockets, shared-memory pixel side channel). UI/theming is in [05-theming.md](05-theming.md); the file manager's Finder-facing behavior is in [06-file-manager.md](06-file-manager.md) and the compat surface in [08-os-interop.md](08-os-interop.md); packaging/CI in [09-engineering-plan.md](09-engineering-plan.md).

---

## 1. Component & process topology on macOS

```
┌────────────────────────────────────────────────────────────────┐
│ Bevel.app (C# / .NET 9 / Avalonia.Native, LSUIElement=YES)      │
│  · Desktop window (kCGDesktopWindowLevel+1, all Spaces)         │
│  · Taskbar window (kCGMainMenuWindowLevel-ish, dock-reserved)   │
│  · Start menu / tray popovers (floating panels)                 │
│  · File-manager windows (normal level)                          │
└───────────────┬────────────────────────────────────────────────┘
                │  IPC: gRPC over Unix domain sockets, with a
                │  shared-memory side channel for pixel data;
                │  see 01-architecture.md for contracts + reconnect.
┌───────────────▼────────────────────────────────────────────────┐
│ BevelHelper.app  (Swift/ObjC, separately signed, crash-isolated)│
│  · SCK capture engine (status items, wallpaper thumbnails)      │
│  · CGWindowList enumeration (windows + status items)            │
│  · AXUIElement drivers (window mgmt + status-item menu clicks)  │
│  · CGEvent synthesis (click forwarding, ⌘-drag item relocation) │
│  · Menu-bar reclaim (consented _HIHideMenuBar system toggle)    │
│  · Apple Events server (NSAppleEventManager + Bevel.sdef, §6)   │
│  · TCC-sensitive work lives HERE so a permission-denied or an   │
│    AppKit ABI break crashes the helper, not the shell.          │
└─────────────────────────────────────────────────────────────────┘
```

**Requirement 1.1** — The main shell process MUST run as an `LSUIElement`/`NSApplicationActivationPolicyAccessory` agent (no Dock tile of its own). All Accessibility, Screen Recording, and event-synthesis calls MUST originate in `BevelHelper`, never in the .NET process, so that a TCC denial or a SCK/AX crash is contained (see [01-architecture.md](01-architecture.md) §crash-isolation).

**Requirement 1.2** — `BevelHelper` MUST be independently launchable, restartable, and health-pinged by the shell (heartbeat ≤ 2 s). On helper death the shell degrades gracefully (tray freezes to last-known frames, taskbar window-control buttons grey out) and relaunches with backoff.

**Why two processes and not P/Invoke into AppKit from .NET?** Avalonia.Native already hosts an ObjC runtime in-process, so we *could* dlopen and message AppKit directly. We reject that for the fragile surfaces: (a) TCC prompts attach to the requesting binary — a stable, minimal, separately-notarized helper keeps the permission grant durable across shell updates; (b) ScreenCaptureKit and AX drivers are the two things most likely to hang or crash across macOS point releases, and we do not want them taking the desktop down with them. Non-fragile AppKit calls (window level, collection behavior) are done in-process via `IMacOSTopLevelPlatformHandle` (§3).

---

## 2. loginwindow Finder-key replacement, recovery & uninstall

### 2.1 Background

macOS has no "shell" registry value. The closest lever is the per-user preference read by `loginwindow` that names the Finder bundle. Setting:

```bash
defaults write com.apple.loginwindow Finder /Applications/Bevel.app
```

does **not** stop Finder from launching in modern macOS the way it once did on older systems — on Sonoma/Sequoia/Tahoe `loginwindow` still resurrects `Finder.app` (SIP-protected, `/System/Library/CoreServices/Finder.app`) because numerous system paths (`NSWorkspace`, the "Force Quit" list, `open`, Spotlight reveal) hard-depend on the `com.apple.finder` bundle id being alive. The historical `Finder` defaults key is unreliable-to-inert as a *suppression* mechanism on current OSes.

### 2.2 Decision: hidden-Finder coexistence is v1; full replacement is a gated later-v1.x opt-in

**We do NOT bet v1 on suppressing Finder.** Instead:

1. **Default mode ("Coexist"):** Finder keeps running but is made invisible and inert to the user:
   - Hide all Finder windows and disable desktop icon drawing by Finder:
     ```bash
     defaults write com.apple.finder CreateDesktop -bool false
     killall Finder            # relaunch honoring the pref → no desktop, no icons
     ```
     This is a *supported* Finder preference: Finder stops drawing the desktop/icon layer, which is exactly the layer our desktop window replaces. Finder remains available for Apple Events and `NSWorkspace`.
   - Our desktop window (§3) owns the wallpaper + icon grid; our taskbar owns window management; the real Finder is headless-but-alive, which conveniently enables Tier-3 Apple Events mirroring (§6).

2. **Advanced mode ("Replace Finder") — not in v1; gated for a later v1.x:** an explicit, heavily-warned toggle that additionally attempts loginwindow-key replacement and Finder termination-on-launch. It ships **only after** the loginwindow viability spike (see §2.4 recovery paths and Open questions) demonstrates the recovery paths are bulletproof — v1 ships Coexist-only and does not expose this toggle. It is best-effort, may be neutralized by an OS update, and is documented as such. It never removes SIP protection or touches `/System`.

**Rationale:** `CreateDesktop=false` gives us 95% of the visible win (no competing desktop) with zero SIP fights and full interop preservation, and it is trivially reversible. Fully killing Finder buys us little and breaks `open`, share sheets, and AppleScript targets we actually want (§6). Rejected alternative — *bundle-id shadowing as the primary strategy* (make our app claim `com.apple.finder`): high risk of Gatekeeper/LaunchServices refusal and of poisoning every `NSWorkspace` path system-wide; kept only as a Tier-2 experiment (§6.3).

### 2.3 Install / activation

**Requirement 2.1** — Activation is a single user action ("Make Bevel my desktop"). It MUST:
1. Register `Bevel.app` as a Login Item via `SMAppService.mainApp` (modern replacement for the deprecated `SMLoginItemSetEnabled`); the helper registers as `SMAppService.agent(plistName:)`.
2. Write `com.apple.finder CreateDesktop -bool false` and `killall Finder`.
3. Snapshot every pref it changes into `~/Library/Application Support/Bevel/backup/loginwindow-finder.plist` (see 2.4) *before* mutating, with the prior value and a timestamp.
4. Start the shell's desktop/taskbar/tray.

**Requirement 2.2** — The shell MUST detect whether the current session already has our desktop up (single-instance guard via a named lock in `~/Library/Application Support/Bevel/`), and MUST NOT double-hide Finder.

### 2.4 Recovery & uninstall (this is a safety-critical path)

A desktop shell that can't be cleanly removed is malware-shaped. Uninstall MUST be bulletproof.

**Requirement 2.3 — Reversibility manifest.** Every system mutation (defaults writes, login-item registrations, loginwindow key edits) MUST be journaled to `~/Library/Application Support/Bevel/backup/mutations.json` as `{key, domain, oldValue, newValue, appliedAt}` records *before* application. Uninstall replays them in reverse.

**Requirement 2.4 — One-command restore.** Ship `Bevel.app/Contents/Resources/restore-finder.sh` (also surfaced as a GUI "Restore Finder & Uninstall" button) that, without needing the shell to be running:
```bash
defaults write com.apple.finder CreateDesktop -bool true     # or delete if key was absent
defaults delete com.apple.loginwindow Finder 2>/dev/null || true
# unregister login items:
#   (from the app)  SMAppService.mainApp.unregister()
open -a Finder ; killall Finder 2>/dev/null || true
```
It MUST restore *absent* keys by deleting them, not by writing a guessed default.

**Requirement 2.5 — Failsafe boot escape.** Because a broken shell at login-item level can produce a blank desktop, we MUST provide two out-of-band escapes, documented in-app and in the README:
- **Hold a hotkey during launch** (chord read by the helper's earliest code path, e.g. hold ⌥ for 3 s) → shell starts in "Safe Mode": desktop/taskbar/tray disabled, only a recovery window shown.
- **Terminal recovery:** `~/Library/Application Support/Bevel/restore-finder.sh` runnable from Recovery/SSH/another user. A user who can reach a shell prompt (or Safe Boot → different admin account) can always get Finder back in one command.

**Requirement 2.6** — Uninstall MUST NOT leave the user at a black screen: it re-enables Finder desktop and `killall Finder` so a real desktop repaints before our process exits.

**Open decision recorded:** we do NOT attempt to disable Finder via `launchctl` unload of `com.apple.Finder` — it is SIP/`loginwindow`-managed and reloads; and doing so risks breaking file dialogs. See §Open questions.

---

## 3. Getting Avalonia windows to desktop level

### 3.1 Handle access

Avalonia's macOS backend (`Avalonia.Native`, `libAvaloniaNative.dylib`) exposes the underlying `NSWindow*` through `IMacOSTopLevelPlatformHandle`:

```csharp
// PAL: MacOS/NativeWindow.cs
var top = (TopLevel)window;
if (top.PlatformImpl?.Handle is IMacOSTopLevelPlatformHandle mac)
{
    IntPtr nsWindow = mac.NSWindow;   // NSWindow*
    IntPtr nsView   = mac.NSView;     // content NSView*
    _windowConfigurator.ApplyDesktopLevel(nsWindow);
}
```

`IMacOSTopLevelPlatformHandle` is the documented, supported accessor ([Avalonia docs: native window handles](https://docs.avaloniaui.net/xpf/interop/native-window-handles)); we do NOT reach into `PlatformImpl` internals. Manipulation of the `NSWindow` (level, collection behavior, `ignoresMouseEvents`, `sharingType`) is done by messaging AppKit via a tiny ObjC shim compiled into the PAL (in-process; these calls are neither TCC-gated nor crash-prone). Anything requiring `objc_msgSend` is centralized in `MacOS/AppKitInterop.cs`.

**Known limit of the handle route:** Avalonia.Native creates `NSWindow` subclasses, not `NSPanel`s, and `NSWindowStyleMaskNonactivatingPanel` is honored only by `NSPanel` and only when set at window creation. The handle therefore covers levels/collection-behavior/mouse-transparency, but **cannot** retrofit non-activating-panel semantics onto shell surfaces — see Requirement 3.6 for the backend workstream this forces.

### 3.2 Window levels & roles

**Requirement 3.1** — Window levels (via `-[NSWindow setLevel:]`, values relative to `CGWindowLevelForKey`):

| Shell surface        | Level                                              | Collection behavior                                                              | Notes |
|----------------------|----------------------------------------------------|----------------------------------------------------------------------------------|-------|
| Desktop / wallpaper  | `kCGDesktopWindowLevel` + 1 (below icons)          | `canJoinAllSpaces` \| `stationary` \| `ignoresCycle`                              | Behind everything; never activates. |
| Desktop icon grid    | `kCGDesktopIconWindowLevel`                         | `canJoinAllSpaces` \| `stationary`                                               | Accepts clicks/drag; own child window over wallpaper. |
| Taskbar              | `kCGMainMenuWindowLevel` − 1 (above normal, below menu)| `canJoinAllSpaces` \| `stationary` \| `ignoresCycle` \| `fullScreenAuxiliary`     | Always visible; §9 space-reservation. |
| Start menu / tray flyouts | `kCGPopUpMenuWindowLevel`                     | `moveToActiveSpace` \| `transient`                                               | `NSPanel` semantics — requires the panel-capable backend (Req 3.6). |
| File-manager windows | `kCGNormalWindowLevel` (default)                   | default managed                                                                 | Ordinary app windows; taskbar-listed. |

**Requirement 3.2** — The desktop and taskbar windows MUST set `collectionBehavior` including `NSWindowCollectionBehaviorCanJoinAllSpaces | NSWindowCollectionBehaviorStationary` so they appear on every Space and do not slide during Space switches (mirrors how Dock/menu bar behave). They MUST set `NSWindowCollectionBehaviorIgnoresCycle` so ⌘-Tab / window cycling skips them.

**Requirement 3.3** — Desktop and taskbar windows MUST be borderless (`NSWindowStyleMaskBorderless`) and SHOULD NOT steal key focus from the user's app when clicked. True non-activation (`NSWindowStyleMaskNonactivatingPanel`) requires the surface to be an `NSPanel` created with that mask — which stock Avalonia.Native does not produce (§3.1) — so this requirement is satisfied via Requirement 3.6's panel backend, or via the interim choreography defined there. The wallpaper window sets `ignoresMouseEvents = true`; the icon-grid child window does not.

**Requirement 3.4** — Windows that must be click-through in parts (e.g. wallpaper behind icons) use a layered approach: a mouse-transparent wallpaper `NSWindow` + an interactive icon-grid child window, rather than per-pixel hit-testing.

**Requirement 3.6 — Panel-capable Avalonia backend workstream (explicit, budgeted in M2).** A non-activating taskbar/tray/start-menu is not achievable through `IMacOSTopLevelPlatformHandle` alone, and it is NOT something `BevelHelper` can fix either — these are the shell process's own windows, and no external process can change another window's class or creation-time style mask. Two acceptable paths, decided by an M2 spike:
1. **Preferred:** contribute a "panel window" option to Avalonia.Native upstream (TopLevel opt-in that creates an `NSPanel` with `NSWindowStyleMaskNonactivatingPanel` / `becomesKeyOnlyIfNeeded`), or maintain a vendored fork of the ObjC backend carrying that patch until upstreamed. This is a real backend fork/patch commitment, not a "tiny ObjC shim" — it MUST be scheduled and owned in [09-engineering-plan.md](09-engineering-plan.md) M2.
2. **Fallback (must be validated for tolerability):** accept that clicking the taskbar activates Bevel.app, and immediately re-activate the previously-frontmost app (`NSRunningApplication.activate` on the tracked previous app / `NSApp.deactivate`) after the click is handled. This produces a visible focus flicker and can drop the first keystroke; M2 exit criteria include a judgment call on whether this is shippable if path 1 slips.

### 3.3 Spaces / Mission Control interactions

- **Per-Space desktops:** macOS gives each Space its own wallpaper. With `canJoinAllSpaces`, our single desktop window shows identically on all Spaces. **Decision:** v1 renders one shared desktop across all Spaces (matches "one desktop" Win2000 mental model). Per-Space wallpaper parity with native macOS is a non-goal in v1; recorded in Open questions.
- **Mission Control** will show our all-Spaces windows as pinned. The taskbar/desktop MUST use `NSWindowCollectionBehaviorStationary` to avoid being animated as thumbnails.
- **Fullscreen apps** create their own Space; our taskbar SHOULD be reachable via `fullScreenAuxiliary` where the app permits, but we accept that a true native fullscreen app hides the taskbar (same as the Dock). Documented behavior, not a bug.

**Requirement 3.5** — On `NSApplicationDidChangeScreenParametersNotification` and on active-Space-change (observed via the helper's `NSWorkspace.activeSpaceDidChangeNotification`), the shell MUST re-assert window levels and re-lay-out the taskbar/desktop for the current display set (§8, multi-display).

---

## 4. AX-based window management for the taskbar

The taskbar's window list needs to (a) enumerate all top-level windows of other apps, (b) show titles/icons, (c) activate/minimize/close them. macOS splits this across two APIs.

### 4.1 Enumeration — CGWindowList

`CGWindowListCopyWindowInfo(kCGWindowListOptionOnScreenOnly | kCGWindowListExcludeDesktopElements, kCGNullWindowID)` returns, per window: `kCGWindowOwnerPID`, `kCGWindowNumber`, `kCGWindowName` (often empty without extra entitlement), `kCGWindowLayer`, `kCGWindowBounds`, `kCGWindowOwnerName`, `kCGWindowIsOnscreen`.

**Requirement 4.1** — The helper MUST poll/enumerate windows via `CGWindowListCopyWindowInfo`, filtering to `kCGWindowLayer == 0` (normal app windows) and excluding our own PID, the Dock, and system UI. Enumeration is **screen-recording-permission-free** for geometry/owner metadata; window *titles* are reliably available only via AX (below), because `kCGWindowName` is frequently empty and, post-Sequoia, restricted.

### 4.2 Control — AXUIElement (Accessibility)

Metadata → control requires the Accessibility API:

```
AXUIElementCreateApplication(pid) → app element
  AXWindows attribute → [AXWindow ...]
    AXTitle, AXMinimized, AXPosition, AXSize, AXMain
    Actions: AXRaise (activate), set AXMinimized=true (minimize),
             AXPress on AXCloseButton (close)
```

**Requirement 4.2** — The helper MUST map CGWindowList windows to AX windows by `(pid, bounds, title)` correlation (there is no stable shared window id between the two APIs — this is the known hard part; we correlate on PID + frame, disambiguating by z-order and title). It exposes to the shell a normalized `TaskbarWindow` list over IPC:

```csharp
// PAL contract (see 01-architecture.md for wire format)
public interface IWindowService
{
    Task<IReadOnlyList<TaskbarWindow>> ListWindowsAsync();     // merged CGWindowList + AX
    Task ActivateAsync(WindowRef w);   // AXRaise + app activate (NSRunningApplication)
    Task MinimizeAsync(WindowRef w);   // set AXMinimized = true
    Task RestoreAsync(WindowRef w);    // set AXMinimized = false + AXRaise
    Task CloseAsync(WindowRef w);      // AXPress AXCloseButton
    IObservable<WindowEvent> Changes;  // created/destroyed/focused/retitled
}
public readonly record struct WindowRef(int Pid, ulong CgWindowId, IntPtr AxWindow);
public sealed record TaskbarWindow(WindowRef Ref, string Title, string AppName,
    string BundleId, bool IsMinimized, bool IsFocused, Rect Bounds);
```

**Requirement 4.3 — Change notifications, not polling where possible.** The helper MUST register `AXObserver`s for `kAXWindowCreatedNotification`, `kAXUIElementDestroyedNotification`, `kAXFocusedWindowChangedNotification`, `kAXTitleChangedNotification`, `kAXWindowMiniaturized/DeminiaturizedNotification` per running app (apps discovered via `NSWorkspace.runningApplications` + `didLaunch/didTerminate` notifications). A slow CGWindowList reconciliation poll (≥ 1 s) is a backstop for apps that misreport AX events.

**Requirement 4.4** — App icons for the taskbar come from `NSRunningApplication.icon` (no TCC needed), not from screen capture.

**Requirement 4.5 — Degradation without Accessibility:** if the Accessibility TCC grant is absent, `ListWindowsAsync` still returns entries (CGWindowList geometry + `NSRunningApplication` for app name/icon) but `Activate/Minimize/Close` are disabled and the taskbar shows an inline "Grant Accessibility to control windows" affordance (§7).

---

## 5. Systray mirroring via the Ice technique

This is the signature feature and the most fragile. We mirror the OS menu-bar `NSStatusItem`s of other apps into our own tray. Reference implementation and breakage history: [jordanbaird/Ice](https://github.com/jordanbaird/Ice) (Ice targets macOS 14+ for exactly these APIs, per its own support notes).

### 5.1 Pipeline

```
(1) DISCOVER  status-item windows via CGWindowListCopyWindowInfo,
              filtered to the menu-bar strip + owner heuristics.
(2) CAPTURE   each status item's pixels via ScreenCaptureKit
              (SCContentFilter targeting the specific SCWindow),
              on a throttled loop → CGImage → tray icon bitmap.
(3) RECLAIM   auto-hide the real menu-bar strip so the native items
              are visually gone and ours are canonical.
(4) FORWARD   on tray-icon click, synthesize a click at the real
              status item's screen coords via CGEvent, so the owning
              app shows its real menu / popover.
```

### 5.2 Discovery

**Requirement 5.1** — Status items live in windows owned by their apps in the menu-bar layer. The helper MUST enumerate `CGWindowListCopyWindowInfo(kCGWindowListOptionOnScreenOnly, kCGNullWindowID)` and select windows whose `kCGWindowBounds` sit within the primary display's menu-bar Y-band and whose `kCGWindowLayer` matches the status-item layer (`kCGStatusWindowLevel`-region). Each is keyed by `(ownerPID, windowNumber)` and correlated to an `NSRunningApplication` for name/icon fallback. This is the Ice approach; there is no public "list all NSStatusItems" API.

**Requirement 5.2** — The helper MUST maintain a stable ordering and identity for mirrored items across capture frames (key on `ownerPID + windowNumber`; when a window number churns, re-correlate by PID + relative x-order) so the tray doesn't flicker/reorder.

### 5.3 Capture — ScreenCaptureKit

**Requirement 5.3** — Pixel capture MUST use ScreenCaptureKit (`SCStream` / `SCScreenshotManager` for one-shots) with an `SCContentFilter` scoped to the individual status-item `SCWindow` (`SCContentFilter(desktopIndependentWindow:)`), NOT a full-screen grab we crop — per-window filtering keeps us off unrelated content and minimizes the Screen Recording exposure. macOS 14 is required for stable per-window SCK capture (§10).

**Requirement 5.4 — Capture cadence & efficiency.** The capture loop MUST be event-throttled, not a fixed high-rate stream:
- Default: capture a status item only when (a) first discovered, (b) its window's bounds/`kCGWindowName` change, or (c) a periodic low-rate refresh (≤ 1 Hz) catches animated icons.
- The shell MUST expose a "reduce capture" setting; when the shell/tray is not visible or the display is asleep, capture pauses. (Ice made exactly this change to reduce the purple Screen-Recording indicator and CPU — we adopt it as a requirement.)

**Requirement 5.5 — Limited mode without Screen Recording.** Without the Screen Recording grant, SCK returns black/blocked frames. In that case the tray MUST fall back to showing the owning app's `NSRunningApplication.icon` (generic, non-live) with a badge indicating "live preview unavailable — grant Screen Recording," and click-forwarding (§5.5) still works via AX/CGEvent. (Ice added a comparable limited mode; we spec it as mandatory.)

### 5.4 Menu-bar reclaim

**There is no per-app API that lets a background process hide the menu bar system-wide.** `NSApplication.presentationOptions` with `.autoHideMenuBar` is app-scoped and only takes effect while the setting app is the *active (frontmost)* application — useless to a helper that must never steal focus. (An earlier draft proposed keeping a small always-frontmost proxy to assert the option; rejected: a permanently-frontmost helper would take key focus from every user application and break the desktop.) Nor is hiding the whole bar the Ice technique: Ice hides individual status *items* by relocating them — a synthesized ⌘-drag past a separator item — and never hides the native menu bar itself. Two real mechanisms exist, and reclaim is specified around them:

**Requirement 5.6 — Reclaim mechanisms (both user-consented, neither silent).**
1. **System-wide menu-bar auto-hide (primary):** onboarding offers to enable macOS's own "Automatically hide and show the menu bar" setting (`defaults write NSGlobalDomain _HIHideMenuBar -bool true`; on Ventura+ the equivalent Control Center → "Automatically hide and show the menu bar" values). This is the only supported system-wide hide. It is global, user-visible, and reversible; it MUST be applied only with explicit consent, journaled per Req 2.3, and restored on uninstall. Where the write does not take effect live, onboarding deep-links the System Settings toggle and instructs the user — this matches [09-engineering-plan.md](09-engineering-plan.md) M3 ("system-wide hiding requires the user toggle in System Settings; the onboarding flow automates what's possible and instructs the rest").
2. **Item relocation (the actual Ice technique, item-level):** the helper MAY hide individual *third-party* status items without touching the bar: synthesize a ⌘-drag `CGEvent` sequence that moves the real item past an overflow boundary (our own always-rightmost separator `NSStatusItem`), pushing it off the visible strip. Apple/system items and the native clock stay visible. This path is per-item, choreography-fragile, and MUST be feature-flagged behind the §5.10 self-test.

**Scope re-statement (revises pillar 3's "canonical tray" promise):** the shell CANNOT unilaterally make its tray canonical. The shipped promise is a **duplicate tray that becomes canonical when the user consents** to mechanism 1 and/or 2. If the user declines both, our tray mirrors a still-visible native bar — documented tradeoff, not a bug.

**Requirement 5.7** — When the native strip is hidden, the shell MUST reproduce the menu-bar essentials the user still needs (clock, and the *system* items like Wi-Fi/battery/Control-Center) — either by mirroring them too, or by rendering our own equivalents. Control Center is a special case (§5.7).

### 5.5 Click forwarding

**Requirement 5.8** — On tray-item interaction the helper MUST reveal the real item's menu/popover. Two forwarding strategies; the helper MUST probe each item's viable strategy **at discovery time** and cache the result per item (re-probed on ownerPID/window churn):
1. **AX press (used only where verified):** locate the owning app's status item AX element (`AXUIElementCreateApplication(pid)` → menu-bar item) and `AXPress` it. This is NOT universally reliable: status items with custom views, `NSPopover`-based items, and SwiftUI `MenuBarExtra` items frequently expose no actionable `AXPress`, or perform it without the popover anchoring correctly. The discovery-time probe checks for a present, plausibly-wired `AXPress` action; the strategy is confirmed (and the cache updated) on the first real user click, falling through to strategy 2 on failure.
2. **CGEvent synthesis (default for unverified items):** compute the real item's screen coordinates from its window bounds and post a synthetic `kCGEventLeftMouseDown/Up` (or right-click) via `CGEventPost(kCGHIDEventTap, …)` at those coords. The real item MUST actually be on screen at those coordinates: if the bar is hidden via system auto-hide (§5.4), the helper first triggers the reveal (synthesized pointer move to the top edge), waits for the reveal animation to complete (asynchronous, ~200 ms+ — there is **no** API to render the menu bar off-screen or at reduced alpha), posts the click, and lets the bar re-hide after menu dismissal. Needs Accessibility for trusted event posting. Items hidden by ⌘-drag relocation (§5.4 mechanism 2) must be temporarily relocated back into the visible strip before clicking — costlier; prefer AX press for relocated items where verified.

**Latency (revises 07-shell-ux R-TR-2):** menus raised by forwarding anchor at the item's *real top-of-screen coordinates*, not at our taskbar (as 07 already concedes), and the hidden-bar reveal animation alone busts a 120 ms budget. Measured targets: **≤ 150 ms** click-to-native-reaction when the bar is visible; **≤ 500 ms** when a hidden bar must be revealed first, with a busy-cursor tick beyond 150 ms. 07-shell-ux R-TR-2's flat "≤ 120 ms" MUST be amended to these two-tier targets.

**Requirement 5.9** — Right-click / option-click / drag on a tray item MUST be forwarded with matching modifiers (status items differentiate behavior by modifier). The helper mirrors the incoming event's button + modifier flags into the synthesized `CGEvent`.

### 5.6 Version breakage watchpoints

**Requirement 5.10** — The systray subsystem MUST be feature-flagged and self-testing. On each launch (and on OS build change detected via `sysctl kern.osversion`), the helper runs a self-test: discover ≥ 1 known status item, capture one frame, verify non-blank, attempt a no-op AX locate. On failure it disables live mirroring, falls back to §5.5 limited mode, and records diagnostics to the **local, offline** telemetry log (v1 telemetry is fully offline: local crash logs + user-initiated export, no network; see [09-engineering-plan.md](09-engineering-plan.md)). Watchpoints to monitor each macOS beta:
- SCK `SCContentFilter(desktopIndependentWindow:)` semantics and the Screen-Recording indicator policy.
- Status-item window layer/level constant drift.
- System auto-hide setting semantics (`_HIHideMenuBar` / Control Center variant) and ⌘-drag status-item reordering behavior (notably changed around Sonoma "notch"/Control-Center era and again in the Tahoe (macOS 26) "Liquid Glass" menu bar).
- Whether `kCGWindowName`/AX title access tightens further under TCC.

### 5.7 Control Center & Apple's own items

Apple's Control Center and its bundled items (Wi-Fi, Bluetooth, Sound, Now Playing) are `com.apple.controlcenter`-owned and are *not* classic `NSStatusItem`s we can cleanly AX-press in all cases. **Decision:** v1 mirrors third-party status items fully; Apple/system items are captured for display but clicking them **forwards via CGEvent to the still-present real control** (we keep the real Control Center reachable rather than reimplement it). Reimplementing Control Center toggles natively (via CoreWLAN/IOBluetooth/private prefs) is out of scope for v1. Recorded in Open questions.

---

## 6. Apple Events / Finder interop — compat tiers

Goal: existing automation and `tell application "Finder" to …` scripts, plus `NSWorkspace` reveal entry points, keep working when Bevel is the desktop. Cross-reference file-manager behaviors in [06-file-manager.md](06-file-manager.md).

### 6.1 Tier 0 (always on): NSWorkspace reveal + open

**Requirement 6.1** — The shell MUST implement the non-AppleEvent reveal/open surface that apps call directly:
- `NSWorkspace.activateFileViewerSelecting(_:)` ("Reveal in Finder") — since Finder is alive in Coexist mode, this still opens *real* Finder by default. **Decision:** in Coexist mode we let real Finder service reveal (it works, zero risk). In Advanced/Replace mode we intercept by registering Bevel as the handler for the relevant path and open *our* file-manager window instead. Reveal interception in Coexist mode is a Tier-2 concern (bundle shadowing), not Tier 0.
- `open`/`NSWorkspace.open(_:)` of folders → our file manager when we own the folder UTI handler (Advanced mode); real Finder otherwise.

### 6.2 Tier 1 (v1 guaranteed): our own scripting suite mirroring Finder terminology

**Decision:** Bevel ships an AppleScript scripting suite whose terminology mirrors the subset of Finder's dictionary we can meaningfully honor, served from *our* process. Scripts targeting **`application "Bevel"`** work fully; scripts hard-coded to `application "Finder"` are handled per Tier 2/3.

**Requirement 6.2** — Ship an `sdef` (`Bevel.sdef`, carried in the bundle that receives the events — the helper, per Req 6.3 and Req 11.1 — referenced from that bundle's `Info.plist` `OSAScriptingDefinition` + `NSAppleScriptEnabled=YES`) mirroring Finder terms: `Finder window`, `desktop`, `selection`, `open`, `reveal`, `activate`, `make new folder`, `move`, `duplicate`, `delete`, `empty trash`, `get properties of`, item/file/folder class hierarchy, `insertion location`, `POSIX path`/`as alias` coercions. Terminology and 4-char AppleEvent codes SHOULD match Finder's where public (`core`/`aevt` standard suite + Finder's `fndr` suite codes) so a script written against Finder verbs parses against our suite.

**Requirement 6.3** — Apple Events terminate in **`BevelHelper` (Swift)**, which owns the `NSAppleEventManager` handlers and the sdef, performs AE object-specifier resolution natively, and converts resolved commands into command-model IPC calls to the file-manager core ([06-file-manager.md](06-file-manager.md)). This matches the Decision recorded in [08-os-interop.md](08-os-interop.md) §2.1.2 and the PAL table in [01-architecture.md](01-architecture.md); the rejected alternative — `NSAppleEventManager`/`AEInstallEventHandler` P/Invoke in the .NET process — would re-implement the object-specifier resolver from scratch with no crash isolation. Unsupported verbs return `errAEEventNotHandled` so callers can fall through gracefully rather than silently mis-execute; unhandled verbs are logged **locally only** (offline telemetry policy, §5.10). Consequence: the helper must be the LaunchServices-registered scripting target for the suite, which shapes the bundle layout (Req 11.1) and raises an addressing question — scripts say `tell application "Bevel"`, so the scripting-facing bundle name/id must resolve to the AE-receiving process (see Open questions).

**Requirement 6.4** — `System Events`-based UI scripting of Finder is out of scope to emulate; documented.

### 6.3 Tier 2 (experiment, off by default): bundle-id shadowing

The idea: make `application "Finder"` resolve to *us* by claiming the `com.apple.finder` bundle id / registering as the handler for Finder's AppleEvent target so LaunchServices routes `tell application "Finder"` to Bevel.

**Requirement 6.5** — Tier 2 MUST be gated behind an explicit "Intercept Finder automation (experimental)" toggle, defaulting OFF, with a warning that it can break system automation and share sheets. Mechanics under evaluation:
- Registering an additional `LSItemContentTypes`/`CFBundleIdentifier` alias is **not** permitted for a reserved Apple id via normal means; instead we evaluate intercepting at the AppleEvent routing layer: a helper that installs as the `com.apple.finder` AppleEvent *target proxy* is not officially supported.
- Because this cannot be done cleanly/safely without private hooks, **Decision:** Tier 2 remains a research spike, NOT shipped enabled in v1. Rationale: LaunchServices/Gatekeeper will resist a third-party binary claiming `com.apple.finder`, and even partial success poisons every system code path that expects the real Finder. Risk >> reward for v1.

### 6.4 Tier 3 (opt-in): headless real-Finder mirroring

Since Coexist mode keeps a live-but-hidden Finder, we can *delegate*: forward `tell application "Finder"` semantics to the real Finder and mirror the results into our UI, giving perfect fidelity for the automation that matters while our UI stays canonical.

**Requirement 6.6** — In Tier 3 (opt-in "High-fidelity Finder automation"), for verbs we choose to delegate (e.g. complex `get`/property queries), the shell MAY re-dispatch the AppleEvent to `com.apple.finder` (the real, hidden Finder) via a fresh `NSAppleEventDescriptor` target and return its reply. State-changing verbs (`move`, `delete`) are executed by *our* file core to keep our UI consistent, then reconciled. This is the most robust path and is our recommended "advanced automation" story, precisely because we deliberately keep Finder alive (§2.2).

**Decision summary:** v1 ships **Tier 0 + Tier 1** on by default and **Tier 3** as an opt-in advanced toggle; **Tier 2 is not shipped enabled.** This gives working automation for `application "Bevel"` scripts and for delegated `application "Finder"` scripts (Tier 3) without the systemic risk of bundle shadowing.

---

## 7. TCC permission onboarding

Three TCC grants matter. None can be scripted silently; each requires a user gesture in System Settings → Privacy & Security. The prompts attach to `BevelHelper` (stable, minimal, separately notarized — §1).

| Permission           | Requested by            | Enables                                                        | Degradation without it |
|----------------------|-------------------------|---------------------------------------------------------------|------------------------|
| **Accessibility**    | BevelHelper (AX + CGEvent)| Taskbar window control (activate/min/close), AX click-forward | Taskbar becomes read-only list; tray click-forward falls back to visible-CGEvent only; §4.5 |
| **Screen Recording** | BevelHelper (SCK)       | Live systray icon pixels, wallpaper/window thumbnails         | Tray shows static app icons + "grant to enable live preview"; §5.5 |
| **Full Disk Access** | BevelHelper (+ main app)| File manager reaching TCC-protected dirs (Desktop, Documents, Downloads, iCloud, other apps' containers) without per-folder prompts | File manager works but triggers per-folder consent prompts / hides protected locations; see 06-file-manager.md |

**Requirement 7.1 — Guided onboarding.** First run MUST present a checklist screen showing each permission, its purpose in plain language, current grant status (polled via `AXIsProcessTrustedWithOptions`, `CGPreflightScreenCaptureAccess`, and a probe read for FDA), and a "Open the right settings pane" button that deep-links:
```
x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility
x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture
x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles
```

**Requirement 7.2** — The shell MUST function in a **reduced but non-broken** mode with zero permissions: desktop + wallpaper + start menu + clock + file manager (own directories) all work; taskbar is a read-only window list; tray shows static icons. No permission is a hard prerequisite for the shell to be usable — this is a design invariant (a desktop must never be un-bootable because a checkbox is off).

**Requirement 7.3** — Grant status MUST be observed live (poll ≤ 2 s while onboarding UI is open; `CGRequestScreenCaptureAccess()` triggers the system prompt for SCK). When a grant flips on, the corresponding subsystem hot-enables without an app restart where the API allows (Screen Recording historically required app relaunch — if so, we prompt "Restart Bevel to enable live tray" rather than silently failing).

**Requirement 7.4** — Because TCC grants are keyed to the code signature, the helper MUST have a **stable signing identity + bundle id across updates** so users don't re-grant on every release (see §11, [09-engineering-plan.md](09-engineering-plan.md)).

---

## 8. Multi-display

**Requirement 8.1** — The shell MUST enumerate displays via `NSScreen.screens` and observe `NSApplicationDidChangeScreenParametersNotification`. Per display it renders a desktop/wallpaper; the **taskbar renders on the primary display only** in v1 (the display with the menu bar, `NSScreen.screens[0]` / the one containing `(0,0)`), matching Win2000's single-taskbar model. Secondary displays get wallpaper + icons, no taskbar. (Per-display taskbars: post-v1, Open questions.)

**Requirement 8.2** — The menu-bar strip and thus the systray live on the display that hosts the menu bar. If "Displays have separate Spaces" is on, each display has its own menu bar; the helper MUST capture status items on the menu-bar display and place our tray on the taskbar (primary) display, translating coordinates for click-forwarding across displays (§5.5) using each `NSScreen.frame`.

**Requirement 8.3** — On display hot-plug/unplug/resolution/scale change, the shell MUST re-layout within one animation frame budget, re-assert window levels (§3.5), reassign the taskbar to the new primary if the old primary vanished, and rescale wallpaper per each screen's `backingScaleFactor`.

**Requirement 8.4** — Coordinate handling MUST account for macOS's bottom-left global origin and mixed per-display scale factors; all IPC geometry is in a documented top-left, points (not pixels) convention converted at the helper boundary (see [01-architecture.md](01-architecture.md)).

---

## 9. Work area: Dock strategy & overlap mitigation

macOS has **no** public API to reserve screen work area (no `_NET_WM_STRUT` equivalent). Apps maximize into `NSScreen.visibleFrame`, which the system computes from the *menu bar + Dock*, not from arbitrary windows. Our always-on taskbar would therefore overlap maximized windows. Three physical facts constrain every strategy — earlier cross-chapter wording that ignored them ([07-shell-ux.md](07-shell-ux.md) §10 "Auto-hidden, retained as work-area shim"; [08-os-interop.md](08-os-interop.md) §4.1 "Dock auto-hide + Bevel taskbar reserves via shim") is **superseded by this section** and must be reconciled to it:

1. **An auto-hidden Dock reserves zero work area.** With `autohide=true`, `NSScreen.visibleFrame` regains the Dock inset. "Auto-hide the Dock and keep it as the reservation" is self-contradictory; a shim requires a *visible* Dock.
2. **The Dock's minimum reserved thickness does not match our taskbar.** Even at minimum tile size the Dock inset is realistically ~45–60+ px (exact figures per Req 9.4), versus the themed taskbar's 30 logical px (07-shell-ux R-TB-1). The reservation can never coincide with the bar; either the taskbar grows to the inset, or a dead band shows above it.
3. **A taskbar rendered over a visible Dock leaves the Dock alive-but-covered:** badges, bounce notifications, drag-to-Dock, and Dock context menus become unreachable. Covered-Dock mode disables these affordances by construction.

**Decision — default is Dock auto-hidden + AX window nudging; the visible-Dock shim is an opt-in strict mode.**

**Requirement 9.1 — Default mode ("Nudge").** With consent (journaled per Req 2.3), the installer sets the Dock to auto-hide and moves it to a **side edge** (left/right) so its pointer-reveal zone does not collide with our bottom taskbar. There is then no OS-level reservation: the taskbar overlaps the work area at its themed height, and overlap is mitigated per Req 9.2. The Dock stays reachable (side-edge reveal) with all its affordances intact.

**Requirement 9.2 — AX overlap mitigation (active in default mode).** The shell MUST detect windows that overlap the taskbar band (via the §4 AX/CGWindowList data) and, per window, nudge maximized/overlapping windows out by setting `AXPosition`/`AXSize` when they cross into the band. Honest caveats, documented in-product: this is best-effort and jittery — some apps re-assert their own frames, some windows are not AX-resizable, and nudges MUST be rate-limited and MUST never fight a user drag in progress (suppress while the mouse button is down on the target window).

**Requirement 9.3 — Opt-in strict mode ("Dock shim").** The Dock is kept **visible** on the taskbar's edge at minimum size, and the taskbar's height is set to the *measured* Dock inset (Req 9.4) so maximized apps stop exactly at the taskbar's top edge. Consequences shown inline in the setting: the taskbar is taller than the authentic theme metric (the Win2000 chrome renders at the inflated height — non-authentic, documented), and the covered Dock's affordances (badges, bounce, drag-to-Dock, context menus) are unreachable while this mode is on.

**Requirement 9.4 — Measurement & tests (prerequisite for 9.3).** CI/dev validation on macOS 14 and 15 MUST measure and record in-repo: (a) `visibleFrame` with Dock auto-hide on vs. off, asserting the inset disappears when auto-hidden; (b) the minimum Dock inset across tile sizes and magnification settings, per edge; (c) inset behavior on scale/display changes. Strict-mode taskbar height derives from these measurements, never from guessed constants.

**Requirement 9.5** — The chosen strategy MUST be a single setting ("Reserve space using: [Window nudging ▸ recommended] / [Dock shim (strict) — taller bar, Dock covered] / [None — overlap allowed]") with the tradeoffs shown inline. Rejected alternative — a private `SetWindowWorkarea`-style call: no such supported API exists; we will not depend on private SkyLight symbols in shipping builds.

---

## 10. macOS version support matrix

Floor is **macOS 14.0 Sonoma** (SCK per-window capture maturity + the status-item semantics Ice relies on; Ice itself supports 14+). We validate each release; "Liquid Glass"/menu-bar changes in macOS 26 (Tahoe) are an active watchpoint (§5.6).

| macOS               | Version | Status  | Notes / risk |
|---------------------|---------|---------|--------------|
| Ventura & earlier   | ≤ 13    | **Unsupported** | Older SCK/status-item behavior; not worth the back-port. |
| Sonoma              | 14      | **Supported (floor)** | Primary dev/test target; SCK per-window filters, AX, CGEvent all validated. |
| Sequoia             | 15      | **Supported** | Watch tightening of `kCGWindowName`/AX title TCC; new Screen-Recording weekly-consent nag — mitigate via §5.4 throttling. |
| Tahoe               | 26      | **Supported, watch** | Liquid-Glass menu bar + presentation-option changes may affect auto-hide (§5.6) and status-item layout; systray self-test (§5.10) gates. |
| Future betas        | 27+     | **Best-effort** | Feature-flag + self-test gate; ship "systray disabled on unverified OS" rather than break the desktop. |

**Requirement 10.1** — On an OS build newer than the last validated one, systray live mirroring and menu-bar reclaim MUST default to *self-test-gated*: enabled only if §5.10 self-test passes, else limited mode. Desktop/taskbar/file-manager remain fully enabled regardless.

**Requirement 10.2** — Architecture: universal binary (`arm64` + `x86_64`). .NET publishes both RIDs; the helper builds a fat binary. Apple Silicon is the primary test target.

---

## 11. Packaging, signing, notarization

Cross-reference build/CI in [09-engineering-plan.md](09-engineering-plan.md); this section fixes the macOS-specific requirements.

**Requirement 11.1 — Bundle layout.** Ship a single `Bevel.app` with the helper nested. Because Apple Events terminate in the helper (Req 6.3, [08-os-interop.md](08-os-interop.md) §2.1.2), the scripting suite lives in the **helper's** bundle, and the helper is the LaunchServices-registered scripting target:
```
Bevel.app/
  Contents/Info.plist            LSUIElement=YES, usage strings (see 11.4)
  Contents/MacOS/Bevel           (.NET host, apphost)
  Contents/Resources/restore-finder.sh
  Contents/Library/LoginItems/BevelHelper.app   (separately signed helper)
    Contents/Info.plist          NSAppleScriptEnabled=YES,
                                 OSAScriptingDefinition=Bevel.sdef,
                                 NSAppleEventsUsageDescription (Tier-3 sends)
    Contents/Resources/Bevel.sdef
  Contents/MonoBundle/ or publish output (self-contained .NET 9)
```
The helper registers via `SMAppService.agent`; the main app via `SMAppService.mainApp`. Scripting addressing (making `tell application "Bevel"` / `tell application id …` resolve to the helper rather than the outer app) needs an early spike — candidate answers are naming the helper's scripting identity accordingly or a stable helper bundle id documented for `tell application id` — recorded in Open questions.

**Requirement 11.2 — Signing.** Both binaries signed with the existing **individual** Developer ID Application certificate — `Developer ID Application: Cezar Pokorski (Team ID 4TP7TPH2K6)` — under an **individual** Apple Developer enrollment (**no D-U-N-S, no organization account**). Sign with **Hardened Runtime enabled**, `--options runtime`, `--timestamp`, and a **stable bundle id (`pl.ikari.bevel`, frozen at M0) + signing identity across releases** (required for durable TCC grants, §7.4). The helper is signed *before* the outer app (inside-out), and the outer app's signature covers it.

**Requirement 11.3 — Entitlements.** .NET requires JIT-related exceptions under Hardened Runtime. Minimum entitlements:
```
com.apple.security.cs.allow-jit                       = true   (.NET runtime)
com.apple.security.cs.allow-unsigned-executable-memory= true   (RyuJIT)
com.apple.security.cs.disable-library-validation      = true   (load our helper/native dylibs, libAvaloniaNative)
com.apple.security.automation.apple-events            = true   (on the HELPER —
                                                                it sends AppleEvents to
                                                                Finder for Tier 3, §6.4)
```
NSAppleEventsUsageDescription (helper, per 11.1) and the Privacy usage strings (11.4) are required. We do **not** sandbox (`com.apple.security.app-sandbox` = NO): a shell needs AX/SCK/FDA and system-wide window control that the App Sandbox forbids. **Decision:** distribute via **Developer ID + notarization outside the Mac App Store**; MAS is incompatible with our entitlements. Recorded as final for v1.

**Requirement 11.4 — Usage strings (Info.plist).** `NSAppleEventsUsageDescription`, plus TCC purpose strings surfaced at grant time. Screen Recording and Accessibility do not use Info.plist usage strings the same way (they're grant-list, not prompt-string), but FDA and Apple Events do; provide clear text.

**Requirement 11.5 — Notarization.** `notarytool submit --wait --keychain-profile bevel` on the zipped app (the `bevel` notarytool keychain profile holds the individual Developer ID credentials from Req 11.2), then `stapler staple Bevel.app`. CI ([09-engineering-plan.md](09-engineering-plan.md)) gates release on successful staple. Distribute as a signed, notarized, stapled DMG or PKG.

**Requirement 11.6 — Updates.** Auto-update MUST preserve bundle id + signing identity (TCC durability) and MUST re-run inside-out signing. Use **Velopack** (the cross-platform updater mandated by [01-architecture.md](01-architecture.md) UPD-01 and the 00 master plan R7); the updater MUST NOT require re-granting TCC on normal version bumps (verify via a post-update permission self-check).

---

## Risks

1. **Systray mirroring is the single biggest ABI-fragility.** Every macOS release can move status-item window layers, SCK filter semantics, or menu-bar auto-hide. Mitigation: helper crash-isolation (§1), feature-flag + self-test gating (§5.10), limited mode (§5.5), and tracking Ice's issue tracker as an early-warning canary.
2. **Menu-bar reclaim is consent-gated and mechanically limited.** There is no unilateral hide (§5.4): the primary path is the user-visible global auto-hide setting, and item-level relocation depends on ⌘-drag choreography Apple could change. Pillar 3 is therefore scoped as "duplicate tray, canonical with consent" — if Apple removes the auto-hide setting's effect or breaks ⌘-drag reordering, we degrade further to duplicate-only.
3. **Screen Recording consent fatigue.** Sequoia+ periodically re-prompts for Screen Recording; a shell that captures continuously will nag users. §5.4 throttling is essential; if Apple tightens further, live tray previews may become impractical and we fall back to static icons broadly.
4. **CGWindowList↔AX correlation is heuristic.** No shared window id means taskbar control can mis-target under rapid window churn or identical-frame windows. Mitigation: PID+frame+z-order+title correlation and AX-observer-driven updates; residual edge cases (e.g. tabbed windows, sheets) will misbehave.
5. **Full Finder replacement can strand a user at a blank desktop.** Mitigated by Coexist-default (§2.2), the reversibility manifest, Safe-Mode hotkey, and the standalone `restore-finder.sh` (§2.4). This must be treated as safety-critical in QA ([09-engineering-plan.md](09-engineering-plan.md)).
6. **.NET Hardened-Runtime + notarization friction.** JIT entitlements and disable-library-validation weaken the hardened posture and can draw extra notarization scrutiny; a future Apple policy change could require AOT publishing. Mitigation: keep a NativeAOT publish path evaluated in CI as a contingency.
7. **Avalonia.Native surface coverage — including a committed backend fork/patch.** We depend on `IMacOSTopLevelPlatformHandle` and on Avalonia not fighting our window-level/collection-behavior changes; if Avalonia re-asserts levels on activation, we may need an ObjC-side observer to re-apply (§3.5). Bigger: non-activating panels require an upstream or vendored change to the Avalonia.Native ObjC backend (Req 3.6) — a maintained fork is a real ongoing cost if upstreaming stalls. Classic.Avalonia is theming-only and does not affect this layer.
8. **Window nudging is the default work-area strategy and will visibly jitter for some apps.** The default mode (§9) has no OS-level reservation; AX nudging fights apps that re-assert frames. If field feedback is bad, the escape hatches are strict Dock-shim mode (taller bar, covered Dock) or overlap-allowed — none is fully satisfying; this is macOS's missing-API tax.

## Open questions

1. **Per-Space wallpaper parity:** should v1 honor macOS per-Space wallpapers, or is one shared desktop across all Spaces (current decision, §3.3) acceptable for launch? Affects desktop-window Space strategy.
2. **Control Center reimplementation depth:** v1 forwards clicks to the real Control Center (§5.7). Is native reimplementation of Wi-Fi/Bluetooth/Sound toggles (CoreWLAN/IOBluetooth) a post-v1 priority, given the maintenance burden and private-pref risk?
3. **Full "Replace Finder" scope — resolved (2026-07-04):** Advanced/Replace mode (§2.2) is **not** a v1 feature. v1 ships Coexist-only; the loginwindow `Finder`-key replacement is a gated opt-in that ships only in a later v1.x, *after* a loginwindow viability spike proves the recovery paths (§2.4). Full replace-by-default remains the long-term north star. Remaining open item: scoping and scheduling that viability spike (owned in [09-engineering-plan.md](09-engineering-plan.md)).
4. **Tier 3 delegated-Finder default:** should high-fidelity Finder automation (§6.4) be on-by-default given it depends on keeping Finder alive, or remain opt-in? Impacts default coexistence posture.
5. **Per-display taskbars (§8.1):** single primary-display taskbar for v1 is decided; do we commit to multi-taskbar for v1.x, and does that change the space-reservation model per display?
6. **Strict Dock-shim mode viability (§9.3):** pending the Req 9.4 measurements, is a taskbar inflated to the Dock's real minimum inset (~45–60+ px vs. the themed 30 px) acceptable enough to keep strict mode in the settings UI at all, or does it ship as a hidden/advanced option only?
7. **Weekly Screen-Recording re-consent (Sequoia+):** if Apple's periodic re-prompt proves too intrusive with our throttled capture, do we ship live tray previews at all by default, or make static-icon mode the default with live preview opt-in?
8. **Scripting-target addressing (Req 6.3/11.1):** with Apple Events terminating in the nested helper, what bundle naming/id arrangement makes `tell application "Bevel"` (and `tell application id …`) resolve to the helper? Needs an early LaunchServices spike; fallback is documenting a distinct scripting target name.
9. **Panel backend path (Req 3.6):** upstream a panel-window option to Avalonia.Native or carry a vendored fork? And if neither lands by M2, is the activate-then-refocus fallback shippable? Owner call after the M2 spike.
10. **Forwarding latency targets (§5.5):** validate the two-tier 150/500 ms targets against real hardware and amend 07-shell-ux R-TR-2 accordingly (the flat 120 ms budget is retired by this chapter).
