# Flat — look delta (runtime vs. source-led target)

Pixel-fidelity pass for the Flat theme (Blue variant and base tokens) against the
supplied references and the approved source-led SVG targets. Companion file to
`source-analysis/README.md` (authoritative source facts) and
`source-analysis/reference-*.jpg`.

Method: all reference values below were **measured programmatically**
(python3 + PIL/numpy, JPEG-decoded RGB; SVG parsed as text). Runtime values were
sampled the same way from `renders/flat-window-blue.png`,
`renders/flat-taskbar-blue.png`, `renders/flat-startmenu-blue.png` and
`renders/flat-filer.png`. FlatTheme.axaml line numbers refer to the revision
being corrected (git `6f565f8` lineage, pre-fix).

Reference shorthand:

- **R-W** — `source-analysis/reference-whistler.jpg` (500x554)
- **R-CS** — `source-analysis/reference-classic-shell.jpg` (900x600)
- **R-SB** — `source-analysis/original-start-button.png` (58x66)
- **SVG-O** — `flat-watercolor-overall.svg` (approved overall target)
- **SVG-D** — `flat-watercolor-details.svg` (approved detail target)
- **README** — `source-analysis/README.md` (authoritative source facts)

---

## 1. Delta table

| # | Region | Reference value (source + region) | FlatTheme.axaml current (line) | Fix |
|---|--------|-----------------------------------|--------------------------------|-----|
| 1 | Window frame | Solid blue frame #3567CB, square (README direction; R-W/R-CS show a solid blue chrome band, R-W x=250 y=0..30) | `Bevel.Color.WindowFrame` #3567CB (17), `Bevel.Brush.WindowFrame` #3567CB (48) | **Keep.** Render `flat-window-blue.png` already draws a solid #3567CB frame (x=0..3, x=516..519) |
| 2 | Caption gradient, active | Saturated cobalt ramp **#4A85DF → #3570D6 → #2862C8** (README direction; the same ramp is already used by `Flat.Brush.Progress` 108-110 and `Blue2001.Brush.Start` 190). R-W x=250 y=0..30 measures a lighter periwinkle→slate wash ≈ #9CB6DB→#5473A8 — the watercolor *band family* (README §Material Separation), not the runtime ramp | `Flat.Brush.CaptionActive` #5E9CF9→#4A8CEF pastel (71-73); `Bevel.Brush.CaptionActive` same (121-123) | **Change both to the cobalt 3-stop** (0/.5/1). Render shows the pastel ramp (#5D9BF8→#4A8CEF over y=0..25) — confirmed wrong |
| 3 | Caption text | White on the saturated band (README: text white by state; SVG-O `ActiveCaptionText` white) | `ActiveCaptionTextColorKey` #FFFFFF (34), `Bevel.Brush.ActiveTitleText` #FFFFFF (59) | **Keep** |
| 4 | Caption buttons | ~21x19, fill **#3570D6**, 1px **#7AA4EB** border, white glyphs (README direction) | Size 21x19 (250) ✓; border #7AA4EB (65) ✓; glyph #FFFFFF (69) ✓; **fill `Flat.Brush.CaptionButton` #2862C8 (64) and `Flat.Brush.CaptionClose` #2862C8 (68) — too dark** | **Change fill to #3570D6** on both tokens. Hover #3B7AE6 / pressed #163F94 (66-67) already read as lighter/darker of the same hue — keep |
| 5 | Client area | Solid white `Main_background=#FFFFFF` (README; SVG-O line 34 white content rect) | `Bevel.Brush.Window` #FFFFFF (39), `Flat.Brush.MenuArea` #FFFFFF (174) | **Keep.** Render confirms white client |
| 6 | Menubar | Warm neutral shell face (README §Material Separation; SVG-O line 31 #EDEDEA, 22px) | `Bevel.Color.Menu` #ECE9D8 (19), `MenuBarHeight` 21 (233) | **Keep** — warm-paper per direction; SVG-O's 22px vs 21px within tolerance |
| 7 | Taskbar | Compact warm bar, height ≈30-classic (R-W bottom band ≈y=518..554, gray-green ≈#8E908D..#A1A3A0; R-CS task strip is warm gray) | `Flat.Brush.Taskbar` #F4F2E8→#ECE9D8 (86-88), `Bevel.Brush.TaskbarBackground` same (127-129), height 30 (235) | **Keep.** Render `flat-taskbar-blue.png` measures #EDEFE9→#D7D9CF — warm family ✓ |
| 8 | Start button | R-SB measured directly: face top #FFFFFF→#DEE0DA (y=1..20), hover wash #C7DCF6→#9BBEE6 (y=23..43), pressed #7C9FCC→#A3BEDC (y=44..65) | `Flat.Brush.Start` #EEF0EA→#E5E7E1→#D6D8CE (99-100), `StartHover` #BDD8F4→#93B7DF (102-103), `StartPressed` #8CAFD8→#B8D0EC (105-106) | **Keep** — same warm-gray/blue family as the source button; hues within JPEG noise of R-SB |
| 9 | Start menu — pinned column | White main column (README `Main_background=#FFFFFF`; slices X=10,144,2,2,168,10; R-CS left column measured #FFFFFF) | `Blue2001.Brush.StartMenuPinnedColumn` #FFFFFF (205), `Flat.Brush.StartMenuPinned` #FFFFFF (147) | **Keep** |
| 10 | Start menu — places column | **Pale blue-lilac secondary column** ≈#D9E3F3/#B9CCEF (README §Material Separation item 4; R-CS right column measured ≈#C6D7F1 at x=420..680 y=400..560, with a #2B73C8 cobalt gutter at x≈390; SVG-O line 45 fills the places column #D9E3F3) | `Blue2001.Brush.StartMenuPlacesColumn` gradient **#3570D6→#2862C8 saturated cobalt** (226); `Flat.Brush.StartMenuPlaces` #3570D6 (148); place text `Blue2001.Brush.StartMenuPlaceText` **#FFFFFF** (208) | **Column → solid #D9E3F3** (226 and twin 148); **place text → #1A202C dark ink** (208) — dark text on pale column per README ("Main and submenu text are dark/white by state") |
| 11 | Start menu — header / footer | Header is the blue-violet chrome band (README layer 2); footer warm neutral | `Blue2001.Brush.StartMenuHeader` #4A85DF→#2862C8 cobalt (223) ✓; `Blue2001.Brush.StartMenuFooter` #ECE9D8→#DCD7C8 (224) ✓ warm | **Keep** |
| 12 | Submenu geometry | Submenu slices X=2,174,2 / Y=2,18,2 (README) → 2px frame, 18px item band; sibling Blue2001 token is 18 (`Blue2001Tokens.axaml:85`) | `Bevel.Metric.MenuItemHeight` 21 (234) | **21 → 18** |
| 13 | Scrollbar | Classic 16px-class width; SVG-O line 38 track #EDF0F6, stroke #C2C5BC; SVG-D "15px matte scrollbar" | Width 16 (232); `Bevel.Brush.Scrollbar` #EDF0F6 (53); `Bevel.Theme.ScrollBar` background #EDF0F6 (364) | **Keep** (16px/track within the approved target's tolerance). NOTE: `flat-filer.png` x=740..756 samples the filer scrollbar face as warm #ECE9D8 — the filer's internal scrollbars do not resolve `Bevel.Theme.ScrollBar`; follow-up |
| 14 | Selection — primary | Saturated blue #316AC5 (README; R-CS hover rows y=383..531 dense #316AC5) | `Bevel.Color.Highlight` #316AC5 (26), `Bevel.Brush.Highlight` #316AC5 (54), `Flat.Brush.Check` #316AC5 (115) | **Keep** — pinned-row hover in StartMenu consumes `Bevel.Brush.Highlight` ✓ |
| 15 | Selection — secondary | #5096F8 (README; R-CS y=532..569 x=0..390 band) | **No token exists** | **Gap — not fixable here:** no secondary-selection brush is mapped in FlatTheme.axaml (selection-inactive state). Follow-up token needed |
| 16 | New-item selection | Pale warm yellow #FEEAB6 (README; SVG-O line 46 "New item" row fill; R-CS sparse ≈37 px due to JPEG) | Only icon tint `Flat.Brush.StartMenuFolder` #FEEAB6 (165) / `Blue2001.Brush.StartMenuPlaceIconFolder` (215) | **Gap — not fixable here:** no new-item *row* brush is consumed by the menu template; row-state mapping is a StartMenu.axaml follow-up |
| 17 | Borders / corner radii | Square corners throughout (README direction; classic bitmap slices are square; shared Classic templates carry no radius) | Chrome `Bevel.Metric.CornerRadius` 0 (236), start menu radii 0 (142-144) ✓ — but every Flat control template rounds 1px (277 Button, 291 CheckBox, 315 TextBox, 321 ComboBox ×3, 327 ProgressBar ×2, 332 Slider ×3, 339 TabItem, 346 TabControl, 351 scroll line button, 358/361 scroll thumbs, 380 ToggleButton) | **Flatten all 1px radii → 0** |
| 18 | Caption decoration | Clean gradient band, chrome only (R-W caption band; SVG-O header band is a plain gradient) | `PART_DualTone` translucent glass-tile canvas re-enabled on `:active` (625-627), drawing the pastel-era floating tiles over active captions | **Remove the `:active` re-show style** so the tile layer stays hidden (canvas + base style remain, dormant) |

## 2. Applied corrections (FlatTheme.axaml, old → new)

| Line (pre-fix) | Token / element | Old | New |
|---|---|---|---|
| 64 | `Flat.Brush.CaptionButton` | `#2862C8` | `#3570D6` |
| 68 | `Flat.Brush.CaptionClose` | `#2862C8` | `#3570D6` |
| 71-73 | `Flat.Brush.CaptionActive` stops | `#5E9CF9` / `#4A8CEF` | `#4A85DF` / `.5 #3570D6` / `#2862C8` |
| 121-123 | `Bevel.Brush.CaptionActive` stops | `#5E9CF9` / `#4A8CEF` | `#4A85DF` / `.5 #3570D6` / `#2862C8` |
| 148 | `Flat.Brush.StartMenuPlaces` | `#3570D6` | `#D9E3F3` |
| 208 | `Blue2001.Brush.StartMenuPlaceText` | `#FFFFFF` | `#1A202C` |
| 226 | `Blue2001.Brush.StartMenuPlacesColumn` | cobalt gradient `#3570D6→#2862C8` | solid `#D9E3F3` |
| 234 | `Bevel.Metric.MenuItemHeight` | `21` | `18` |
| 277, 291, 315, 321 (×3), 327 (×2), 332 (×3), 339, 346, 351, 358, 361, 380 | control-template `CornerRadius` | `1` / `0,4,4,0` / `1,1,0,0` / `0,1,1,1` | `0` / `0` / `0,0,0,0` / `0` — pairs positionally: 321 ComboBox `0,4,4,0`→`0`, 339 TabItem `1,1,0,0`→literal `0,0,0,0`, 380 ToggleButton `0,1,1,1`→`0`, remainder `1`→`0` |
| 625-627 | `^:active /template/ Canvas#PART_DualTone` re-show style | active captions re-enable the glass-tile canvas | style removed; canvas stays hidden |

Kept intentionally (already on target): frame #3567CB, white client, warm menubar #ECE9D8
(21px), warm taskbar 30px, start-button face/hover/pressed ramps, #316AC5 selection,
21x19 caption buttons with #7AA4EB border + white glyphs, 16px #EDF0F6 scrollbar,
square chrome corners.

## 3. Deferred / follow-ups (outside this file — not applied)

1. **`src/Bevel.UI/FlatVariantService.cs` shadows several of these keys at runtime** for the
   Blue variant (lines 34, 36-57): it re-injects the pastel caption
   (`#5E9CF9→#4A8CEF`), the cobalt `Flat.Brush.CaptionButton/#2862C8` fill, the saturated
   `Blue2001.Brush.StartMenuPlacesColumn`/`#3570D6` column and white place text whenever a
   Flat variant is active, overriding this file's corrected base values. Until that service
   is updated to the source-led values, variant-applied runs keep the old looks; the
   corrected base renders when no variant override is applied.
   **RESOLVED — no change needed.** The service early-returns for the Blue variant
   (`FlatVariantService.cs:28-33`): Blue is the theme-native look that `Gradient()`
   cannot express, so Blue never receives the re-injection. Non-Blue variants receive the
   minimal tuple at `:34-40` (synthetic accent calculator), re-injected at `:49-62`. That
   does touch three token groups above by design — `Flat.Brush.CaptionButton`/`CaptionClose`
   (`:49-50`), `Blue2001.Brush.StartMenuPlacesColumn` (`:59`) and
   `Blue2001.Brush.StartMenuPlaceText` (`:61`) — giving each variant its own pigments; the
   corrected base values hold wherever no variant is applied. §5 confirms at render level:
   every corrected token lands byte-identical on the filtered Blue renders (early-return),
   the silver/amber/ergonomic caption-button deltas of §5 rows 6-8 are exactly this variant
   re-injection, and the blue/ergonomic/silver taskbar strips change only the 49-px clock
   recolor (amber adds its start-glyph ink, row 5) — strips do not exercise the
   caption/start-menu surfaces.
2. `docs/design/flat/runtime-review.md` is stale (pre-source-analysis conclusions); it was
   not consulted for this delta and should be rewritten once the variant service lands the
   same corrections.
   *(Deferred rewrite — ✅ prerequisite met this pass per item 1 Resolution, but the rewrite
   stays out of this bead's scope.)* — **Deferred (follow-up bead).**
3. Secondary-selection (#5096F8) and new-item-row (#FEEAB6) tokens: no consuming template
   state exists yet (rows 15-16).
4. Start-menu proportions: source slices total 336px wide (10+144+2+2+168+10) vs
   `Flat.Metric.StartMenuWidth` 390 (139); the per-column widths live in
   `src/Bevel.Taskbar/StartMenu.axaml`, which is out of scope for this pass.

---

## 4. Baseline integrity (confirmed)

The pristine baseline (`renders/_before/`, 13 PNGs) was regenerated from the reviewed parent
commit with the render harness clock pinned at `2026-01-01 14:14 UTC`. Window and start-menu
output is byte-deterministic: `_before/flat-window-blue.png` and
`_before/flat-startmenu-blue.png` sha256-match the committed renders exactly (`923f551e…` /
`6265b8f9…`). **Taskbar PNGs are phase-captured** — the taskbar scene runs Background-priority
timers (`ClockWidget.axaml.cs` DispatcherTimer, `TaskbarView` tooltip timer, `ShellModel` 2s
reconcile) that `RunJobs()` does not settle before capture, so even regenerating from the same
commit yields different taskbar bytes (e.g. `_before` blue `2a0437f2…` vs committed
`a03855b3…`, amber `47700031…` vs committed `ca784a6b…`). The baseline therefore reproduces
the committed window/start-menu renders exactly and the taskbar surfaces only up to clock
phase; with that caveat it is the correct PRISTINE reference for this pass's delta (numbers in
§5 re-verified on the fresh 14:00 re-render).

`renders/pastel-taskbar.png` in the committed baseline contains a stale `#A9D7AA`
left-border artifact from before `PastelTheme.axaml` was corrected to `#BCE3F2`/`#A0D2E8`
(bound at `:89`/`:401`). It is a pre-existing baseline artifact, not a regression from this
pass — the fresh delta below measures it as a 49-px clock-digit change identical in size
and location to the flat taskbars'. No action taken; flagging for completeness.

## 5. Render delta — PRISTINE (`renders/_before/`, parent commit) → MINE (this pass)

Simplest-first, most precise-to-coarse variant ordering. Source render sizes: taskbar
900x30, window 520x360, start menu 390x537. Method: per-variant `renders` file diffed
pixel-exactly against its `renders/_before/` twin (python3 + PIL); differing-pixel
coordinates mapped onto each variant's real layout (start button at x≈9..58, task strip,
clock digits x=882..887 / caption band y=0..25, caption buttons x=310..519 y=40..62 /
73..74 / banner x=1..118, pinned column, places column x=122..389).

| # | Variant | red px | % | Location of difference | Reading |
|---|---------|-------:|---:|------------------------|---------|
| 1 | flat-taskbar-blue | 146 | 0.54% | clock digits x=866..887, y=10..19 | clock text/backplate recolored (`#242A34→#1A202C` ink) — only non-Blue-early-return surface reachable at runtime. Phase-dependent: 49 px on the 10:59 run, 146 px on the fresh 14:00 run |
| 2 | flat-taskbar-ergonomic | 146 | 0.54% | clock digits x=866..887, y=10..19 | same clock recolor; phase-dependent as row 1 |
| 3 | flat-taskbar-silver | 146 | 0.54% | clock digits x=866..887, y=10..19 | same clock recolor; phase-dependent as row 1 |
| 4 | pastel-taskbar | 146 | 0.54% | clock digits x=866..887, y=10..19 | baseline artifact only (§4); phase-dependent as row 1 |
| 5 | flat-taskbar-amber | 587 | 2.17% | start button + first task button x=9..58, clock digits x=866..887, rows y=6..23 | amber theme's start glyph + pressed/checked task button go warm-gray/paper → dark navy `#1A202C` family; clock recolor as above; phase-dependent (490 px on the 10:59 run) |
| 6 | flat-window-silver | 4131 | 2.21% | caption buttons cluster x=310..519 y=40..62, y=73..74; caption/border rows y=0..15, 269..272 | caption buttons + thin caption bands recolored |
| 7 | flat-window-amber | 4131 | 2.21% | caption buttons cluster x=310..519 y=40..62, y=73..74; caption/border rows | same as silver (amber window chrome) |
| 8 | flat-window-ergonomic | 4131 | 2.21% | caption buttons cluster x=310..519 y=40..62, y=73..74; caption/border rows y=0..15, 269..272 | same as silver/amber (ergonomic window chrome) |
| 9 | flat-window-blue | 18345 | 9.80% | full-width caption band y=0..25 (+ y=40..62, 73..74, 85..86, 98..99, 108..112, 121..122, 133..134, 136..145, 147..148, 161..162, 169..172, 179..180, 193..196, 219..220, 271..272 scattered glyph/edge rows) | caption band pastel → 3-stop cobalt ramp `#4A85DF→#3570D6→#2862C8`; caption buttons + glyphs to `#3570D6`/`#7AA4EB`/white; corner radii flattened → square |
| 10 | flat-startmenu-blue | 87656 | 41.85% | whole menu surface: banner x=1..118, header band, pinned column, places column x=122..389, rows y=1..52 and y=57..495 | banner+places column saturated cobalt → pale lilac `#D9E3F3` with `#1A202C` ink place text (`#3570D6→#D9E3F3` single largest swap); selection row `#316AC5`, white pinned column, header/footer bands unchanged |

Total: **119 565 differing pixels** across the ten variants on the fresh 14:00 re-render
(119 080 on the 10:59 run — the 485-px difference is entirely clock-phase jitter in the five
taskbar rows; rows 6-10 are byte-stable across runs, and `pastel-window`, `pastel-startmenu`,
`flat-filer` diff 0 px on both runs). Nothing outside these regions changed; before/after
montages read identical except for the recolored areas (no shifted widgets, no ghosting).

Prose reading: **taskbars** — the change is the Classic start-button/strip + clock ink
family, not a strip repaint; **windows** — the blue caption chrome and caption buttons vs
amber/silver's button-only scope; **start menu** — the pale places column turning the
menu from saturated-blue to the approved two-column pastel layout.

Note — the bboxes recorded against the committed baseline (e.g. `633 px
(9,6,888,24)` for silver) describe the STALE *committed→pristine* state, not this pass's
effect; the numbers above are authoritative for PRISTINE→MINE.

Re-verification (fresh 14:00 re-render): rows 6-10 are byte-stable (4131 / 4131 / 4131 /
18345 / 87656 px — the ergonomic window was measured directly; silver records the same
4131 px with an identical changed-pixel map, amber too except a 6-px anti-aliasing sliver
at (330,20)-(331,26), and all five rows match the 10:59 run, same inputs); rows 1-5 moved
49/49/49/49/490 → 146/146/146/146/587 because the taskbar clock phase differs between runs
(exactly +97 px on every strip). The diff clusters are unchanged; only the set of differing
digit-ink pixels moves. Jitter source: `ClockWidget` / `TaskbarView` / `ShellModel`
Background-priority timers not settled by `RunJobs()` before capture — see §4.
