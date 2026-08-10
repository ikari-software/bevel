# Menu-bar management (Ice-style hide/reveal) — spike

**Bead:** bevel-7hf4 · **Status:** spike / design · **Supersedes direction of:** bevel-voqo (tray capture-mirror)

## Why

Bevel's tray currently **screenshot-mirrors** the macOS menu-bar extras (ScreenCaptureKit per item)
into its own taskbar tray. In practice that produces a wall of **generic placeholder icons** (capture
comes back empty → owning-app fallback → a default), and it fights macOS to re-draw pixels it doesn't
own. The better model — proven by **Ice** (`jordanbaird/Ice`, MIT) and Bartender — is to **manage the
real items in place**: hide/collapse the actual status items behind a control item and reveal them on
demand. No pixel capture, no rebuild flakiness, real icons because they're the real items.

We already hold the **Accessibility** grant (the helper is a trusted AX client) and already have the
enumeration + click primitives, so we're well-positioned.

## What we already have (helper, `TrayServiceImpl.swift`)

- **Enumeration:** `CGWindowListCopyWindowInfo` filtered to the status-item window layer
  (`kCGStatusWindowLevel` = 25) in the menu-bar Y-band → each item's **owner PID, window number,
  bounds (x/width/frame)**, and identity (`kCGWindowName`/owner). This is exactly the position data
  Ice needs to reason about ordering and off-screen state.
- **Interaction:** `forwardClick` → `pressViaAX` (`AXUIElementCopyElementAtPosition` at the item's
  point + `AXUIElementPerformAction`) already clicks a status item via AX.
- A deny-list and a live/limited-mode self-test already exist.

## How Ice does it (teardown — read from `jordanbaird/Ice` source, not memory)

Ice offers **two** strategies, chosen by the `useIceBar` setting:

### Strategy A — expand a control item to push items off (`ControlItem.swift`, `MenuBarSection.swift`)

1. **Own control items as section dividers.** Ice creates three `NSStatusItem`s — `iceIcon` (the
   visible, clickable one), `hidden`, and `alwaysHidden` (`ControlItem.Identifier`). Each is created
   `withLength: 0`; Ice removes the internal min-width auto-layout constraint so a divider can be
   present-but-invisible (needed to delimit which real items belong to which section). *Comment in the
   source flags this as fragile across macOS releases.*
2. **Anchor via `autosaveName` + `preferredPosition`.** Each control item sets
   `statusItem.autosaveName` and seeds `StatusItemDefaults[.preferredPosition, autosaveName]`
   (iceIcon → 0, hidden → 1). This is the private per-status-item position store macOS itself uses;
   it's what keeps the control items at stable, predictable slots. **This is the piece our PoC lacked
   — with no `autosaveName`/`preferredPosition`, macOS parked our item at x=−5056.** `deinit` even
   caches and restores `preferredPosition`, because removing a status item wipes it.
3. **Hide = expand.** `updateStatusItem(with:)` sets the length: the `visible` section's item is always
   `Lengths.standard` (`NSStatusItem.variableLength`); a `hidden`/`alwaysHidden` item is
   `Lengths.expanded` (**`10_000`**) when `state == .hideItems`, `.standard` when `.showItems`.
   The expanded (10 000pt, clamped to the bar) divider occupies the space and pushes the items to its
   left off-screen — exactly the frame behaviour our PoC observed.
4. **Reveal / rehide.** `MenuBarSection.show()/hide()` flips every section's `controlItem.state`;
   `startRehideChecks()` drives auto-rehide (timer / mouse monitor). Show-on-hover is also supported.

### Strategy B — the "Ice Bar" (`UI/IceBar/IceBar.swift`)

When `useIceBar` is on, Ice does **not** fight the menu-bar layout. `show()` opens a **separate floating
panel** that renders the hidden items, and leaves the real items collapsed. This dodges the notch and
positioning problems wholesale — at the cost of the hidden items living in a panel, not the real bar.

### Also: spacing (`MenuBarItemSpacingManager.swift`)

A separate feature: Ice changes global item spacing by writing the `NSStatusItemSpacing` /
`NSStatusItemSelectionPadding` UserDefaults and **relaunching the affected apps** (it force-terminates
and restarts them). Not part of hide/reveal, but shows Ice reaches for UserDefaults + relaunch, not AX
dragging, to reorder/space real items.

**Corrected key insight:** the expand-to-10 000 trick is real (our PoC matched it), but the thing that
makes it *usable* is `autosaveName` + `preferredPosition`, not hand-rolled coordinate/notch math. And
there's a legitimate alternative (Ice Bar) that avoids menu-bar positioning entirely.

## Proposed Bevel design

- **Ownership.** The control status item(s) must live in a process that can own `NSStatusItem`s. The
  helper is `.accessory` (can own status items) and already owns the menu-bar domain — natural home —
  but confirm `.accessory` status items behave (some status APIs want a regular UI app). Fallback: the
  main Bevel app owns them and talks to the helper over gRPC.
- **Sections (v1 minimal):** one divider → `visible | hidden`. Expand-to-hide + click-to-reveal. Add
  `always-hidden` later.
- **"Owned" set:** which items to hide = everything left of the divider, minus the deny-list and
  Bevel's own control item. Persist the user's divider choice.
- **Reveal affordance:** click the control item; optional global hotkey; optional auto-rehide.
- **Retire the SCK capture-mirror** (bevel-voqo) once hide/reveal works — it becomes redundant and it's
  the source of the placeholder wall. (Keep the CGWindowList enumeration; drop the per-item capture.)

## Empirical findings (PoC — `native/helper-macos/menubar-hide-poc.swift`)

Validated live on macOS 26.5, 1728px display, a crowded menu bar. Conclusion: **the mechanism works;
the real work is control-item positioning.**

