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

## How Ice does it (teardown)

Ice does **not** move other apps' items directly (macOS exposes no public "set status-item position").
Instead:

1. **Own control items.** Ice creates its *own* `NSStatusItem`s that act as **section dividers**:
   `visible | hidden | always-hidden`. These are the only items Ice can freely reposition/resize.
2. **Collapse by expansion.** macOS lays status items out **right-to-left**. To hide a section, Ice
   sets its divider item's `length` to a large value (autosize) so the divider **occupies the space
   and pushes the items to its left off the visible bar** (off the left edge / under the notch). The
   items still exist — they're just positioned off-screen.
3. **Reveal.** Clicking the control item (or a hotkey / hover) collapses the divider back to a small
   length, letting the hidden items slide back into view. Ice also supports auto-rehide on timeout.
4. **Ordering.** Items are ordered by their status-item position; a user drags items across the
   divider (⌘-drag) to choose which are hidden. Ice reads live positions via CGWindowList (as we do)
   to know what's where and whether a section is currently visible.

Key insight: **the whole trick is one wide status item we own** + reading positions. No private API is
strictly required for the basic hide; AX is used for richer features (clicking hidden items, identity).

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

**Verdict:** feasible. Build it in the Bevel `.app` (has bundle identity, a run loop, and TCC grants),
port Ice's `ControlItem` positioning + notch handling; the raw hide primitive is proven.

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

## References

- Ice — `jordanbaird/Ice` (MIT). Study its `ControlItem`, `MenuBarSection`, and the length/position
  handling for collapse + notch.
