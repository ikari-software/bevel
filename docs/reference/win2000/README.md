# Windows 2000 Reference Corpus

This is a **public, clean-room reference corpus** for Bevel's Windows 2000 theme: eight
domain notes, each built entirely from publicly reachable secondary sources (screenshot
galleries, archived Microsoft documentation, retrospectives, hobbyist palette
reconstructions) with per-fact citations. Companion images referenced by each doc live in
`refs/win2000/<domain>/` — that directory is **git-ignored**; the images are pixel-sampling
working material, not committed assets, and are not required for the corpus's factual claims
to be checked (every fact also carries a URL).

**Clean-room rules (hard):** every fact here must trace to a publicly reachable source with a
citation, never to Windows binaries, extracted resources, decompiled code, or leaked source —
and no value should ever be "corrected" by eyedropping or copying pixels 1:1 from a
screenshot into a shipped asset. Treat this corpus as *observed appearance and behavior*
(facts), not as a substitute for the from-scratch recreation process `05-theming.md` §7
requires for actual asset production.

---

## Index

| Domain | Doc | Facts† | Images‡ | Scope |
|---|---|---|---|---|
| Color schemes & accessibility | [color-schemes-accessibility.md](color-schemes-accessibility.md) | 21 | 11 | Appearance tab, named color schemes, High Contrast, Accessibility Options/Wizard, DPI/font-size axis |
| Dialogs, wizards & Control Panel | [dialogs-wizards-controlpanel.md](dialogs-wizards-controlpanel.md) | 30 | 20 | Message boxes, property sheets, Wizard97 template, common Open/Save dialog + Places Bar, Control Panel web view, About/Run/Shutdown dialogs |
| Explorer / file manager | [explorer-file-manager.md](explorer-file-manager.md) | 25 | 9 | Menu/toolbar/address-bar chrome, Web view (`folder.htt`), Details-view columns, status bar, search task pane |
| Icons / pixel art style | [icons-pixel-art-style.md](icons-pixel-art-style.md) | 21 | 15 | 16/32/48px icon sizes, 16- vs 256-color variants, light-source/perspective rules, overlay badges, 1-bit transparency |
| RTL & localized (Hebrew/Arabic) | [rtl-localized.md](rtl-localized.md) | 14 | 5 | `WS_EX_LAYOUTRTL` mirroring mechanics, title-bar/menu/dialog mirroring, non-mirrored exceptions |
| Taskbar & Start menu | [taskbar-start-menu.md](taskbar-start-menu.md) | 28 | 12 | Taskbar chrome/height, Quick Launch, task buttons, tray, classic single-column Start menu, Personalized Menus |
| Typography, metrics & effects | [typography-metrics-effects.md](typography-metrics-effects.md) | 20 | 6 | Tahoma vs MS Sans Serif, `MS Shell Dlg`/`MS Shell Dlg 2`, Wizard97 typography, Effects tab defaults, sounds |
| Window chrome & controls | [window-chrome-controls.md](window-chrome-controls.md) | 17 | 23 | Title-bar gradient, 4-tone bevel algebra, caption buttons, menu/toolbar/tab/scrollbar/progress-bar metrics |

† Count of bullets in each doc's "Key visual facts" section (a floor, not the full fact count — Measurements/Colors/Behavior-notes tables add more).
‡ Count of rows in each doc's "Images" table.

---

## Cross-domain highlights

The 10 most implementation-relevant facts across all eight docs — chosen for what they
constrain in Bevel's engine or theme, not just decoration:

1. **Icon transparency is a strict 1-bit AND-mask** — no alpha channel exists in the Win2000
   era; alpha-blended 32-bit icons are an XP-only addition. This is the single most
   load-bearing constraint on Bevel's icon renderer if it wants era-accurate compositing, not
   just era-accurate art. — [icons-pixel-art-style.md](icons-pixel-art-style.md) (gdgsoft.com)
2. **Explorer's toolbar redesign (IE5.5-derived) uses labeled list-style buttons** — Back
   carries a text label, Search/Folders/History are new labeled buttons — and this shrank the
   toolbar row to roughly half of Windows 98's height. — [explorer-file-manager.md](explorer-file-manager.md) (gekk.info)
3. **Web view (`folder.htt`-driven) is the default folder rendering**, toggled off per-machine
   via Folder Options → "Use Windows classic folders" — classic ListView-only rendering is the
   non-default fallback, not the baseline. — [explorer-file-manager.md](explorer-file-manager.md) (MSDN Magazine, June 2000)
