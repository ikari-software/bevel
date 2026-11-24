> Part of the [open-beads master plan](README.md). Read README.md first: it sets the rules, the order, and the rulings that override individual cards.
> Written 2026-11-18 from reading the tree at `51582c0`. Nothing here was built or run. `UNVERIFIED:` marks a claim the researcher could not confirm — check it before relying on it.

## Cluster D: Theming engine, themes and tokens

**Owners (all in `src/Bevel.UI/` unless noted).** `ThemeService` (static; registry `Themes` + `Apply(string?)` — swaps one `StyleInclude`
per theme in `Application.Styles`; `Current` = applied id). `ThemeVariants` (static; `Apply(BevelSettings)` routes to the active theme's
recolour engine and `Clear()`s the others). Three recolour engines that OVERRIDE static tokens at Application level:
`ColorSchemeService` (Industrial1999 schemes, one merged `ResourceInclude`), `Blue2001.Blue2001VariantService` (colour x gloss, ~140
direct `Application.Resources[key]` writes), `FlatVariantService` (4 variants, direct writes). `Glyphs` (code-drawn icons + brush cache),
`GlyphIcon` (the one self-refreshing glyph host). Tokens: `src/Bevel.Themes.Industrial1999/theme.json` -> `tools/ThemeGen` ->
`Tokens.axaml` + `src/Bevel.UI/ThemeTokens.cs`. All live applies happen in ONE place per process: `src/Bevel.App/App.axaml.cs:165-181`
(`settings.Changed` -> UI thread -> `ThemeService.Apply` -> `ThemeVariants.Apply` -> `ThemeOptions.ApplyCrispBevels` -> `FontService.Apply`).

**Name mapping (older beads use vendor names; the rename is finished in code).**
| Bead vocabulary | Today |
|---|---|
| Win2000 / `win2000` / `Bevel.Themes.Win2000` | `src/Bevel.Themes.Industrial1999`, id `ThemeIds.Industrial1999` = `"industrial1999"`, "Bevel 1999 Industrial" |
| Luna / `luna` / `Bevel.Themes.Luna` / `LunaTheme.axaml` / `LunaTokens.axaml` | `src/Bevel.Themes.Blue2001` (`Blue2001Theme.axaml`, `Blue2001Tokens.axaml`), id `"blue2001"`, "Bevel 2001" |
| `LunaVariant.cs` / `LunaVariantService` / `LunaColorMath` / `Luna.Brush.*` | `src/Bevel.UI/Blue2001/Blue2001Variant.cs` (`Blue2001VariantService`), `Blue2001ColorMath.cs`, `Blue2001.Brush.*` |
| `LunaThemeTests` | `tests/Bevel.Taskbar.Tests/Blue2001ThemeTests.cs` |
| Flat (Whistler Watercolor) | `src/Bevel.Themes.Flat/FlatTheme.axaml`, id `"flat"` — the SOURCE-FAITHFUL cobalt Whistler skin + `FlatVariantService` (Blue/Ergonomic/Silver/Amber) |
| "Flat" as described in `docs/design/flat/flat-theme-spec.md` §1-§3 (pastel washes, cream/lilac, dark ink) | `src/Bevel.Themes.Pastel/PastelTheme.axaml`, id `"pastel"`, "Bevel Pastel (preview)". The spec's palette (`#D6EEF7`→`#86C3DA` caption) is Pastel's, not Flat's |
| token-only `flat` stub | `src/Bevel.Themes.Industrial1999/ThemeFlat.axaml` — DEAD (no reference anywhere; its comment names a non-existent `Bevel.Themes.Aquarelle`) |

**The one invalidation contract (decided; card bevel-yfkw establishes it).** Existing signal, kept under its existing name:
`Bevel.UI.Glyphs.ThemeCacheInvalidated` (`public static event Action?`, raised by `Glyphs.InvalidateThemeCache()`). Rule: XAML uses
`{DynamicResource}`; anything code-drawn or rasterized that cannot bind subscribes to this ONE event on attach, unsubscribes on detach, and
rebuilds. No second event, no rename (`GlyphIcon` + `AddressBarFolderGlyphTests` pin the name). Three defects make it unreliable today:
(1) `FlatVariantService.Apply/Clear`, `ColorSchemeService.Clear`, `Blue2001VariantService.Clear` never raise it; (2)
`ColorSchemeService.Apply:81` and `Blue2001VariantService.Apply:200` raise it BEFORE writing resources, so a synchronous subscriber
(`GlyphIcon`) rebuilds from the OLD palette and re-poisons the cache; (3) a second private signal exists (`Blue2001VariantService.Changed`,
only subscriber `InfoPane.axaml.cs:51`). Target: every engine mutation calls `InvalidateThemeCache()`; the raise is coalesced to one
posted notification per UI turn, after all engines finished.

**Epic order by dependency:** contract (yfkw→5rel, yf7c) → neutral key contract + generated tokens (lqot, ayhn, quif) = remainder of
engine epic **dob** → Blue2001 finish (8lb, 7cq, ekcv) → Flat/Pastel finish (rqcp, q455, **ii93**) → derivation + dark (q91m, 50hs, yd2n,
**fdo5**) → packages (tknt → te20) → lineage (**lkw1**, last: needs neutral keys, generated tokens, and ideally the package format).

## Order

