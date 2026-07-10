# Window Chrome & Controls — Win2000 reference notes

## Overview

Windows 2000's default "Windows Standard" visual style (the "Windows Classic" look) renders every window with a hard-edged, two-tone-gradient title bar, a flat gray (`#D4D0C8`) 3-D bevel system for buttons/wells/frames built from exactly four grayscale tones, and small non-antialiased bitmap glyphs for caption buttons, checkboxes and radios. Compared to Windows 95/NT4, Win2000 kept the same bevel algebra and control metrics but defaulted to a *gradient* active caption (navy-blue `#0A246A` fading to pale blue `#A6CAF0`) instead of a flat color, and switched the system UI font from MS Sans Serif (bitmap) to Tahoma. Every raised/sunken surface — buttons, wells, status bars, group boxes, list views — is built from the same four-color bevel vocabulary (face gray, white highlight, mid-gray shadow, black/dark-gray dark-shadow), which is the core algebra this domain must replicate pixel-for-pixel. Scrollbars use a dithered 50%-gray track pattern with solid steppers and a raised thumb; focus rectangles and default-button rings are drawn with 1-bit dotted/thick borders rather than glows.

## Key visual facts

- Windows 2000 uses the "Windows Standard" default color scheme, a refinement of the Windows 95/NT Classic look; Tahoma replaced MS Sans Serif as the default UI typeface in Windows 2000. — https://en.wikipedia.org/wiki/Windows_XP_visual_styles
- Windows 98 introduced two-color gradient window-caption support (16-bit color or higher); Windows 2000 ships this gradient caption on by default. — https://github.com/grassmunk/Chicago95/issues/12
- Measured directly from a Win2000 Pro screenshot: the active-caption gradient starts at `#0A246A` (RGB 10,36,106) at the left edge of the title bar and lightens smoothly toward `#A6CAF0` (RGB 166,202,240) near the caption-button cluster at the right edge — a near-linear interpolation across the full caption width. — https://guidebookgallery.org/screenshots/win2000pro (notepad-window.png / explorer-window-chrome.png, measured locally)
- Title bar height measures 18px in three independent screenshots (Notepad, WordPad, Explorer window), measured from the top of the gradient fill to the row where the menu bar's face-gray resumes. — https://guidebookgallery.org/screenshots/win2000pro (measured locally)
- The window's outer raised border is 4px thick at top and left in every sampled screenshot: 1px face gray (`#D4D0C8`), 1px white highlight, then 2px face gray before the caption/client area begins. — https://guidebookgallery.org/screenshots/win2000pro (measured locally)
- The title bar carries a small (~16×16px) application icon flush against the left inner edge of the caption, followed immediately by the gradient caption text area. — https://guidebookgallery.org/screenshots/win2000pro (explorer-window-chrome.png, measured locally)
- Caption buttons (minimize/maximize/close) form a tight cluster at the top-right of the title bar; each button is roughly 15–16px wide and ~14px tall, inset a couple of pixels from the 18px-tall caption bar, separated by 1px gaps, each with its own raised bevel (white top/left, `#808080`/`#404040` bottom/right). — https://guidebookgallery.org/screenshots/win2000pro (explorer-window-chrome.png, measured locally)
- `BTNFACE`/`COLOR_3DFACE` is the face color for 3-D display elements and dialog backgrounds (aliased `COLOR_BTNFACE`); `COLOR_3DHIGHLIGHT`/`BTNHIGHLIGHT` lights edges facing the light source; `COLOR_3DSHADOW`/`BTNSHADOW` shades edges facing away from it; `COLOR_3DDKSHADOW` is a separate, darker outer shadow tone. — https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getsyscolor
- Measured menu-bar height in Notepad/WordPad is ~20px (face-gray band directly below the 18px caption, ending where the sunken client-area border begins). — https://guidebookgallery.org/screenshots/win2000pro (measured locally)
- Measured Explorer toolbar band height is ~22px, sitting directly under a 1px menu-bar separator. — https://guidebookgallery.org/screenshots/win2000pro (explorer-window-chrome.png, measured locally)
- A property-sheet tab (Date/Time Properties) measures ~20px tall including its 1px white top/left highlight edge; the selected tab's white highlight runs unbroken into the page body below it, visually fusing tab and page. — https://guidebookgallery.org/screenshots/win2000pro (datetime-properties-tabs.png, measured locally)
- Sunken 3-D edges (e.g., around Notepad's edit control, and toolbar button separators) are drawn as a 2px band: `#808080` (mid-gray) outer line then `#404040` (dark gray) inner line, immediately followed by the white/face client fill — not the pure-black `WINDOWFRAME` tone documented for the nominal 3DDKSHADOW default (see Open Questions). — https://guidebookgallery.org/screenshots/win2000pro (measured locally)
- The focus rectangle is drawn with an XOR (`R2_NOTXORPEN`-style) checkerboard/dotted brush rather than a solid color, a mechanism dating to 1-bit-per-pixel 1983 monochrome displays; on modern 32-bpp displays the XOR trick can invert unexpected hues (e.g. blue `#0078D7` XORs to orange `#FF8728`), a side effect of the same technique Win2000 inherited. — https://devblogs.microsoft.com/oldnewthing/20211102-00/?p=105866
- Default push buttons use the `BS_DEFPUSHBUTTON` style, which draws an extra dark outer border/ring around the button distinguishing it from ordinary `BS_PUSHBUTTON` controls. — https://learn.microsoft.com/en-us/answers/questions/3823429/black-border-around-selected-buttons-in-windows-ap
- The classic (non-visual-styles) progress bar renders as discrete rectangular "chunks"/blocks by default; `PBS_SMOOTH` swaps this for a continuous fill bar, but `PBS_SMOOTH` is honored only under the Windows Classic theme — later visual styles override it back to a solid gradient fill. — https://github.com/MicrosoftDocs/win32/blob/docs/desktop-src/Controls/progress-bar-control-styles.md
- Windows draws checkboxes and radio buttons at a 13×13px box (100% / 96-DPI scaling) in the classic theme; this scales to 16×16px at 125% DPI and 20×20px at 150% DPI in later Windows releases that kept classic-theme scaling rules. — https://www.telerik.com/forums/how-to-increase-radcheckbox-size (general Windows classic-control sizing knowledge, ~ estimate for Win2000 itself)
- On the Mac and in Windows 2000, scrollbars are commonly cited as 16px wide/tall for the track and steppers. — general programming reference summary, not independently re-verified against a primary Microsoft spec (~ estimate, see Open Questions)

## Measurements

| element | value | confidence | source |
|---|---|---|---|
| Title bar height | 18px | high (measured, 3 screenshots agree) | https://guidebookgallery.org/screenshots/win2000pro |
| Outer window border (top/left) | 4px (1 face + 1 white + 2 face) | high (measured) | https://guidebookgallery.org/screenshots/win2000pro |
| Title bar app icon | ~16×16px | medium (measured, single sample) | https://guidebookgallery.org/screenshots/win2000pro |
| Caption button width | ~15–16px | medium (measured, single sample) | https://guidebookgallery.org/screenshots/win2000pro |
| Caption button height | ~14px (within 18px caption) | medium (measured, single sample) | https://guidebookgallery.org/screenshots/win2000pro |
| Menu bar height | ~20px | medium (measured, 2 apps agree) | https://guidebookgallery.org/screenshots/win2000pro |
| Explorer toolbar band height | ~22px | medium (measured, single sample) | https://guidebookgallery.org/screenshots/win2000pro |
| Property-sheet tab height | ~20px | medium (measured, single sample) | https://guidebookgallery.org/screenshots/win2000pro |
| Sunken-edge bevel band | 2px (`#808080` then `#404040`) | medium (measured, repeats across samples) | https://guidebookgallery.org/screenshots/win2000pro |
| Checkbox/radio box | ~13×13px @ 96 DPI | ~ estimate (general Windows-classic knowledge, not Win2000-specific primary source) | https://www.telerik.com/forums/how-to-increase-radcheckbox-size |
| Scrollbar width/height | ~16px | ~ estimate (secondary summary, unverified) | search-engine synthesis, no single primary URL found |

## Colors

Values below are the Windows XP "Classic (Standard)" scheme column from a system-colors reference table; this scheme is the unmodified carry-forward of Windows 2000's own default "Windows Standard" color scheme (GetSysColor defaults were not changed between the Win2000 and XP classic-theme baselines), so they are used here as Win2000 defaults. Several are independently corroborated by the pixel measurements above.

| role | hex | source |
|---|---|---|
| Active caption (gradient start) | `#0A246A` | https://www.quppa.net/syscol/ (corroborated by local pixel measurement) |
| Active caption (gradient end) | `#1084D0` (table) / `#A6CAF0` (measured, see Open Questions) | https://www.quppa.net/syscol/ ; measured locally |
| Active caption text | `#FFFFFF` | https://www.quppa.net/syscol/ |
| Inactive caption | `#808080` | https://www.quppa.net/syscol/ |
| Inactive caption text | `#C0C0C0` | https://www.quppa.net/syscol/ |
| Inactive caption gradient end | `#B5B5B5` | https://www.quppa.net/syscol/ |
| Button face / 3D face (`BTNFACE`) | `#D4D0C8` | https://www.quppa.net/syscol/ (corroborated by local pixel measurement) |
| Button highlight / 3D highlight (`BTNHIGHLIGHT`) | `#FFFFFF` | https://www.quppa.net/syscol/ |
| Button shadow / 3D shadow (`BTNSHADOW`) | `#808080` | https://www.quppa.net/syscol/ (corroborated by local pixel measurement) |
| 3D dark shadow (`3DDKSHADOW`, table value) | `#000000` | https://www.quppa.net/syscol/ |
| 3D light (`3DLIGHT`) | `#C0C0C0` | https://www.quppa.net/syscol/ |
| Window background | `#FFFFFF` | https://www.quppa.net/syscol/ |
| Window text | `#000000` | https://www.quppa.net/syscol/ |
| Selection highlight | `#000080` | https://www.quppa.net/syscol/ |
| Selection highlight text | `#FFFFFF` | https://www.quppa.net/syscol/ |
| Window frame | `#000000` | https://www.quppa.net/syscol/ |
| Scrollbar track base | `#C0C0C0` | https://www.quppa.net/syscol/ |
| Gray/disabled text | `#808080` | https://www.quppa.net/syscol/ |
| Desktop background (default teal) | `#3A6EA5` (table) | https://www.quppa.net/syscol/ |
| Menu face | `#C0C0C0` | https://www.quppa.net/syscol/ |
| Menu text | `#000000` | https://www.quppa.net/syscol/ |
| Sunken-edge dark line (measured, not in official table) | `#404040` | measured locally, see Open Questions |

## Behavior notes

- Focus rectangles are drawn by XOR-ing a checkerboard/dotted brush into the destination, a technique that dates to 1-bit monochrome displays (1983) and is retained through the Win2000 era; the visible color is a byproduct of bit-inverting whatever background color sits underneath, not a fixed hue. — https://devblogs.microsoft.com/oldnewthing/20211102-00/?p=105866
- Default push buttons (`BS_DEFPUSHBUTTON`) render an extra dark ring/border around the button face to distinguish the dialog's default action from ordinary buttons; pressing Enter in the dialog activates whichever button currently holds this style. — https://learn.microsoft.com/en-us/answers/questions/3823429/black-border-around-selected-buttons-in-windows-ap
- Classic-theme progress bars advance in discrete rectangular chunks rather than a smooth fill; a smooth-fill mode (`PBS_SMOOTH`) exists but is only honored while the Classic theme (not a later visual style) is active. — https://github.com/MicrosoftDocs/win32/blob/docs/desktop-src/Controls/progress-bar-control-styles.md
- Gradient captions are a togglable effect (the "Show window contents while dragging"-adjacent "Gradient Caption" setting family introduced with Win98/Win2000); when disabled, captions fall back to a flat single-color fill using the base `ACTIVECAPTION`/`INACTIVECAPTION` color rather than the two-stop gradient. — https://github.com/grassmunk/Chicago95/issues/12
- Property-sheet tabs visually fuse with the active page: the selected tab's white top/left highlight border continues unbroken into the page body directly beneath it, so the seam between tab strip and page reads as a single raised surface. — https://guidebookgallery.org/screenshots/win2000pro (observed locally)

## Open questions

- The exact pixel width/height of caption buttons and the vertical caption-button inset could only be approximated from a single screenshot sample (~15–16px × ~14px); no primary Microsoft spec (e.g. an authoritative `SM_CXSIZE`/`SM_CYSIZE` default-value table) could be reached — several candidate pages (jasinskionline.com, sgr.info, jose.it-berater.org) were unreachable (timeouts/connection resets) after two attempts each.
- The measured gradient-active-caption endpoint (`#A6CAF0`, sampled directly from a Win2000 Pro screenshot) does not match the `#1084D0` value listed under "GradientActiveCaption" in the XP-carried-forward classic color table — it's unclear whether Windows 2000's own default differed slightly from XP's classic scheme, whether `#A6CAF0` is a different named constant that only coincides with the caption gradient's visual endpoint, or whether the sampled column simply hadn't reached the true asymptotic endpoint before the caption-button cluster intervened.
- A secondary dark bevel tone of `#404040` appears repeatedly in sunken-edge rendering (menu/toolbar separators, edit-control frames) in real screenshots, but the closest available public reference table lists `COLOR_3DDKSHADOW`'s classic-scheme default as pure black `#000000`. Whether Win2000 actually used `#404040` for this role (differing from XP) or this is an application-specific (non-system-color) shade could not be confirmed from public sources.
- Could not verify an authoritative default pixel width for `SM_CXVSCROLL`/`SM_CYHSCROLL` (scrollbar thickness) specific to Windows 2000; the commonly repeated "16px" figure comes from secondary summaries, not a primary Microsoft table, and no scrollbar thumb/track was clearly visible in the screenshots gathered to measure directly.
- Checkbox/radio-button box size (13×13px) is well attested for the general "Windows Classic" control family but no Win2000-specific primary source (vs. a later-Windows retrospective) could be located; flagged as an estimate.
- toastytech.com/guis and betawiki.net's Windows Classic article both returned errors (403 Forbidden on betawiki, and toastytech's index pages needed link-following via raw HTML rather than the page-level image list) — betawiki in particular likely has more precise Win2000-specific chrome details that could not be extracted this session.
- Did not find an archived MSDN "Windows User Experience" or "Windows Interface Guidelines" page with literal dialog-unit/pixel specification tables via web.archive.org; the GUIdebook catalog page for "The Windows Interface Guidelines for Software Design" (1995 book) has only a table of contents, no scanned measurement pages.

