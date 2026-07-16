# Win2000 Icon Specification

**Status**: Implementation spec for `bevel-assets` — self-produced Win2000 core asset set
**Generated**: 2026-07-12
**Scope**: Vector glyphs in `Glyphs.cs`/`ToolbarIcons.cs` + SVG/PNG exports in `Assets/`

---

## 1. Design Principles (Clean-Room)

| Principle | Implementation |
|-----------|----------------|
| **No Microsoft assets** | All geometry authored from visual observation of Win2000 Explorer screenshots in `refs/win2000/` and `docs/reference/win2000/` |
| **Scènes à faire only** | Metaphors: folder, trash, computer, printer, paper sheet — unprotectable office objects (*Apple v. Microsoft*, 35 F.3d 1435) |
| **Style ≠ Expression** | Isometric grid (45°/15°), upper-left light, 16-color VGA palette, drop shadow are *design principles*, not protectable expression |
| **Semantic color binding** | All fills/strokes reference `Bevel.Color.*` tokens — scheme variants (Brick, Desert, Eggplant, etc.) swap automatically |
| **16-unit space** | All glyphs authored in `viewBox="0 0 16 16"`, scaled via `Viewbox` to target DPI |
| **Vector-first** | `Path` + `LinearGradientBrush`/`ConicGradientBrush` — no bitmap encoding in source |

---

## 2. Color Token Mapping

### Primary Palette (Windows Standard)

| Token | Hex | Win2000 Role | Used For |
|-------|-----|--------------|----------|
| `Bevel.Color.ButtonFace` | `#D4D0C8` | 3D face | Toolbar button face, drive body |
| `Bevel.Color.ButtonHighlight` | `#FFFFFF` | Top/left highlight | Paper fold, window title highlight |
| `Bevel.Color.ButtonLight` | `#D4D0C8` | Light 3D edge | (alias of ButtonFace) |
| `Bevel.Color.ButtonShadow` | `#808080` | Bottom/right shadow | Paper edge, drive edge |
| `Bevel.Color.ButtonDkShadow` | `#404040` | Deep shadow | CD drive slot, monitor bevel |
| `Bevel.Color.Window` | `#FFFFFF` | Document background | Paper fill, toolbar button background |
| `Bevel.Color.WindowText` | `#000000` | Primary text | File name lines, code brackets |
| `Bevel.Color.WindowFrame` | `#000000` | Window frame | Monitor edge, drive bay outline |
| `Bevel.Color.GrayText` | `#808080` | Disabled text | Script prompt, archive zipper |
| `Bevel.Color.ActiveTitle` | `#0A246A` | Active title bar | EXE title bar, console title bar |
| `Bevel.Color.GradientActiveTitle` | `#A6CAF0` | Title gradient | (unused in glyphs) |
| `Bevel.Color.Highlight` | `#0A246A` | Selection | Web globe, history clock |
| `Bevel.Color.HighlightText` | `#FFFFFF` | Selection text | (unused in glyphs) |
| `Bevel.Color.HotTracking` | `#000080` | Hot link | (unused in glyphs) |
| `Bevel.Color.InfoWindow` | `#FFFFE1` | Tooltip bg | (unused in glyphs) |
| `Bevel.Color.InfoText` | `#000000` | Tooltip text | (unused in glyphs) |

### Derived Semantic Brushes (for glyph authoring)

