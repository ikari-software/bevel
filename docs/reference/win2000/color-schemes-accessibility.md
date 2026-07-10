# Color Schemes & Accessibility — Win2000 reference notes

## Overview

Windows 2000's default look is the "Windows Standard" appearance scheme: squarish, 1px-beveled 3D controls, a dark-navy active title bar (#0A246A) fading to a lighter blue on 16-bit+ color displays, and a solid mid-blue desktop background (#3A6EA5) when no wallpaper is set. Every visible UI surface — title bars, menus, scrollbars, tooltips, message boxes — is driven by a single flat table of ~24 named system colors, editable one role at a time through Control Panel → Display → Appearance. On top of that base mechanism, Windows 2000 ships roughly two dozen preset "Scheme" palettes (Windows Standard/Classic plus named cosmetic sets like Brick, Eggplant, Teal, Wheat) that swap the whole color table at once, plus a set of dedicated High Contrast schemes and a separate Large-Fonts/DPI axis for accessibility. Accessibility itself is split across two Control Panel applets: Display Properties (colors, fonts, icon size) and the dedicated Accessibility Options applet (High Contrast toggle, StickyKeys/FilterKeys/ToggleKeys, SoundSentry/ShowSounds, MouseKeys), with an Accessibility Wizard that configures several of these at once from a short questionnaire.

## Key visual facts