## Images

| filename | source URL | shows |
|---|---|---|
| desktop-full.png | https://guidebookgallery.org/screenshots/win2000pro | Full Win2000 Pro desktop with open windows, taskbar |
| notepad-window.png | https://guidebookgallery.org/screenshots/win2000pro | Notepad — title bar, menu bar, sunken edit-control border |
| wordpad-window.png | https://guidebookgallery.org/screenshots/win2000pro | WordPad — title bar, menu, toolbar, ruler |
| calculator-buttons.png | https://guidebookgallery.org/screenshots/win2000pro | Calculator — raised pushbutton grid |
| addressbook-window.png | https://guidebookgallery.org/screenshots/win2000pro | Address Book — toolbar, list view, sunken panes |
| mediaplayer-window.png | https://guidebookgallery.org/screenshots/win2000pro | Windows Media Player — buttons, slider |
| volume-control-sliders.png | https://guidebookgallery.org/screenshots/win2000pro | Volume Control — multiple vertical sliders |
| display-appearance-colorscheme.png | https://guidebookgallery.org/screenshots/win2000pro | Display Properties Appearance tab — live color-scheme editor with preview window (title bar, buttons, dialog controls) |
| display-properties-tabs.png | https://guidebookgallery.org/screenshots/win2000pro | Display Properties — property-sheet tab strip |
| mouse-properties-tabs-sliders.png | https://guidebookgallery.org/screenshots/win2000pro | Mouse Properties — tabs, sliders, checkboxes |
| datetime-properties-tabs.png | https://guidebookgallery.org/screenshots/win2000pro | Date/Time Properties — tab strip (measured for tab height) |
| accessibility-options-checkboxes.png | https://guidebookgallery.org/screenshots/win2000pro | Accessibility Options — checkboxes, group boxes |
| explorer-window-chrome.png | https://guidebookgallery.org/screenshots/win2000pro | Explorer window — full chrome measured (title bar, borders, buttons, menu, toolbar) |
| task-manager-tabs-progress.png | https://guidebookgallery.org/screenshots/win2000pro | Task Manager — tabs, list views |
| run-dialog-buttons.png | https://guidebookgallery.org/screenshots/win2000pro | Run dialog — small dialog buttons |
| about-windows-dialog.png | https://guidebookgallery.org/screenshots/win2000pro | About Windows — dialog chrome, default button ring |
| font-dialog-listbox.png | https://guidebookgallery.org/screenshots/win2000pro | Font selection dialog — list boxes, sunken frames |
| openfile-dialog.png | https://guidebookgallery.org/screenshots/win2000pro | Open File dialog — toolbar, combo box, list view |
| colour-selector-sliders.png | https://guidebookgallery.org/screenshots/win2000pro | Color selector — custom-color sliders, well |
| wrongpassword-dialog-buttons.png | https://guidebookgallery.org/screenshots/win2000pro | Wrong-password message box — small dialog buttons |
| shutdown-dialog.png | https://guidebookgallery.org/screenshots/win2000pro | Shut Down Windows — radio buttons, dialog chrome |
| toastytech-filebrowser-window.png | http://toastytech.com/guis/w2k.html | Explorer window chrome (independent source corroboration) |
| toastytech-devicemgr-mmc-tabs.png | http://toastytech.com/guis/w2k2.html | Device Manager (MMC) — tree view, toolbar, tabs |

## Sources

- https://guidebookgallery.org/screenshots/win2000pro
- http://toastytech.com/guis/w2k.html
- http://toastytech.com/guis/w2k2.html
- https://en.wikipedia.org/wiki/Windows_XP_visual_styles
- https://github.com/grassmunk/Chicago95/issues/12
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getsyscolor
- https://www.quppa.net/syscol/
- https://devblogs.microsoft.com/oldnewthing/20211102-00/?p=105866
- https://learn.microsoft.com/en-us/answers/questions/3823429/black-border-around-selected-buttons-in-windows-ap
- https://github.com/MicrosoftDocs/win32/blob/docs/desktop-src/Controls/progress-bar-control-styles.md
- https://www.telerik.com/forums/how-to-increase-radcheckbox-size
- https://guidebookgallery.org/books/thewindowsinterfaceguidelinesforsoftwaredesign
