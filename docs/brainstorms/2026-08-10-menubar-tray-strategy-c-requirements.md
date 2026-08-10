# Requirements — Strategy C: live functional menu-bar tray

**Date:** 2026-08-10 · **Bead:** bevel-7hf4 · **Status:** brainstorm complete, design de-risked by live spikes (macOS 26.5)

## Problem & goal

macOS menu bars get crowded. Bevel is a Windows-style shell and wants the system tray where a Windows
user expects it — the **bottom taskbar**. Today Bevel screenshot-mirrors menu-bar extras into its tray
but renders **placeholder icons** (per-item SCK capture returns empty). Goal: real, glanceable,
clickable menu-bar items living in Bevel's bottom tray, de-duplicated from the real macOS bar.

## Outcome

The user sees their real menu-bar icons — live-updating — in Bevel's bottom taskbar tray; the macOS
menu bar is decluttered (items hidden via the proven control-item expansion); clicking a tray icon
interacts with the real item.

## Design — stack `A + F3 + C2 + C1` (each element proven or grounded in a live spike)

- **A — Hide / anchor (PROVEN live).** Expand an anchored control `NSStatusItem` to hide real items;
  narrow to reveal. Anchor = `statusItem.autosaveName` + the `NSStatusItem Preferred Position <name>`
  UserDefault (matches Ice). Without the anchor our control item flies off-screen; with it, it stays put.
- **F3 — Live capture (capability PROVEN).** Capture each item's window **by ID** via
  `SCContentFilter(desktopIndependentWindow:)`. Proven to return real pixels **cross-process** and while
  the item is **off-screen or occluded**. Freshness = adaptive: baseline triggers (item add/remove/move,
  Space change) + a high-rate `SCStream` burst while the tray is visible/hovered. Capture the whole
  menu-bar region and crop per item (Ice's approach) — not per-item capture, which is today's bug.
- **C2 — Reveal-at-top click (universal spine).** Click a tray icon → temporarily show the real item at
  the top (Ice temporary-show) and let its menu open there, with a designed "lift" motion so the jump
  reads as intentional. Works for every app.
- **C1 — Native-bottom proxy (readable subset).** For items backed by a readable `NSMenu`, rebuild it as
  a native Bevel (Luna/Win2000) context menu at the bottom, forwarding selection via AX. On-brand; only
  covers the subset whose menu is readable without opening it.

## Rejected / out of scope

- **C3 — move the real item window to the bottom via private CGS (`CGSMoveWindow`): PROVEN DEAD.** The
  spike moved nothing; the WindowServer ignores cross-connection window moves. Ice links private CGS yet
  still refuses window moves (it reorders via synthetic drags, horizontal-only). **True bottom-native
  menus are off the table** — this is why the click uses reveal-at-top + proxy, not relocation.
- **Full menu relocation for arbitrary apps:** impossible; an app's menu/popover anchors to its real
  item, which we cannot move.

## Key constraints & findings (from the spikes)

- **macOS 26 (Tahoe) ownership:** *every* status item — ours and third-party — reports owner
  **"Control Centre"** (the ControlCenter process hosts them). **Cannot identify items by owner PID/name.**
  Identification must track **window IDs + positions** (and possibly AX). Affects both excluding our own
  control items and labelling which app owns each tray icon.
- **Bundle identity is mandatory** for any `NSStatusItem` (a bare executable creates none); the control
  item lives in the Bevel `.app`. Capture needs the Screen Recording grant (Bevel has it).
- **Never block the UI thread:** all hide/capture in the Swift helper; push BGRA over the existing mmap
  pool; marshal only cheap results to the UI.

## Success criteria

- Tray shows **real** (non-placeholder) icons for all menu-bar extras.
- Icons update live (battery %, spinners) with no perceptible staleness while the tray is visible.
- The real menu bar is decluttered, with our control item stable and clickable.
- Clicking a tray icon reaches the real item's menu/popover — reveal-at-top spine, native-bottom for the
  readable subset.

## Ordered implementation spikes (hand-off to ce-plan)

1. **Whole-region composite capture + crop** to replace per-item capture → fixes the placeholder wall;
   confirm all items render real icons in one pass.
2. **Identification on Tahoe:** track item window IDs across reflows/Space changes; exclude our own
   control items by ID (not PID); map windowID → owning app (AX or heuristics).
3. **Adaptive freshness (F3):** triggers + `SCStream` burst-while-visible; measure CPU.
4. **C2 reveal-at-top:** temporary-show + synthetic click; verify menu opens at top and rehides.
5. **C1 subset:** detect readable-`NSMenu` items; rebuild as a native Bevel bottom menu.
6. **Integrate:** retire the placeholder mirror; wire hide/reveal + live tray render end-to-end.

## Outstanding questions

- Freshness cadence: acceptable idle refresh vs CPU; is `SCStream`-while-visible sufficient?
- Does hidden-capture stay reliable for **every** item on Tahoe? Our expansion reflows some items
  on-screen rather than off; proven for off-screen (our item) and occluded (a third-party item) samples.
- Window-ID stability across reflow / Space changes (identification robustness).
- C1 subset size: how many real apps expose a readable `NSMenu` without opening it? (May be small.)

## Spike artifacts

- `native/helper-macos/menubar-hide-poc.swift` — A (hide/anchor), proven.
- `native/helper-macos/menubar-c3-move-poc.swift` — C3 (private-API move), proven dead.
- `native/helper-macos/menubar-capture-poc.swift` — capture-while-hidden, proven.
- Full teardown + frame data: `docs/design/menubar-management.md`.
