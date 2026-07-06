# Windows 2000 Explorer — Chrome Layout & Palette (measured reference)

**Provenance (clean-room):** every value below is *measured from public 1:1 reference
screenshots* (guidebookgallery.org, Windows 2000 Professional, 96 DPI) using pixel/colour
sampling. These are observed appearance and dimensions — facts, not expression — and are
derived from **no** proprietary source. Do not backfill any value from leaked source; if a
number here is uncertain, confirm it against a running Win2000 VM screenshot instead.

**Units:** all lengths are **DIP** (device-independent pixels, 1 DIP = 1/96"). Windows 2000
was authored at 96 DPI, so its pixel measurements map **1:1 to Avalonia DIPs**. Author the
UI in these DIP values and it scales proportionally and crisply at 100 / 150 / 200 / 300 %.

Confidence: **[M]** measured from the column/colour dumps below; **[S]** standard documented
system metric cross-checked against the screenshots; **[~]** estimate, confirm on a VM.

---

## 1. Palette

| Token (proposed `Bevel.Color.*`) | Hex | RGB | Role |
|---|---|---|---|
| `Face` (ButtonFace / 3DFace) | `#D4D0C8` | 212,208,200 | menu, toolbar, status, dialog bg — **[M]** all identical |
| `Window` | `#FFFFFF` | 255,255,255 | client / list / edit bg — **[M]** |
| `Hilight` (3DHighlight) | `#FFFFFF` | 255,255,255 | raised top-left, sunken bottom-right — **[M]** |
| `Light` (3DLight) | `#D4D0C8` | 212,208,200 | inner raised edge (= Face) — **[M]** |
| `Shadow` (3DShadow) | `#808080` | 128,128,128 | raised bottom-right, sunken top-left — **[M]** |
| `DarkShadow` (3DDkShadow) | `#404040` | 64,64,64 | inner sunken edge — **[M]** |
| `ActiveTitleLeft` | `#0A246A` | 10,36,106 | title gradient start — **[S/M]** |
| `ActiveTitleRight` | `#A6CAF0` | 166,202,240 | title gradient end (measured x470 ≈ 158,193,233) — **[M]** |
| `InactiveTitleLeft` | `#808080` | 128,128,128 | inactive gradient start — **[~]** |
| `InactiveTitleRight` | `#B5B5B5` | 181,181,181 | inactive gradient end — **[~]** |
| `TitleText` | `#FFFFFF` | 255,255,255 | caption text — **[S]** |
| `Text` | `#000000` | 0,0,0 | control text — **[M]** |
| `GrayText` | `#808080` | 128,128,128 | disabled text — **[S]** |
| `Highlight` (selection) | `#0A246A` | 10,36,106 | selected item bg — **[M]** |
| `HotTracking` | `#000080` | 0,0,128 | hover/hot link — **[S]** |
| `InfoWindow` (tooltip) | `#FFFFE1` | 255,255,225 | tooltip bg — **[S]** (earlier white sample was a miss) |
| `Hyperlink` | `#0000FF`? | — | web-view links — **[~]** confirm teal variant on VM |

> **Cross-validated against `05-theming.md` §Colors** — every measured value above matches the
> documented "Windows Standard" palette. This table is confirmation, not a competing source;
> `05-theming.md` remains canonical for `Bevel.Color.*`.

---

## 2. The edge primitive (draws ~80 % of the chrome)

Every 3-D surface is the same 2-DIP `DrawEdge`. Confirmed pixel-by-pixel in the column dump.
Implement **once** as a shared `Bevel.UI` border primitive parameterised by style; every
control composes from it.

```
RAISED  (buttons, toolbar buttons, status grip):
  outer  top+left = Hilight #FFFFFF     bottom+right = DarkShadow #404040
  inner  top+left = Light   #D4D0C8     bottom+right = Shadow     #808080
  (a "soft"/thin raised uses just Hilight / Shadow, 1 DIP)

SUNKEN  (address combo, list view, edit fields, status panels):
  outer  top+left = Shadow    #808080   bottom+right = Hilight #FFFFFF
  inner  top+left = DarkShadow #404040  bottom+right = Light   #D4D0C8

ETCHED  (menu/toolbar separators, group lines):
  line 1 = Shadow #808080   then   line 2 = Hilight #FFFFFF
```

---

## 3. Window chrome — vertical stack (measured column dump, top → down)

| Band | Height (DIP) | Conf | Notes |
|---|---|---|---|
| Window top border | 4 | [M] | Face + `#FFFFFF` hilite; sizing frame `SM_CxSizeFrame` |
| **Title bar** | **18** | [M] | horizontal gradient L→R; `SM_CYCAPTION`; y4–21 |
| etched separator | 2 | [M] | `#808080` over `#FFFFFF` |
| **Menu bar** | **~20** | [M] | text baseline ≈ y35; `SM_CYMENU` |
| **Toolbar** | **~26** | [M] | 16×16 icons, buttons ~22–24; y≈45–70 |
| sunken edge | 2 | [M] | |
| **Address bar** | **~22** | [M] | white sunken combo; y75–92 |
| sunken client edge | 2 | [M] | list view is sunken |
| **Client** | fill | [M] | `#FFFFFF`; from y≈102 |
| **Status bar** (bottom) | ~20–22 | [~] | Face, sunken panels, diagonal grip |

Title-top → client-top ≈ **94 DIP** of chrome (18 + 20 + 26 + 22 + border + separators).

---

## 4. Per-control metrics

**Title bar** — 18 DIP. Icon 16×16 at left (~2 DIP inset). Caption text Tahoma 8pt Bold,
white, vertically centred. Min/Max/Close buttons ~16×14, right-aligned, ~2 DIP gap; Close
has a 1–2 DIP gap before it. Buttons are raised, `X`/`_`/`□` glyphs in black.

**Menu bar** — ~20 DIP, Face bg. Items Tahoma 8pt, black, ~6 DIP horizontal padding. Hover =
raised 1 DIP outline; open = sunken.

**Toolbar** — ~26 DIP, Face bg. 16×16 icons. First button ("Back") shows icon **+ text
label**; others icon-only with a dropdown chevron where applicable. Buttons flat until hover
(raised) / press (sunken). Vertical etched separators between groups.

**Address bar** — ~22 DIP row. "Address" label (Tahoma 8pt) + sunken combo (white, 2-DIP
sunken edge) containing a 16×16 folder icon + path text; "Go" button at right.

**Details list header** (trashcan shot) — ~17 DIP. Raised button-style column headers, Tahoma
8pt, left-aligned text, ~4 DIP padding; e.g. `Name | Original Location | Date Deleted | Type
| Size`. Sort divider is a 1-DIP etched line.

**Status bar** — ~20–22 DIP. Multiple sunken panels (2-DIP sunken edge), Tahoma 8pt text ~2
DIP inset; diagonal resize grip bottom-right. Explorer shows `N object(s)` | free space | etc.

**Scrollbars** — 16 DIP wide (`SM_CXVSCROLL`). Raised thumb + arrow buttons, Face track (often
a 50 % dithered `#FFFFFF`/`#D4D0C8` checker).

**Web-view info pane** (left, in My Computer/Control Panel) — white-ish panel with a
watercolour header graphic behind a large heading, a description paragraph (gray), and blue
hyperlinks. Optional to replicate; see `07-shell-ux.md`.

---

## 5. Typography

- UI font: **Tahoma 8 pt** (≈ 11 px at 96 DPI). Bold for title caption and headings.
- Tahoma is not redistributable — use the metric-compatible OFL substitute (see theming
  plan). At 1× a pure-vector render is marginally softer than Win2000's hinted bitmap
  strike; see §6.

---

## 6. HiDPI / vector rendering notes

- **DIP = Win2000 px**, so these values give faithful proportions at every scale for free.
- Draw all chrome (edges, gradients, glyphs) as **vector geometry**, not bitmaps → razor
  sharp at 200 %/300 %.
- Keep `UseLayoutRounding = true` and snap 1–2 DIP edges to device pixels so hairlines stay
  crisp; integer scales (100/200/300 %) are pixel-perfect, 150 % needs snapping tolerance.
- **Icons** are the one non-vector part: ship multi-resolution bitmap sets (16/32/48 + 2×)
  as real Windows did, or redraw as vector (loses pixel-art fidelity). Decide per icon.

---

---

## 7. Reconciliation with existing specs

This reference **confirms** `05-theming.md` and `06-file-manager.md`; it does not override them.
Deltas surfaced by measurement:

- **Canonical wins:** menu bar = `MenuBarHeight 19` / `MenuItemHeight 17` (05 §3), not my ~20
  estimate; tooltip = `InfoWindow #FFFFE1` (05), not white.
- **Promote to `05-theming.md` §3 metrics** (currently only in 06's ASCII, unpinned):
  `Bevel.Metric.ToolbarHeight ≈ 26` (add a `ToolbarHeightWithText` variant), `AddressBarHeight
  ≈ 22`, `StatusBarHeight ≈ 20–22` — so file-manager chrome reads metrics, never literals
  (satisfies ENG-01 / MET-01).
- Everything else — palette, edge composites, title gradient, caption/edge/scrollbar metrics
  — is already correct in 05/06 and measurement-confirmed.

---

## 8. Edge rendering modes (Crisp / Smooth)

The 3-D edge has two rasterization modes, selected per theme with an optional user override.
Both keep the **same logical thickness** (§3 `EdgeThickness`) — proportions never change.

- **Crisp** (today's `DPI-01` device snapping): each 1-DIP colour band is a hard, device-pixel-
  snapped line. `devicePx = max(1, round(1 × scale))`. Pixel-authentic; at 3× a band is a fat hard line.
- **Smooth** (**default**, decision 2026-07-06): the edge keeps its logical thickness, but the band colours are drawn as an **eased
  sRGB gradient** (`outer → inner → face`) whose *rectangle bounds stay logical-pixel-aligned*
  while the *fill rasterizes at device resolution*. At 1× it collapses to ~2 px (near-crisp); at
  2×/3× the hard step becomes a sub-pixel ramp. Ease: hold the outer (light-catching) colour to
  ~35 % of the thickness, then ramp — a linear ramp reads washed-out.

**Why Smooth exists:** it uses HiDPI density for finesse instead of thicker hard blocks, reads
closer to how these bevels looked on a CRT, and — the sleeper win — **fixes 150 % fuzziness**: a
crisp 1-DIP line at 150 % lands on 1.5 physical px and blurs ambiguously, whereas a gradient at
150 % is *meant* to occupy fractional pixels, so it looks intentional.

**Selection:**
- Theme default `Bevel.Edge.Rendering = Smooth` at **all** scalings (verified on-device against
  a live Crisp/Smooth A/B at real DPI — the ramp reads well even at 1×/2× and it removes the
  150 % fuzziness). This is a deliberate departure from strict pixel-authenticity in favour of
  the HiDPI-native look.
- Whitelisted user override (05 §1 layer 4): a Display-settings toggle **"Crisp bevels"** for
  pixel purists — era-appropriate next to Win2000's real "Smooth edges of screen fonts".
  Persist under `theme:<id>`.

**Corners:** both modes miter at 45° — each edge is clipped to a trapezoid so adjacent edges
meet on the diagonal (highlight-L meets shadow-L), matching Win2000's real 3-D corners. This is
essential in Smooth mode: without it a *vertical* gradient would abut a *horizontal* one at the
corner and leave a visible seam. Along the diagonal both edges share the same normalized depth,
so light meets dark cleanly.

**Implementation:** spike lives in `src/Bevel.UI/BevelBorder.cs` (`EdgeRendering` enum;
`DrawMiteredEdge` clips each edge to a `Trapezoid`, `DrawEdgeFill` has the Crisp/Smooth
branches). In the shipping theme this folds into `ClassicBorderDecorator` / `BevelRenderer`
(05 §W2K-02 / §DPI-01) — the 8 `BorderStyle`s just pass the mode through; the band→colour
mapping is unchanged. Tracked as `bevel-38y` sub-work.

---

_Measured 2026-07-06 from public reference screenshots for the Bevel Win2000 theme. Related:
`05-theming.md`, `06-file-manager.md`; beads `bevel-38y` (semantic token layer), `bevel-wp3`._
