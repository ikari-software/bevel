# AppleScript compatibility corpus (M4-E / bevel-4qr)

Real-world `tell application "Finder"` snippets that Bevel's Apple Events tier 1 must handle after a
mechanical retarget. Sourced from the patterns Alfred workflows, Hammerspoon configs, and app
"Reveal in Finder" implementations actually emit (08-os-interop.md §2.1.2).

## The retargeting rule

Each `*.applescript` here is written against **Finder**. The harness runs each one twice:

1. verbatim against Finder (baseline — establishes the expected effect), then
2. with a one-token rewrite `application "Finder"` → `application "Bevel"` (what
   `bevelctl translate-script` does) against Bevel.

A snippet **passes** when Bevel's observable effect matches Finder's (the right window opens /
selection is set / folder is created / item is trashed / query returns the same value).

## Exit criteria (v1, matches 09-engineering-plan.md M4 acceptance 1)

- **100%** of the six core verbs' single-statement forms pass
  (`reveal`, `open`, `select`, `make`, `delete`, `duplicate` — `move` is duplicate+delete).
- **≥ 30 / 40** corpus scripts behave correctly after the mechanical retarget.

Post-v1 quality campaign (not an M4 gate): grow to ≥ 300 harvested scripts; target ≥ 80% of
app-emitted AEs and ≥ 50% of the general corpus.

## Out of scope (counted as expected failures)

- System Events UI scripting of Finder (`tell application "System Events" … tell process "Finder"`)
- `entire contents` (unbounded recursion)
- Finder-window chrome manipulation beyond `target` / `current view`

## Running it

`run-corpus.sh` mechanically retargets each snippet (`application "Finder"` → the Bevel target) and
runs it via `osascript`, classifying pass / fail / out-of-scope, and prints the `≥30/40` verdict.

```
bash run-corpus.sh                 # against the ae-probe2 harness (default)
BEVEL_TARGET=Bevel bash run-corpus.sh   # against the running real app
```

**Target matters.** Against the *minimal* ae-probe2 harness only the tier-1 mutation/reveal verbs
(reveal/make/delete/duplicate/move + object specifiers) are handled, and each passes when run in
isolation (verified). Two things break a *batch* run against the harness, and both are real findings:

1. `activate` / `open` (`odoc`) reach NSApp's defaults, which terminate the window-less harness —
   the same lifecycle quirk the AE spike hit. The full app has windows and is unaffected.
2. The **query verbs** `get selection`, `count windows`, `get home`, and `set selection` are not
   implemented inbound yet — they need `core`/`getd`/`setd`/`cnte` handlers that write a *result* into
   the reply descriptor (filed follow-up). Until then they return `errAEEventNotHandled`.

So the meaningful `≥30/40` gate runs against the **real app** (which handles the lifecycle + query
verbs); the harness is a smoke test for the mutation verbs.

## Status

This directory is the **corpus + the contract**. The runner is a stub until the native
`NSAppleEventManager` handler lands (M4-C, `bevel-376`): AE delivery requires the running Bevel.app
process, so the scripts can only execute end-to-end once that handler feeds
`AppleEventObjectResolver` → `IShellAutomation`. Until then these files are reviewed as the target
surface and drive the resolver's unit tests.

Current starter set: 12 snippets across all six verbs + property/query/whose forms. Grow toward 40
before the M4 exit gate.
