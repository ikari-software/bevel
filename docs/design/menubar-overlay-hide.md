# Menu-bar OVERLAY-HIDE — design + feasibility (Strategy C live-updates fix)

**Bead:** bevel-7hf4 (Strategy C) · **Status:** design / feasibility · **Depends on:** the proven
off-screen hide (`MacMenuBarControl.cs`), the legacy CG capture (`LegacyWindowCapture.swift`), and the
tray enumerator (`TrayServiceImpl.swift`). **PoC:** `native/helper-macos/menubar-overlay-poc.swift`.

## The problem this solves

Strategy C hides the real macOS menu-bar extras and mirrors captured copies into Bevel's bottom tray.
The shipped hide (`MacMenuBarControl.SetHidden`) expands an app-owned control `NSStatusItem` to
`10_000`pt, which pushes the real items **fully off every display**. macOS then **freezes their backing
store**: `CGWindowListCreateImageFromArray` returns byte-identical frames forever, so live tray copies
(clock, battery %, iStat graphs, spinners) never update. This is documented empirically in
`docs/design/menubar-management.md` (Strategy C, 2026-08-10): SCK returns `-3811` and even the legacy CG
path yields static bytes once a window leaves every display.

**OVERLAY-HIDE** keeps the real items **on-screen and on a display** — where macOS keeps compositing
and the owning apps keep redrawing them — but **visually covers** the status-item strip with an opaque
Bevel window. The helper keeps capturing the (now covered-but-live) item windows by id, so fresh pixels
flow to the tray. The menu bar looks hidden; the captures stay live.

## The key evidence it can work

The teardown already contains the decisive data point. From `menubar-management.md` Strategy C:

> **ScreenCaptureKit** captures a window that is visible **or occluded** (on a display, covered) — a
> third-party icon **under our expanded control item captured fine**. But the instant a window is pushed
> **fully off every display** it fails with `-3811` … 0/16.

So the freeze is bound to **off-display**, not to **occlusion**. An item that is *covered but still on a
display* was observed capturing live. Overlay-hide deliberately keeps every managed item in exactly that
"occluded, on-display" state — the one state proven to stay live. That is the whole thesis, and the PoC
exists to confirm it end-to-end (occluded → captures keep *changing*, not merely succeeding).

## Mechanism (one paragraph)

For each display with a menu bar, Bevel places a borderless, non-activating `NSPanel` at window level
`kCGStatusWindowLevel + 1` (= 26) spanning the top strip from the leftmost managed item to the right
edge. Status-item windows live at level 25, so level 26 covers them; pop-up menus live at level 101, so
a *revealed* menu still draws above the cover. The panel is opaque (painted to match the menu-bar
background, or the desktop wallpaper clipped to the strip, à la Ice). The real items are never moved:
they stay on the bar, keep redrawing, and the helper keeps capturing them by id. A tray click forwards a
synthetic AX-press to the real item's live coordinates; its menu opens at the top, above the overlay.

## Feasibility verdict per question

### Q1 — Can a borderless top-level window cover the menu-bar strip above level 25? → **VIABLE (validate live)**

Yes, with high confidence, and Ice already demonstrates the primitives on the same OS:

- Ice's **`MenuBarOverlayPanel`** (`Ice/MenuBar/Appearance/MenuBarOverlayPanel.swift`) is an `NSPanel`
  at `self.level = .statusBar` (25) that draws *over the menu-bar strip* every frame (tint, border,
  wallpaper). It is `styleMask: [.borderless, .fullSizeContentView, .nonactivatingPanel]`,
  `collectionBehavior = [.fullScreenNone, .ignoresCycle, .moveToActiveSpace]`, `backgroundColor = .clear`,
  `ignoresMouseEvents = true`. This proves the strip's pixels are writable by an app window.
- Ice's **`IceBarPanel`** (`Ice/UI/IceBar/IceBar.swift`) uses `self.level = .mainMenu + 1` (= 25) with
  `collectionBehavior = [.fullScreenAuxiliary, .ignoresCycle, .moveToActiveSpace]`.

Bevel's own `TaskbarWindow.ApplyTaskbarBehaviors()` already parks a borderless window near the menu-bar
level (`CGMainMenuWindowLevel - 1`) with `canJoinAllSpaces | stationary | ignoresCycle |
fullScreenAuxiliary` — the same toolkit. To sit **above** the status items rather than beside/below them,
raise the level from 25 to **26**. Recommended config:

```
styleMask          = borderless | nonactivatingPanel
level              = kCGStatusWindowLevel + 1        (26 — above items 25, below menus 101)
collectionBehavior = canJoinAllSpaces | stationary | ignoresCycle | fullScreenAuxiliary
opaque             = true (production)               (translucent in the PoC to watch redraw)
ignoresMouseEvents = true                            (tray drives reveal; see Q3)
```