| Glyph Brush | Maps To | Notes |
|-------------|---------|-------|
| `FolderBack` | `VGrad(IconFolderBackTop #FFE49A, IconFolderBackBottom #F0B03C)` | Manila folder base — **not** ButtonFace (see note) |
| `FolderFront` | `VGrad(IconFolderFrontTop #FFF3CE, IconFolderFrontBottom #FFD064)` | Folder flap |
| `FolderEdge` | `IconFolderEdge #9C6B15` | Folder outline |
| `PaperFill` | `VGrad(Window, GrayText@50%)` | Document body |
| `PaperEdge` | `ButtonShadow` | Document outline |
| `PaperLine` | `ButtonShadow@50%` | Document text lines |
| `ExeTitle` | `ActiveTitle` | Program window title bar |
| `ExeLine` | `GradientActiveTitle@60%` | Program window code lines |
| `Sky` | `GradientActiveTitle` | Photo inset sky |
| `Sun` | `HighlightText` | Photo sun |
| `Mountain` | `ButtonShadow` | Photo mountains |
| `ZipLine` | `ButtonDkShadow` | Archive zipper |
| `ZipTeeth` | `ButtonShadow` | Archive teeth |
| `DriveBody` | `VGrad(ButtonFace, ButtonShadow)` | Drive bay |
| `DriveEdge` | `ButtonDkShadow` | Drive outline |
| `DriveLed` | `HighlightText` | Activity LED (green in Standard) |
| `MonScreen` | `GradientActiveTitle` | Monitor screen |
| `MonInner` | `ActiveTitle` | Monitor inner bezel |
| `MonEdge` | `WindowFrame` | Monitor edge |
| `MonStand` | `ButtonFace` | Monitor stand |
| `Note` | `HotTracking` | Musical note |
| `FilmDark` | `ButtonDkShadow` | Filmstrip base |
| `FilmEdge` | `WindowFrame` | Filmstrip edge |
| `FilmFrame` | `Highlight` | Filmstrip frame |
| `Globe` | `HotTracking` | Web globe |
| `GlobeEdge` | `ActiveTitle` | Globe edge |
| `GearBody` | `ButtonFace` | System gear |
| `GearEdge` | `ButtonShadow` | Gear edge |
| `ConsoleBg` | `ButtonDkShadow` | Console background |
| `ConsoleTitle` | `ButtonShadow` | Console title bar |
| `FontInk` | `WindowText` | Font "A" |
| `PdfRed` | `Highlight` | PDF band |
| `SheetGreen` | `HighlightText@60%` | Spreadsheet grid |
| `SheetGrid` | `ButtonShadow` | Spreadsheet lines |
| `WordBlue` | `Highlight` | Word "W" |
| `PptOrange` | `Highlight` | PowerPoint bars |
| `DbBody` | `GradientActiveTitle` | Database cylinder |
| `DbTop` | `GradientActiveTitle@80%` | Database top |
| `DbEdge` | `ButtonShadow` | Database edge |
| `CodeInk` | `WindowText` | Code brackets |
| `DiscBody` | `VGrad(ButtonFace, ButtonShadow)` | Optical disc base |
| `DiscSheen` | `ButtonHighlight` | Disc highlights |
| `DiscRainbow` | ConicGradient (fixed hues) | CD iridescence — *scheme-invariant* |

> **Folder colour (fixed bug).** The folder glyph is manila **yellow**, not the 3D **ButtonFace**
> grey. An earlier draft of this spec mapped `FolderBack`/`FolderFront` onto `ButtonFace`/`ButtonShadow`,
> which rendered a grey folder. The folder now has its **own** semantic tokens —
> `IconFolderBackTop/Bottom`, `IconFolderFrontTop/Bottom`, `IconFolderEdge` (see `Tokens.axaml`,
> `theme.json`, `ThemeTokens.cs`) — so it stays manila and scheme variants can retint it independently
> of the button chrome. Do **not** reintroduce `ButtonFace` on the folder.

---

## 3. Icon Inventory

### 3.1 File-Type Glyphs (`Glyphs.cs`)

