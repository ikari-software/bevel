# Flat vs Pastel Runtime Visual Review

This document records the visual review gate for Phase 4 (`bevel-h0hg.14.4`), comparing runtime crops of Bevel Flat (Whistler Watercolor) and Bevel Pastel against the source references and SVG design targets.

## Methodology

Fixed-size runtime crops were generated headlessly via `RenderFlatAndPastelComparisonTest` across three core shell surfaces:
1. Framed BevelWindow with standard controls gallery (`520x360`)
2. Two-column Start menu (`360x531`)
3. Taskbar with Start button, window buttons, and clock tray (`1920x30`)

All renders are stored in `docs/design/flat/renders/`.

---

## Region-by-Region Paired Comparison

| Region | Flat (Whistler Watercolor) | Pastel | Source / Target Agreement |
|---|---|---|---|
| **Window Frame & Shell Face** | Warm paper `#ECE9D8` face, solid continuous 4px blue border `#3567CB`, square corners. | Warm cream paper `#FBF7F1`, border `#B9C7D6`, soft corners. | Matches Whistler reference and `flat-watercolor-overall.svg`. |
| **Window Caption Bar** | Saturated cobalt blue gradient `#4A85DF` → `#3570D6` → `#2862C8` with bold white title text. | Soft pastel wash `#D6EEF7` → `#B4DEEE` → `#9BD0E5` → `#86C3DA`. | Matches Classic Shell Watercolor skin and Whistler reference. |
| **Caption Buttons** | 21x19px blue squares (`#3570D6`) with 1px light border (`#7AA4EB`) and white glyphs (`#FFFFFF`). Uniform blue close button (no red). | Rounded pastel buttons on `#EDE7F2`. | Verified against Whistler build 2419 reference. |
| **Column Headers** | Thin raised plate `#ECE9D8` with `#FFFFFF` highlight, separated by `#ACA899` rule. | Lavender-gray `#EDE7F2` plate. | Matches Section 04 of `flat-watercolor-details.svg`. |
| **Start Menu Pinned Column** | Solid white `#FFFFFF` background (`Main_background=#FFFFFF`), saturated blue selection `#316AC5` with white text. | Warm cream paper `#FBF7F1` with soft aqua selection `#A6D8EC` and dark ink text. | Confirms separation between Flat and Pastel. |
| **Start Menu Places Column** | Saturated blue `#3570D6` → `#2862C8` panel with white text `#FFFFFF`. | Soft lilac `#D8C9EE` panel. | Matches `Watercolor.skin` Main2 metrics and blue variant. |
| **Start Menu Footer** | Warm paper `#ECE9D8` band with Log Off and Turn Off buttons. | Warm rose/mist footer. | Matches taskbar tone and source skin. |
| **Start Button** | Raised classic bevel `#EEF0EA` → `#E5E7E1` → `#D6D8CE` with square corners, dark bold "Start" text, and Bevel mark. | Soft sage pill `#BFE3C4` → `#8FC79A`. | Matches `original-start-button.png` states. |
| **Taskbar Body & Tray** | Flat warm paper `#F4F2E8` → `#ECE9D8`; inset clock well `#E4E0D0` → `#ECE9D8`. | Light sky band `#CFEBF4` → `#ADD6E8` → `#CFEBF4`. | Matches `reference-classic-shell.jpg`. |
| **Control Geometry** | Crisp square controls (`CornerRadius="1"`), Whistler blue accents (`#316AC5` check/slider/progress), `#ECE9D8` inactive tabs. | Rounded soft controls, `#A6D8EC` focus. | Faithful compact vector implementation. |

---

## Variation Set Verification

Flat exposes four source variations:
1. **Blue** (Default): Caption `#BBC8F0` → `#688ADE`, Taskbar `#B9C7D7` → `#688ADE`, Places `#D9E3F3`, Select `#316AC5`.
2. **Ergonomic**: Caption `#C7E3DC` → `#75B6A7`, Taskbar `#B8DCD5` → `#78B6A9`, Places `#DDEBDD`, Select `#6B936A`.
3. **Silver**: Caption `#EDF0F1` → `#BFC0C0`, Taskbar `#E1E4E4` → `#BFC0C0`, Places `#E7E8E8`, Select `#A0A0A0`.
4. **Amber**: Caption `#DCC7B7` → `#A98A78`, Taskbar `#D9C5B5` → `#A98A78`, Places `#F0DFD0`, Select `#B77743`.

All four variations generate valid runtime crops and demonstrate clean hue shifts across captions, taskbars, and selection brushes while preserving the underlying warm gray shell architecture.

---

## Visual Gate Verdict

- **Identity Separation**: Flat (Whistler Watercolor) and Pastel are completely separated in code, resources, and visual output.
- **Source Fidelity**: All hex values, slice rules, and geometry conform to `Watercolor.skin`, `reference-whistler.jpg`, and the approved SVG targets.
- **HiDPI Vector Execution**: Zero raster bitmap skins used; all chrome is pure scalable vector geometry.
- **Status**: PASSED.