4. **Windows 2000 introduced the 5-icon "Places Bar"** (History, Desktop, My Documents, My
   Computer, My Network Places) to the common Open/Save dialog — a Win2000-specific addition,
   not carried from NT4/98. — [dialogs-wizards-controlpanel.md](dialogs-wizards-controlpanel.md) (guidebookgallery.org)
5. **Wizard97 has two distinct page templates** — 317×193 DLU exterior (watermark bitmap,
   uncontrolled left margin) vs. 317×143 DLU interior (system-drawn header band) — with header
   title in Verdana Bold 12pt and body text in 8pt MS Shell Dialog. — [dialogs-wizards-controlpanel.md](dialogs-wizards-controlpanel.md) / [typography-metrics-effects.md](typography-metrics-effects.md) (learn.microsoft.com/windows/win32/controls/wizards)
6. **Taskbar height is a fixed 28px at 96 DPI**, confirmed identically on two independent,
   different-resolution screenshots (800×600 and 640×480) — a true fixed-pixel constant, not
   resolution-relative. — [taskbar-start-menu.md](taskbar-start-menu.md)
7. **The Start menu has no XP-style two-column pinned "Most Frequently Used" layout** —
   it's a classic single-column cascading menu, with "Personalized Menus" (IntelliMenus)
   hiding rarely-used items behind a chevron instead. Do not build the XP MFU pattern for the
   Win2000 theme. — [taskbar-start-menu.md](taskbar-start-menu.md) (toastytech.com)
8. **RTL layout is a per-window opt-in** (`WS_EX_LAYOUTRTL`), not a global screen transform —
   dialogs/message boxes need `MB_RTLREADING` explicitly, and mirroring is a true left-right
   flip of the title-bar button cluster (Close/Minimize/Restore order fully reverses), not just
   a relocation. — [rtl-localized.md](rtl-localized.md) (MSDN aa913269, flylib.com)
9. **Classic-theme progress bars render as discrete rectangular "chunks" by default**;
   `PBS_SMOOTH` continuous fill exists but is honored only under the Windows Classic theme —
   later visual styles override it back to a solid gradient. Relevant if Bevel's engine shares
   control templates across themes. — [window-chrome-controls.md](window-chrome-controls.md) (MicrosoftDocs progress-bar-control-styles)
10. **Tahoma is the intended Win2000 system font (via `MS Shell Dlg 2`), replacing bitmap MS
    Sans Serif — but adoption was inconsistent even inside Microsoft's own shipped dialogs**,
    so a period-faithful theme should expect (and can legitimately mix in) some MS
    Sans Serif holdouts rather than assuming 100% Tahoma coverage. — [typography-metrics-effects.md](typography-metrics-effects.md) (stealthpuppy.com, MS Learn)

---

## Discrepancies vs specs

Values below are where the newly gathered corpus evidence disagrees with `05-theming.md`
and/or `docs/spec/win2000-explorer-chrome.md`. **The specs are not edited here** — this is a
flag list for a human/spec-owner to reconcile.

