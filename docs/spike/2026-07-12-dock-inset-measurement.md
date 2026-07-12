# Dock-inset measurement (Req 9.4) — findings

**Bead:** bevel-3qc · **Date:** 2026-07-12 · **Spec:** [02-macos-platform.md §9](../spec/02-macos-platform.md)

## Goal

Req 9.4 requires measuring the Dock inset that strict **Dock-shim** mode (Req 9.3) would use
for the taskbar height — "strict-mode taskbar height derives from these measurements, never
from guessed constants." The measurement must record: (a) `visibleFrame` with Dock auto-hide
on vs off (asserting the inset disappears when auto-hidden); (b) the minimum Dock inset per
edge; (c) inset behaviour on scale/display changes.

## Method

Standalone Swift probe (`scratchpad/dockinset.swift`), run as a **registered GUI app**
(`NSApplication.setActivationPolicy(.regular)` + run-loop pump — a bare CLI process sees the
menu bar but not the Dock in `visibleFrame`). It reads `NSScreen.frame` vs `visibleFrame`
for every screen, toggling `com.apple.dock autohide` and `killall Dock` between states, then
restores the original preference. Bevel was stopped during measurement so its DockController
auto-hide healing could not fight the toggle.

## Results (this machine)

- Environment: macOS 15, single display **1728×1084 @2×**, Dock `orientation=bottom
  tilesize=64`, user preference **`autohide=1`**. (Note: `NSScreen.main` reports 1728×1084,
  while CGWindowList / the Bevel taskbar see 1710×1074 — a BetterDisplay virtual-display
  artifact.)

| State | top inset (menu bar) | bottom inset (Dock) |
|-------|----------------------|---------------------|
| A — current (`autohide=1`) | 30 pt | **0 pt** |
| B — Dock forced visible (`autohide=false` + `killall Dock`) | 30 pt | **0 pt** |
| C — Dock auto-hidden (`autohide=true` + `killall Dock`) | 30 pt | **0 pt** |

## Findings

1. **`visibleFrame` reports the menu-bar inset (30 pt) reliably, but the bottom Dock inset is
   0 in every state — even with the Dock forced visible.** On this environment `NSScreen
   .visibleFrame` is *not* a usable source for the Dock inset. (Likely interaction with the
   BetterDisplay virtual display; the process's `visibleFrame` never reflects the Dock
   reservation even as a `.regular` app with the run loop pumped.)
2. **The user keeps the Dock auto-hidden by preference (`autohide=1`).** Dock-shim mode's
   whole premise is a *visible* Dock reserving space — directly contrary to this preference.
3. Req 9.4(a) (inset disappears when auto-hidden) is technically satisfied (bottom inset is 0
   auto-hidden), but 9.4(b) (minimum visible Dock inset) could **not be obtained** here.

## Implication for Req 9.3 (Dock-shim strict mode)

The measurement prerequisite cannot be satisfied reliably on this hardware, and strict mode
would force a visible Dock against the user's preference while inflating the taskbar from the
authentic 30 pt to the Dock inset (spec: ~45–60+ pt, unmeasurable here). The **Nudge** strategy
(default, already implemented and verified for drag + zoom) meets the need without these costs.

**Recommendation:** keep Nudge as the strategy; treat Dock-shim as *measured-and-deferred*
pending a reliable inset measurement on reference hardware (standard displays, no virtual-display
layer). This resolves the spec's open question Q6.461 toward "do not ship strict mode yet" for
this environment. Re-run this probe on reference hardware before implementing Req 9.3.