1. bevel-yfkw — establishes the single invalidation contract; three other P1/P2 bugs (5rel, yf7c, and bevel-lej1's visible damage) hang off it.
2. bevel-5rel — P1; live rows keep the old selection brush; fix is pure DynamicResource, small once #1 lands.
3. bevel-yf7c — P2; Filer toolbar bitmaps + InfoPane's private signal move onto the contract.
4. bevel-i9iu — already fixed (391c673); close to shrink the list.
5. bevel-rqcp — P1 in_progress but the deliverables are on disk; verify, fix one stale doc, close.
6. bevel-ayhn — the CI token drift gate points at a deleted path and is silently inert; cheap, protects everything below.
7. bevel-lqot — neutral `Bevel.Brush.*` contract; every new theme and the package format need it.
8. bevel-quif — Blue2001 hexes that ignore the colourway; visible under Silver/Black/Purple today.
9. bevel-dob — epic; engine landed, lists what remains.
10. bevel-8lb — done in code; close.
11. bevel-7cq — mostly done; two real leftovers.
12. bevel-q455 — done in code under two theme ids; close with the mapping.
13. bevel-ii93 — epic over the Flat/Pastel work; exit criteria only.
14. bevel-safz — one-line dead class removal.
15. bevel-zvo0 — enum rename; persisted by name, so needs a tolerant parse.
16. bevel-50hs — tokenise stragglers; the easy half now, InfoPane half with fdo5.
17. bevel-yd2n — profile-gated; do the measurement, likely fold into fdo5.
18. bevel-q91m — first slice of the derivation engine.
19. bevel-fdo5 — epic: derivation → dark mode.
20. bevel-tknt — `.beveltheme` packages; needs lqot + ayhn + a product decision on runtime XAML.
21. bevel-te20 — git install; blocked on tknt and on counsel.
22. bevel-lkw1 — epic: four more themes; last by dependency.
23. bevel-ti3 — M5 golden coverage for Industrial1999; large, steady.
24. bevel-ztdt — Theme Studio save; the tool is currently broken by the id rename, fix that first.
25. bevel-ekcv — cosmetic menu accelerators.
26. bevel-assets — human-driven asset production.
27. bevel-gye — RTL startup; needs a human fidelity decision.
28. bevel-zr4 — P4 research, human.

## Cards

### bevel-yfkw — Glyphs.Icon() direct consumers never rebuild on live theme switch
- **Meta:** P1 · bug · size M · AGENT
- **State today:** Signal exists: `Glyphs.ThemeCacheInvalidated` (`src/Bevel.UI/Glyphs.cs:56`), raised from `ThemeService.cs:142`, `ColorSchemeService.cs:81`, `Blue2001/Blue2001Variant.cs:200`; only subscriber `GlyphIcon.cs:52`. Direct `Glyphs.Icon(...)` callers that never rebuild: `src/Bevel.FileManager/Components/ItemView.axaml.cs:316,325,334,353,378`, `src/Bevel.Taskbar/StartMenu.axaml.cs:520`, `src/Bevel.FileManager/Components/PropertiesDialog.axaml.cs:169,214`. Contract defects 1-2 in the cluster summary verified by reading (not by running).
- **Depends on:** none
- **Files:** `src/Bevel.UI/Glyphs.cs`, `src/Bevel.UI/GlyphIcon.cs`, `src/Bevel.UI/ColorSchemeService.cs`, `src/Bevel.UI/FlatVariantService.cs`, `src/Bevel.UI/Blue2001/Blue2001Variant.cs`, the three consumer files above, `tests/Bevel.Taskbar.Tests/ThemeInvalidationContractTests.cs` (new), `tests/Bevel.FileManager.Tests/ItemViewTests.cs`
- **Decision:** (a) Keep the event name. `Glyphs.InvalidateThemeCache()` clears `_brushCache` immediately, increments `public static int ThemeGeneration` (new), and posts ONE coalesced raise via `Dispatcher.UIThread.Post` (guard `_raisePending` (new)); the posted callback clears the cache again, then invokes the event. Reason: engines run back-to-back in one handler; one raise after the last write is the only order that is always correct. (b) Every engine mutation calls it: add to `FlatVariantService.Apply` (both exits), `FlatVariantService.Clear`, `ColorSchemeService.Clear`, `Blue2001VariantService.Clear`. Not `FontService`/`ThemeOptions` (no colour change). (c) All hosts use `GlyphIcon`; add `public static GlyphIcon For(IconKey key, int size)` (new) = `new GlyphIcon { Glyph = key.SemanticId ?? "doc.generic", Size = size }`. (d) `GlyphIcon` builds only while attached and only when `Child is null` or its stored generation != `Glyphs.ThemeGeneration` (today it rebuilds 3x per row: two property sets + attach).
- **Test first:** `ThemeInvalidationContractTests.Every_engine_mutation_raises_once_after_resources_are_written` in `[Collection("TaskbarTheme")]`: subscribe a handler that records `Application.Current.Resources["Bevel.Color.Highlight"]`; call `Blue2001VariantService.Apply("Silver","Gloss")`, `RunJobs()`, assert raised exactly once and the recorded value equals the post-apply value; repeat for `Blue2001VariantService.Clear()`, `FlatVariantService.Apply(new BevelSettings{FlatVariant="Amber"})`, `FlatVariantService.Clear()`, `ColorSchemeService.Apply("Brick")`, `ColorSchemeService.Clear()`. Plus `ItemViewTests.Row_icons_are_GlyphIcon_hosts_and_rebuild_on_invalidation` (pattern: `AddressBarFolderGlyphTests.Folder_icon_re_resolves_when_a_recolour_engine_drops_the_cached_brushes`).
- **Steps:**
  - [ ] Write both tests; confirm they fail.
  - [ ] Change `Glyphs.InvalidateThemeCache` per (a); add the four missing calls per (b).
  - [ ] Add `GlyphIcon.For` and the generation check per (c)/(d).
  - [ ] Replace the 5 ItemView sites with `GlyphIcon.For(vm.IconKey, n)`; StartMenu `SetItemIcon` with `new GlyphIcon { Glyph = key, Size = 16 }`; both PropertiesDialog sites with `GlyphIcon.For(...)`.
  - [ ] `grep -rn "Glyphs\.Icon(" src` → only `GlyphIcon.cs` and `Glyphs.cs` remain (`GlyphPreview` in `src/Bevel.IconPreview`, `tools/BevelShot` is one-shot tooling; leave).
  - [ ] Update the XML doc on `ThemeCacheInvalidated` to state it is the app-wide palette-changed signal and is posted.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~ThemeInvalidationContract"` and `dotnet test tests/Bevel.FileManager.Tests/Bevel.FileManager.Tests.csproj --filter "FullyQualifiedName~ItemViewTests|FullyQualifiedName~AddressBarFolderGlyph"` → all pass
- **Done when:** switching theme/scheme/colourway with a Filer window and the Start menu open recolours every icon without re-listing; then `bd close bevel-yfkw --reason "single posted invalidation contract; all glyph hosts are GlyphIcon"`

### bevel-5rel — ItemView static selection brushes resolved once at type-init, never invalidated
- **Meta:** P1 · bug · size S · AGENT
- **State today:** PARTLY DONE — ab58f0b turned the `static readonly` fields into properties (`ItemView.axaml.cs:271-273`), so the process-lifetime freeze is gone. Remaining: `SelBgConv`/`SelFgConv` (`:274-277`) run only when `IsSelected` flips, so every realized row keeps the previous theme's brush objects (selected bar AND unselected text) until reselected or recycled. Same pattern, never tokenised: `src/Bevel.FileManager/Components/TabStrip.axaml.cs:32-33` (`static readonly` White / `#D4D0C8`), `:87` `Brushes.Gray`, applied at `:149`.
- **Depends on:** none (pure DynamicResource; does not need the event)
- **Files:** `src/Bevel.FileManager/Components/ItemView.axaml.cs`, `src/Bevel.FileManager/Components/ItemView.axaml`, `src/Bevel.FileManager/Components/TabStrip.axaml.cs`, `tests/Bevel.FileManager.Tests/ItemViewTests.cs`
- **Decision:** No brush objects in code. In `NameCell`: `highlight.Classes.Add("namecell")` and `highlight.BindClass("selected", new Binding(nameof(ItemViewModel.IsSelected)), null)` (Avalonia 11.3.18 `StyledElementExtensions.BindClass`). In `ItemView.axaml` add `<UserControl.Styles>`: `Border.namecell > TextBlock` Foreground `{DynamicResource Bevel.Brush.WindowText}`; `Border.namecell.selected` Background `{DynamicResource Bevel.Brush.Highlight}`; `Border.namecell.selected > TextBlock` Foreground `{DynamicResource Bevel.Brush.HighlightText}`. Delete `ThemeBrush`, `SelectionFill`, `SelectionText`, `NormalText`, `SelBgConv`, `SelFgConv`. Reason: zero per-row event subscriptions in a virtualized list. TabStrip: replace the two statics with `entry.Container[!Border.BackgroundProperty] = new DynamicResourceExtension(isActive ? ThemeTokens.BrushWindow : ThemeTokens.BrushButtonFace)` and `:87` with `ThemeTokens.BrushButtonShadow`.
- **Test first:** `ItemViewTests.Selection_colours_follow_a_live_token_change_without_reselecting`: show an ItemView with 2 items, select one, `RunJobs()`; set `Application.Current.Resources[ThemeTokens.BrushHighlight] = Brushes.Red` (try/finally remove); `RunJobs()`; assert the selected row's `Border` with class `namecell` has `Background == Brushes.Red` with no selection change.
- **Steps:**
  - [x] Write the test; confirm it fails on the stale brush.
  - [x] Apply the ItemView change; check the rename path (`ItemView.axaml.cs` near `:994`) does not read the deleted members.
  - [x] Apply the TabStrip change.
  - [x] `grep -n "static readonly IBrush\|ThemeBrush(" src/Bevel.FileManager/Components/*.cs` → no hits.
- **Verify:** `dotnet test tests/Bevel.FileManager.Tests/Bevel.FileManager.Tests.csproj --filter "FullyQualifiedName~ItemViewTests|FullyQualifiedName~ClickSelection|FullyQualifiedName~MarqueeSelection|FullyQualifiedName~TabbedBrowsing"` → pass
- **Done when:** a live theme switch recolours selected and unselected rows in place; then `bd close bevel-5rel --reason "selection + tab brushes are DynamicResource styles"`

### bevel-yf7c — Filer Toolbar.ApplyIcons() rasterized once in the ctor, never re-applied on live theme switch
- **Meta:** P2 · bug · size S · AGENT
- **State today:** Unchanged. `src/Bevel.FileManager/Components/Toolbar.axaml.cs:8-12` calls `ApplyIcons()` once; `ToolbarIcons.Raster` (`ToolbarIcons.cs:113`) bakes a `RenderTargetBitmap` from tokens resolved at that instant. Second instance of "private signal": `InfoPane.axaml.cs:51-52` listens to `Blue2001VariantService.Changed`, so its XP palette copy (`CopyApplicationBrush`, `:153`) never refreshes on a THEME switch or a Flat/scheme change.
- **Depends on:** bevel-yfkw
- **Files:** `src/Bevel.FileManager/Components/Toolbar.axaml.cs`, `src/Bevel.FileManager/Components/InfoPane.axaml.cs`, `src/Bevel.UI/Blue2001/Blue2001Variant.cs`, `tests/Bevel.FileManager.Tests/ToolbarThemeTests.cs` (new)
- **Decision:** Toolbar overrides `OnAttachedToVisualTree`/`OnDetachedFromVisualTree` to add/remove `Glyphs.ThemeCacheInvalidated += ApplyIcons` and calls `ApplyIcons()` on attach (not in the ctor). `Set` disposes the previous `SmallIcon` bitmap after assigning the new one. NOT routed through `FileManagerWindow.ApplyFolderOptions` (the bead's suggestion): that fan-out only runs on settings writes in the Filer role and would make a second invalidation path. InfoPane switches its two subscriptions to `Glyphs.ThemeCacheInvalidated`; then delete `Blue2001VariantService.Changed` (`Blue2001Variant.cs:56` and its two `Changed?.Invoke()` calls).
- **Test first:** `ToolbarThemeTests.Toolbar_icons_are_re_rasterized_when_the_theme_cache_is_invalidated`: show a `Toolbar` in a `Window`, `RunJobs()`, keep `((Classic.CommonControls.ToolBarButton)toolbar.Views).SmallIcon`; call `Glyphs.InvalidateThemeCache()`; `RunJobs()`; `Assert.NotSame(before, after)`; also assert `AutomationProperties.GetName(toolbar.BackButton) == "Back"` still holds.
- **Steps:**
  - [x] Write the test; confirm it fails.
  - [x] Move `ApplyIcons()` to attach + subscribe/unsubscribe; dispose replaced bitmaps.
  - [x] Re-point InfoPane; delete `Blue2001VariantService.Changed`; `grep -rn "VariantService.Changed" src tests tools` → no hits.
  - [x] Run `InfoPaneStyleTest` (it renders the XP pane under each colourway).
- **Verify:** `dotnet test tests/Bevel.FileManager.Tests/Bevel.FileManager.Tests.csproj --filter "FullyQualifiedName~ToolbarTheme|FullyQualifiedName~InfoPaneStyle|FullyQualifiedName~AccessibilityNaming"` → pass
- **Done when:** an open Filer window's toolbar and XP info pane recolour on a live switch; then `bd close bevel-yf7c --reason "Toolbar + InfoPane on Glyphs.ThemeCacheInvalidated; private Changed event removed"`

### bevel-i9iu — AddressBar's folder icon uses hardcoded hex, not theme tokens
- **Meta:** P3 · task · size S · AGENT
- **State today:** LIKELY DONE — commit 391c673 "fix(ui): the address bar's folder icon follows the theme tokens (bevel-i9iu)" (merged in 1d33f2a). `src/Bevel.FileManager/Components/AddressBar.axaml:46` is `<ui:GlyphIcon Glyph="folder" Size="16"`; the `#F0B84A/#FFD064/#9C6B15` literals are gone. Guarded by `tests/Bevel.FileManager.Tests/AddressBarFolderGlyphTests.cs` (2 tests). The one hex left in that file (`:53`, `#404040` drop arrow) belongs to bevel-50hs.
- **Depends on:** none
- **Files:** none
- **Decision:** Close; nothing to build.
- **Test first:** existing `AddressBarFolderGlyphTests`.
- **Steps:**
  - [ ] `grep -n "F0B84A\|FFD064\|9C6B15" src/Bevel.FileManager/Components/AddressBar.axaml` → no output.
  - [ ] Run the Verify command.
- **Verify:** `dotnet test tests/Bevel.FileManager.Tests/Bevel.FileManager.Tests.csproj --filter "FullyQualifiedName~AddressBarFolderGlyph"` → 2 passed
- **Done when:** both checks pass; then `bd close bevel-i9iu --reason "fixed in 391c673: AddressBar hosts GlyphIcon"`

### bevel-rqcp — Author Flat watercolor SVG spec and align runtime
- **Meta:** P1 · feature · size S · AGENT+HUMAN-VERIFY
- **State today:** LIKELY DONE — its blocker bevel-h0hg.9 is closed. Deliverables exist: `docs/design/flat/flat-watercolor-overall.svg`, `flat-watercolor-details.svg`, `flat-watercolor-demo.svg`, `look-delta.md` (measured delta table), `runtime-review.md` ("Status: PASSED"), `renders/*.png`; runtime aligned in 46b8e2d + b382c66 (`FlatTheme.axaml:72` cobalt 3-stop caption, `:223` places column `#D9E3F3`, `:205` place text `#1A202C`). One stale doc: `runtime-review.md` "Start Menu Places Column" row still says saturated blue `#3570D6→#2862C8` with white text — contradicts the code and `look-delta.md` row 10.
- **Depends on:** none (bevel-h0hg.9 closed)
- **Files:** `docs/design/flat/runtime-review.md`
- **Decision:** Treat `look-delta.md` + the code as authoritative; correct the review table rather than the runtime. The owner signs off the look (a visual judgment).
- **Test first:** `RenderFlatAndPastelComparisonTest.Render_flat_and_pastel_comparisons_to_png` regenerates `docs/design/flat/renders/` with no diff.
- **Steps:**
  - [ ] Run the Verify command; `git status docs/design/flat/renders` → clean.
  - [ ] Edit the "Start Menu Places Column" row and the "Variation Set Verification" hexes in `runtime-review.md` to the values in `FlatTheme.axaml` / `FlatVariantService.cs:36-39`.
  - [ ] HUMAN: open `docs/design/flat/gallery.html` and `before-after.html`, confirm the look.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~RenderFlatAndPastelComparison"` → pass, no changed PNGs
- **Done when:** owner accepts the renders; then `bd close bevel-rqcp --reason "SVG targets + look-delta + aligned runtime landed (46b8e2d, b382c66); review doc corrected"`

### bevel-ayhn — Luna tokens are hand-mirrored and not drift-checked (ThemeGen only covers Win2000)
- **Meta:** P3 · task · size M · AGENT
- **State today:** True, and worse: `tools/ThemeGen/Program.cs:13-15` defaults to Industrial1999 only; `src/Bevel.Themes.Blue2001/theme.json` `_comment` admits `Blue2001Tokens.axaml` is hand-mirrored. The CI gate `.github/workflows/ci.yml:187` diffs `src/Bevel.Themes.Win2000/Tokens.axaml` — a path that no longer exists, so `git diff --exit-code` passes vacuously: Industrial1999's `Tokens.axaml` is NOT drift-checked either (only `ThemeTokens.cs` and the scheme dictionaries are). Flat and Pastel have no `theme.json` at all.
- **Depends on:** none
- **Files:** `.github/workflows/ci.yml`, `tools/ThemeGen/Program.cs`, `src/Bevel.Themes.Blue2001/theme.json`, `src/Bevel.Themes.Blue2001/Blue2001Tokens.axaml`
- **Decision:** Two steps, gate first. (1) Fix the CI path to `src/Bevel.Themes.Industrial1999/Tokens.axaml`. (2) Add an override mode to ThemeGen: when the JSON has `"inherits"`, emit only the keys present (palette → `Bevel.Color.*`/`Bevel.Brush.*`, metrics → `Bevel.Metric.*`) into a generated `Blue2001Tokens.g.axaml` (new) that `Blue2001Tokens.axaml` merges; the hand-written file keeps only what theme.json cannot express (the `SystemColors.*` caption keys, gradients). Do NOT regenerate `ThemeTokens.cs` from an inheriting theme. Flat/Pastel get a `theme.json` later under bevel-tknt, not here.
- **Test first:** CI job `theme-drift` fails when `Tokens.axaml` is hand-edited (prove locally: edit one hex in `src/Bevel.Themes.Industrial1999/Tokens.axaml`, run `dotnet run --project tools/ThemeGen`, `git diff --exit-code -- src/Bevel.Themes.Industrial1999/Tokens.axaml` → clean again).
- **Steps:**
  - [x] Fix the path at `ci.yml:187`; commit separately.
  - [x] Add the inherits/override emit path to `Program.cs`; default run generates Industrial1999 AND Blue2001.
  - [x] Move the duplicated plain keys out of `Blue2001Tokens.axaml` into the generated file; diff rendered output via `RenderBlue2001ControlsTest` / `RenderBlue2001WindowTest` (no PNG change).
  - [x] Add `src/Bevel.Themes.Blue2001/Blue2001Tokens.g.axaml` to the CI diff list; update the `theme.json` `_comment`.
- **Verify:** `dotnet run --project tools/ThemeGen && git diff --exit-code -- src/Bevel.Themes.Industrial1999 src/Bevel.Themes.Blue2001 src/Bevel.UI/ThemeTokens.cs` → exit 0
- **Done when:** editing either theme.json without regenerating fails CI; then `bd close bevel-ayhn --reason "ThemeGen covers Blue2001; CI gate path fixed"`

### bevel-lqot — Promote Luna.Brush.StartMenu*/TaskButton*/CaptionButton* to Bevel.Brush.*
- **Meta:** P2 · task · size M · AGENT
- **State today:** Not done; the keys are now `Blue2001.Brush.*` and three OTHER themes define them to feed shared chrome. Shared surfaces reference theme-scoped keys: `src/Bevel.Taskbar/StartMenu.axaml` + `.axaml.cs` (27 `Bevel.Brush.StartMenu*` keys, plus `Bevel.Metric.StartMenu*` and `Bevel.Brush.StartMenuFooterButton*`), `src/Bevel.FileManager/Components/ItemView.axaml:13-14` (`Bevel.Brush.ColumnHeader`, `ColumnSeparator`, `ColumnHeaderLight` — defined in Industrial1999Theme.axaml:98, FlatTheme.axaml:49, PastelTheme.axaml:56), `InfoPane.axaml.cs` (`Bevel.Brush.InfoPane*`). Writers: `Blue2001Variant.cs` (Surfaces table), `FlatVariantService.cs:58-61` (writes `Bevel.Brush.StartMenu*` from the Flat engine).
- **Depends on:** none (do before bevel-quif, bevel-q91m, bevel-tknt)
- **Files:** `src/Bevel.Themes.{Blue2001,Flat,Pastel,Industrial1999}/*Theme.axaml`, `src/Bevel.Taskbar/StartMenu.axaml(.cs)`, `src/Bevel.FileManager/Components/ItemView.axaml`, `src/Bevel.FileManager/Components/InfoPane.axaml.cs`, `src/Bevel.UI/Blue2001/Blue2001Variant.cs`, `src/Bevel.UI/FlatVariantService.cs`, `tests/Bevel.UI.Tests/Blue2001GlossRampTests.cs`, `tests/Bevel.Taskbar.Tests/RenderBlue2001VariantsTest.cs`, `tests/Bevel.UI.Tests/ThemeVariantRegistryTests.cs`
- **Decision:** Rule: a key referenced from outside its own theme project is contract and is named `Bevel.*`. Mechanical rename, no value changes: `Bevel.Brush.StartMenu*` → `Bevel.Brush.StartMenu*`; `Bevel.Brush.TaskButton*` → `Bevel.Brush.TaskButton*`; `Bevel.Brush.CaptionButton*` → `Bevel.Brush.CaptionButton*`; `Bevel.Brush.InfoPane*` → `Bevel.Brush.InfoPane*`; `Bevel.Brush.ColumnHeader*`/`ColumnSeparator` → `Bevel.Brush.ColumnHeader*`/`ColumnSeparator`; `Bevel.Metric.StartMenu*` → `Bevel.Metric.StartMenu*`; `Bevel.Brush.StartMenuFooterButton*` → `Bevel.Brush.StartMenuFooterButton*`. Keys used only inside one theme's own axaml keep their prefix. Hand-written keys stay out of `theme.json`/`ThemeTokens.cs` for now (gradients are not expressible there).
- **Test first:** add `ThemeVariantRegistryTests.No_shared_surface_references_a_theme_scoped_key`: scan `*.axaml`/`*.cs` under `src/Bevel.Taskbar`, `src/Bevel.FileManager`, `src/Bevel.Desktop` for `(Blue2001|Flat|Pastel)\.(Brush|Metric|Color)\.` → zero matches (model it on the existing `No_axaml_references_a_vendor_named_resource_key`).
- **Steps:**
  - [x] Write the test; confirm it lists today's offenders.
  - [x] Rename one family at a time with `grep -rl ... | xargs sed -i ''` across `src tests tools`; build between families.
  - [x] Run `Every_theme_scoped_DynamicResource_key_is_defined_somewhere` after each family.
  - [x] Confirm the render tests produce no PNG diff (`git status site/shots docs/design/flat/renders`).
- **Verify:** `dotnet test tests/Bevel.UI.Tests/Bevel.UI.Tests.csproj --filter "FullyQualifiedName~ThemeVariantRegistry"` and `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~Render"` → pass, no changed shots
- **Done when:** the new test passes and renders are byte-identical; then `bd close bevel-lqot --reason "shared-surface keys renamed to Bevel.*; guard test added"`

### bevel-quif — Luna ControlThemes have hardcoded hex that ignores the colour variant
- **Meta:** P3 · bug · size S · AGENT
- **State today:** Still true, at new locations in `src/Bevel.Themes.Blue2001/Blue2001Theme.axaml`: ProgressBar `:480` `#F4F2E9` / `:481` `#8A887A`; GroupBox (`Bevel.Theme.HeaderedContentControl`) `:576` `#D0CDBF`; TabItem `:608` `#D7D1BC` + selected gradient ending `#ECE9D8` (`:628`). Static `Bevel.Brush.TaskButtonChecked` (`:119`) vs the runtime-regenerated lit ramp (`Blue2001Variant.cs:127`). UNVERIFIED: the bead's inactive-caption `#E8EEF7` claim — that literal no longer appears in `Blue2001Variant.cs`; check `Blue2001Tokens.axaml` before acting on it.
- **Depends on:** bevel-lqot (key names), bevel-yfkw (new surfaces must be raised)
- **Files:** `src/Bevel.Themes.Blue2001/Blue2001Theme.axaml`, `src/Bevel.UI/Blue2001/Blue2001Variant.cs`, `tests/Bevel.Taskbar.Tests/RenderBlue2001VariantsTest.cs`
- **Decision:** Add theme-local brushes `Blue2001.Brush.ProgressWell`, `ProgressWellBorder`, `GroupFrame`, `TabFace`, `TabFaceSelected` (new) in the theme's resources with today's hexes, bind the five sites with `{DynamicResource}`, and add each to `Blue2001VariantService.Surfaces` as `Surface.Solid(key, true, hex)` so the colourway re-hues them. Make the static `TaskButtonChecked` stops equal the Blue+Hybrid output of the engine (the engine is the source of truth, per commit 04299e3) so the unthemed fallback and the runtime agree.
- **Test first:** `RenderBlue2001VariantsTest` — add an assertion that a ProgressBar well pixel and a GroupBox frame pixel differ between `Blue` and `Silver`.
- **Steps:**
  - [x] Add the assertion; confirm it fails (same beige in both).
  - [x] Introduce the five brushes + bindings; add to `Surfaces`.
  - [x] Reconcile the static `TaskButtonChecked` fallback; `RenderPressedTaskButtonTest` must still pass.
  - [x] Re-harvest `site/shots` only if the Blue default changed (it should not).
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~RenderBlue2001|FullyQualifiedName~RenderPressedTaskButton|FullyQualifiedName~Blue2001PurpleBandContrast"` → pass
- **Done when:** progress/group/tab chrome follows Silver/Black/Purple; then `bd close bevel-quif --reason "remaining Blue2001 hexes routed through variant surfaces"`

### bevel-dob — Theme engine: per-theme control-template sets (unlocks XP/Luna/Aero chrome)
- **Meta:** P2 · epic · size L · AGENT
- **State today:** PARTLY DONE — item (1) landed: `ThemeService.StylesFor` + `_appliedStyles` swap a per-theme `Styles` set with rollback (`ThemeService.cs:66-134`); four themes register. Item (3) superseded: chrome is vector `ControlTheme`s, never bitmap (spec `docs/spec/05-theming.md` LUN-01 "authored raster 9-slice" is STALE and contradicts the vector-only rule). Item (4): shipped names are already vendor-neutral (`ThemeService.Themes`). Remaining: ENG-01 (Classic templates onto `Bevel.Metric.*`), neutral key contract, generated tokens, registry as data.
- **Depends on:** none
- **Children, in order:** bevel-yfkw → bevel-5rel → bevel-yf7c (live switch is trustworthy) → bevel-ayhn → bevel-lqot → bevel-quif → bevel-7cq → bevel-ekcv. bevel-8lb closes as done.
- **New beads to create:**
  - "ENG-01: route remaining vendored Classic templates onto Bevel.Metric.* tokens" — audit `third_party/classic-avalonia/Classic.Avalonia.Theme/Styles/*.axaml` for literal sizes; one PR per control family.
  - "ThemeService registry as data: `ThemeDescriptor` record replaces the `Themes` tuple array + `StylesFor` switch" — prerequisite of bevel-tknt; fixes the `"blue2001"` string literal at `ThemeService.cs:68` (should be `ThemeIds.Blue2001`).
  - "Delete dead `src/Bevel.Themes.Industrial1999/ThemeFlat.axaml` and `ThemeService.SourceFor`" — `SourceFor` returns null for every id; the stub is unreferenced.
  - "Theme switch-away live revert (bevel-p3va follow-up)" only if `ThemeSwitchCaptionTest` still shows residue after bevel-yfkw.
- **Exit criterion:** every `Bevel.Theme.*` contract key is supplied by all four themes (`ThemeEngineTests.Flat_and_pastel_supply_every_builtin_control_theme` extended to Blue2001), no shared surface references a theme-scoped key, all theme tokens are generated and drift-gated, and a round trip through all four themes leaves no stale colour (bevel-yfkw test). Then `bd close bevel-dob`.

### bevel-8lb — Luna theme: port vector style guide into Bevel.Themes.Luna ControlThemes
- **Meta:** P2 · task · size S · AGENT
- **State today:** LIKELY DONE — the project is `src/Bevel.Themes.Blue2001` (theme.json + `Blue2001Tokens.axaml` + 1301-line `Blue2001Theme.axaml`), registered at `ThemeService.cs:68`. It defines Button, CheckBox, RadioButton, ProgressBar, Slider, HeaderedContentControl, TabItem, TabControl, ToggleButton, StartButton, ComboBox, ScrollBar, TextBox, Menu, MenuItem, ContextMenu, ToolTip, CaptionButtons. Verified by headless renders: `tests/Bevel.Taskbar.Tests/Blue2001ThemeTests.cs`, `RenderBlue2001ControlsTest.cs`, `RenderBlue2001WindowTest.cs`, `RenderBlue2001MenuTest.cs`, `RenderBlue2001StartMenuTest.cs`. Source guide still at `docs/design/luna/styleguide.html`.
- **Depends on:** none
- **Files:** none
- **Decision:** Close as delivered; leftovers are tracked in bevel-7cq / bevel-quif / bevel-ekcv.
- **Test first:** the existing render tests above.
- **Steps:**
  - [ ] Run the Verify command.
  - [ ] `grep -c 'ControlTheme x:Key' src/Bevel.Themes.Blue2001/Blue2001Theme.axaml` → 27.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~Blue2001"` → pass
- **Done when:** tests pass; then `bd close bevel-8lb --reason "delivered as Bevel.Themes.Blue2001; leftovers in bevel-7cq/quif/ekcv"`

### bevel-7cq — Luna theme: remaining ControlThemes (caption, scrollbar, progress, tabs, checkbox/radio, menu, taskbar, start menu)
- **Meta:** P2 · task · size M · AGENT
- **State today:** PARTLY DONE — every listed ControlTheme exists in `Blue2001Theme.axaml` (caption `:146`+`Blue2001.Theme.CaptionButton`, scrollbar, ProgressBar `:479`, TabItem `:601`/TabControl, CheckBox/RadioButton, Menu/MenuItem `:1125`, ToggleButton task pills `:787`, StartButton; two-column Start menu in `src/Bevel.Taskbar/StartMenu.axaml` `Blue2001Layout`). Remaining: (a) ThemeGen wiring = bevel-ayhn; (b) Blue2001 does not supply `{x:Type commonControls:StatusBar}`/`StatusBarItem` or `AutoAttachTitleBar` themes that Flat does — Filer's status bar renders with Classic chrome under Blue2001.
- **Depends on:** bevel-ayhn (covers the ThemeGen sentence), bevel-lqot
- **Files:** `src/Bevel.Themes.Blue2001/Blue2001Theme.axaml`, `tests/Bevel.Taskbar.Tests/ThemeEngineTests.cs`, `tests/Bevel.Taskbar.Tests/RenderBlue2001WindowTest.cs`
- **Decision:** Scope this bead down to (b): add a `StatusBar` + `StatusBarItem` ControlTheme to Blue2001 (flat `Bevel.Brush.ButtonFace` band, 1px top rule `Blue2001.Brush.ButtonBorder`, no sunken wells — the XP look). Model on `FlatTheme.axaml`'s `{x:Type commonControls:StatusBar}` theme. Do not add an `AutoAttachTitleBar` theme (Blue2001 already themes the caption through `CaptionButtons` + tokens).
- **Test first:** extend `ThemeEngineTests.Flat_and_pastel_supply_every_builtin_control_theme` with a Blue2001 case asserting the same `Bevel.Theme.*` key list resolves; add a status-bar crop assertion to `RenderBlue2001WindowTest` (no `ButtonShadow`-coloured sunken edge inside the status band).
- **Steps:**
  - [ ] Add the assertions; confirm the status-bar one fails.
  - [ ] Add the two ControlThemes.
  - [ ] Re-harvest any `site/shots` PNG the change touches (`git status site/shots`), per the landing-page rule in CLAUDE.md.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~ThemeEngineTests|FullyQualifiedName~RenderBlue2001Window"` → pass
- **Done when:** Filer under Blue2001 has a themed status bar; then `bd close bevel-7cq --reason "all listed ControlThemes present; status bar added; ThemeGen in bevel-ayhn"`

### bevel-q455 — Implement the Flat (Whistler watercolor) theme from the design spec
- **Meta:** P2 · task · size S · AGENT+HUMAN-VERIFY
- **State today:** LIKELY DONE, split across two ids — ab58f0b + 46b8e2d. `ThemeService.StylesFor` registers `flat` → `src/Bevel.Themes.Flat/FlatTheme.axaml` and `pastel` → `src/Bevel.Themes.Pastel/PastelTheme.axaml` (`ThemeService.cs:69-70`); both supply the full `Bevel.Theme.*` set (`ThemeEngineTests.Flat_and_pastel_supply_every_builtin_control_theme`); the Start menu two-column switch is no longer `== luna` (`StartMenu.axaml.cs:156` covers Blue2001, Flat, Pastel). The look the bead describes (pastel washes, cream/lilac, dark ink) is **Pastel**; "Flat" became the source-faithful cobalt skin. Not done from the bead: no `FlatTokens.axaml`, no "Flat" column in theme.json (tokens live inline in `FlatTheme.axaml`), and the Start-menu key promotion (= bevel-lqot).
- **Depends on:** bevel-lqot (the one unfinished clause)
- **Files:** `docs/design/flat/flat-theme-spec.md` (status header only)
- **Decision:** Close against the acceptance criterion ("coherent, distinct watercolor look … not a Win2000 clone"), which both skins meet. Do not create `FlatTokens.axaml` by hand: token files come from ThemeGen (bevel-ayhn/bevel-tknt). Update the spec's status block, which still calls `flat` "a token-only stub" and points at `Industrial1999/ThemeFlat.axaml`.
- **Test first:** `ThemeEngineTests` + `RenderFlatAndPastelComparisonTest` pass.
- **Steps:**
  - [x] Run the Verify command.
  - [x] Rewrite the 6-line status block at the top of `flat-theme-spec.md`: implemented; palette §2-§3 = `pastel`; `flat` = source-led Whistler per `look-delta.md`.
  - [x] HUMAN: confirm "Pastel (preview)" is the intended home of the spec's look.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~ThemeEngineTests|FullyQualifiedName~RenderFlatAndPastel"` → pass
- **Done when:** owner confirms; then `bd close bevel-q455 --reason "implemented as flat + pastel Styles-set themes (ab58f0b, 46b8e2d); key promotion tracked in bevel-lqot"`

### bevel-ii93 — EPIC: Watercolor Flat theme (Whistler Watercolor)
- **Meta:** P2 · epic · size M · AGENT+HUMAN-VERIFY
- **State today:** PARTLY DONE — tokens, wash brushes, ControlTheme set and tests exist for both `flat` and `pastel` (see bevel-q455, bevel-rqcp). Not done: the "icon-palette override" (neither `FlatTheme.axaml` nor `PastelTheme.axaml` overrides any `Bevel.Color.Icon*` token, so file icons keep the Industrial1999 palette), and the engine raise for Flat variants (bevel-yfkw).
- **Depends on:** bevel-yfkw, bevel-lqot
- **Children, in order:** bevel-rqcp → bevel-q455 → (new) icon palette → (new) Pastel out of preview.
- **New beads to create:**
  - "Flat + Pastel icon-palette override" — override the `Bevel.Color.Icon*` tokens (folder, paper, drive) per spec §"icon palette"; verify with a Filer render per theme. Needs bevel-yfkw so `Glyphs` re-resolves.
  - "Pastel: leave preview" — owner decision on the display name "Bevel Pastel (preview)" (`ThemeService.cs:42`) and a `site/index.html` shot. HUMAN.
  - "FlatVariantService: data table instead of a 17-tuple switch" — `FlatVariantService.cs:34-40`; move variant hexes into the theme's theme.json once bevel-ayhn's override mode exists.
- **Exit criterion:** both skins pass `RenderFlatAndPastelComparisonTest`, recolour file icons, and appear on the landing page with real harvested shots; then `bd close bevel-ii93`.

### bevel-safz — Dead AXAML class: Classes="taskbar-start" matches no selector
- **Meta:** P3 · bug · size S · AGENT
- **State today:** Still true. `src/Bevel.Taskbar/TaskbarView.axaml:123` is the only occurrence of `taskbar-start` in `src/`, `third_party/` and `docs/design/flat/` (grep). The Start button is already themed through `Theme="{DynamicResource Bevel.Theme.StartButton}"`, which all four themes supply.
- **Depends on:** none
- **Files:** `src/Bevel.Taskbar/TaskbarView.axaml`
- **Decision:** Remove the class. Reason: the theme contract for this button is the `Bevel.Theme.StartButton` ControlTheme key; a style class would be a second, competing hook, and nothing has needed it in six weeks.
- **Test first:** `grep -rn "taskbar-start" src third_party` → empty after the edit.
- **Steps:**
  - [ ] Delete ` Classes="taskbar-start"` from line 123.
  - [ ] Run the Verify command (renders must not change).
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~RenderTaskbar|FullyQualifiedName~TaskbarThemeTests"` → pass, no changed PNGs
- **Done when:** grep is empty and renders are identical; then `bd close bevel-safz --reason "removed; Start button is themed via Bevel.Theme.StartButton"`

### bevel-zvo0 — InfoPaneStyle enum still carries vendor names (Win2000/WinXP/Win9x)
- **Meta:** P3 · bug · size S · AGENT+HUMAN-VERIFY
- **State today:** Still true. `src/Bevel.Core/SettingsService.cs:1041` enum members `Win2000`, `WinXP`, `Modern`, `Win9x` (+ `Auto`, `Off`). It persists BY NAME: written with `.ToString()` (`:531`), read with `Enum.TryParse` falling back to `Auto` (`:690`) — so a rename silently resets a saved value to Auto (no crash). User-visible vendor strings: `src/Bevel.FileManager/FolderOptionsWindow.axaml.cs:30-33` ("Windows 2000 / Me (Web View)", "Windows XP (Common Tasks)", …). Shot tags `winxp`/`win9x` in `tests/Bevel.FileManager.Tests/InfoPaneStyleTest.cs:29,31` → `site/shots/infopane-winxp.png`, `infopane-win9x.png`, referenced at `site/index.html:330,332`. The page's figure captions are already neutral ("1999", "2001", "mid-2000s", "mid-1990s").
- **Depends on:** none
- **Files:** `src/Bevel.Core/SettingsService.cs`, `src/Bevel.FileManager/FolderOptionsWindow.axaml.cs`, `src/Bevel.FileManager/FileManagerWindow.axaml.cs`, `src/Bevel.FileManager/Components/InfoPane.axaml.cs`, `tests/Bevel.FileManager.Tests/InfoPaneStyleTest.cs`, `site/index.html`, `site/shots/`
- **Decision:** Rename members by era/function: `Win2000`→`WebView1999`, `WinXP`→`Tasks2001`, `Modern`→`Navigation2006`, `Win9x`→`Minimal1995`. No migration (same reasoning as 07e89a3). Labels: "Web view (1999)", "Common tasks (2001)", "Navigation (2006)", "Minimal (1995)". Shot tags `winxp`→`tasks2001`, `win9x`→`minimal1995`, `modern`→`navigation2006`; `git mv` the three PNGs and update `site/index.html`. Also rename the x:Names `WinXPView`/`Win9xView` in `InfoPane.axaml`.
- **Test first:** `InfoPaneStyleTest` theory rows compile with the new members and write the new filenames; add to `tests/Bevel.Core.Tests` a settings round-trip test `InfoPaneStyle_unknown_persisted_value_falls_back_to_Auto` (feed `"WinXP"`).
- **Steps:**
  - [ ] Add the Core test; rename the enum (IDE/`sed -i ''` across `src tests`).
  - [ ] Update labels, tags, x:Names; `git mv` shots; fix `site/index.html`.
  - [ ] HUMAN: back up then edit this machine's `~/.config/bevel/settings.db` value if it is not `Auto` (the shell must be stopped; core is the sole writer).
  - [ ] `grep -rn "Win2000\|WinXP\|Win9x" src site/index.html` → only comments about history remain.
- **Verify:** `dotnet test tests/Bevel.FileManager.Tests/Bevel.FileManager.Tests.csproj --filter "FullyQualifiedName~InfoPaneStyle"` → pass; `ls site/shots | grep infopane` → 4 neutral names
- **Done when:** no vendor name in the enum, labels, or shot filenames; then `bd close bevel-zvo0 --reason "InfoPaneStyle renamed by era; shots + page updated"`

### bevel-50hs — Tokenize straggler hardcoded colours/sizes
- **Meta:** P2 · task · size M · AGENT
- **State today:** Open. Full sweep of `src/` outside the theme projects (grep `#RRGGBB`, `Color.Parse`, `Brushes.X`): **chrome that must be tokens** — `Components/PropertiesDialog.axaml:11` and `FolderPickerDialog.axaml:10` (`#D4D0C8`), `Components/SearchPane.axaml:12,13,19,60,104`, `Components/AddressBar.axaml:53` (`#404040`), `Components/ItemView.axaml:90-91` (marquee `#220066FF`/`#CC0066FF`), `src/Bevel.Desktop/DesktopView.axaml:4` (`#3A6EA5`), `src/Bevel.Taskbar/StartMenu.axaml:273,320,321,323` and `StartMenu.axaml.cs:248` (`Brushes.RoyalBlue`/`White`), `src/Bevel.Taskbar/TaskbarView.axaml:69,72,76`, `src/Bevel.UI/BevelBorder.cs:32-38` (default palette). **Font size** — `FontSize=11` literals at `ItemView.axaml:21,29,41,53` and `ItemView.axaml.cs:257,286,356-358,994`; no font-size token exists (`ThemeTokens` has only `FontUI/FontCaption/FontMono`). **Fixed by design (object colours, leave + comment):** `ToolbarIcons.cs:73-87`, `StartMenu.axaml` place-icon illustrations (`:263-535`), `Glyphs.DiscRainbow` (`Glyphs.cs:200`), `BevelMark.cs`, status dots `OnboardingWindow.axaml.cs:297-311` and `TaskbarView.axaml:359,501`. **Deferred to bevel-fdo5:** `Components/InfoPane.axaml` (63 hexes) + `InfoPane.axaml.cs:327-328`.
- **Depends on:** bevel-lqot (naming rule); InfoPane half depends on bevel-fdo5
- **Files:** the chrome + font-size files listed above; `src/Bevel.Themes.Industrial1999/theme.json`; generated `Tokens.axaml`, `ThemeTokens.cs`
- **Decision:** Add one metric `"FontSizeUI": { "double": 11 }` to theme.json (generates `ThemeTokens.MetricFontSizeUI` = `Bevel.Metric.FontSizeUI`). Map chrome hexes to EXISTING tokens: `#D4D0C8`→`Bevel.Brush.ButtonFace`, `#808080`→`Bevel.Brush.ButtonShadow`, `#404040`→`Bevel.Brush.ButtonDkShadow`, `#000000` text→`Bevel.Brush.WindowText`, desktop→`Bevel.Brush.Desktop`, marquee→`Bevel.Brush.Highlight` with `Opacity`/`StrokeOpacity`, Start-menu hover→`Bevel.Brush.Highlight`/`HighlightText`. `DiscRainbow` stays fixed (iridescence depicts the object). Split the InfoPane work into a new bead under bevel-fdo5.
- **Test first:** new `tests/Bevel.FileManager.Tests/ThemeTokenTests.No_chrome_hex_literals_in_shared_axaml`: scan the chrome files above for `#[0-9A-Fa-f]{6,8}` → zero, with an explicit allow-list for the fixed-by-design files.
- **Steps:**
  - [ ] Write the scan test with the allow-list; confirm it fails.
  - [ ] Add `FontSizeUI`; run `dotnet run --project tools/ThemeGen`; replace the 10 `FontSize` literals (`{DynamicResource Bevel.Metric.FontSizeUI}` / `tb[!TextBlock.FontSizeProperty] = new DynamicResourceExtension(...)`).
  - [ ] Replace chrome hexes file by file; run the render tests after each.
  - [ ] Add a one-line "object colour, fixed by design" comment at each allow-listed site.
  - [ ] `bd create` the InfoPane follow-up (title: "InfoPane: semantic-vs-fixed token table + derive from Bevel.Brush.InfoPane*").
- **Verify:** `dotnet test tests/Bevel.FileManager.Tests/Bevel.FileManager.Tests.csproj --filter "FullyQualifiedName~ThemeTokenTests"` → pass; `git diff --exit-code src/Bevel.UI/ThemeTokens.cs` after ThemeGen → clean
- **Done when:** scan test passes and renders under Industrial1999 are unchanged; then `bd close bevel-50hs --reason "chrome + font-size stragglers tokenised; InfoPane split to follow-up"`

### bevel-yd2n — PROFILE FIRST: colourway switch does ~140 individual resource writes
- **Meta:** P3 · bug · size M · AGENT
- **State today:** Still true (`Blue2001Variant.cs:208-220` per-key remove + assign, `:281-286` Clear). `FlatVariantService.Put` does the same (`FlatVariantService.cs:72-76`). `ColorSchemeService` already uses one merged dictionary (`:87-96`). No benchmark exists for theme apply (`docs/perf.md` lists it unmeasured, per the bead; UNVERIFIED that the line is still there).
- **Depends on:** bevel-yfkw (so the measured switch includes the real consumer rebuilds)
- **Files:** `benchmarks/Bevel.Benchmarks/ThemeSwitchBenchmarks.cs` (new), `docs/perf.md`, then conditionally `src/Bevel.UI/Blue2001/Blue2001Variant.cs`, `src/Bevel.UI/FlatVariantService.cs`
- **Decision:** Measure first; budget = one colourway switch ≤ 50 ms wall on the UI thread with one Filer window + taskbar realized (THM-03's 500 ms is for a full theme swap). Only if over budget: build a fresh `ResourceDictionary` off-tree and swap it as ONE entry in `Application.Resources.MergedDictionaries`, tracking the dictionary instead of `_injected`. Risk to check in that case: a merged dictionary has LOWER precedence than direct Application keys and than `Application.Styles` resources — `FontService`/`ThemeOptions` top-level writes still win (good), but confirm the variant still outranks the theme's `Styles`-level `Bevel.Brush.*` (render tests are the proof).
- **Test first:** BenchmarkDotNet `ThemeSwitchBenchmarks.Blue2001_colourway_switch` (headless app, alternate Blue↔Silver) — records the baseline.
- **Steps:**
  - [ ] Add the benchmark; run it; write the number into `docs/perf.md`.
  - [ ] If ≤ budget: `bd close bevel-yd2n --reason "measured <n> ms, under 50 ms budget; no rewrite"`.
  - [ ] If over: implement the single-dictionary swap for Blue2001 then Flat; all `RenderBlue2001*` and `Blue2001GlossRampTests` must stay byte-identical; re-run the benchmark.
- **Verify:** `dotnet run -c Release --project benchmarks/Bevel.Benchmarks -- --filter "*ThemeSwitch*"` → mean reported; `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~RenderBlue2001"` → pass
- **Done when:** a measured number is in `docs/perf.md` and the switch is under budget; then close as above.

### bevel-q91m — Arbitrary accent-colour derivation (free picker)
- **Meta:** P2 · task · size M · AGENT
- **State today:** Not started. `Blue2001VariantService.Colors` is a fixed 4-entry list keyed to `enum Blue2001ColorVariant`; `Apply` does `Enum.Parse<Blue2001ColorVariant>(color)` (`Blue2001Variant.cs:203`) and `Xform(variant)` returns a per-variant `ColorXform`. The machinery (`Blue2001ColorMath.cs`: `ToHsl`/`FromHsl`, `ColorXform`, lightness clamps, on-chrome text decision at `:233-236`) already works from a hue/sat/light transform, not from the enum.
- **Depends on:** bevel-lqot, bevel-yfkw; UI needs a colour picker control (none exists in `src/`)
- **Files:** `src/Bevel.UI/Blue2001/Blue2001Variant.cs`, `src/Bevel.UI/Blue2001/Blue2001ColorMath.cs`, `src/Bevel.UI/ThemeVariants.cs`, `src/Bevel.Core/SettingsService.cs`, `src/Bevel.FileManager/SettingsWindow.axaml(.cs)`, `tests/Bevel.UI.Tests/Blue2001AccentTests.cs` (new)
- **Decision:** `BevelSettings.Blue2001Color` keeps its string type and additionally accepts `#RRGGBB`. Add `internal static ColorXform XformFor(Color accent)` (new): hue shift = accent hue − reference hue of `#0A51DF`, saturation/lightness multipliers = ratios vs the reference, clamped to the ranges the four presets span. `Apply` picks `Xform(variant)` for a named id and `XformFor(Color.Parse(id))` for a hex. Picker v1 = the existing "Colour" combo plus a "Custom…" entry opening a hex `TextBox` + live swatch (no new colour-wheel control; that is a separate design task). Text contrast: reuse the existing on-chrome luminance switch; add a WCAG ≥ 4.5 assertion.
- **Test first:** `Blue2001AccentTests.Custom_accent_recolours_chrome_and_keeps_text_readable`: for hexes `#C0392B`, `#2ECC71`, `#F1C40F`, `#111111`, `#FAFAFA` apply, then assert `Bevel.Brush.CaptionActive` mid-stop hue ≈ accent hue (±8°) and contrast(`ActiveCaptionTextBrushKey`, caption mid-stop) ≥ 4.5; `Clear()` removes every injected key.
- **Steps:**
  - [ ] Write the test (in `[Collection]` with the other theme-mutating UI tests); confirm it fails on `Enum.Parse`.
  - [ ] Add `XformFor`; branch in `Apply`; make `IsKnownColor` accept a valid hex.
  - [ ] Add the "Custom…" option to the Appearance tab via `ThemeOption` (extend the record with an optional free-text flag).
  - [ ] Render one custom accent into `site/shots` only when the owner wants it on the page.
- **Verify:** `dotnet test tests/Bevel.UI.Tests/Bevel.UI.Tests.csproj --filter "FullyQualifiedName~Blue2001Accent|FullyQualifiedName~Blue2001GlossRamp|FullyQualifiedName~ThemeVariantRegistry"` → pass
- **Done when:** any hex typed in Appearance recolours Bevel 2001 live with readable chrome text; then `bd close bevel-q91m --reason "free accent via XformFor; presets unchanged"`

### bevel-fdo5 — EPIC: Derivation engine → derived dark mode
- **Meta:** P2 · epic · size L · AGENT+HUMAN-VERIFY
- **State today:** Not started as an engine. Pieces that exist: HSL transform + clamps in `Blue2001ColorMath.cs`, on-chrome text switch in `Blue2001Variant.cs:233-236`, per-engine `Clear()` symmetry enforced by `ThemeVariants.Apply`. Constraint: the app pins `RequestedThemeVariant="Light"` and popups follow `Application.ActualThemeVariant` — so dark mode must be a DERIVED TOKEN SET applied by an engine, never Avalonia `ThemeVariant.Dark` (spec ENG-04/W11-02 in `docs/spec/05-theming.md` says the opposite and is stale for this decision).
- **Depends on:** bevel-yfkw, bevel-lqot, bevel-ayhn (single-source tokens), bevel-yd2n (decides direct-write vs one dictionary — the engine should emit ONE dictionary)
- **Children, in order:** bevel-q91m (accent → transform) → bevel-50hs (no dead-end literals) → new beads below.
- **New beads to create:**
  - "Derivation: WCAG `TextOn(Color background)` helper in Bevel.Core + use it for every on-chrome text token" — replaces the ad-hoc `L > 0.52` test.
  - "Derivation: state derivation (hover/pressed/disabled/selected from one base) as pure functions with table tests".
  - "Derivation: lift `ColorXform` + the three engines behind one `IThemeRecolourEngine { Apply(BevelSettings); Clear(); }` registered in `ThemeVariants`" — makes engine order and `Clear()` symmetry structural instead of three `if`s (`ThemeVariants.cs:97-102`).
  - "Derived dark mode: `BevelSettings.DarkMode` + a dark twin generated from any theme/scheme/accent; renders for all four themes" — HUMAN sign-off on the look.
  - "InfoPane semantic-vs-fixed token table" (from bevel-50hs).
- **Exit criterion:** toggling dark mode on any of the four themes, any scheme, any accent yields a legible shell (automated contrast test over every `Bevel.Brush.*Text` / background pair ≥ 4.5), and toggling back leaves `Application.Resources` identical to before (key-set equality test); then `bd close bevel-fdo5`.

### bevel-tknt — The .beveltheme package pipeline / third-party themes are unimplemented
- **Meta:** P2 · feature · size L · AGENT+HUMAN-VERIFY
- **State today:** Still unimplemented. `ThemeService` is a static class with a hardcoded 4-entry `Themes` array (`ThemeService.cs:34-43`) and a `StylesFor` switch (`:66-72`). No `IThemeService`, `ThemeDescriptor`, `InstallPackageAsync`, user-themes directory or manifest exists in `src/` (grep). Spec: `docs/spec/05-theming.md` §2 (THM-01..04, PKG-01..03).
- **Depends on:** bevel-lqot (neutral keys a package can target), bevel-ayhn (ThemeGen override mode = the package's token compiler), the "ThemeService registry as data" bead from bevel-dob
- **Files:** `src/Bevel.UI/ThemeService.cs`, `src/Bevel.UI/ThemeDescriptor.cs` (new), `src/Bevel.Core/Themes/ThemePackage.cs` (new), `src/Bevel.Core/Themes/ThemePackageInstaller.cs` (new), `tests/Bevel.Core.Tests/ThemePackageTests.cs` (new), `tests/fixtures/themes/` (new)
- **Decision:** v1 package = TOKENS ONLY: a zip holding `theme.json` (palette, metrics, fonts, `"inherits": "<builtin id>"`, plus `author`/`license`/`attribution_notice`) and optional vector assets. NO AXAML in v1. Reason: runtime AXAML needs `AvaloniaRuntimeXamlLoader`, which is reflection-based and breaks the NativeAOT publish (`Bevel.App.csproj:20-26`) — the spec's PKG-01 "AXAML without x:Class" contradicts north-star rule 8; flag to owner. A package therefore reskins a built-in template set (PKG-03 "inherits" path). Install dir: `~/.config/bevel/themes/<id>/` (next to `settings.db`); installer validates in Bevel.Core off the UI thread, rejects any entry that is not `theme.json`, `.svg`, `.ttf/.otf`, or `provenance.json`, rejects path traversal, caps size. Only the core process installs (one owner); peers learn of new themes through the settings snapshot.
- **Test first:** `ThemePackageTests`: `Valid_token_package_installs_and_lists`, `Package_with_dll_or_axaml_is_rejected`, `Zip_slip_entry_is_rejected`, `Unknown_inherits_id_is_rejected`, `Installed_theme_applies_tokens_over_its_base` (the last in `tests/Bevel.Taskbar.Tests`, `[Collection("TaskbarTheme")]`).
- **Steps:**
  - [ ] HUMAN: confirm "tokens-only v1, no runtime AXAML" (product direction).
  - [ ] Land `ThemeDescriptor` + data-driven registry (no behaviour change; `ThemeEngineTests` green).
  - [ ] Implement `ThemePackage` parse/validate + `ThemePackageInstaller.InstallAsync(string path, CancellationToken)`; tests above.
  - [ ] Runtime: build a `ResourceDictionary` from the package's theme.json in code (same mapping ThemeGen uses; share the mapping via a small library, do not duplicate) and merge it over the base theme; raise `Glyphs.InvalidateThemeCache()`.
  - [ ] Settings UI: "Install theme…" file picker in the Appearance tab; list installed themes after the built-ins.
  - [ ] CI: round-trip Blue2001's tokens as a package fixture (PKG-02, tokens part only).
- **Verify:** `dotnet test tests/Bevel.Core.Tests/Bevel.Core.Tests.csproj --filter "FullyQualifiedName~ThemePackage"` → pass
- **Done when:** a `.beveltheme` token package installs from the Appearance tab, applies live, survives restart, and code-carrying packages are refused; then `bd close bevel-tknt --reason "token-only .beveltheme v1 shipped"`

### bevel-te20 — Git-repo theme install (.beveltheme from user-supplied git URL)
- **Meta:** P2 · feature · size M · HUMAN
- **State today:** Not started; nothing to build on until bevel-tknt exists. Licensing / disclaimer / provenance enforcement is explicitly parked (`docs/legal/open-questions-parked.md`; tracked by bevel-legal-branding, in_progress, lease expired 2026-09-30).
- **Depends on:** bevel-tknt; bevel-legal-branding (outside cluster) for the install disclaimer wording
- **Files:** `src/Bevel.Core/Themes/ThemeGitInstaller.cs` (new), `src/Bevel.FileManager/SettingsWindow.axaml(.cs)`, `tests/Bevel.Core.Tests/ThemeGitInstallerTests.cs` (new)
- **Decision:** Engineering path (buildable now, behind the tknt installer): shell out to the system `git` (`git clone --depth 1 --branch <ref> <url> <tmp>`) from the CORE process, off the UI thread, with `GIT_TERMINAL_PROMPT=0`, a 60 s timeout, `https://` URLs only, no submodules, no hooks (`-c core.hooksPath=/dev/null`); then hand the checkout to `ThemePackageInstaller` (same fail-closed validation) and promote into `~/.config/bevel/themes/<id>/`. No libgit2 dependency (native binary, AOT friction). HUMAN part: the disclaimer text, whether provenance fields are required or advisory, and whether to ship before counsel answers.
- **Test first:** `ThemeGitInstallerTests.Clones_a_local_file_repo_and_installs_its_theme` (create a temp repo with `git init` in the test), `Rejects_non_https_remote_urls`, `Rejects_repo_containing_assemblies`, `Times_out_and_cleans_temp_dir`.
- **Steps:**
  - [ ] HUMAN: decide disclaimer wording + provenance policy (or explicitly ship with "third-party content, not published by Bevel" placeholder).
  - [ ] Implement `ThemeGitInstaller` + tests (local `file://` allowed only under a test-only flag).
  - [ ] UI: URL + optional ref fields next to "Install theme…"; progress + error text; never blocks the UI thread.
  - [ ] Document in `site/` / README that Bevel is not the publisher of third-party assets.
- **Verify:** `dotnet test tests/Bevel.Core.Tests/Bevel.Core.Tests.csproj --filter "FullyQualifiedName~ThemeGitInstaller"` → pass
- **Done when:** pasting a public https git URL installs a token theme, and a repo with any code file is refused; then `bd close bevel-te20 --reason "git install over the tknt validator"`

### bevel-lkw1 — EPIC: Theme lineage — Win10 flat, KDE Breeze, Aero, Win11
- **Meta:** P2 · epic · size L · AGENT+HUMAN-VERIFY
- **State today:** Not started; no project, spec or design file for any of the four. Theme names are vendor marks — shipped ids/names must follow the neutral scheme already used (`industrial1999`, `blue2001`): HUMAN naming decision per theme (bevel-legal-branding).
- **Depends on:** bevel-lqot (neutral shared keys), bevel-ayhn (generated tokens), bevel-dob exit (contract complete), bevel-fdo5 for any theme with a dark twin; Win11-style additionally needs taskbar centring (outside this cluster)
- **Children, in order:** none exist yet.
- **New beads to create (one per theme, each = design spec → tokens → ControlTheme set → renders → landing-page shot):**
  - "Theme: flat 2015 (Win10-style) — spec in docs/design/flat2015/" — first: closest to existing `Bevel.Themes.Flat` structure, token-heavy, low template risk.
  - "Theme: Breeze-style (first non-Windows lineage)" — proves the contract is not Windows-shaped; needs a client-side decoration decision.
  - "Theme: glass 2006 (Aero-style) — painted vector glass, no blur dependency" — XL; blur/translucency is a PAL capability question (HUMAN + other cluster).
  - "Theme: 2021 (Win11-style)" — XL; gated on taskbar centring and an honest rounded-window template.
  - "Contract check: add each new theme to `ThemeEngineTests` contract list and to `ThemeVariantRegistryTests`".
- **Exit criterion:** each shipped theme supplies every `Bevel.Theme.*` key, has a `Render*` test and a harvested `site/shots` image, and switches live to and from every other theme without residue; then `bd close bevel-lkw1`.

### bevel-ti3 — M5: Win2000 theme completion across every control (pixel-golden)
- **Meta:** P2 · task · size L · AGENT+HUMAN-VERIFY
- **State today:** Open; parent epic bevel-m5 (outside cluster). The theme is `src/Bevel.Themes.Industrial1999` (aliases the vendored Classic templates through `BasedOn`, `Industrial1999Theme.axaml`, 103 lines). Golden coverage today: `tests/Bevel.Taskbar.Tests/RenderIndustrial1999BorderTest.cs`, `RenderTaskbarTest.cs`, `RenderPressedTaskButtonTest.cs`, `RenderFileMenuBarTest.cs`, `tests/Bevel.FileManager.Tests/ThemeTokenTests.cs` (palette + metrics vs spec table). There is NO Industrial1999 equivalent of `RenderBlue2001ControlsTest` (a per-control gallery). Note the repo's rule: fidelity = colours + feel + function, not pixel-perfect — "pixel-golden" here means regression goldens of OUR render, not matching MS pixels.
- **Depends on:** bevel-50hs (stragglers would bake wrong colours into goldens); ENG-01 bead from bevel-dob
- **Files:** `tests/Bevel.Taskbar.Tests/RenderIndustrial1999ControlsTest.cs` (new), `site/shots/`, `src/Bevel.Themes.Industrial1999/Industrial1999Theme.axaml`
- **Decision:** Define "every control" as the `Bevel.Theme.*` contract list in `ThemeEngineTests` plus Window caption, ToolTip, ContextMenu, StatusBar, ListBox/TreeView, GroupBox. One gallery render test (copy the structure of `RenderBlue2001ControlsTest`), states: normal/hover/pressed/disabled/focused/default. Goldens pinned by sha256 the way `RenderFlatAndPastelComparisonTest` does. Fix defects found as separate small beads, not inside this one.
- **Test first:** `RenderIndustrial1999ControlsTest.Render_control_gallery_to_png` — settles frames (tick the render timer; no sleeps), writes the PNG, compares hash.
- **Steps:**
  - [ ] Write the gallery test; commit the first golden after HUMAN review of the image.
  - [ ] For each control that looks wrong, `bd create` a bead with the crop coordinates.
  - [ ] Add the gallery image to `site/index.html` only if it replaces an existing shot.
- **Verify:** `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~RenderIndustrial1999"` → pass twice in a row with identical hashes
- **Done when:** the gallery golden is reviewed, deterministic, and has no open defect beads; then `bd close bevel-ti3 --reason "Industrial1999 control gallery golden landed"`

### bevel-ztdt — Theme Studio: fold caption colour/height into Save
- **Meta:** P3 · task · size M · AGENT
- **State today:** Open, and the tool is BROKEN by the rename first: `tools/ThemeStudio/App.cs:65` calls `ThemeService.Apply("luna")` and `StudioWindow.cs:136,376` test `id == "luna"` / `ThemeService.Current == "luna"`. `"luna"` is not a known id, so `Apply` falls back to Industrial1999 and the Blue2001 save branch (`luna` → `Blue2001Tokens.axaml`, `StudioWindow.cs:379`) is unreachable. The skip the bead describes is at `StudioWindow.cs:385` (`if (k is not string key) { skipped.Add(...) }`) — the `SystemColors.*` caption keys and `WindowCaptionHeightKey` are object keys.
- **Depends on:** bevel-ayhn (after it, Blue2001 plain tokens are generated; Save must write theme.json and run ThemeGen for BOTH themes, and only patch the hand-written remainder)
- **Files:** `tools/ThemeStudio/App.cs`, `tools/ThemeStudio/StudioWindow.cs`
- **Decision:** (1) Replace every `"luna"` literal with `Bevel.Core.ThemeIds.Blue2001`. (2) In `Save()`, handle non-string keys: map `SystemColors.ActiveCaptionColorKey`, `GradientActiveCaptionColorKey`, `ActiveCaptionTextColorKey` (and inactive twins) to `PatchXaml(xamlPath, "{x:Static common:SystemColors.<Name>}", hex)` and the caption height key to its `<x:Double>` entry in `Blue2001Tokens.axaml`; for Industrial1999 write the palette entries `ActiveTitle`/`GradientActiveTitle` and metric `CaptionHeight` in theme.json (ThemeGen already derives the SystemParameters caption height from it, `Program.cs:48-52`). (3) `PatchXaml` must match the `x:Static` key form.
- **Test first:** no test project covers `tools/ThemeStudio`; the check is manual: launch `dotnet run --project tools/ThemeStudio`, pick Bevel 2001, change caption colour + height, Save → status shows no "skipped" for those keys and `git diff src/Bevel.Themes.Blue2001/Blue2001Tokens.axaml` shows exactly those entries.
- **Steps:**
  - [ ] Fix the three `"luna"` literals; confirm the Studio opens on Bevel 2001 with the Blue colourway.
  - [ ] Extend `Save()` + `PatchXaml` per the decision.
  - [ ] Reload the Studio; the saved caption values are shown.
  - [ ] `git checkout` the token files afterwards unless the change is intended.
- **Verify:** `dotnet build tools/ThemeStudio/ThemeStudio.csproj -clp:ErrorsOnly` → 0 errors; manual check above
- **Done when:** caption colour/gradient/text/height round-trip through Save for both themes; then `bd close bevel-ztdt --reason "Studio id fixed; caption keys persisted"`

### bevel-ekcv — Luna menus: render mac ⌘-glyph accelerators (not "Cmd+X")
- **Meta:** P3 · task · size S · AGENT
- **State today:** Open. `src/Bevel.Themes.Blue2001/Blue2001Theme.axaml:348` declares `<conv:PlatformKeyGestureConverter x:Key="KeyGestureConverter" />` and `:1128` binds `PART_InputGestureText` through it — the same converter the vendored Classic `MenuItem.axaml:9` uses, which yields text ("Cmd+X"). Flat/Pastel `Bevel.Theme.MenuItem` have no `PART_InputGestureText` hit in the grep (UNVERIFIED whether they show accelerators at all).
- **Depends on:** none
- **Files:** `src/Bevel.UI/MacGlyphKeyGestureConverter.cs` (new), `src/Bevel.Themes.Blue2001/Blue2001Theme.axaml`, `tests/Bevel.UI.Tests/MacGlyphKeyGestureConverterTests.cs` (new), `tests/Bevel.Taskbar.Tests/RenderBlue2001MenuTest.cs`
- **Decision:** New `IValueConverter` `Bevel.UI.MacGlyphKeyGestureConverter`: on macOS (`OperatingSystem.IsMacOS()`) format a `KeyGesture` as modifier glyphs in Apple order ⌃⌥⇧⌘ followed by the key, with `Return`/`Enter`→`Enter` (the design plate says "Enter"), `Back`→`⌫`, `Delete`→`⌦`, `Escape`→`⎋`, arrows→`←↑→↓`, `Tab`→`⇥`; on other OSes delegate to `PlatformKeyGestureConverter`. Swap ONLY the Blue2001 resource at `:348` (key name unchanged), so Industrial1999 keeps the text form as the acceptance criterion requires.
- **Test first:** `MacGlyphKeyGestureConverterTests.Formats_modifiers_and_named_keys` — table: `Meta+X`→`⌘X`, `Meta+Shift+Z`→`⇧⌘Z`, `Enter`→`Enter`, `Back`→`⌫`, `Meta+Alt+Escape`→`⌥⌘⎋` (make the OS check injectable so the test runs on any OS).
- **Steps:**
  - [ ] Write the table test; implement the converter.
  - [ ] Change the resource type at `Blue2001Theme.axaml:348` (add the `xmlns` for `Bevel.UI` if missing).
  - [ ] Re-run `RenderBlue2001MenuTest`; re-harvest its shot if the menu image is on the landing page.
- **Verify:** `dotnet test tests/Bevel.UI.Tests/Bevel.UI.Tests.csproj --filter "FullyQualifiedName~MacGlyphKeyGesture"` → pass; `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "FullyQualifiedName~RenderBlue2001Menu"` → pass
- **Done when:** Bevel 2001 menus read ⌘X / ⌘C / Enter on macOS and Industrial1999 is unchanged; then `bd close bevel-ekcv --reason "glyph accelerator converter for Blue2001"`

### bevel-assets — Self-produce Win2000 core asset set ($0; AI-assisted + open/CC/OFL)
- **Meta:** P2 · task · size L · HUMAN
- **State today:** PARTLY DONE in a different form — icons are code-drawn vectors, not files: `src/Bevel.UI/Glyphs.cs` (700 lines; file/folder/drive/media/start.* families, ~50 icon tokens in theme.json), `src/Bevel.FileManager/Components/ToolbarIcons.cs` (15 toolbar glyphs), `src/Bevel.UI/BevelMark.cs` (logo). Font: `src/Bevel.Themes.Industrial1999/Assets` ships Noto Sans (`Tokens.axaml:187`), not a "Bevel Sans". Sounds: none found in `src/` (UNVERIFIED — only grepped the theme projects' Assets).
- **Depends on:** bevel-legal-branding (outside cluster) for licence allow-list; spec `docs/spec/05-theming.md` §5-6, `docs/spec/win2000-icon-spec.md`
- **Files:** `src/Bevel.UI/Glyphs.cs`, `src/Bevel.Themes.Industrial1999/Assets/`, `provenance` records per THM-04
- **Decision:** HUMAN: (1) is "Bevel Sans" still wanted, or is Noto Sans (OFL) the v1 answer? (2) are sounds in v1? (3) target icon list — produce a gap list by diffing `docs/spec/win2000-icon-spec.md` against the semantic ids `Glyphs.BuildGlyph` handles. Agent-doable slice once decided: new glyphs as code-drawn geometry bound to `Bevel.Color.Icon*` tokens (never bitmaps), previewed with `src/Bevel.IconPreview`.
- **Test first:** `tests/Bevel.UI.Tests/GlyphCultureTests.cs` pattern — every semantic id in the agreed list builds a non-empty glyph.
- **Steps:**
  - [ ] Agent: generate the gap list (spec ids vs `BuildGlyph` branches) and attach it to the bead.
  - [ ] HUMAN: answer the three questions; split into per-family beads (icons / font / sounds).
  - [ ] Close this umbrella once the child beads exist.
- **Verify:** `dotnet run --project src/Bevel.IconPreview` → contact sheet shows every id in the agreed list
- **Done when:** scope is decided and split; then `bd close bevel-assets --reason "split into icon/font/sound beads; icons are code-drawn in Glyphs"`

### bevel-gye — RTL: set FlowDirection from OS locale at startup + verify bevel light source vs RTL Win2000 references
- **Meta:** P4 · task · size M · AGENT+HUMAN-VERIFY
- **State today:** Open. Consumers honour ambient `FlowDirection` (`ItemView.axaml.cs:920`, `VirtualizingWrapPanel.cs:70`, LTR-pinned path field `AddressBar.axaml:36`; `tests/Bevel.FileManager.Tests/RtlSupportTests.cs`) but nothing sets it — no assignment of `FlowDirection` anywhere in `src/Bevel.App` or the window classes (grep). Reference notes exist: `docs/reference/win2000/rtl-localized.md`.
- **Depends on:** none
- **Files:** `src/Bevel.Core/SettingsService.cs` (`BevelSettings`), `src/Bevel.UI/BevelWindow.cs`, `src/Bevel.Taskbar/TaskbarWindow.cs`, `src/Bevel.UI/BevelBorder.cs`, `tests/Bevel.FileManager.Tests/RtlSupportTests.cs`
- **Decision:** Add `BevelSettings.LayoutDirection` (new; `Auto|LeftToRight|RightToLeft`, default `Auto`). Resolve once per process from the settings snapshot: `Auto` → `CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft`. Apply in `BevelWindow`'s constructor (every Bevel window inherits it) and `TaskbarWindow`, and re-apply on `settings.Changed`. Bevel light source: keep it PHYSICAL (top-left) under RTL — do not mirror `BevelBorder`/`ClassicBorderDecorator`; reason: light direction is a property of the scene, and mirroring would flip every pressed/raised cue between two users of the same machine. HUMAN: confirm against `docs/reference/win2000/rtl-localized.md` (Win32 `WS_EX_LAYOUTRTL` did mirror `DrawEdge`); if the owner wants era parity, that is a one-flag change in `BevelBorder`.
- **Test first:** `RtlSupportTests.Window_flow_direction_follows_the_layout_setting` (set `LayoutDirection = RightToLeft` on a fake settings service, construct a `BevelWindow`, assert `FlowDirection.RightToLeft`); `Auto_uses_the_ui_culture` with `CultureInfo.CurrentUICulture = he-IL` in a try/finally.
- **Steps:**
  - [ ] Add the setting (+ persistence key in `SettingsService` next to `infoPaneStyle`), tests first.
  - [ ] Apply in `BevelWindow` / `TaskbarWindow`; add a Folder Options or Appearance combo.
  - [ ] HUMAN: decide the light-source question; record it in `docs/reference/win2000/rtl-localized.md`.
- **Verify:** `dotnet test tests/Bevel.FileManager.Tests/Bevel.FileManager.Tests.csproj --filter "FullyQualifiedName~RtlSupport"` → pass
- **Done when:** an RTL locale (or the override) mirrors Filer and taskbar layout at startup; then `bd close bevel-gye --reason "LayoutDirection setting applied at window construction; light source decision recorded"`

### bevel-zr4 — Second-source the WinDaisy-derived named color schemes (Brick, Desert, Eggplant, etc.) in schemes.json
- **Meta:** P4 · task · size M · HUMAN
- **State today:** Open. `src/Bevel.Themes.Industrial1999/schemes.json` (453 lines; keys `_comment`, `schemes`) feeds `tools/ThemeGen` → `third_party/classic-avalonia/Classic.Avalonia.Theme/Colors/<Name>.axaml`; `ColorSchemeService.Schemes` lists 14 ids (note the id `"Sprouce"` displayed as "Spruce" — `ColorSchemeService.cs:35`; renaming the id would reset a saved scheme). Provenance note: `docs/reference/win2000/color-schemes-accessibility.md`.
- **Depends on:** none
- **Files:** `src/Bevel.Themes.Industrial1999/schemes.json`, `docs/reference/win2000/color-schemes-accessibility.md`
- **Decision:** HUMAN research (needs judgment on source legitimacy and on what may be copied): find a second, independent source per scheme — period `HKCU\Control Panel\Appearance\Schemes` / `Control Panel\Colors` registry exports or dated Display Properties screenshots — and record source + date per scheme in the reference doc. Agent-doable follow-up once values are supplied: edit `schemes.json`, run ThemeGen, commit generated dictionaries. Do not rename `"Sprouce"` without a settings migration.
- **Test first:** the check is the CI `theme-drift` job (after bevel-ayhn fixes its path): generated `Colors/*.axaml` match `schemes.json`.
- **Steps:**
  - [ ] HUMAN: collect a second source for each of the 13 non-Standard schemes; fill a table (scheme, key, current, second source, verdict) in the reference doc.
  - [ ] Agent: apply any corrected values to `schemes.json`; `dotnet run --project tools/ThemeGen`.
  - [ ] Agent: re-render a scheme contact sheet (extend an existing `Render*` test) for owner review.
- **Verify:** `dotnet run --project tools/ThemeGen && git diff --stat third_party/classic-avalonia/Classic.Avalonia.Theme/Colors` → only the intended schemes change
- **Done when:** every scheme has two recorded sources or a documented "single-source, accepted" note; then `bd close bevel-zr4 --reason "schemes second-sourced / accepted with provenance"`