- **Selection highlight / ActiveTitle color (`#0A246A` vs `#000080`).** Both spec files use
  `#0A246A` for `Highlight`/`ActiveTitle` (05-theming.md §8.1; win2000-explorer-chrome.md §1),
  and this is what direct pixel sampling of real screenshots confirms
  ([taskbar-start-menu.md](taskbar-start-menu.md): measured `RGB(10,36,106)` for the selected
  Start-menu item; window-chrome-controls.md's own local pixel measurement of a title bar).
  However, two independent *documented-default* sources cited inside the corpus itself list
  `#000080` navy instead: the quppa.net XP-Classic system-color table
  ([window-chrome-controls.md](window-chrome-controls.md) Colors table, "Selection highlight")
  and an MSDN NT/2000 SDK docs excerpt
  ([explorer-file-manager.md](explorer-file-manager.md) Colors table, "ActiveTitle ... #000080
  ... per SDK docs — but sampled from an actual screenshot ... closer to #0A246A"). Both
  domain docs flag this exact tension as an open question. Net: spec's `#0A246A` is the
  measurement-backed value; the `#000080` figure is a documented-but-unconfirmed SDK default
  that the corpus could not reconcile.
- **`GradientInactiveTitle` end color (`#C0C0C0` vs `#B5B5B5`).** 05-theming.md §8.1 lists
  `#C0C0C0`. win2000-explorer-chrome.md §1 itself gives `#B5B5B5` for `InactiveTitleRight`
  (confidence `[~]`), and [window-chrome-controls.md](window-chrome-controls.md)'s Colors
  table independently cites the same `#B5B5B5` (quppa.net) for "Inactive caption gradient
  end." Two of three sources agree on `#B5B5B5`; 05-theming.md is the outlier.
- **`InactiveTitleText` (`#D4D0C8` vs `#C0C0C0`).** 05-theming.md §8.1 lists `#D4D0C8`.
  [window-chrome-controls.md](window-chrome-controls.md)'s Colors table (quppa.net) lists
  `#C0C0C0` for "Inactive caption text." No pixel measurement in the corpus resolves this
  either way.
- **`Menu`/`Scrollbar` face color (`#D4D0C8` vs `#C0C0C0`).** Both spec files use `#D4D0C8`
  for `ButtonFace`/`Menu`/`Scrollbar`, heavily corroborated by direct pixel sampling across
  three domain docs (color-schemes-accessibility.md, explorer-file-manager.md,
  taskbar-start-menu.md all independently measure `#D4D0C8` on chrome bands). But
  [window-chrome-controls.md](window-chrome-controls.md)'s quppa.net-sourced table separately
  lists `Menu face #C0C0C0` and `Scrollbar track base #C0C0C0` — conflicting with the
  measurement consensus the spec already adopted.
- **`3DDkShadow` (`#404040` vs `#000000`).** Both spec files use `#404040`, matching repeated
  pixel measurement of sunken edges/separators across multiple domain docs. But
  [window-chrome-controls.md](window-chrome-controls.md) flags that quppa.net's documented
  default for `COLOR_3DDKSHADOW` is pure black `#000000` — the corpus could not determine
  whether Win2000 genuinely diverged from that documented default or whether `#404040` is an
  application-specific (non-`GetSysColor`) shade that merely looks similar across sampled
  screenshots.
- **Toolbar row height (~26 DIP vs 22px).** win2000-explorer-chrome.md §3 lists the Toolbar
  band at **~26 DIP**, but 05-theming.md §8.4 specifies the `ToolBar` control's default height
  as **22px**. Two independent domain measurements side with the 22px figure: explorer-file-manager.md
  estimates ~22px (own pixel measurement, low confidence) and window-chrome-controls.md
  measures ~22px on Explorer chrome specifically (medium confidence, "sitting directly under a
  1px menu-bar separator"). None of the three domain-doc measurements land on 26.
- **Menu bar height (19 vs ~20–22 measured).** 05-theming.md's canonical `MenuBarHeight` is
  **19** (win2000-explorer-chrome.md §7 explicitly defers to this over its own ~20 estimate).
  Two domain docs independently re-measured the same UI region and got slightly higher values:
  window-chrome-controls.md ~20px ("2 apps agree", medium confidence) and
  explorer-file-manager.md ~22px (own pixel measurement, low confidence). All three domain
  figures are in the same neighborhood as 19 but none matches it exactly — likely
  measurement-crop/scaling noise rather than a real spec error, but worth a second look.
- **Address bar row height (~22 DIP vs ~20px measured).** win2000-explorer-chrome.md §3 lists
  ~22 DIP for the Address bar row; explorer-file-manager.md's independent pixel measurement of
  the same region gives ~20px (low confidence, own estimate). Minor; not yet promoted into
  05-theming.md's core metrics table either way.
- **Title-bar/caption font point size (implicit 8pt vs a low-confidence ~11pt claim).** Both
  spec files render caption text at the same 8pt Tahoma Bold used for general UI text (no
  distinct larger caption size). [typography-metrics-effects.md](typography-metrics-effects.md)
  surfaced a secondary source (an MS Q&A thread) claiming the Appearance tab's "Title Bar" /
  "Palette Title" item defaults to **~11pt** — but the domain doc itself flags this as
  low-confidence and possibly XP/Luna-era rather than genuine Windows 2000 Classic, so treat
  as a lead to verify, not a confirmed contradiction.

---

## Gaps

Merged and deduplicated from all eight docs' "Open questions" sections.

**Access blockers (would likely resolve several items below if lifted):**
- `betaarchive.com`/`betawiki.net` are Cloudflare-gated and were not accessed this session —
  they likely hold Win2000-specific "Windows Classic" article detail, build-by-build icon
  changelogs, and (per betaarchive.com/wiki) dedicated Hebrew (24 files) and Arabic (5 files)
  screenshot galleries for Windows 2000 Build 2195 that would directly settle the RTL open
  questions below.
- `toastytech.com/guis` was unreachable during the explorer-file-manager.md research pass
  specifically (server misconfiguration on two attempts), even though other domain passes
  (taskbar, typography, window-chrome) successfully used it — worth a retry for that domain.
- No archived copy of the primary "Wizard 97" Platform SDK spec (`ms738248`) body, the 1999
  "Microsoft Windows User Experience" book/guidelines, or the "Windows Interface Guidelines
  for Software Design" scanned measurement pages was reachable via web.archive.org this
  session — several exact metrics below (caption button size, scrollbar thickness, Wizard97
  bitmap dimensions, taskbar pixel specs) rest on screenshot measurement or later-era
  secondary sources instead of these primary specs.

**No genuine Windows-2000-native screenshots found for an entire domain:**
- RTL/BiDi: every piece of photographic evidence is Windows XP (Arabic), and the mirrored/
  unmirrored comparison is a .NET 2.0 WinForms rendering, not native Win2000 shell chrome.
  Specific unresolved questions: does the 3-D bevel light source mirror or stay physical under
  RTL; does Explorer's tree/content pane swap sides; exact Start-button pixel position/size
  under RTL; mixed LTR/RTL path text edge cases (Arabic folder names, UNC paths); no Regional
  Options language-enablement screenshot located.
- No genuine screenshot of the Display Properties Appearance tab's per-element Item/Font/
  Size/Color editor itself was found (GUIdebook's catalog entry mislabels the Background tab
  as Appearance) — all Appearance-tab facts are from prose descriptions, not a verified
  screenshot.
- No screenshot of the Sounds and Multimedia Properties dialog (Sound Events tree UI) was
  found; sound-event facts come from a book excerpt and a sound-file archive, not a screenshot.
- No screenshot of a genuine end-user-facing Wizard97 wizard (e.g. Add/Remove Hardware,
  Network Identification) with the watermark exterior page — the one confirmed Wizard97
  interior-page example is from the Setup GUI phase, not a post-install Control Panel wizard.
- No screenshot of Explorer's in-place rename (F2) edit box, drag-and-drop ghost/insertion-line
  visuals, or the rubber-band/marquee multi-select rectangle.

**Single-sourced or low-confidence values needing a second source:**
- Named cosmetic color-scheme hex values (Brick, Desert, Eggplant, etc.) and the High Contrast
  scheme roster are sourced only from a modern hobbyist GitHub reconstruction (WinDaisy), not
  a period screenshot or registry export.
- Secondary/rare color roles (`HotTrackingColor`, `MenuHilight`, `ButtonDkShadow`,
  `GradientActiveTitle`/`InactiveTitle`) for Windows Standard are single-sourced from a
  WebSearch synthesis.
- The "Windows Standard replaced Windows Classic as the default scheme mid-beta, Tahoma
  became default" claim rests on a single BetaWiki search-result excerpt (page itself
  inaccessible).
- Caret blink rate (~1000ms) and double-click speed default are not corroborated by a
  Win2000-specific primary source.
- Win2000-specific overlay-badge pixel footprint/corner placement, and whether the 4-bit
  (16-color) icon variant ever rendered in normal 256-color+ desktop use, are unconfirmed
  (later XP/Vista guide values used as a cautious proxy).
- Specific hex values used inside real Win2000 system icons (folder yellow, Recycle Bin blue)
  were not found in any public design-guideline text.
- Scrollbar thickness (`SM_CXVSCROLL`/`SM_CYHSCROLL`, commonly cited as 16px) and checkbox/
  radio box size (13×13px) are not confirmed against a Win2000-specific primary source, only
  general Windows-classic-control-family knowledge.
- Exact caption-button pixel width/height/inset (~15–16 × ~14px) rests on a single screenshot
  sample; no primary `SM_CXSIZE`/`SM_CYSIZE` default-value table was reachable.

**Behavioral/UI details not yet pinned down:**
- Whether the Appearance tab detaches per-item edits from the named Scheme automatically, or
  only via explicit "Save As" (inferred from button layout, not confirmed procedurally).
- Which dialog classes get the "What's This?" (?) caption-bar help-icon button and which don't.
- Exact stock `folder.htt`/`shellstyle.css` source is unconfirmed; two secondary sources
  disagree on the web-view sidebar width (200px fixed vs. 30%-relative) and it's unclear
  whether the 30%-relative version is a Win98/early-Desktop-Update variant.
- Exact hex of the web-view banner's pastel-square decorative bitmap (visual-only, not
  numerically documented anywhere found).
- Exact Explorer toolbar overflow-chevron pixel dimensions/trigger width.
- Start button's exact fixed width when not auto-sized (min/max), taskbar resize-drag
  snapping increment, and full default Quick Launch icon order (only inferred/spot-checked,
  not pixel-verified against an out-of-box screenshot).
- Whether Win2000's Appearance tab exposes independently enabled per-item Bold/Italic toggles
  for text-bearing items (only seen with a non-text item selected, so toggles appeared grayed).
- Column-resize drag-handle cursor and double-click-to-autofit behavior in Details view, not
  verified against a Win2000-specific source.
