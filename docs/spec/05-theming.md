# Theming Engine & the Three Themes

## Summary

Bevel's theming engine is built on Avalonia 11's `ControlTheme` system plus layered resource dictionaries, wrapped in a data-only package format (`.beveltheme`, defined in 01-architecture.md §6.1, THM-01..THM-04) that first-party and third-party themes share. Three themes are specced; **delivery is staged**. v1 ships **Windows 2000 Classic** (default, forked from Classic.Avalonia, MIT) as its only full theme, plus a deliberately minimal **stub second theme** — a palette-swap package inheriting the Classic templates (PKG-03) — whose sole job is to prove engine swappability end-to-end. **Windows XP Luna** (Blue variant first) targets v1.1; **Windows 11 Fluent-like** (built over Avalonia's FluentTheme) targets v1.2. Their asset work is self-produced ($0 cash — AI-assisted + open/CC, see §6) in parallel during v1 development, off the release critical path (09-engineering-plan.md). The theming *engine* — package format, metrics system, semantic keys, behavior-profile hooks — is v1 scope and is built early (M1), so deferring the themes defers assets and templates, not architecture. Chrome (bevels, gradients, frames) is **vector-redrawn with device-pixel snapping**; icons and cursors are **pixel art integer-scaled with nearest-neighbor** — this hybrid is the DPI position, chosen over pure integer scaling (blurry text, wasted retina density) and pure vector (kills the pixel-grid aesthetic). Fonts are metric-compatible **open** substitutes (SIL OFL / Creative Commons — license posture decided 2026-07-04, §5): a self-produced OFL "Bevel Sans" for Classic/Luna, Selawik for Win11. Trade-dress closeness of a face to a Microsoft original still gets a counsel look; the license does not. Every shipped asset is an original recreation with provenance metadata enforced in CI; the legal line is "same layout language, zero copied bytes." **Legal gate (resolved 2026-07-04):** the theming *engine* work — metrics system, ControlThemes, runtime switching, DPI strategy, asset-pipeline plumbing — is legally neutral and **proceeds normally**; only the specific user-facing theme names, the start-button logo/mark, the third-party-theme trust posture, and trade-dress closeness are **blocked on legal counsel** and stand as placeholders until sign-off before public launch (§7, Risk 5). The asset/font *license* posture is **decided** — open only, CC/OFL preferred (§5) — and is not gated.

Cross-references: engine hosting and package trust model in 01-architecture.md §6; era-faithfulness rules and per-theme shell UX (taskbar/start menu layout per theme) in 07-shell-ux.md §1; file manager chrome theming in 06-file-manager.md; perf budgets and asset-production schedule in 09-engineering-plan.md.

---

## 1. Theme engine architecture

### 1.1 Avalonia primitives: control themes vs resource dictionaries

Avalonia 11 gives us two orthogonal mechanisms; we use both, for different jobs:

| Mechanism | What it controls | How we use it |
|---|---|---|
| `ControlTheme` (keyed by control `Type` or explicit key) | Template + setters for a control class: the *structure* of a Button, ScrollBar, Menu | One `ControlTheme` per control per theme. Templates differ radically between themes (Win2000 ScrollBar has stepper buttons and a flat-bevel thumb; Win11 has an overlay thumb) — this cannot be done with brushes alone. |
| `ResourceDictionary` (brushes, metrics, geometry, fonts) | The *palette and numbers* a template consumes via `{DynamicResource}` | Semantic key layer (`Bevel.Color.*`, `Bevel.Metric.*`, `Bevel.Font.*`, §3) that all templates and all first-party app chrome (taskbar, file manager) reference. |

Rules:

- **ENG-01** Control templates MUST reference colors, metrics, and fonts only via `{DynamicResource Bevel.*}` keys — never literals — so palette variants (Luna Blue/Olive/Silver, user-recolored Classic schemes) are dictionary swaps, not template forks.
- **ENG-02** Structural differences between themes are expressed as separate `ControlTheme`s, not triggers/pseudo-class contortions inside one mega-template. One theme = one coherent set of templates.
- **ENG-03** Bevel-specific composite widgets (taskbar, start menu, tray, file manager chrome, desktop icon) are `TemplatedControl`s with their own `ControlTheme` per theme — they participate in the same engine as stock controls. This is the layer no upstream theme library provides (§11).
- **ENG-04** Avalonia's `ThemeVariant` (Light/Dark) is orthogonal and mostly unused by Classic/Luna (they are single-variant); the Win11 theme maps `ThemeVariant.Light`/`Dark` to its two palettes. The manifest declares `"variants"` (§2).

### 1.2 Layering model

Resolution order (last wins), all installed into `Application.Resources`/`Application.Styles` scopes:

1. **Base layer** (`Bevel.Themes.Base`): defines every `Bevel.*` semantic key with fallback values, plus theme-agnostic behaviors (focus adorner plumbing, text selection). Guarantees a missing key never crashes a template.
2. **Theme layer**: the active theme's control themes + resource dictionaries.
3. **Variant layer**: palette variant within a theme (Luna Olive; Classic user color scheme; Win11 Dark).
4. **User overrides**: a small, whitelisted set of keys the settings UI may override (accent color, font size bump, wallpaper) persisted under `"theme:<id>"` in settings (01-architecture.md §5).

### 1.3 C# surface

```csharp
namespace Bevel.Themes.Abstractions;

public interface IThemeManager
{
    IReadOnlyList<ThemeDescriptor> InstalledThemes { get; }   // built-in + user dir
    ThemeDescriptor ActiveTheme { get; }
    string? ActiveVariantId { get; }
    Task ApplyAsync(string themeId, string? variantId = null, CancellationToken ct = default);
    Task<ThemeDescriptor> InstallPackageAsync(string bevelThemePath, CancellationToken ct = default); // validates, copies to user themes dir
    event EventHandler<ThemeChangedEventArgs> ThemeChanged;    // fired AFTER resources swapped, BEFORE window rebuild completes
}

public sealed record ThemeDescriptor(
    string Id,                      // "win2000", "luna", "win11", or reverse-dns for 3rd party
    string DisplayName,
    Version Version,
    Version MinBevelVersion,
    IReadOnlyList<ThemeVariant2> Variants,   // id, display name, preview swatch
    ThemeMetrics Metrics,           // §3, parsed from manifest
    ThemeSource Source);            // BuiltIn | UserPackage

public sealed record ThemeMetrics(  // logical px @ 1.0 scale; full key list in §3
    double CaptionHeight, double CaptionButtonWidth, double CaptionButtonHeight,
    double ResizeBorder, double EdgeThickness, double ScrollBarSize,
    double MenuBarHeight, double MenuItemHeight, double TaskbarHeight,
    double IconGridCellWidth, double IconGridCellHeight,
    double CornerRadius,            // 0 for Classic; Luna top-corner value; Win11 8
    IReadOnlyDictionary<string, double> Extended);  // theme-specific extras
```

Consumers (taskbar layout, work-area reservation in platform chapters 02–04, desktop grid) read `ThemeMetrics`, never hardcoded numbers. `ThemeMetrics` values are also published into the resource system as `Bevel.Metric.*` for AXAML use — `ThemeMetrics` is generated from the same manifest block, so C# and AXAML cannot disagree.

### 1.4 Runtime switching

Per THM-03 (01-architecture.md) switching is live, no restart, target < 500 ms.

Mechanics — Avalonia caveat: swapping a `ControlTheme` dictionary does not reliably re-template already-realized controls in all cases. Rather than chase invalidation edge cases:

- **ENG-05** On `ApplyAsync`: (1) swap the theme/variant dictionaries in `Application`; (2) for every open Bevel window, detach the content view, and re-instantiate the view against the retained view-model (MVVM state survives; visual state like scroll offsets is best-effort restored via a `IViewStateCapture` hook); (3) fire `ThemeChanged`. Windows are rebuilt back-to-front; the wallpaper/desktop window rebuilds first to avoid a flash of unstyled desktop.
- **ENG-06** Theme switch also triggers: tray icon re-render requests, `IconInvalidated` on the app-icon service (01-architecture.md §2.2), work-area re-reservation if `TaskbarHeight` changed (per-platform, chapters 02–04), and sound-scheme swap.
- **ENG-07** A failed `ApplyAsync` (bad third-party package at runtime) rolls back to the previous theme atomically; the previous dictionaries are not disposed until the new theme's first frame renders.

---

## 2. Theme package format (`.beveltheme`)

Extends THM-01 (01-architecture.md). A zip with this layout:

```
mytheme.beveltheme
├── theme.json                  # manifest (schema below, versioned "$schema")
├── controls/*.axaml            # ControlThemes, runtime-loaded via AvaloniaRuntimeXamlLoader
├── palette/{default,dark,...}.axaml   # one dictionary per variant
├── assets/
│   ├── icons/{16,32,48}/*.png  # pixel art sizes actually authored (§6)
│   ├── icons/scalable/*.svg    # optional vector sources (Win11 theme uses these)
│   ├── cursors/*.png + cursors.json   # image + hotspot table
│   ├── sounds/*.wav
│   └── wallpaper/*.png
├── fonts/*.ttf|*.otf           # each font MUST have a matching entry in provenance
└── provenance.json             # per-asset {created-by, license, source} — THM-04 CI gate
```

`theme.json` (illustrative):

```json
{
  "$schema": "https://bevel.example/schemas/theme-1.json",
  "id": "com.example.mytheme",
  "name": "My Theme",
  "version": "1.2.0",
  "minBevelVersion": "1.0",
  "variants": [{ "id": "default", "name": "Default" }],
  "metrics": { "captionHeight": 18, "resizeBorder": 4, "scrollBarSize": 16, "...": 0 },
  "fonts": { "ui": "Bevel Sans", "caption": "Bevel Sans Bold", "monospace": "..." },
  "sounds": { "startup": "sounds/startup.wav", "error": "sounds/error.wav" },
  "capabilities": ["classic-color-schemes"]
}
```

- **PKG-01** No assemblies; loader rejects `x:Class`, code-behind, and non-`avares`/package-relative URIs. AXAML parse errors at install time fail `InstallPackageAsync` with line/column diagnostics. **Trust posture is pending counsel/decision:** the engineering leaning is **declarative-only** (no code-carrying third-party themes), but this is not finalized — the format and install pipeline are built to that leaning now, and the final third-party-theme trust model (curation, install-time warnings, permitted content) awaits sign-off (Risk 5, open question 5). Do not treat code-carrying themes as permitted.
- **PKG-02** First-party themes are compiled assemblies (`Bevel.Themes.Win2000|Luna|Win11`) for startup speed but CI also packs and round-trips them as `.beveltheme` (THM-02), so the third-party path is continuously exercised and the "supported AXAML subset" doc stays honest.
- **PKG-03** Missing pieces fall back to the base layer (§1.2). A theme may legitimately ship only a palette + icons (a "reskin" of Classic templates) by declaring `"inherits": "win2000"` — template inheritance resolves at load, one level deep only. The v1 **stub second theme** is exactly this: an `"inherits": "win2000"` palette-swap package (plus at least one structurally different control template — see Risk 8) that keeps the swap path (ENG-05), install, and rollback continuously exercised at near-zero asset cost.
- **PKG-04** Third-party SDK (post-v1, architected now): `bevel theme new|pack|validate|preview` CLI; `preview` boots the shell against `Bevel.Pal.Fake` (01-architecture.md DI-05) with a scripted desktop and produces a screenshot gallery of every control state — the same harness our own visual-regression tests use (09-engineering-plan.md).

---

## 3. Metrics system

Semantic keys, defined per theme, consumed by templates and by layout code via `ThemeMetrics`. All values are **logical px at 1.0 scale** (§4). Core table with the three themes' values (Luna and Win11 columns are locked now as engine-design inputs; their pixel-golden verification runs only when each theme ships, so the visual-test matrix at v1 covers Win2000 + the stub — 09-engineering-plan.md):

| Key | Win2000 | XP Luna | Win11 | Notes |
|---|---|---|---|---|
| `Bevel.Metric.CaptionHeight` | 18 | 25 | 32 | Excl. frame edge |
| `Bevel.Metric.CaptionButton.W×H` | 16×14 | 21×21 | 46×32 | W11 buttons are full-height hit areas |
| `Bevel.Metric.ResizeBorder` | 4 | 4 | 4 | Invisible extension outside rounded corner on W11 |
| `Bevel.Metric.EdgeThickness` | 2 | 1 | 1 | Classic 3D edge = outer+inner bevel |
| `Bevel.Metric.ScrollBarSize` | 16 | 17 | 14 (overlay 6) | W11 expands on hover |
| `Bevel.Metric.MenuBarHeight` | 19 | 20 | 40 | W11 uses menu-as-flyout style |
| `Bevel.Metric.MenuItemHeight` | 17 | 18 | 34 | |
| `Bevel.Metric.TaskbarHeight` | 28 | 30 | 48 | Consumed by work-area reservation (ch. 02–04) |
| `Bevel.Metric.IconGrid.Cell` | 75×75 | 75×75 | 76×76 | Desktop grid (07-shell-ux.md §5.2) |
| `Bevel.Metric.CornerRadius` | 0 | 8 (top only) | 8 | Luna's is bitmap-clipped, W11 true rounding |
| `Bevel.Metric.FocusRectInset` | 1 | 1 | 2 | Classic dotted rect vs W11 2px rounded ring |
| `Bevel.Metric.ButtonPadding` | 6,1 | 8,2 | 12,5 | |

- **MET-01** Metrics are data in `theme.json`; the compiled first-party themes generate both the C# record and the `Bevel.Metric.*` resources from the same JSON at build time (source generator) — single source of truth.
- **MET-02** A user "large fonts" accessibility toggle multiplies font sizes and font-derived metrics (`MenuItemHeight`, `CaptionHeight` where the era did the same) by 1.25 — mirroring Windows' "Large Fonts" mode rather than inventing a new scaling system.

---

## 4. DPI strategy

**Decision: hybrid — vector-redrawn chrome with device-pixel snapping; pixel-art assets integer-scaled with nearest-neighbor.** Rationale and rejected alternatives:

- *Pure integer pixel-art scaling of everything* (render at 1× and blit ×2/×3): authentic, but text becomes chunky-blurry at retina and fractional scales (1.25/1.5 common on Windows/Linux later) produce shimmering. Rejected.
- *Pure vector redraw*: text and bevels crisp everywhere, but 16px icons redrawn as smooth vectors stop looking like Win2000 — anti-aliased curves kill the pixel grid that *is* the aesthetic. Rejected.
- *Hybrid* keeps text native-resolution (Skia renders fonts at device resolution), keeps bevels mathematically crisp, and keeps icons on an integer pixel grid. This is also what well-regarded retro-faithful projects converge on.

Rules:

- **DPI-01** All chrome drawing (bevels, gradients, frames, separators) goes through a `BevelRenderer`/theme drawing layer that converts logical thickness to device pixels: `devicePx = max(1, round(logicalPx × scale))`, and aligns strokes to the device-pixel grid (the 0.5-offset trick in device space). `UseLayoutRounding=true` app-wide.
- **DPI-02** Pixel-art raster assets (Classic + Luna icons, cursors, Luna chrome bitmaps) render with `RenderOptions.BitmapInterpolationMode=None` at integer multiples: at scale `s`, blit factor `k = max(1, floor(s))` and center within the logical slot. At 2.0 (macOS retina — the v1 platform, ch. 02) a 16px icon renders as crisp 32 device px of doubled pixels. At 1.5, `k=1` and the icon is device-pixel-aligned but smaller relative to text — accepted trade-off; fractional scales are rare on macOS.
- **DPI-03** Where an authored larger size exists (16/32/48 sets, §6), prefer *authored* art at the nearest ≤ target device size over algorithmic upscale of smaller art (32px art at 2.0× scale for a 16-logical slot, i.e. real detail, not doubled 16px — per-icon flag, because some icons look wrong "detailed").
- **DPI-04** The Win11 theme is vector-first (SVG icon sources, true rounded corners, no pixel-grid conceit): standard Avalonia scaling, no special handling beyond DPI-01 snapping for 1px hairlines.
- **DPI-05** Per-monitor DPI: assets and pen widths are resolved per-`TopLevel` scale, re-resolved on `ScalingChanged` (window dragged across monitors).

---

## 5. Font strategy

Licensing reality: Tahoma, MS Sans Serif, Trebuchet MS, Franklin Gothic, Segoe UI/Segoe Fluent Icons are Microsoft-licensed and **cannot ship**. **Font-license posture — decided (2026-07-04): open licenses only, SIL OFL and Creative Commons (CC0/CC-BY) preferred; avoid LGPL font files wherever an OFL/CC equivalent exists.** This resolves the earlier OFL-vs-LGPL-Wine question in favor of open/OFL. What still gets a counsel look is *trade dress* — how close a shipped face may look to a specific Microsoft one — not the license. The substitution *mechanism* (FNT-01..FNT-04) is legally neutral regardless of which faces are chosen.

| Role | Original | Ship (with theme) | License | Notes |
|---|---|---|---|---|
| Classic UI text | MS Sans Serif 8pt (bitmap) / Tahoma | **Bevel Sans** — an OFL/CC bitmap-style face at MS Sans Serif / Tahoma metrics, antialiasing off — self-produced (AI-assisted + open source material) **in v1** | SIL OFL 1.1 (target) | Preferred over Wine Tahoma so no LGPL file ships. Wine Tahoma (LGPL-2.1+, renamed per FNT-03) is an acceptable *open* stopgap only if Bevel Sans isn't ready at first boot. Classic.Avalonia already targets Tahoma-with-no-AA as the classic look. |
| Classic captions | MS Sans Serif Bold | Wine Tahoma Bold | LGPL-2.1+ | |
| Luna UI text | Tahoma 8pt | Wine Tahoma | LGPL-2.1+ | Same file. |
| Luna caption | Trebuchet MS Bold | Wine Tahoma Bold (compromise) | — | No open metric-clone of Trebuchet exists. Compromise accepted for Luna's initial release (v1.1); sourcing or self-producing a Trebuchet-flavored OFL/CC face is an open item. |
| Luna Start button | Franklin Gothic Medium Italic | **Libre Franklin** SemiBold Italic | SIL OFL 1.1 | Franklin Gothic revival; stylistically right, metrics close enough for a single label. |
| Win11 UI text | Segoe UI Variable | **Selawik** | SIL OFL 1.1 | Microsoft's own metric-compatible Segoe UI substitute. No variable optical axes — acceptable; fixed instances per size band. |
| Win11 glyph/icon font | Segoe Fluent Icons | **Fluent UI System Icons** (microsoft/fluentui-system-icons) | MIT | Microsoft's open icon set; legally clean and visually canonical. |
| Monospace (all) | Lucida Console/Consolas | **Cascadia Code** (W11) / **Liberation Mono** (Classic/Luna) | OFL / OFL | |

- **FNT-01** Fonts embed via `avares://` and register with `FontManagerOptions`/`FontFallbacks`; themes reference `Bevel.Font.UI`, `Bevel.Font.Caption`, `Bevel.Font.Mono` keys only.
- **FNT-02** Classic/Luna text renders with antialiasing disabled (`RenderOptions.TextRenderingMode=Alias`) at scales ≤ 1.0 and with AA on at ≥ 2.0 (aliased text at retina looks broken, not retro; the pixel-grid conceit applies to icons, not device-resolution text). Threshold per-theme-overridable.
- **FNT-03** No shipped file may be named `tahoma.ttf`, `segoeui.ttf`, etc., and internal font family names must not collide with Microsoft names (Wine Tahoma's internal name is "Tahoma" — rename the family table to "Bevel Tahoma" in our build step to avoid substituting into user documents and to dodge trademark issues; metric compatibility is unaffected).
- **FNT-04** CJK/global text: fall back to Noto Sans (OFL) via `FontFallbacks`; era themes never had good CJK bitmap coverage, correctness beats fidelity here.

---

## 6. Asset pipeline

Source of truth lives in `assets-src/` (repo), built to per-theme packages by `Bevel.AssetPipeline` (a build-time dotnet tool):

1. **Pixel icons (Classic, Luna)**: authored in Aseprite (`.ase` committed); exporter emits 16/32/48 PNGs + a generated `icons.json` (id → sizes → integer-upscale-allowed flag, DPI-03). Palette-locked: Classic icons restricted to the classic 16/256-color-era palette ramps; Luna to its 32-bit alpha style guide. Style guides live beside the sources so the author's AI-assisted and hand-authored passes stay consistent (09-engineering-plan.md).
2. **Vector icons (Win11)**: Fluent UI System Icons (MIT) consumed as SVG, subset by manifest; colored document/folder icons are original recreations in the Fluent style (SVG).
3. **Cursors**: PNG frames + `cursors.json` hotspots; loaded as `new Cursor(bitmap, hotSpot)`. Animated cursors (hourglass) are frame lists. Reality note: custom cursors apply **only inside Bevel's own windows** — the OS cursor elsewhere is untouched (macOS constraint, ch. 02); the desktop/taskbar/file manager being ours makes this cover most of the experience.
4. **Sounds**: original recreations *evocative of* (not sampled from) the era sets — self-produced or sourced under CC0/CC-BY — startup, shutdown, error, asterisk, ding, recycle. 16-bit/44.1kHz WAV. Mapped by `theme.json` `"sounds"`; played via the PAL audio service.
5. **Wallpapers**: original art (a period-appropriate teal solid is just a color; "Bliss-like" imagery must be an original photo/render, not the Bliss photograph).
6. **Provenance gate (THM-04)**: every file in `assets/` and `fonts/` must have a `provenance.json` entry (`created-by`, `license`, `source`, `commissioned-under` for work-for-hire); CI fails on missing entries, on licenses outside the allowlist (OFL, MIT, CC0, CC-BY-4.0, LGPL-fonts-only, proprietary-work-for-hire), and on known-hash matches against a blocklist of extracted Microsoft assets (shell32.dll icon hashes etc. — cheap tripwire against a contributor "helpfully" dropping in the real thing).

**Asset-production schedule (staggered, 2026-07-04 — feeds 09-engineering-plan.md):** assets are **self-produced by the author at $0 cash** — AI generation tools (incl. the author's existing graphics-AI subscriptions) for originals, plus **open-licensed source material (Creative Commons CC0/CC-BY and SIL OFL preferred)**; no paid commissioning. The **Windows 2000 core set** (~80–120 icons + the Bevel Sans font + core sounds) is produced **during v1**; the **Luna icon set** **mid-v1** (for the v1.1 Luna release, LUN-05); the **Win11 set** **after v1** (for v1.2). Caveat tied to the legal gate (§7, Risk 5): generic file/folder/device icons proceed now, but the **start-button glyph and any trademark-adjacent art wait for counsel** — regardless of how they're produced, don't finalize those until sign-off.

---

## 7. The legal line

Position (engineering policy, not legal advice). **Status (2026-07-04):** counsel review is a defined pre-launch gate that specifically blocks the user-facing theme names, the start-button logo/mark, and the third-party-theme trust posture (§2), plus any trade-dress judgment on how close a shipped asset may look to a specific Microsoft original. **The font/asset *license* posture is decided — open only, Creative Commons and SIL OFL preferred (§5–§6) — and is no longer gated.** The engineering policy below stands and the theming engine proceeds; the gated items are placeholders until sign-off.

- **Copyright — bright line.** Icons, bitmaps, cursors, sounds, fonts extracted from Windows are copyrighted works: never shipped, never traced 1:1, never auto-converted. Recreations are drawn from scratch to a written style description ("16px printer: gray body, paper feeding out the top, 1px black outline"), not by eyedropping the original pixel-for-pixel. A recreation that is pixel-identical to the original is treated as a copy regardless of process.
- **Look and feel — defensible ground.** *Apple v. Microsoft* (9th Cir. 1994) and *Lotus v. Borland* (1st Cir. 1995) establish that UI layout conventions, widget arrangements, and functional interface elements get thin-to-no copyright protection. A taskbar at the bottom, a Start-shaped menu, beveled gray buttons, a two-pane file manager — these are ideas/methods of operation we may reimplement.
- **Trade dress** is the live risk: a *totality* of look that indicates source. Mitigations: (a) no Microsoft word marks or logos anywhere — no Windows flag (our start button uses an original logo), no "Windows", "Luna", or "Fluent" in user-facing strings (internal ids `win2000/luna/win11` are fine; user-facing names such as "Classic 2000", "Silver Blue", "Modern" are **placeholder pending counsel** and not final); (b) marketing never claims affiliation and describes themes as "retro/classic style"; (c) the product name and about-box branding are entirely our own.
- **Fonts**: typeface *designs* are not copyrightable in the US, but font *files* are software — hence substitutes (§5), never converted Microsoft font files. Names like "Tahoma"/"Segoe" are trademarks — internal family renaming (FNT-03).
- **Sounds**: recordings are copyrighted; "reminiscent of a soft major-key chime" is not. Self-produced originals or CC0/CC-BY-licensed sources only.
- **LEG-01** All of the above is enforced mechanically where possible (THM-04 gate, string-lint for forbidden marks in user-facing resources) and by PR checklist elsewhere.

---

## 8. Windows 2000 Classic theme (default) — detailed spec

### 8.1 Palette

Default scheme "Windows Standard" (values from Win2000 `HKCU\Control Panel\Colors` defaults), exposed as `Bevel.Color.*`:

| Key (classic name) | Hex | Key (classic name) | Hex |
|---|---|---|---|
| ButtonFace / Menu / Scrollbar | `#D4D0C8` | ActiveTitle | `#0A246A` |
| ButtonHighlight | `#FFFFFF` | GradientActiveTitle | `#A6CAF0` |
| ButtonLight (3DLight) | `#D4D0C8` | InactiveTitle | `#808080` |
| ButtonShadow | `#808080` | GradientInactiveTitle | `#C0C0C0` |
| ButtonDkShadow | `#404040` | ActiveTitleText | `#FFFFFF` |
| Window | `#FFFFFF` | InactiveTitleText | `#D4D0C8` |
| WindowText / MenuText / InfoText | `#000000` | Highlight | `#0A246A` |
| WindowFrame | `#000000` | HighlightText | `#FFFFFF` |
| GrayText | `#808080` | HotTracking | `#000080` |
| AppWorkspace | `#808080` | InfoWindow (tooltip) | `#FFFFE1` |
| Desktop background | `#3A6EA5` | | |

_Verified 2026-07-11 against `docs/reference/win2000/` (bevel-8g5, following the public
reference corpus from bevel-t97): `Highlight`/`ActiveTitle` `#0A246A`, `ButtonFace`/`Menu`
`#D4D0C8`, and `ButtonDkShadow` `#404040` are each independently confirmed by direct pixel
sampling of multiple real Win2000 screenshots — see window-chrome-controls.md, taskbar-start-menu.md,
color-schemes-accessibility.md — despite a secondary hobbyist reference table (quppa.net)
listing `#000080`/`#C0C0C0`/`#000000` instead; this spec's values are the measurement-backed
ones. **Open, not yet resolved:** `GradientInactiveTitle` here is `#C0C0C0`, but that same
secondary table (quppa.net) gives `#B5B5B5` for the inactive-caption gradient endpoint, and
win2000-explorer-chrome.md's own figure for this value was itself only an unmeasured estimate
(not a pixel sample) — corrected there to note the same open question rather than presenting
either figure as resolved. Neither hex has a genuine screenshot pixel-measurement backing it
yet; leave `#C0C0C0` shipping until one does.

- **W2K-01** The full classic *color scheme* system ships: the manifest capability `classic-color-schemes` enables a settings page with the stock schemes (Windows Standard, Brick, Desert, Eggplant, High Contrast Black/White, Lilac, Maple, Marine, Plum, Pumpkin, Rainy Day, Red White and Blue, Rose, Slate, Spruce, Storm, Teal, Wheat) as palette-variant dictionaries generated from a schemes JSON. High-contrast schemes double as our accessibility story for this theme. **v1 ships theme presets only** (resolved 2026-07-04): the stock schemes above are selectable, but the full Win2000 "Appearance" dialog (per-element color/font editing) is **deferred**. The engine stays per-element capable — schemes are already per-element palette dictionaries — so the editor is a later UI surface over an unchanged data model, not new engine work.

### 8.2 Bevel algebra

The entire theme derives from Win32 `DrawEdge` semantics. Four stroke pairs (top-left color / bottom-right color), each 1 logical px:

| Edge | Top-left | Bottom-right |
|---|---|---|
| Raised outer | ButtonLight | ButtonDkShadow |
| Raised inner | ButtonHighlight | ButtonShadow |
| Sunken outer | ButtonShadow | ButtonHighlight |
| Sunken inner | ButtonDkShadow | ButtonLight |

Composites: `EDGE_RAISED` = raised outer + raised inner (2px, e.g. buttons, window frame); `EDGE_SUNKEN` = sunken outer + sunken inner (text boxes, status bar wells use sunken-outer only, 1px); "thin raised" = raised inner only (toolbar buttons on hover, menu bar items). Note ButtonLight == ButtonFace in the standard scheme, so raised-outer's top-left blends into the face — this is correct and scheme-dependent, not a simplification.

- **W2K-02** All bevels are drawn by the shared `ClassicBorderDecorator` (extended from Classic.Avalonia's port of WPF's) parameterized by `BorderStyle` (Raised, RaisedPressed, RaisedFocused, Sunken, Etched, Bump, ThinRaised, ThinSunken), consuming the four `Bevel.Color.Button*` resources — recolorable by scheme (W2K-01).

### 8.3 Window chrome

- Frame: `EDGE_RAISED` (2px) + `ResizeBorder` 4px total including bevel; corners square.
- Title bar: height 18, horizontal gradient `ActiveTitle → GradientActiveTitle` (left→right, sRGB linear interpolation as GDI did); caption text left-aligned, bold, `ActiveTitleText`, 2px left inset after the 16px window icon.
- Caption buttons: 16×14, classic glyphs (minimize `_`, maximize `▢`, restore double-rect, close `✕` — drawn as pixel-art glyph bitmaps, not font glyphs), right-aligned with 2px margin, 2px gap between close and the maximize/minimize pair. Buttons are `EDGE_RAISED`, pressed = `RaisedPressed` (sunken, glyph shifts +1,+1).
- Unfocused window: `InactiveTitle → GradientInactiveTitle` gradient, `InactiveTitleText` caption.

### 8.4 Per-control spec (normative summary)

| Control | Spec |
|---|---|
| Button | Face fill, EDGE_RAISED; default button adds 1px black outer rect; pressed = sunken + content shift +1,+1; focus = 1px dotted rect inset 1px inside bevel (marching-ants pattern, `FocusRectInset`); disabled text = embossed (GrayText with ButtonHighlight +1,+1 shadow) |
| CheckBox / Radio | 13×13 sunken well, white fill; check/dot are pixel glyphs; indeterminate = dithered gray fill (true 50% checkerboard of Face/Window, not alpha) |
| TextBox | EDGE_SUNKEN (2px), Window fill, caret 1px, selection Highlight/HighlightText |
| ComboBox | TextBox well + 16px dropdown button (EDGE_RAISED, black triangle glyph); editable and list modes |
| ScrollBar | 16px wide; stepper buttons EDGE_RAISED with triangle glyphs, pressed-sunken w/o glyph shift; thumb EDGE_RAISED face-fill, no grip; track = 50% checkerboard dither of Scrollbar/ButtonHighlight; page-press inverts track region |
| Menu | Menu fill; menubar item hover = ThinRaised, open = ThinSunken; popup = EDGE_RAISED panel; item selection = Highlight bar full-width; accelerator right-aligned; separators = etched 2px; check/radio marks pixel glyphs; disabled = embossed |
| TabControl | EDGE_RAISED page; tabs raised with rounded-by-1px top corners; selected tab 2px taller, overlaps page edge |
| ProgressBar | Sunken well; fill = discrete Highlight blocks 8px wide, 2px gap (block mode is the Win2000 default) |
| Slider | Sunken 2px track; pointed-thumb pixel shape; tick marks 1px ButtonDkShadow |
| TreeView / ListView | Window-filled EDGE_SUNKEN wells; tree lines dotted 1px; ±expanders 9×9 boxes; ListView per 06-file-manager.md (Icon/SmallIcon/List/Details modes) |
| ToolBar | ThinRaised on hover only (flat otherwise), 22px height default; grippers = double etched line |
| StatusBar | Face fill, panels ThinSunken, size grip = diagonal ButtonShadow/ButtonHighlight lines |
| Tooltip | InfoWindow fill, 1px WindowFrame border, InfoText |
| GroupBox | Etched (Bump) 2px border, label knocks out border on Face |

- **W2K-03** Dithered fills (scrollbar track, indeterminate checks) are true 1px checkerboards rendered via a shared tiled `ImageBrush` generated from the live scheme colors — never a flat 50% alpha blend.

Bevel-widget chrome for this theme (taskbar = raised strip; Start button; tray well = ThinSunken; desktop icons with Highlight-tinted selection and label backplates) is specced behaviorally in 07-shell-ux.md; visually it composes exclusively from §8.1–8.2 primitives plus W2K metrics (§3).

---

## 9. Windows XP Luna theme (scoped spec — targets v1.1)

**Deferred out of v1.** Luna ships in v1.1 with **Blue** variant only; Olive/Silver as palette variants later (the engine supports them from day one via §1.2 layer 3). This spec is locked now so the style guide and asset production (LUN-05) can start during v1 development, in parallel and off the release critical path; nothing in this section gates v1.

- **LUN-01** Chrome is *authored raster*, like the original: title bars, caption buttons, Start button, taskbar, and button faces are 9-slice PNG sets (original recreations per §6 style guide — glossy gradients, 1px dark blue outlines), stretched per Avalonia 9-slice (`NineSliceBrush`-style custom drawing since Avalonia lacks native nine-slice — small utility control, also useful to third-party themes).
- **LUN-02** Key palette: title bar blues `#0055EA→#0831D9` band structure, window face `#ECE9D8`, selection `#316AC5`, taskbar blue `#245EDC`, Start green `#3B8E3F` range. Full palette table lives in the theme's style guide doc, not here.
- **LUN-03** Metrics per §3 column. Top window corners rounded (bitmap-clipped ~8px); bottom corners square. Caption buttons 21×21 glossy squares; close is red.
- **LUN-04** Controls keep Classic *structure* where XP did (menus, list wells are near-classic with recolored palette); only the visibly-Luna controls get new templates: Button, scrollbars (gradient thumb with grip dots), TabControl, ProgressBar (green gradient blocks), title bar/caption, Start menu two-column layout (07-shell-ux.md §3.1), taskbar grouping pills.
- **LUN-05** Icons: XP-style 32-bit alpha pixel art with the era's 3D perspective and drop shadows — the costliest asset set (48px hero sizes); self-production plan (AI-assisted + open/CC sources, $0) in 09-engineering-plan.md.

## 10. Windows 11 theme (scoped spec — targets v1.2)

**Deferred out of v1**, sequenced after Luna (v1.2). The spec is locked now because the engine must accommodate it from day one: the variant model (ENG-04), the vector-first DPI path (DPI-04), and the animation-level manifest property (W11-05) are all v1 engine features that Win11 merely exercises later. The v1 engine likewise carries the XP/Win11 `BehaviorProfile` flags from day one; they are simply **left unset by the only theme shipped at v1** (Windows 2000) and become active when Luna (v1.1) and Win11 (v1.2) land.

- **W11-01** Base: Avalonia's built-in **FluentTheme** control themes, overridden where Avalonia's Fluent diverges from Win11 (WinUI 2.x vs WinUI 3 styling) — notably: 8px corner radius on windows/popups/buttons, accent-pill selection, `Bevel.Metric` column per §3, and the Win11 taskbar/start (centered icons, pinned grid start menu — 07-shell-ux.md).
- **W11-02** Light + Dark via `ThemeVariant` (ENG-04). Accent color user-selectable (layer 4); default `#0067C0`.
- **W11-03** Mica/Acrylic: true blur-behind-desktop is compositor-dependent. Position: approximate Mica as an opaque tint of the current wallpaper (sample + blur once per wallpaper change, cheap and deterministic); use Avalonia `TransparencyLevelHint.AcrylicBlur` only where the platform grants it (macOS vibrancy works, ch. 02); never block on it.
- **W11-04** Typography Selawik (§5); glyphs Fluent UI System Icons (MIT); colored file-type/folder icons are original Fluent-style SVG recreations.
- **W11-05** Motion: Fluent-style easing (cubic-bezier(0.0,0.0,0.0,1.0) decelerate, 150–300ms) on flyouts/taskbar; Classic and Luna themes declare `"animations": "none"` and `"minimal"` respectively in their manifests — animation level is a theme property, honored engine-wide (era-faithfulness, 07-shell-ux.md §1).

---

## 11. Classic.Avalonia as the base — evaluation

Verified against the upstream repo (BAndysc/Classic.Avalonia, MIT, targets Avalonia 11 stable / 12 beta):

**What it provides (we take):**

- `Classic.Avalonia.Theme`: classic control themes for the stock Avalonia control set, ported from WPF's Classic theme — including the crucial `ClassicBorderDecorator` (bevel algebra, §8.2) — plus DataGrid and ColorPicker add-on packages.
- `ClassicWindow` (classic title bar chrome), classic `MessageBox` (with sounds), `ToolBar`, a `ListView` with Icon/SmallIcon/List modes, `AboutDialog`, `FontDialog`.
- The Tahoma-no-antialiasing text approach it already uses matches our FNT decision at 1× (§5, FNT-02).

**What's missing (we build):**

| Gap | Our plan |
|---|---|
| Taskbar, Start menu, tray, desktop icon grid, balloon tips | `Bevel.*` templated widgets, ENG-03, behavior in 07-shell-ux.md |
| File manager chrome (address bar, rebar toolbars, Details header, folder tree band) | 06-file-manager.md; templates in `Bevel.Themes.Win2000` |
| Metrics-as-data, semantic resource keys, theme manifest/packaging | §2–3 (upstream hardcodes values in templates) |
| Classic color schemes (recolorability) | W2K-01; requires auditing upstream templates for literal brushes → ENG-01 refactor |
| DPI device-pixel snapping & integer pixel-art scaling | §4; extend `ClassicBorderDecorator` per DPI-01 |
| Win2000-specific fidelity deltas (upstream is 9x-flavored in places: default `#C0C0C0`-era assumptions, gradient captions, font metrics) | Fidelity pass against a real Win2000 VM screenshot corpus (09-engineering-plan.md test rig) |
| Luna, Win11 themes | Ours entirely, post-v1 (§9–10; FluentTheme base for W11) |
| Icon/cursor/sound assets | §6 pipeline (upstream ships none of consequence) |

**Decision: fork, don't just depend.** We need invasive changes (resource-key refactor ENG-01, metrics extraction MET-01, DPI-01 snapping) that upstream may not want. Fork into `Bevel.Themes.Win2000` with clear MIT attribution, keep a periodic upstream diff-merge while divergence is low, and upstream any generally-useful fixes (nine-slice control, DPI snapping) as goodwill. Rationale: velocity and fidelity control outweigh merge cost; MIT makes this clean. Rejected: (a) depend-and-override — the override surface would exceed the library; (b) write from scratch — throws away a working, debugged port of WPF's classic theme for no benefit.

---

## Risks

1. **Runtime-AXAML gaps** (THM-01/PKG-01): `AvaloniaRuntimeXamlLoader` may not support everything compiled XAML does; mitigated by round-tripping our own themes through the package path in CI (PKG-02), but third-party authors may still hit undocumented edges. Maintain the supported-subset doc from day one.
2. **Theme-switch rebuild cost** (ENG-05): rebuilding all window content in <500 ms with a populated file manager and dense desktop is unproven; if missed, fall back to a brief crossfade snapshot overlay to hide the rebuild. Perf budget owned by 09-engineering-plan.md.
3. **Font substitute fidelity**: the self-produced "Bevel Sans" OFL face is the v1 primary. If it isn't ready at first boot, the Wine Tahoma LGPL stopgap covers it — but Wine Tahoma at aliased small sizes may render with spacing artifacts on macOS/Skia (upstream Classic.Avalonia reports exactly this). Mitigation is finishing Bevel Sans (already v1 scope); risk is author time, not commission lead time.
4. **Asset volume for Luna** (LUN-05): XP-style 32-bit icons are the most time-intensive set to produce at quality (author time, even AI-assisted). Deferring Luna to v1.1 takes this off the v1 critical path, but production lead time is real — if it doesn't start during v1 development, v1.1 slips; and a half-Luna theme at v1.1 damages the pillar-2 promise just as it would have at v1. Per-icon triage (smaller set + Classic fallbacks) remains the scope-control lever.
5. **Trade-dress exposure** (§7): mitigations are engineering-side only. As of 2026-07-04 counsel review is a **defined pre-launch gate** blocking the user-facing theme names, the start-button logo/mark, the third-party-theme trust posture (§2), and trade-dress closeness of assets to Microsoft originals; the asset/font *license* posture is decided (open only, CC/OFL preferred, §5) and is not gated. The theming engine proceeds normally in the meantime, but the gated items are placeholders and could force late renames or asset redraws if counsel rules against the current leanings.
6. **Fork drift from Classic.Avalonia**: our ENG-01/MET-01 refactor makes upstream merges progressively harder; accept after v1 (by then the fork is the canonical Win2000 implementation for our purposes).
7. **Fractional-scale pixel art** (DPI-02): at 1.25/1.5 (future Windows/Linux targets) icons render smaller relative to text; if user feedback is bad, the alternative (bilinear at fractional only) contradicts the aesthetic position and would need revisiting.
8. **Stub theme under-exercises the engine**: a palette-swap stub proves dictionary swapping, package install, and rollback, but not radically different control templates or the vector pipeline (DPI-04) — structural-template engine bugs could surface only when Luna/Win11 land in v1.1/v1.2. Mitigation: the stub MUST override at least one control template structurally (e.g. ScrollBar) so re-templating (ENG-05) is exercised, and PKG-02 round-trips keep the third-party package path honest from v1.

## Open questions

1. **User-facing theme names** ("Classic 2000" / "Silver Blue" / "Modern" placeholders) and the start-button logo design — **now gated on legal counsel** (Risk 5, §7), not just a product/branding decision. Tracked as placeholder-pending-counsel; no chapter action until sign-off.
2. **Asset production** — *Resolved (2026-07-04): **$0 cash**; assets self-produced by the author (AI-assisted + open/CC sources, CC0/CC-BY/OFL preferred), no paid commissioning (§6; 09-engineering-plan.md Q6).* Staggered schedule holds (Win2000 core during v1, Luna mid-v1, Win11 after v1). The only remaining hold is counsel on the start-button glyph and trademark-adjacent art (§6, Risk 5).
3. **Classic color-scheme editor**: *resolved (2026-07-04)* — v1 ships **preset schemes only** (W2K-01); the full Win2000 "Appearance" dialog (per-element color/font editing) is deferred to a later version. The engine stays per-element capable, so the editor is later UI over an unchanged data model.
4. **Font license** — *Resolved (2026-07-04): open only, SIL OFL and Creative Commons (CC0/CC-BY) preferred; avoid LGPL font files where an OFL/CC equivalent exists (§5). Bevel Sans is a self-produced OFL face in v1; Wine Tahoma LGPL is a stopgap only.* Not a counsel item — only trade-dress closeness of a face to a Microsoft original is.
5. **Third-party theme marketplace/trust**: `.beveltheme` is data-only, but sounds+wallpapers+fonts can still carry infringing content; do we want an install-time warning, a curation policy, or nothing (user's responsibility)? **Now under the counsel gate** (§2 PKG-01, Risk 5) — the trust posture is pending counsel/decision; engineering leaning is declarative-only but code-carrying themes are not asserted as permitted.
6. **Luna Olive/Silver**: confirmed post-v1.1 (Luna launches Blue-only), or does a marketing argument pull one variant into v1.1?
7. **v1 launch narrative with one theme**: pillar 2 promises three themes; product/marketing must position v1 as "Win2000 now, Luna and Modern soon". Does the stub second theme surface in the settings UI as a user-selectable curiosity, or stay hidden/dev-only? And is the Luna-before-Win11 ordering (v1.1 vs v1.2) — assumed here from asset lead times — confirmed against marketing priorities?