| Semantic ID | Extension Groups | Win2000 Metaphor | Status |
|-------------|------------------|------------------|--------|
| `folder` | — | Closed manila folder | ✅ |
| `folder.open` | — | Open manila folder | ✅ (same geometry, different flap) |
| `drive.fixed` | — | Hard disk drive | ✅ |
| `drive.cd` | — | CD-ROM drive + disc | ✅ |
| `drive.net` | — | Network drive | ✅ (falls to `drive.fixed`) |
| `drive.removable` | — | Floppy/removable | ✅ (falls to `drive.fixed`) |
| `computer` | — | Tower PC + monitor | ✅ |
| `network` | — | Connected computers | ❌ **MISSING** |
| `trash.empty` | — | Waste basket | ❌ **MISSING** |
| `trash.full` | — | Waste basket w/ paper | ❌ **MISSING** |
| `doc.generic` | (fallback) | Paper sheet + fold | ✅ |
| `doc.exe` | exe, com, scr, msi, app | Window + title bar | ✅ |
| `doc.png` | png, jpg, gif, bmp, ico, webp, tif, tiff, svg | Photo (sky/sun/mountains) | ✅ |
| `doc.zip` | zip, rar, 7z, tar, gz, bz2, xz, cab | Folder + zipper | ✅ |
| `doc.mp3` | mp3, wav, wma, mid, ogg, flac, m4a, aac, aiff, au | Sheet + musical note | ✅ |
| `doc.avi` | avi, mpg, mpeg, wmv, mov, mp4, mkv, flv, webm, m4v, 3gp | Filmstrip + play | ✅ |
| `doc.htm` | htm, html, xhtml, mht, mhtml, url, asp, aspx, php, jsp | Sheet + wireframe globe | ✅ |
| `doc.dll` | dll, sys, drv, ocx, vxd, cpl | Sheet + gear | ✅ |
| `doc.bat` | bat, cmd, vbs, js, ps1, sh, py, pl, rb, wsf | Console window + prompt | ✅ |
| `doc.ttf` | ttf, otf, fon, fnt, ttc, woff, woff2 | Sheet + serif "A" | ✅ |
| `doc.pdf` | pdf | Sheet + red band | ✅ |
| `doc.xls` | xls, xlsx, xlsm, csv, tsv, ods, numbers | Sheet + green grid | ✅ |
| `doc.doc` | doc, docx, rtf, odt, pages, wpd | Sheet + blue "W" | ✅ |
| `doc.ppt` | ppt, pptx, pps, ppsx, odp, key | Sheet + orange bars | ✅ |
| `doc.db` | db, sqlite, sqlite3, mdb, accdb, sql, dbf | Cylinder (database) | ✅ |
| `doc.xml` | xml, xaml, json, yaml, toml, css, cs, c, cpp, h, java, go, rs, ts, swift, kt | Sheet + angle brackets | ✅ |
| `doc.iso` | iso, img, dmg, vhd, vhdx, bin, cue, nrg, toast | Iridescent CD | ✅ |

**Missing from IconKey but should exist:**
- `network` — "Network Neighborhood" two connected PCs
- `trash.empty` / `trash.full` — Recycle Bin states

### 3.2 Toolbar Glyphs (`ToolbarIcons.cs`)

| Method | Win2000 Metaphor | Status |
|--------|------------------|--------|
| `Back()` | Green circle + left arrow | ✅ |
| `Forward()` | Green circle + right arrow | ✅ |
| `Up()` | Folder + green up-arrow | ✅ |
| `Search()` | Blue circle + magnifying glass | ✅ |
| `Folders()` | Folder + horizontal lines | ✅ |
| `History()` | Blue clock face | ✅ |
| `MoveTo()` | Folder + green right-arrow | ✅ |
| `CopyTo()` | Two folders (peek) | ✅ |
| `Cut()` | Scissors (two crossed + handles) | ✅ |
| `Copy()` | Two stacked pages + lines | ✅ |
| `Paste()` | Clipboard + page | ✅ |
| `Undo()` | Blue curved arrow (CCW) | ✅ |
| `Delete()` | Red X | ✅ |
| `Properties()` | Page + green checkmark | ✅ |
| `Views()` | Four colored squares | ✅ |

---

## 4. Implementation Requirements

### 4.1 Semantic Color Binding (REQUIRED)

**Current problem**: Hardcoded hex in `Glyphs.cs`/`ToolbarIcons.cs` (e.g., `#FFE49A`, `#86D24E`)

**Solution**: Replace with `Bevel.Color.*` token lookups via `DynamicResource` or code-behind resource resolution.