Why "validate live" and not "certain": the system menu bar gets special treatment in *some* states
(auto-hide, fullscreen). In the ordinary always-visible menu-bar state a level-26 window covers the
status area — Ice's level-25 panel already tints it, and one level up wins the tie against the level-25
items. The residual unknowns are cosmetic edges (does the notch region or the menu-bar's own hairline
draw over us) and the fullscreen/auto-hidden menu-bar case, which the overlay must simply hide for
(mirror Ice's `isMenuBarHiddenBySystem → alphaValue = 0`). The PoC's red strip answers coverage in one
glance.

### Q2 — Do covered-but-on-screen items keep redrawing so captures update? → **LIKELY VIABLE (the one thing the PoC must prove)**

This is the crux. Reasoning about the macOS distinction:

- **Off every display** (today's hide): the WindowServer stops maintaining a live composited backing
  store for the window, and the owning process's occlusion state goes non-visible, so timer/`App Nap`
  throttling can stop redraws. Result: frozen bytes (observed).
- **Occluded but on a display** (overlay-hide): the window still has a maintained backing store — it is
  still "on" a display, just covered. `kCGWindowIsOnscreen` stays true (Ice reads exactly this field,
  `WindowInfo.isOnScreen ← kCGWindowIsonscreen`, to distinguish the two). The owning app keeps its
  update timer running because most menu-bar apps redraw on a repeating timer, **not** gated on
  visibility. And on macOS 26 the item windows are composited by **Control Center** (every status item
  reports owner "Control Centre", FB18327911) — a system process that does not App-Nap. The direct
  evidence above (an item *under the expanded control item* captured fine) is the occluded-on-display
  case already succeeding.

**Bounded risk:** full *opaque* occlusion could, for a *third-party* app that both owns its own status
window and gates its drawing on `NSWindowOcclusionState`/App Nap, cause it to stop redrawing while
covered — reproducing the freeze for that one app. This is the single reason Q2 is "likely" not
"certain". Two things de-risk it: (a) the macOS 26 Control-Center hosting model means the *capturable*
window is the system's, not the app's; (b) if it ever bites, the mitigation is trivial — leave the
overlay a hair translucent, or a 1px uncovered sliver, so occlusion never reads "fully hidden". The PoC
prints a per-item `CHANGED/SAME` column while items are `COVERED`: a live item that keeps printing
`CHANGED` under the strip proves the mechanism; `SAME`-forever-while-covered would falsify it.

### Q3 — Reveal / click-through: how does a tray click open the real item's menu above the overlay? → **VIABLE**

Cleaner than the current off-screen reveal dance. Because the items are **never moved**, their live
on-screen coordinates are always valid — `TrayServiceImpl.forwardClick` already re-reads the item's live
bounds and AX-presses at its centre, and that keeps working with no "collapse-then-reveal-then-rehide"
timer (`forwardClickWithReveal` can drop the reveal step entirely for overlay mode). The resulting menu
is a system pop-up at level ~101, which is **above** the level-26 overlay, so it shows normally. The
overlay itself is `ignoresMouseEvents = true` (click-through), so it never intercepts the synthetic
click aimed at the covered item, and a user's stray click on the strip passes through to the real item
rather than being eaten by an inert panel. The only cosmetic compromise is identical to today's shipped
behaviour and documented as unavoidable (C3 is dead): the item's highlight and its menu appear at the
**top** of the screen, not at the tray. Options considered and rejected: *temporarily hiding the
overlay* on click (adds a flash and re-introduces a freeze window) and *positioning the overlay to not
block the item* (defeats the hide). Click-through + higher-level menus is the clean path.

### Q4 — Notch + multiple displays → **VIABLE (notch is a non-issue; multi-display is per-screen, well-trodden)**

- **Notch:** status items always sit to the **right** of the notch. The overlay covers only the
  right-hand status strip (from the leftmost managed item to the right edge), which is entirely to the
  right of the notch — so the overlay and the notch never overlap and no notch math is needed. (This is
  simpler than Ice's tint overlay, which spans the full width to tint the app-menu side too and
  therefore carries `hasNotch` inset code.) Use `screen.safeAreaInsets.top` (the notch/menu-bar height on
  notched panels) vs `NSStatusBar.system.thickness` (non-notched) for the strip height — the PoC does
  exactly this.
- **Multiple displays:** one overlay panel per screen that shows a menu bar, each positioned from its
  own `NSScreen.frame`, `collectionBehavior` including `canJoinAllSpaces`/`moveToActiveSpace` so it
  follows Spaces. Ice's model is one `MenuBarOverlayPanel` per `owningScreen` held in an
  `overlayPanels` set — mirror that. v1 may cover only the primary display (where Bevel's taskbar and
  tray live) and add secondaries later; enumerate `NSScreen.screens` and skip mirrored displays.

### Q5 — Do we still need the control `NSStatusItem`? → **NO for hiding; a hybrid is optional, not required**

Overlay-hide replaces the *hide* function of the expanding control item outright — nothing is pushed
off-screen, so the `10_000`-length expansion, the fragile `autosaveName` + preferred-position anchoring,
and the off-screen coordinate math all go away. Recommendation: **retire the expand-to-hide control item
for overlay mode**; keep `TrayServiceImpl`'s CGWindowList enumeration and `LegacyWindowCapture`, which
are orthogonal and still needed.

Comparison:

| | Off-screen push (today) | Overlay-hide (proposed) |
|---|---|---|
| Live captures | ✗ frozen (the bug) | ✓ items stay on-display and redraw |
| Item coordinates for click | off-screen; needs reveal/rehide dance | live + stable; direct AX-press |
| Fragility | high (10 000-length + preferred-position, "fragile across macOS releases" per Ice) | moderate (window level + per-screen placement) |
| Fighting the OS | layout engine | compositor/occlusion |
| Cover artwork | none needed | must paint a convincing strip |
| Stray user clicks on strip | impossible (items gone) | pass through (click-through) — real item could open; benign |
| Notch / multi-display | off-screen math per Ice | per-screen panel; notch avoided by right-strip-only |

**Optional hybrid:** keep a *zero-width, non-expanding* control `NSStatusItem` purely as a stable
self-exclusion anchor and a reveal hotspot, but do **not** expand it. Not required — the enumerator can
self-exclude by the overlay's own window id and the `◂` marker as it does now. Prefer the simpler
overlay-only design unless a concrete need for the anchor appears.

### Q6 — Where it lives in code → sketch

New file **`src/Bevel.Pal.MacOS/MacMenuBarOverlay.cs`**, a static class mirroring the shape of
`MacMenuBarControl` (main Bevel app process, AppKit main thread — the app has the real `NSApplication`
run loop; the gRPC helper does not, per the `MacMenuBarControl` header note). It creates the panel(s) via
the existing `AppKitInterop` objc bridge and is driven by the same settings-apply path that today calls
`MacMenuBarControl.SetHidden`.

```csharp
public static class MacMenuBarOverlay
{
    // one retained NSPanel per covered screen
    private static readonly List<IntPtr> _panels = new();

    public static void SetHidden(bool hidden)   // called where MacMenuBarControl.SetHidden is today
    {
        if (hidden) EnsureCreated(); else RemoveAll();
    }

    private static void EnsureCreated()
    {
        AppKitInterop.EnsureAppKitLoaded();
        // for each NSScreen: compute the right-strip frame (AppKit bottom-left coords),
        //   alloc NSPanel, initWithContentRect:styleMask:backing:defer:
        //   setLevel: (kCGStatusWindowLevel + 1), setCollectionBehavior:,
        //   setOpaque:YES, setHasShadow:NO, setIgnoresMouseEvents:YES,
        //   setBackgroundColor: (matched fill / wallpaper layer),
        //   orderFrontRegardless, retain, add to _panels
    }
    // RemoveAll: [panel orderOut:nil]; [panel release]; clear
}
```

Interop additions to **`AppKitInterop.cs`** (the only genuinely new native surface):

- `NSPanel`/`NSWindow` creation needs `initWithContentRect:styleMask:backing:defer:`, which **passes** an
  `NSRect` (already declared as a 4-double struct — an HFA passed in `v0–v3` on arm64) plus two `nuint`s
  and a `bool`. Add one `objc_msgSend` overload with that signature. Frame updates likewise need a
  "pass-`NSRect`" `setFrame:display:` (or `setFrameOrigin:` with the 2-double `NSPoint`). The file
  already has the *return*-`NSRect` overload (`SendNSRect`) and NSScreen frame helpers to compute
  placement from, so this is additive and low-risk.
- A couple of new selectors/`setBool:` overloads (`setLevel:` is `SendVoid` on an `nint`; `setOpaque:`,
  `setHasShadow:`, `setIgnoresMouseEvents:`, `setCollectionBehavior:` are `void`+`nint`/`bool`). These
  mirror `TaskbarNative`'s existing setters — consider promoting the shared ones.

Wiring: replace (or gate behind a mode flag) the call site that invokes `MacMenuBarControl.SetHidden`
with `MacMenuBarOverlay.SetHidden`. The Swift-side `SetConsolidation` RPC and the tray enumerator need
**no change** — enumeration already includes the (now on-screen, covered) items and `enumerateWithCapture`
already uses `LegacyWindowCapture` for their icons. Self-exclusion of the overlay: the panel is an
ordinary window, not a status item at layer 25, so it is already excluded by the enumerator's
`layer == 25` filter (bonus over the control-item case, which needed a window-id/`◂` exclusion).
`forwardClickWithReveal` can short-circuit its reveal/rehide timers in overlay mode (items are never
hidden), simplifying that path.

## Risks

1. **Occlusion-throttle for a self-hosted third-party item (Q2).** The one falsifiable risk. Mitigation:
   keep a hair of translucency / a 1px sliver so occlusion never reads fully-hidden; or fall back to the
   off-screen push for specific apps. **The PoC measures this directly — run it before building.**
2. **Fullscreen / auto-hidden menu bar.** When the menu bar is system-hidden the overlay must hide too
   (`alphaValue = 0`), or it floats as a stray strip. Mirror Ice's `isMenuBarHiddenBySystem` handling
   and observe `NSApplication` presentation / active-space changes (Bevel already tracks screen changes
   in `TaskbarWindow`).
3. **Cover artwork fidelity.** An opaque strip that doesn't match the menu-bar background reads as a bug.
   Options: solid menu-bar-matched fill (simplest), or the wallpaper clipped to the strip like Ice's
   `updateDesktopWallpaper`. v1: matched fill; upgrade later. Must respect light/dark and per-display
   wallpaper.
4. **Non-managed items in the covered region.** The overlay covers a *contiguous* strip, so it also
   hides Control Center / Wi-Fi / clock if they fall inside it — same contiguity limitation the
   off-screen push has (it pushes everything left of the divider). Decide the covered span from the
   managed set's leftmost x; keep the deny-list items *right* of the cover or accept they're hidden too.
5. **Menu bar item reflow moves the covered span.** Items are added/removed live, shifting the leftmost
   managed x. Recompute the overlay frame on the same poll that re-enumerates (the `Changes` stream
   already re-enumerates on a cadence), and on `didChangeScreenParameters`.
6. **Multi-display Spaces.** Getting one panel per screen to follow Spaces correctly is fiddly; Ice has
   real code here. Start primary-display-only.
7. **Stray click opens a real menu at the top.** With click-through, a user clicking the covered strip
   hits the real item and its menu pops at the top — surprising but benign. Acceptable for v1; could add
   a transparent-but-click-eating region later if it annoys.

## Recommendation

Overlay-hide is the right mechanism for the live-updates requirement, and it is *simpler* than the
off-screen push on the dimensions that matter (stable coordinates, no reveal dance, no fragile
preferred-position anchoring). It trades one hard problem (off-display freeze — unfixable) for a softer,
measurable one (occlusion redraw — likely fine, PoC-verifiable, with an easy translucency fallback).

**Next step:** the owner runs `native/helper-macos/menubar-overlay-poc.swift` on their Mac. If covered
items keep printing `CHANGED`, implement `MacMenuBarOverlay.cs` as sketched and swap the hide call site;
keep the off-screen push behind a flag as a fallback for any app the PoC flags as freezing under
occlusion.

## References

- `docs/design/menubar-management.md` — Strategy C teardown; the off-display-freeze finding this fixes.
- `src/Bevel.Pal.MacOS/MacMenuBarControl.cs` — the control item being replaced for the hide function.
- `src/Bevel.Pal.MacOS/AppKitInterop.cs` — objc bridge to extend (NSRect-passing msgSend, window setters).
- `src/Bevel.Taskbar/TaskbarWindow.cs` — existing borderless-window level/collectionBehavior pattern to copy.
- `native/helper-macos/Sources/BevelHelper/TrayServiceImpl.swift` + `LegacyWindowCapture.swift` — the
  enumerator/capture that need no change; overlay keeps items in the "occluded, on-display" state they rely on.
- Ice (`jordanbaird/Ice`, MIT): `MenuBar/Appearance/MenuBarOverlayPanel.swift` (level-25 panel over the
  strip), `UI/IceBar/IceBar.swift` (`.mainMenu + 1`, per-screen panel), `Utilities/WindowInfo.swift`
  (`isOnScreen ← kCGWindowIsOnscreen`, the on-display vs off-display distinction). Note: Ice itself does
  **not** overlay-hide — it pushes off-screen (`ControlItem`) and, when it needs live items, renders them
  in the separate **Ice Bar** panel rather than covering the real bar. Overlay-hide is Bevel-specific;
  the Ice Bar is the philosophical cousin (show captured items in a Bevel surface) but Bevel's surface is
  the bottom tray, not a floating menu-bar panel.
```