- The Display Properties "Appearance" tab's live preview renders a nested "Inactive Window" behind an "Active Window" containing a menu bar with Normal/Disabled/Selected states, a "Window Text" list box, and a floating "Message Box" with an OK button — all driven by the same color table being edited. [guidebookgallery.org/screenshots/appearance](https://guidebookgallery.org/screenshots/appearance)
- The Appearance tab has four controls under the preview: **Scheme** dropdown (with Save As.../Delete buttons), **Item** dropdown, and per-item **Size**, **Color**, and **Color 2** controls, plus a **Font** row with its own Size/Color and Bold/Italic toggle buttons. [guidebookgallery.org/screenshots/appearance](https://guidebookgallery.org/screenshots/appearance)
- The default scheme selected on a fresh install is named **"Windows Standard"** in the Scheme dropdown (confirmed directly in a Display Properties screenshot). [guidebookgallery.org/screenshots/appearance](https://guidebookgallery.org/screenshots/appearance)
- The **Item** dropdown's selectable elements include (at minimum): Active Title Bar, Icon, Inactive Title Bar, Menu, Message Box, Palette Title, Selected Items, ToolTip, and Desktop. [informit.com — Choosing Colors and Backgrounds](https://www.informit.com/articles/article.aspx?p=411736&seqNum=159)
- Applying a Scheme overwrites the desktop background color too — InformIT's Win2000 guide warns "applying a color scheme will override your background color choice; you'll have to set the color again if you want it to be different from the one in the color scheme." [informit.com — Choosing Colors and Backgrounds](https://www.informit.com/articles/article.aspx?p=411736&seqNum=159)
- Font customization is per-Item: selecting an Item that supports text (e.g. Active Title Bar, Message Box, Menu, ToolTip) enables the Font/Size/Color/Bold/Italic controls; items without text keep them disabled. "Unless you pick one of these items, the font choices will not be available." [informit.com — Changing Fonts](https://www.informit.com/articles/article.aspx?p=411736&seqNum=160)
- Two-color gradient title bars (the fade from navy to pale blue across the Active Title Bar) were introduced in Windows 98 for 16-bit color and higher, and Windows 2000 continues this behavior — this is why the color table has separate `GradientActiveTitle`/`GradientInactiveTitle` roles alongside the flat `ActiveTitle`/`InactiveTitle` colors. [Hacker News comment thread on Windows title bar gradients](https://news.ycombinator.com/item?id=18671914)
- The default color picker (Windows' standard `ChooseColor` common dialog, reached from the Appearance tab's Color swatch) shows Basic Colors swatches, a hue/saturation gradient field, a luminosity slider, Hue/Sat/Lum and Red/Green/Blue numeric fields, and Custom Colors slots with "Add to Custom Colors". [guidebookgallery.org/screenshots/colourselector](https://guidebookgallery.org/screenshots/colourselector)
- A screenshot of that color picker with the desktop-background color loaded shows the numeric fields reading **Red 58, Green 110, Blue 165** — i.e. RGB(58,110,165) = #3A6EA5 — which is direct visual confirmation of the default desktop background color (resolving conflicting #3A6EA5 vs #3B6EA5 claims found elsewhere in favor of #3A6EA5). [guidebookgallery.org/screenshots/colourselector](https://guidebookgallery.org/screenshots/colourselector)
- The Background tab's "Select a background picture" list (None selected by default in the screenshot) includes stock wallpapers Blue Lace 16, Boiling Point, Chateau, Coffee Bean, Fall Memories, Feather Texture, etc., with a "Picture Display" mode dropdown (Center/Tile/Stretch) and a "Pattern..." button for the legacy 8×8 monochrome desktop pattern. [guidebookgallery.org/screenshots/appearance](https://guidebookgallery.org/screenshots/appearance)
- The Web tab exposes a single "Show web content on my Active Desktop" checkbox governing whether desktop HTML components (like "My Current Home Page") render live. [guidebookgallery.org/screenshots/appearance](https://guidebookgallery.org/screenshots/appearance)
- The Effects tab's default state (observed in screenshot): "Use transition effects for menus and tooltips" checked with a Fade-effect dropdown; "Smooth edges of screen fonts" unchecked; "Use large icons" unchecked; "Show icons using all possible colors" checked; "Show window contents while dragging" unchecked; "Hide keyboard navigation indicators until I use the Alt key" checked. [guidebookgallery.org/screenshots/appearance](https://guidebookgallery.org/screenshots/appearance)
- High Contrast is **not** on the Display Properties Appearance tab — it lives in the separate Accessibility Options applet, Display tab, behind a "Use High Contrast" checkbox and a "Settings..." button that opens a "Settings for High Contrast" dialog where the actual scheme (Black/White/#1/#2, etc.) is picked. [Microsoft — Step By Step Tutorials for Windows 2000 Accessibility Options, p.70–71](https://www.agrability.org/wp-content/uploads/2016/02/Windows-2000-accessibility.pdf)
- High Contrast can also be toggled directly via keyboard shortcut without opening any dialog, per Microsoft's own framing of the feature as one of several "Display and Readability" accessibility options alongside icon size, screen resolution, and Magnifier. [learn.microsoft.com — Suggested Features for Different Types of Disabilities](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-2000-server/cc939834(v=technet.10))
- Font-size scaling in Windows 2000 is a separate axis from color schemes: Display Properties → Settings tab → Advanced → General tab exposes a "Font size" dropdown with **Small Fonts** (normal, 96 dpi, default), **Large Fonts** (~120% of normal, 120 dpi), and **Other...** which opens a Custom Font Size dialog with a ruler/percentage slider for arbitrary scaling. [dispersednet.com / techspot.com Win2000 font threads, cross-checked against spacejock.com 120dpi guide](https://spacejock.com/Setup120dpi.html)
- The Accessibility Wizard (Start → Programs → Accessories → Accessibility → Accessibility Wizard) leads with a **Text Size** dialog whose first meaningful option is literally labeled "Use large window titles and menus," then branches into screen-resolution downscaling, scroll-bar/window-border size, and icon-size dialogs, before asking disability-category questions (blind/low-vision, deaf/hard-of-hearing, physical/dexterity). [Microsoft — Step By Step Tutorials for Windows 2000 Accessibility Options, p.9–14](https://www.agrability.org/wp-content/uploads/2016/02/Windows-2000-accessibility.pdf)
- The Accessibility Options applet has 5 tabs: **Keyboard** (StickyKeys, FilterKeys, ToggleKeys, "Show extra keyboard help in programs"), **Sound** (SoundSentry, ShowSounds), **Display** (Use High Contrast), **Mouse** (MouseKeys), **General** (automatic reset timeout, on/off notification sound+message, SerialKeys support, "Apply all settings to logon desktop" / "...to defaults for new users"). [guidebookgallery.org/screenshots/accessibility](https://guidebookgallery.org/screenshots/accessibility)
- Windows 2000's default install ships with StickyKeys, FilterKeys, ToggleKeys, SoundSentry, ShowSounds, High Contrast, and MouseKeys all **off** (every checkbox unchecked in the stock screenshots). [guidebookgallery.org/screenshots/accessibility](https://guidebookgallery.org/screenshots/accessibility)
- The General tab of Accessibility Options defaults to "Give warning message when turning a feature on" and "Make a sound when turning a feature on or off" both **checked**, while "Turn off accessibility features after idle," "Apply all settings to logon desktop," and "Apply all settings to defaults for new users" are unchecked. [guidebookgallery.org/screenshots/accessibility](https://guidebookgallery.org/screenshots/accessibility)
- The stock cosmetic scheme roster carried into Windows 2000 (same generic system-color scheme names used since Windows 95/98, not tied to any particular Windows version) is: Windows Standard, Windows Classic, Brick, Desert, Eggplant, Lilac, Maple, Marine (high color), Plum (high color), Pumpkin (large), Rainy Day, Red White and Blue (VGA), Rose, Slate, Spruce, Storm (VGA), Teal (VGA), Wheat, plus High Contrast Black, High Contrast White, High Contrast #1, and High Contrast #2 (and large-font variants of several of these). [WebSearch synthesis cross-referenced against cnkt/WinDaisy theme collection](https://github.com/cnkt/WinDaisy)
- Some scheme names historically carried a parenthetical color-depth or size qualifier — e.g. "Marine (High Color)", "Pumpkin (Large)", "Storm (VGA)" — signaling that a given palette was tuned for a specific display depth or resolution rather than being resolution-agnostic. [WebSearch synthesis](https://www.google.com)

## Measurements

| Element | Value | Confidence | Source |
|---|---|---|---|
| Normal/default DPI (Small Fonts) | 96 dpi (100%) | high | [spacejock.com/Setup120dpi.html](https://spacejock.com/Setup120dpi.html) |
| Large Fonts DPI | 120 dpi (~125% of normal) | high | [spacejock.com/Setup120dpi.html](https://spacejock.com/Setup120dpi.html) |
| Custom font scaling | Arbitrary %, set via ruler slider in "Other..." Custom Font Size dialog | medium | WebSearch synthesis of InformIT / TechSpot Win2000 threads |
| Appearance preview dialog crop (GUIdebook capture) | 404 × 448 px | ~ (screenshot crop size, not a UI spec) | [guidebookgallery.org/screenshots/appearance](https://guidebookgallery.org/screenshots/appearance) |
| Accessibility Options dialog crop (GUIdebook capture) | 367 × 443 px | ~ (screenshot crop size, not a UI spec) | [guidebookgallery.org/screenshots/accessibility](https://guidebookgallery.org/screenshots/accessibility) |

## Colors

**Windows Standard (default scheme) — system color table**, cross-checked between a search-engine-synthesized registry dump and the independently-published `classic-standard` DaisyUI theme in the WinDaisy project (values agree on every role present in both):

| Role | Hex | RGB | Source |
|---|---|---|---|
| ActiveTitle (active caption) | #0A246A | 10, 36, 106 | WebSearch synthesis; corroborated by [WinDaisy classic-standard/theme.css](https://raw.githubusercontent.com/cnkt/WinDaisy/main/themes/classic-standard/theme.css) (`--color-primary`) |
| GradientActiveTitle | #A6CAF0 | 166, 202, 240 | WebSearch synthesis (single-sourced, not cross-checked) |
| InactiveTitle | #808080 | 128, 128, 128 | WebSearch synthesis |
| GradientInactiveTitle | #C0C0C0 | 192, 192, 192 | WebSearch synthesis (single-sourced) |
| Desktop / Background (solid, no wallpaper) | #3A6EA5 | 58, 110, 165 | Directly confirmed via numeric RGB fields in [color picker screenshot](https://guidebookgallery.org/screenshots/colourselector) |
| ButtonFace / 3D Face (Control) | #D4D0C8 | 212, 208, 200 | WebSearch synthesis; corroborated by WinDaisy `--color-base-200`/`--color-neutral` |
| ButtonShadow / ControlDark | #808080 | 128, 128, 128 | WebSearch synthesis; corroborated by WinDaisy `--color-base-300` |
| ButtonDkShadow / ControlDarkDark | #404040 | 64, 64, 64 | WebSearch synthesis (single-sourced) |
| ButtonText / ControlText | #000000 | 0, 0, 0 | WebSearch synthesis |
| Window (client area) | #FFFFFF | 255, 255, 255 | WebSearch synthesis; corroborated by WinDaisy `--color-base-100` |
| WindowText | #000000 | 0, 0, 0 | WebSearch synthesis |
| Hilight / Highlight (selection bg) | #0A246A | 10, 36, 106 | WebSearch synthesis; matches WinDaisy `--color-accent` |
| HilightText / HighlightText | #FFFFFF | 255, 255, 255 | WebSearch synthesis |
| InfoWindow (ToolTip background) | #FFFFE1 | 255, 255, 225 | WebSearch synthesis; corroborated by WinDaisy `--color-info` |
| InfoText (ToolTip text) | #000000 | 0, 0, 0 | WebSearch synthesis |
| Menu | #D4D0C8 | 212, 208, 200 | WebSearch synthesis |
| MenuText | #000000 | 0, 0, 0 | WebSearch synthesis |
| Scrollbar | #D4D0C8 | 212, 208, 200 | WebSearch synthesis |
| GrayText (disabled text) | #808080 | 128, 128, 128 | WebSearch synthesis |
| HotTrackingColor | #000080 | 0, 0, 128 | WebSearch synthesis (single-sourced) |
| MenuHilight | #0A246A | 10, 36, 106 | WebSearch synthesis (single-sourced) |

**Named cosmetic schemes** — approximate window/button/accent colors reconstructed from the publicly-published [cnkt/WinDaisy](https://github.com/cnkt/WinDaisy) theme collection (a modern hobbyist recreation of the classic Windows scheme palette, not an extraction from Microsoft binaries; treat as medium confidence pending a second independent source):

| Scheme | Window/base | 3D face | Accent (title bar) | Source |
|---|---|---|---|---|
| Brick | #FFFFFF | #C2BFA5 | #800000 | [WinDaisy brick/theme.css](https://raw.githubusercontent.com/cnkt/WinDaisy/main/themes/brick/theme.css) |
| Desert | #FFFFFF | #D5CCBB | #008080 | [WinDaisy desert/theme.css](https://raw.githubusercontent.com/cnkt/WinDaisy/main/themes/desert/theme.css) |
| Eggplant | #FFFFFF | #90B0A8 | #588078 | [WinDaisy eggplant/theme.css](https://raw.githubusercontent.com/cnkt/WinDaisy/main/themes/eggplant/theme.css) |
| Lilac | #FFFFFF | #AEA8D9 | #5A4EB1 | [WinDaisy lilac/theme.css](https://raw.githubusercontent.com/cnkt/WinDaisy/main/themes/lilac/theme.css) |
| Maple | #FFFFFF | #E6D8AE | #800000 | [WinDaisy maple/theme.css](https://raw.githubusercontent.com/cnkt/WinDaisy/main/themes/maple/theme.css) |
| Marine | #C8E0D8 | #88C0B8 | #000080 | [WinDaisy marine/theme.css](https://raw.githubusercontent.com/cnkt/WinDaisy/main/themes/marine/theme.css) |
| Plum | #D8D0C8 | #A89890 | #484060 | [WinDaisy plum/theme.css](https://raw.githubusercontent.com/cnkt/WinDaisy/main/themes/plum/theme.css) |
| Pumpkin | #FFFFFF | #ECD59D | #D7A52F | [WinDaisy pumpkin/theme.css](https://raw.githubusercontent.com/cnkt/WinDaisy/main/themes/pumpkin/theme.css) |
| Rainy Day | #FFFFFF | #8399B1 | #4F657D | [WinDaisy rainy-day/theme.css](https://raw.githubusercontent.com/cnkt/WinDaisy/main/themes/rainy-day/theme.css) |
| Red White and Blue | #FFFFFF | #C0C0C0 | #800000 | [WinDaisy red-white-blue/theme.css](https://raw.githubusercontent.com/cnkt/WinDaisy/main/themes/red-white-blue/theme.css) |
| Rose | #FFFFFF | #CFAFB7 | #9F6070 | [WinDaisy rose/theme.css](https://raw.githubusercontent.com/cnkt/WinDaisy/main/themes/rose/theme.css) |
| Slate | #FFFFFF | #9DB9C8 | #558097 | [WinDaisy slate/theme.css](https://raw.githubusercontent.com/cnkt/WinDaisy/main/themes/slate/theme.css) |
| Spruce | #FFFFFF | #A2C8A9 | #599764 | [WinDaisy spruce/theme.css](https://raw.githubusercontent.com/cnkt/WinDaisy/main/themes/spruce/theme.css) |
| Storm | #FFFFFF | #C0C0C0 | #800080 | [WinDaisy storm/theme.css](https://raw.githubusercontent.com/cnkt/WinDaisy/main/themes/storm/theme.css) |
| Teal | #FFFFFF | #C0C0C0 | #008080 | [WinDaisy teal/theme.css](https://raw.githubusercontent.com/cnkt/WinDaisy/main/themes/teal/theme.css) |
| Wheat | #FFFFFF | #DEDEA0 | #808000 | [WinDaisy wheat/theme.css](https://raw.githubusercontent.com/cnkt/WinDaisy/main/themes/wheat/theme.css) |

**High Contrast schemes** (base/face/accent colors, same source and confidence caveat as above):

| Scheme | Window/base | 3D face | Accent | Source |
|---|---|---|---|---|
| High Contrast Black | #000000 | #000000 | #800080 (magenta) | [WinDaisy high-contrast-black/theme.css](https://raw.githubusercontent.com/cnkt/WinDaisy/main/themes/high-contrast-black/theme.css) |
| High Contrast White | #FFFFFF | #FFFFFF | #000000 | [WinDaisy high-contrast-white/theme.css](https://raw.githubusercontent.com/cnkt/WinDaisy/main/themes/high-contrast-white/theme.css) |
| High Contrast #1 | #000000 | #000000 | #0000FF (blue) / #008000 (green) | [WinDaisy high-contrast-1/theme.css](https://raw.githubusercontent.com/cnkt/WinDaisy/main/themes/high-contrast-1/theme.css) |
| High Contrast #2 | #000000 | #000000 | #00FFFF (cyan) / #0000FF (blue) | [WinDaisy high-contrast-2/theme.css](https://raw.githubusercontent.com/cnkt/WinDaisy/main/themes/high-contrast-2/theme.css) |

## Behavior notes

- Selecting a Scheme in the Appearance tab immediately repaints the live preview panel above it (Inactive Window / Active Window / Message Box), letting the user see title bar, menu, and text colors update before clicking Apply. [guidebookgallery.org/screenshots/appearance](https://guidebookgallery.org/screenshots/appearance)
- Custom per-Item edits are not silently merged into the named Scheme; the "Save As..." button exists specifically because tweaking one Item's color/font detaches you from the preset, requiring an explicit save to a new scheme name to persist the combination. [informit.com — Choosing Colors and Backgrounds](https://www.informit.com/articles/article.aspx?p=411736&seqNum=159) (inferred from Save As/Delete button pairing in the dialog)
- High Contrast is framed by Microsoft's own accessibility documentation primarily as a low-vision aid ("offers a simple color palette and omits images that make text difficult to read") rather than a purely cosmetic option — enabling it also suppresses certain background images/textures in supporting apps like Internet Explorer 5. [learn.microsoft.com — Suggested Features for Different Types of Disabilities](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-2000-server/cc939834(v=technet.10))
- The Accessibility Wizard's flow branches based on a checkbox-style "Set Wizard Options" step ("I am blind or have difficulty seeing things on screen," etc.) — later dialogs (Scroll Bar and Window Border Size, Icon Size) only appear if the "seeing" branch was selected, meaning the wizard tailors which display-scaling controls it shows based on self-reported need rather than always presenting the full set. [Microsoft — Step By Step Tutorials for Windows 2000 Accessibility Options, p.11–12](https://www.agrability.org/wp-content/uploads/2016/02/Windows-2000-accessibility.pdf)
- Turning on/off any Accessibility Options feature (StickyKeys, High Contrast, etc.) can optionally trigger both a warning dialog and a sound cue, controlled independently from the General tab — both are checked by default. [guidebookgallery.org/screenshots/accessibility](https://guidebookgallery.org/screenshots/accessibility)
- "Apply all settings to logon desktop" and "Apply all settings to defaults for new users" (General tab, both unchecked by default) are the mechanism for making accessibility settings persist system-wide (at the logon/welcome screen and for newly-created user profiles) rather than being scoped to the currently logged-in user only. [guidebookgallery.org/screenshots/accessibility](https://guidebookgallery.org/screenshots/accessibility)

## Open questions

- Exact pixel measurements for title-bar height, window-border width, and icon spacing under each font-size/DPI setting were not pinned down from available sources — would require direct pixel-measurement of full-resolution screenshots or a period SDK reference (e.g. an archived MSDN "Windows User Experience" guideline page), which could not be reached this session (web.archive.org fetches were blocked in this environment).
- Whether Windows 2000 exposed a distinct labeled "Extra Large Fonts" preset (as some modern summaries claim) or only "Small Fonts" / "Large Fonts" / "Other..." (custom %) — sources conflict; the more specific, procedure-level sources (TechSpot/InformIT threads) only mention Small/Large/Other, so the doc above follows that reading, but this is not 100% certain.
- The exact total count of stock non-high-contrast color schemes bundled with Windows 2000 (one source claimed "22 presets including 4 high contrast" but this could not be independently verified against a primary Microsoft source).
- Full high-contrast scheme roster for Windows 2000 specifically (vs. Windows 95/98, which are documented with large/extra-large variants like "High Contrast Black (extra large)") — the tumblr scheme-list source covers Windows 95, and it's not confirmed whether Windows 2000 kept all of those size variants or trimmed the list.
- Secondary/rare color roles (HotTrackingColor, MenuHilight, ButtonDkShadow, GradientActiveTitle/InactiveTitle) for Windows Standard are only single-sourced from a WebSearch synthesis and were not independently cross-checked against a second primary source.
- The named cosmetic schemes' exact hex values are sourced from a single modern hobbyist GitHub reconstruction (WinDaisy); no period screenshot or archived Microsoft registry export was found this session to cross-verify them.
- betawiki.net's "Windows Classic" article (likely a strong source for this domain) was blocked by a Cloudflare challenge and could not be fetched.

## Images

| Filename | Source URL | Shows |
|---|---|---|
| win2000pro-appearance-view1.png | https://guidebookgallery.org/screenshots/appearance | Display Properties, Background tab: wallpaper list, monitor preview, Picture Display dropdown, Pattern button |
| win2000pro-appearance-view2.png | https://guidebookgallery.org/screenshots/appearance | Display Properties, Appearance tab: live preview (Inactive/Active Window, Message Box), Scheme="Windows Standard", Item/Font/Size/Color controls |
| win2000pro-appearance-view3.png | https://guidebookgallery.org/screenshots/appearance | Display Properties, Web tab: "Show web content on my Active Desktop" checkbox, home page list |
| win2000pro-appearance-view4.png | https://guidebookgallery.org/screenshots/appearance | Display Properties, Effects tab: desktop icon list, Change Icon/Default Icon buttons, visual-effects checkboxes |
| win2000pro-accessibility-view1.png | https://guidebookgallery.org/screenshots/accessibility | Accessibility Options, Keyboard tab: StickyKeys/FilterKeys/ToggleKeys sections, all off by default |
| win2000pro-accessibility-view2.png | https://guidebookgallery.org/screenshots/accessibility | Accessibility Options, Sound tab: SoundSentry, ShowSounds, both off by default |
| win2000pro-accessibility-view3.png | https://guidebookgallery.org/screenshots/accessibility | Accessibility Options, Display tab: "Use High Contrast" checkbox + Settings button (the High Contrast entry point) |
| win2000pro-accessibility-view4.png | https://guidebookgallery.org/screenshots/accessibility | Accessibility Options, Mouse tab: MouseKeys section |
| win2000pro-accessibility-view5.png | https://guidebookgallery.org/screenshots/accessibility | Accessibility Options, General tab: Automatic reset, Notification (warning message/sound), SerialKeys, Administrative options |
| win2000pro-color-selector-dialog.png | https://guidebookgallery.org/screenshots/colourselector | Standard ChooseColor dialog with Basic Colors, hue/sat gradient field, luminosity slider, and RGB fields showing R58/G110/B165 — the default desktop background color |
| win2000pro-font-selection-dialog.png | https://guidebookgallery.org/screenshots/font | Standard Font common dialog (Font/Font style/Size lists, Effects, Sample preview) as invoked from Appearance-tab font pickers |

## Sources

- https://guidebookgallery.org/screenshots/appearance
- https://guidebookgallery.org/screenshots/accessibility
- https://guidebookgallery.org/screenshots/colourselector
- https://guidebookgallery.org/screenshots/font
- https://guidebookgallery.org/screenshots/win2000pro
- https://www.informit.com/articles/article.aspx?p=411736&seqNum=159
- https://www.informit.com/articles/article.aspx?p=411736&seqNum=160
- https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-2000-server/cc939834(v=technet.10)
- https://www.agrability.org/wp-content/uploads/2016/02/Windows-2000-accessibility.pdf
- https://github.com/cnkt/WinDaisy
- https://spacejock.com/Setup120dpi.html
- https://news.ycombinator.com/item?id=18671914
- http://blog.pythonaro.com/2017/07/windows-vintage-default-desktop-colours.html
- http://taskboy.com/blog/Windows_2000_XP_2003_desktop_color.html
- https://gist.github.com/Xarkam/0ae82779e25647700b1b7bfd59dee71c
- https://www.tumblr.com/ms-dos5/67518699593/windows-95-color-schemes-in-order-brick