- **A bundle is mandatory.** A bare compiled executable creates *no* menu-bar item — `NSStatusBar` has
  nothing to attach to without LaunchServices identity. Wrapped in a minimal `.app` (Info.plist +
  `LSUIElement`, ad-hoc signed) the item registers.
- **Create it in `applicationDidFinishLaunching`.** Building the `NSStatusItem` before `app.run()`
  silently no-ops; the status bar isn't ready until launch completes. Needs an `NSApplicationDelegate`.
- **A wide item hides its left neighbours — confirmed.** At `length = 10_000` the item's window is
  `(-136, 1054, 5002, 30)`: it spans the whole visible bar (0–1728) and overflows both edges, pushing
  every item to its left off-screen-left. This is the Ice hide, working.
- **Naive length-toggle mis-parks the control item.** Back at `length = 80` the item's window lands at
  `(-5056, 1054, 82, 30)` — ~5000px off-screen left, so *our own* control item is invisible/unclickable.
  This is the crux Ice solves: anchor the control item at a stable, visible slot and drive hide/reveal
  from there, rather than trusting the layout engine after a width change.
- **Anchoring fixes it — CONFIRMED LIVE.** Seeding `UserDefaults.standard["NSStatusItem Preferred
  Position BevelPocItem"] = 0` before creating the item and setting `statusItem.autosaveName` keeps the
  control item on-screen and stable through the length toggle. Observed live: the `◀BEVEL` item is
  visible in narrow state, and expanding to 10 000 hides the crowd to its left while the control item
  stays put. **The full hide/reveal round-trip works.**

**Verdict:** feasible. Build it in the Bevel `.app` (has bundle identity, a run loop, and TCC grants).
The hide primitive is proven; the fix for our off-screen control item is `autosaveName` +
`StatusItemDefaults[.preferredPosition]` (per the Ice teardown above), **not** custom geometry. Decide
up front between Strategy A (expand-to-hide on the real bar) and Strategy B (Ice Bar floating panel).

## Risks / open questions

- **macOS version fragility + the notch.** Off-screen-left math must account for the notch and
  multiple displays; Ice has a lot of code here — study it before trusting a naive width.
- **`.accessory` status items:** verify the helper can own a working `NSStatusItem` with a controllable
  `length`, or move ownership to the app.
- **Live-validation only:** none of this is headless-testable; it's runtime menu-bar behavior. Expect a
  build → run → observe loop (like the restart/TCC work).
- **Interaction with our clock/tray UI:** decide whether Bevel's taskbar keeps a tray region at all, or
  whether the menu bar becomes the single surface managed in place.

## Incremental steps

1. **PoC (helper or app):** create one control `NSStatusItem`; toggle its `length` small↔large; confirm
   live that expanding it pushes neighbours off the visible bar. (This is the make-or-break primitive.)
2. Wire a divider + persist position; hide = expand on launch, reveal = click.
3. Identify + label the hidden set from the existing CGWindowList enumeration.
4. Notch/multi-display correctness (study Ice).
5. Retire the SCK per-item capture; keep enumeration.
6. Reveal polish: hotkey, auto-rehide, drag-to-reorder.

## Strategy C spikes (2026-08-10) — live functional tray

Brainstorm + spikes for rendering menu-bar items *functionally* in Bevel's bottom tray (see
`docs/brainstorms/2026-08-10-menubar-tray-strategy-c-requirements.md`). Proven live on macOS 26.5:

- **C3 (move the real item to the bottom via private CGS) is DEAD — rigorously.** `menubar-c3-retry-poc.swift`
  adds a **positive control**: our own plain `NSWindow` moves via `CGSMoveWindow` (`err=0`, frame
  300→700) — so the API and binding are correct. A third-party status window (wid 59457, owner "Control
  Centre") returns `err=0` from **both** `CGSMoveWindow` and `SLSMoveWindow` yet the frame is unchanged:
  the WindowServer **accepts the call and silently no-ops** it. That's the signature of a cross-connection
  restriction — you can move windows your connection owns, not another connection's (on macOS 26 all
  status items are owned by the Control Centre connection). Ice corroborates: it links private CGS but
  never moves windows (reorders via synthetic drags, horizontal-only).

  **Two kinds of "move" — don't conflate them.** Status items DO move *horizontally* via the menu-bar
  layout engine: when our control item expands, neighbours reflow along the bar (observed live: a target
  slid x 585→1511, `y` stayed 0). That is real and is exactly how the hide works. What is impossible is
  *arbitrary* repositioning — changing an item's `y` to put it at the **bottom** of the screen, off the
  bar. Layout reflow never leaves the bar vertically, and `CGSMoveWindow` (the only API that could set an
  arbitrary y) no-ops cross-connection. C3 needs the second kind, so **true bottom-native menus are
  impossible** — the click uses reveal-at-top + native-bottom proxy instead.
- **Capture-while-hidden WORKS.** `menubar-capture-poc.swift` captured a status item by window ID via
  `SCContentFilter(desktopIndependentWindow:)` and got real pixels **cross-process** and while the item
  was **off-screen (our item at x=-3491)** and **occluded (a third-party item under our expanded control
  item)** — identical to the visible capture. This is the make-or-break for C, and it passed.
- **Tahoe ownership gotcha:** on macOS 26 *every* status item (ours + third-party) reports owner
  "Control Centre". Cannot filter/identify by owner PID/name — track window IDs + positions instead.
  (Also: `NSWindow.windowNumber` can be negative for status items — `CGWindowID(Int)` traps; identify by
  frame x.)

## References

- Ice — `jordanbaird/Ice` (MIT). Study its `ControlItem`, `MenuBarSection`, `MenuBarItemImageCache`
  (whole-region capture + crop), and the length/position handling for collapse + notch.
