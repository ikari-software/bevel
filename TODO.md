# TODO — agent handoff (2026-09-30)

> **Source of truth is `bd` (beads), not this file.** This is a one-off handoff snapshot requested by the owner
> (ikari). Each item links its bead; update the bead, not this list. Run `bd prime` first.

## Where things stand

The IP-risk work (report: `~/Downloads/Bevel Software IP Risk Analysis.md`, brief: `docs/legal/2026-09-29-branding-research-brief.md`,
parent bead `bevel-legal-branding`) is largely done in engineering. **Changes 1–5** landed; residual counsel asks
are parked in `docs/legal/open-questions-parked.md` (not a build gate). Working-tree branding/docs edits for
**Bevel Desktop** + theme git/TBD policy may still be uncommitted until the owner asks.

## Owner's hard rules (do not break)

- **Do NOT commit or push unless asked.** Never add `Co-Authored-By: Claude` / "Generated with Claude Code" anywhere (global CLAUDE.md).
- **Discuss and brainstorm every change with the owner before making it** (explicit standing instruction today).
- Task tracking = `bd`. macOS/BSD shell syntax. Never `kill -9` the shell (hides the Dock): `pkill -f "dist/Bevel.app/Contents/MacOS/Bevel$"`.
- Never `git checkout --`/reset a file with uncommitted work; read the full diff, not `--stat`.
- Vector-only assets; never disable antialiasing; fix root causes, not symptoms; no hiding dead controls.
- Rebuilding `dist/Bevel.app` makes the owner's LIVE shell relaunch itself (expect 2 hops in dev: dev-sign rewrites the binary
  after build). Tell the owner before rebuilding.

## Decisions already made (don't relitigate)

| Topic | Decision |
|---|---|
| Start badge | Bevel mark, C3b (core inside translucent glass, spectrum edges on all 9 edges). Tux stays on Linux. |
| Sizes | Badge 20px in both themes via token. Full mark (core + rays) from 24px up; plain glass (C2g) below. Checkbox forces full at 20px. |
| Glass tints | Fixed translucent tints (button colour shows through) — NOT theme tokens. |
| Theme names | Use the report's names: Luna (XP) → **Bevel 2001 Blue**; Classic → **Bevel 1999 Industrial**. **Never "Aero"** (Microsoft mark, the report wrongly suggests it). Internal ids `win2000`/`luna`/`flat` stay. |
| Fixtures | Replace committed macOS icons with open-licensed ones, then purge history (repo is **private** now — cheapest moment). |
| Product name | **Bevel Desktop** (ids stay `pl.ikari.bevel` / `bevel://`). |
| Trade dress | Owner posture: clean redesign + deep cross-OS customisation; not waiting on counsel. Parked asks in `docs/legal/open-questions-parked.md`. |
| Themes from others | No hosted gallery; git repos OK; licensing blur TBD. |

## Still to do

### Change 2 — rename themes · `bevel-qkx5` ✅
### Change 3 — copy, disclaimer, About box · `bevel-rgr9` ✅

### Change 4 — icon fixtures · `bevel-fztj` — fixtures replaced; history purge still pending
Bevel-drawn Browser/Console/Viewer/Calculator (SVG in `src/`). Apple PNGs removed from HEAD.
**Destructive purge of old blobs from history still needs an explicit go-ahead.**

### Change 5 — screenshots + app icon · `bevel-g35v` ✅
Re-harvested `site/shots/` (hero Start badge is BevelMark). App icon: `packaging/macos/BevelMark.svg` → `build-icns.sh` → `Bevel.icns`; site `#logo` is the cube.

### Not yet scheduled (raise with the owner; each needs its own discussion)
- Git-repo theme install UX + `theme.json` provenance → bead **`bevel-te20`**.
- Clearance search for the cube mark (optional; parked).
- Attach `bevel.run` on Deno Deploy (`bevel-site`) + push site; deploy ikari-software alias.
- Opt-in `updateFeedUrl=https://bevel.run/updates` once you want checks (default stays empty).
- Commit housekeeping when asked.

## Two pre-existing test failures · `bevel-yslj` ✅ fixed

Optimistic click/minimize now claims through `ShellModel.ClaimFocus` → `ApplyExclusiveFocus` (not bare
`IsFocused = true`). Tests expect exclusive optimistic press.

## Other open items from earlier today (full brief was sent to the Cursor agent, `herdr` pane `w5:p2`, name `cursor-helper`)

Unverified-live / open: Jump Desktop other-Space activation; Kiro per-window activation has no raise (SkyLight key-focus only; AX returns -25211 for
Kiro); Kiro title flips between "minimal_test.py"/"Untitled-1"; pressed state for AX-less multi-window apps; every app's other-Space windows
now stay listed (confirm desired); launcher skew relaunch has **no relaunch-count guard**; Swift spawner tests skipped (terminal not AX-trusted);
hidden AX-less apps appear after ~4s. Details in the Swift files' doc comments + CODEMAP (BevelHelper bullet).

## Housekeeping

- `.beads/issues.jsonl` is modified by `bd` — normal; commit it with the related work if the owner wants.
- Untracked, NOT ours to commit: `pi-session-*.html` (repo root), `.context/`, `tools/eval/__pycache__/`.
- Scratch renders/sketches from today's design work live in the session scratchpad (`.../scratchpad/badge/`), not in the repo.