```csharp
// BEFORE (hardcoded)
static readonly IBrush FolderBack = VGrad("#FFE49A", "#F0B03C");

// AFTER (semantic) — resolved at render time via theme.
// NOTE: the folder has its OWN manila tokens — it must NOT map onto ButtonFace/ButtonShadow
// (that produced a grey folder; fixed bug — see the "Folder colour" note in §2).
static readonly string FolderBackTop = ThemeTokens.ColorIconFolderBackTop;       // #FFE49A
static readonly string FolderBackBottom = ThemeTokens.ColorIconFolderBackBottom; // #F0B03C
```

**Implementation pattern**: Since glyphs are created in code-behind (not XAML), resolve brushes from the `Application.Current.Resources` dictionary using `ThemeTokens.Color*` keys at glyph construction time.

### 4.2 SVG/PNG Export (REQUIRED for designer handoff)

Generate assets into `src/Bevel.Themes.Win2000/Assets/`:

```
Assets/
├── Icons/
│   ├── 16/          # 16×16 PNG
│   ├── 32/          # 32×32 PNG
│   ├── 48/          # 48×48 PNG
│   └── SVG/         # Scalable vector source
│       ├── folder.svg
│       ├── folder-open.svg
│       ├── drive-fixed.svg
│       ├── ...
│       └── toolbar/
│           ├── back.svg
│           ├── forward.svg
│           └── ...
├── Fonts/
│   ├── NotoSans-Regular.ttf
│   └── NotoSans-Bold.ttf
└── README.md        # License: CC0 for icons, OFL for fonts
```

**Generation approach**: Reuse the existing `Raster()` pattern in `ToolbarIcons.cs` but render to SVG via `Avalonia.Svg` or manual SVG string generation from the same path data.

### 4.3 Missing Icons to Implement

| Priority | Semantic ID | Notes |
|----------|-------------|-------|
| P0 | `network` | Two connected monitors/PCs (Network Neighborhood) |
| P0 | `trash.empty` | Waste basket outline |
| P0 | `trash.full` | Waste basket with crumpled paper |
| P1 | `drive.net` distinct | Network drive (globe on drive) |
| P1 | `drive.removable` distinct | Floppy disk metaphor |

---

## 5. Validation Checklist

- [ ] All `Color.Parse("#...")` replaced with `ThemeTokens.Color*` lookups
- [ ] Glyphs render correctly under all 14 schemes (`schemes.json`)
- [ ] SVG exports match C# geometry 1:1
- [ ] PNG exports at 16/32/48 crisp (no anti-alias artifacts at 16px)
- [ ] `dotnet run --project tools/ThemeGen` passes (no CI drift)
- [ ] `dotnet test Bevel.sln` passes
- [ ] Icons readable at 16×16 (simplify if needed)

---

## 6. License Posture

| Asset | License | Source |
|-------|---------|--------|
| Vector glyphs (C#) | CC0 | Clean-room authored |
| SVG exports | CC0 | Derived from CC0 source |
| PNG exports | CC0 | Derived from CC0 source |
| Noto Sans fonts | SIL OFL 1.1 | Bundled, unmodified |
| Bevel Sans (future) | OFL | To be produced |

Per `docs/spec/05-theming.md` §6: "Asset pipeline — clean-room, CC0/OFL only."

---

## 7. References

- `docs/spec/05-theming.md` §8.1 — Canonical palette table
- `docs/spec/win2000-explorer-chrome.md` — Measured chrome metrics
- `docs/reference/win2000/icons-pixel-art-style.md` — Icon design rules (sizes, light source, overlay badges)
- `docs/reference/win2000/color-schemes-accessibility.md` — 14 named schemes + WinDaisy hex sources
- `refs/win2000/icons-pixel-art-style/` — Pixel-sampling working material (git-ignored)
- `src/Bevel.Themes.Win2000/theme.json` — Single source of truth for active theme
- `src/Bevel.Themes.Win2000/schemes.json` — 14 scheme definitions
- `tools/ThemeGen/Program.cs` — Generator (Tokens.axaml + ThemeTokens.cs + per-scheme .axaml)