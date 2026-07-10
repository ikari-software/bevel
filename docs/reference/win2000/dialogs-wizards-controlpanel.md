# Dialogs, Wizards & Control Panel — Win2000 reference notes

## Overview

Windows 2000's secondary UI surfaces (message boxes, property sheets, wizards, the common file dialog, Control Panel, and the shutdown/run dialogs) all share the same classic 3-D chrome: a flat, non-gradient-except-titlebar dialog face in `#D4D0C8` ("Button Face"), a solid navy-to-pale-blue gradient active caption bar, and Tahoma/MS Sans Serif system text. Property sheets always place OK/Cancel/Apply right-aligned along the bottom, with tabs across the top and a "General" page first. Wizards use the Wizard97 template — a 317×193 dialog-unit exterior page with a left-hand watermark bitmap, or a 317×143 DLU interior page with a top header band containing a bold title and a small header bitmap in the upper right. Windows 2000 was the release that added the vertical five-icon "Places Bar" to the common Open/Save dialog, and Control Panel gained an Explorer-style "web view" (icon grid plus a left description pane) as an alternative to the classic icon-only list. Message boxes are unstyled system dialogs — plain gray face, a single 32×32 stock icon (exclamation/stop/question/info), and buttons whose combinations and default focus are fully determined by the `MB_*` flags passed to `MessageBox()`.

## Key visual facts

- Active title bar gradient runs from `RGB(10,36,106)` / `#0A246A` at the left edge to a pale blue (`~RGB(163,199,237)` measured before caption buttons cut it off, spec value `#A6CAF0`) at the right — measured directly from pixel column scan of a Win2000 About dialog screenshot. Source: https://guidebookgallery.org/screenshots/win2000pro (image: pics/gui/interface/dialogs/aboutgui/win2000pro.png)
- Standard dialog/button face color is `RGB(212,208,200)` = `#D4D0C8`, sampled identically across About, Run, Shutdown, Log Off and message-box screenshots. Source: https://guidebookgallery.org/screenshots/win2000pro
- Caption bar (title bar) height measures ~18px of solid gradient plus a 3px top window border/highlight line, i.e. ~21px total from the outer window edge to the client area, measured on a 419×329px About dialog capture. Source: https://guidebookgallery.org/screenshots/win2000pro (aboutgui/win2000pro.png)
- Message boxes are simple dialog frames: title bar + text + centered icon + button row, no tab strip, no "?" help button in the caption (unlike some property sheets). Confirmed on the "Logon Message" wrong-password dialog (494×126px) which uses `MB_ICONEXCLAMATION` (yellow triangle, "!"). Source: https://guidebookgallery.org/screenshots/win2000pro (interface/misc/wrongpassword/win2000pro.png)
- `MB_ICONEXCLAMATION` and `MB_ICONWARNING` both render the same yellow exclamation-triangle icon; `MB_ICONERROR`/`MB_ICONSTOP`/`MB_ICONHAND` all render the same red stop-sign icon; `MB_ICONASTERISK`/`MB_ICONINFORMATION` both render a lowercase "i" in a circle; `MB_ICONQUESTION` renders a question mark (Microsoft's own guidance says avoid it — "does not clearly represent a specific type of message"). Source: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-messagebox
- `MB_CANCELTRYCONTINUE` (Cancel / Try Again / Continue) was introduced in Windows 2000 (`WINVER >= 0x0500`) specifically to replace the older `MB_ABORTRETRYIGNORE` for its clearer wording. Source: https://devblogs.microsoft.com/oldnewthing/?p=19773/ and https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-messagebox
- `MB_HELP` (adds a Help button, placed after the other buttons) requires `MB_DEFBUTTON4` to default-focus a 4th button when combined with a 3-button style. Source: https://devblogs.microsoft.com/oldnewthing/?p=19773/
- Property sheets: OK/Cancel/Apply (and optional Help) are right-aligned as a row of command buttons along the bottom of the sheet, outside any individual tab page; Apply applies pending changes but keeps the sheet open, OK applies-and-closes, Cancel discards-and-closes (does not undo already-applied changes). Source: https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bb226808(v=vs.85)
- Every property sheet is expected to include a "General" tab, spatially first/leftmost and the default active page; an "Advanced" tab (if present) must be spatially last. Source: https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bb226808(v=vs.85)
- Win32 (unmanaged) property-page template `IDD_PROPPAGE_LARGE` measures 252×218 dialog units with a fixed 7 DLU margin around the content area. Source: https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bb226808(v=vs.85)
- Windows 2000 Display Properties is a 6-tab sheet in this fixed order: Background, Screen Saver, Appearance, Web, Effects, Settings — directly confirmed from three separate captures (Background/Screen Saver/Settings tabs all visible in the same tab strip). Source: https://guidebookgallery.org/screenshots/win2000pro (settings/display, settings/screensaver)
- The Display Properties sheet client area (including tab strip and OK/Cancel/Apply row) measures 404×448px in the GUIdebook captures. Source: https://guidebookgallery.org/screenshots/win2000pro (settings/screensaver/win2000pro.png)
- Appearance tab's "Item" list includes: Active Title Bar, Icon, Inactive Title Bar, Menu, Message Box, Palette Title, Selected Items, ToolTip (and others); selecting an item enables that item's Font/Size/Color controls, with Bold and Italic toggle buttons; a "Scheme" dropdown at the top applies a whole named color scheme (background, title bars, borders, and text colors together) in one action. Source: https://www.informit.com/articles/article.aspx?p=411736&seqNum=159 and seqNum=160
- The out-of-box default scheme in Windows 2000 (as it shipped, post-beta) is called "Windows Standard," which — per beta-history documentation — replaced the older "Windows Classic" gray scheme partway through the Windows 2000 beta cycle: default background changed from gray to a light beige/tan, title bars became a lighter blue, and the default system font moved to Tahoma. Source: https://betawiki.net/wiki/Windows_Classic
- Wizard97 exterior (welcome/completion) pages use a 317×193 dialog-unit template; the left margin is reserved uncontrolled space for a watermark bitmap that the property-sheet control composites in automatically — you must not place controls there. Source: https://learn.microsoft.com/en-us/windows/win32/controls/wizards
- Wizard97 interior pages use a smaller 317×143 DLU template because the top of the page is occupied by a system-drawn header band (title + subtitle + small header bitmap) that is not part of the page template itself. Source: https://learn.microsoft.com/en-us/windows/win32/controls/wizards
- Wizard97 interior-page header: bold 12-point Verdana for the title static control, standard dialog font for the explanatory subtitle text beneath it. Source: https://learn.microsoft.com/en-us/windows/win32/controls/wizards
- If the watermark bitmap is smaller than its reserved area (e.g. due to large system fonts), Wizard97 does not stretch it — it stays anchored top-left and the remaining area is filled with the bitmap's own top-left pixel color. Source: https://learn.microsoft.com/en-us/windows/win32/controls/wizards
- Introduction pages show enabled Next + disabled Back; completion pages replace Next with Finish and enable Back — this is the default Wizard97 button state model apps must explicitly re-assert per page via `PSM_SETWIZBUTTONS`. Source: https://learn.microsoft.com/en-us/windows/win32/controls/wizards
- In Wizard97 mode, the property sheet's overall caption text comes from the *current page's* dialog template caption, not from the `PROPSHEETHEADER.pszCaption` — so apps typically set the caption only on the first (intro) page's template so it's inherited going forward. Source: https://learn.microsoft.com/en-us/windows/win32/controls/wizards
- A real Wizard97 interior page is directly visible in the Windows 2000 Setup GUI phase ("Installing Components" screen): blue titlebar reading "Windows 2000 Professional Setup", white header band with bold black title + regular subtitle, small Windows-flag header bitmap top-right, gray body, and grayed Back / enabled-but-inactive Next buttons bottom-right separated from the body by a thin divider line. Source: https://guidebookgallery.org/screenshots/win2000pro (pics/gui/installation/copying/win2000pro.png)
- Windows 2000 introduced the "Places Bar" to the common Open/Save dialog: a vertical strip of five large icon+label buttons down the left edge — confirmed directly as History, Desktop, My Documents, My Computer, My Network Places (top to bottom) — with the current-folder shortcut visually pressed/highlighted. Source: https://guidebookgallery.org/screenshots/win2000pro (interface/dialogs/openfile/win2000pro.png)
- The common Open dialog's non-Places-Bar chrome is standard: "Look in:" combo + toolbar (Up-one-level, Create-new-folder, List/Details view toggle) across the top, file list, then "File name:" and "Files of type:" combos, with Open/Cancel at bottom right. Source: https://guidebookgallery.org/screenshots/win2000pro (interface/dialogs/openfile/win2000pro.png)
- The Shut Down Windows dialog is a small fixed-size sheet: Microsoft/Windows 2000 masthead-free variant showing a monitor icon, a single labeled dropdown ("What do you want the computer to do?": Log off / Shut down / Restart / Stand by, per the dropdown's known option set), a one-line description of the selected action, and OK/Cancel/Help across the bottom. Source: https://guidebookgallery.org/screenshots/win2000pro (startupshutdown/shutdownwindow/win2000pro.png)
- The "Log Off Windows" confirmation is a plain 2-button (Yes/No) message-box-style dialog with a small keyhole/user icon, no Cancel button, sized far smaller (288×123px capture) than the Shut Down sheet. Source: https://guidebookgallery.org/screenshots/win2000pro (startupshutdown/logout/win2000pro.png)
- The Run dialog is a small fixed dialog with a folder/hourglass-style icon, instructional text, an "Open:" combo box with MRU dropdown, and OK/Cancel/Browse... across the bottom; captured at 347×179px. Source: https://guidebookgallery.org/screenshots/win2000pro (system/features/run/win2000pro.png)
- Control Panel in Windows 2000 supports the Explorer "web view": a left rail with a bold "Control Panel" heading, one-line description text, and blue hyperlink-style "Windows Update"/"Windows 2000 Support" shortcuts, alongside a right-hand icon grid of applets in a standard Explorer toolbar/address-bar chrome. Source: https://guidebookgallery.org/screenshots/win2000pro (settings/menu/win2000pro.png)
- About boxes (About Windows / About Notepad) share one template: large diagonal Windows-flag/Microsoft banner top, product name + "Version 5.0 (Build 2195)" + copyright line, licensed-to name/org block, "Physical memory available to Windows: N KB" line, single OK button bottom-right. Source: https://guidebookgallery.org/screenshots/win2000pro (interface/dialogs/aboutapplication and aboutgui)
- The standard Color picker dialog exposes Basic colors (a 6×8 swatch grid), a Custom colors row (2×8 empty swatches), a full hue/saturation gradient square plus a vertical luminosity slider, numeric Hue/Sat/Lum and Red/Green/Blue fields, and a "Define Custom Colors >>" disclosure button. Source: https://guidebookgallery.org/screenshots/win2000pro (interface/dialogs/colourselector/win2000pro.png)
- The standard Font dialog uses the classic 3-list layout (Font name / Font style / Size) with independent scrollable list boxes above a "Sample" preview box, plus Effects checkboxes (Strikeout, Underline), a Color combo, and a Script combo — confirmed from a live capture showing "This is an OpenType font..." helper text. Source: https://guidebookgallery.org/screenshots/win2000pro (interface/dialogs/font/win2000pro.png)

## Measurements

| Element | Value | Confidence | Source |
|---|---|---|---|
| Active title bar gradient start color | `#0A246A` (RGB 10,36,106) | measured (exact) | https://guidebookgallery.org/screenshots/win2000pro |
| Active title bar gradient end color (pre-buttons) | `~#A3C7ED` (RGB 163,199,237) | measured, ~ (occluded by caption buttons before reaching true endpoint) | https://guidebookgallery.org/screenshots/win2000pro |
| Dialog/button face color | `#D4D0C8` (RGB 212,208,200) | measured (exact, consistent across 5+ screenshots) | https://guidebookgallery.org/screenshots/win2000pro |
| Caption bar height (incl. top border) | ~21px @ captured scale | measured, ~ | https://guidebookgallery.org/screenshots/win2000pro (aboutgui) |
| MB_ICON* stock icon size | ~32×32px | ~ (visual estimate from message box crop) | https://guidebookgallery.org/screenshots/win2000pro (misc/wrongpassword) |
| Wizard97 exterior page template | 317×193 dialog units | spec (exact) | https://learn.microsoft.com/en-us/windows/win32/controls/wizards |
| Wizard97 interior page template | 317×143 dialog units | spec (exact) | https://learn.microsoft.com/en-us/windows/win32/controls/wizards |
| Wizard97 header title font | 12pt Verdana Bold | spec (exact) | https://learn.microsoft.com/en-us/windows/win32/controls/wizards |
| Win32 property page (IDD_PROPPAGE_LARGE) | 252×218 dialog units, 7 DLU margin | spec (exact, but doc is MMC 3.0-era) | https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bb226808(v=vs.85) |
| Display Properties sheet capture size | 404×448px | measured (screenshot crop size, not authoritative window size) | https://guidebookgallery.org/screenshots/win2000pro |
| Open dialog capture size | 563×347px | measured (screenshot crop size) | https://guidebookgallery.org/screenshots/win2000pro |
| Run dialog capture size | 347×179px | measured (screenshot crop size) | https://guidebookgallery.org/screenshots/win2000pro |
| Shut Down dialog capture size | 417×269px | measured (screenshot crop size) | https://guidebookgallery.org/screenshots/win2000pro |
| Log Off dialog capture size | 288×123px | measured (screenshot crop size) | https://guidebookgallery.org/screenshots/win2000pro |
| About dialog capture size | 419×329px (329px in App variant), 326px (Wikimedia VirtualBox capture) | measured, minor variance between sources | https://guidebookgallery.org/screenshots/win2000pro ; https://commons.wikimedia.org/wiki/File:ABOUT_Windows_2000_DE,_VirtualBox_20230707.png |
| Places Bar entries | 5 (History, Desktop, My Documents, My Computer, My Network Places) | measured (directly counted/read in screenshot) | https://guidebookgallery.org/screenshots/win2000pro (openfile) |

## Colors

| Role | Hex/RGB | Source |
|---|---|---|
| Active title bar (gradient start) | `#0A246A` | https://guidebookgallery.org/screenshots/win2000pro |
| Active title bar (gradient end, approx.) | `~#A3C7ED` | https://guidebookgallery.org/screenshots/win2000pro |
| Dialog / button face ("3D Face") | `#D4D0C8` | https://guidebookgallery.org/screenshots/win2000pro |
| Window/list background (white controls) | `#FFFFFF` | https://guidebookgallery.org/screenshots/win2000pro |
| Default desktop teal-blue (Windows Standard scheme, unverified secondary source) | `~#3B6EA5` | https://gist.github.com/Xarkam/0ae82779e25647700b1b7bfd59dee71c |
| MB_ICONEXCLAMATION / MB_ICONWARNING glyph | yellow triangle, black "!" (no hex extracted) | https://guidebookgallery.org/screenshots/win2000pro (misc/wrongpassword) |

## Behavior notes

- `MB_APPLMODAL` is the MessageBox default modality: it blocks only the owner window's own thread hierarchy, not the whole system; `MB_SYSTEMMODAL` adds `WS_EX_TOPMOST` and is reserved for serious/damaging errors (e.g. out-of-memory); `MB_TASKMODAL` blocks all top-level windows of the calling thread when no owner `hWnd` is available. Source: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-messagebox
- If a message box has no Cancel button, pressing ESC does nothing unless an OK button is present, in which case ESC acts as OK. Source: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-messagebox
- Property sheet Cancel is defined to behave exactly like clicking the title-bar Close button — it does not undo changes already committed via a prior Apply, which is a known source of user confusion Microsoft explicitly flags in its own guidance. Source: https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bb226808(v=vs.85)
- When multiple objects with differing property values are selected and a shared property sheet is shown, controls bound to differing values should show a "mixed value" state rather than an arbitrary single value. Source: https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bb226808(v=vs.85)
- Wizard page navigation is driven by `WM_NOTIFY` with `PSN_SETACTIVE` / `PSN_WIZBACK` / `PSN_WIZNEXT` / `PSN_WIZFINISH` notification codes; a page can veto Back/Next by setting `DWL_MSGRESULT` to -1, or force-jump to an arbitrary page by setting it to that page's dialog resource ID. Source: https://learn.microsoft.com/en-us/windows/win32/controls/wizards
- Button state (which of Back/Next/Finish are enabled) is sticky across page navigation — once set via `PSM_SETWIZBUTTONS` it persists until explicitly changed again, so well-behaved wizard pages re-assert their own button state every time they receive `PSN_SETACTIVE`. Source: https://learn.microsoft.com/en-us/windows/win32/controls/wizards
- Windows 2000 shipped a known bug where `MessageBox` with `MB_CANCELTRYCONTINUE | MB_SERVICE_NOTIFICATION` could return an incorrect result value. Source: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-messagebox (via search-indexed KB reference)
- Selecting a Scheme from the Appearance tab's Scheme dropdown is a one-shot bulk operation: it simultaneously reassigns the background color, title bar colors/gradient, window border color, and default text colors — individual per-Item edits made afterward do not "detach" from the scheme name in a way documented here (open question below). Source: https://www.informit.com/articles/article.aspx?p=411736&seqNum=159

## Open questions

- Exact pixel dimensions of the Wizard97 watermark bitmap (commonly cited elsewhere as 164×312px) and header bitmap (commonly cited as ~49×63px) could not be confirmed from a primary Microsoft source in this session — the canonical "Wizard 97" Platform SDK spec document (`ms738248`) is archived as a stub/synopsis-only page with the actual body content no longer present at the URL. Needs a Wayback Machine capture of the full spec or a comctl32 resource dump from a legitimately owned SDK to pin down.
- Could not find a genuine Windows 2000 screenshot of the Appearance tab itself (the per-element Item/Font/Size/Color editor) in any of the public galleries checked this session — GUIdebook's "Appearance" catalog entry for Win2000 Pro actually points to the Background tab (a likely mislabeling on their end), and no equivalent was found on Wikimedia Commons or WinWorld in the time available. All Appearance-tab facts here are from prose descriptions (InformIT), not a verified screenshot.
- The exact "Windows Standard" vs. "Windows Classic" beige/gray and Tahoma-default claim rests on a single BetaWiki search-result excerpt; BetaWiki's live page returned HTTP 403 to direct fetch attempts, so the claim is unverified against the primary page text and should be treated cautiously.
- Could not locate a public screenshot of a genuine end-user-facing Wizard97 wizard (e.g. Add/Remove Hardware Wizard, Network Identification Wizard) with the classic left-side watermark exterior page — the one confirmed Wizard97 interior-page example found this session is from the Setup GUI phase, not a post-install Control Panel wizard.
- Exact dimensions/behavior of the "What's This?" (?) help-icon button that some — but not all — Win2000 dialogs show in the caption bar next to the close box were not pinned down (which dialog classes get it vs. don't).
- No sound-scheme (`.wav` mapping — SystemExclamation, SystemHand, SystemAsterisk, SystemQuestion, SystemDefault) event names were verified against a primary Win2000 source this session; the sounds-to-icon mapping is inferred from general MessageBox documentation, not confirmed registry/Control Panel Sounds tab evidence.

## Images

| Filename | Source URL | Shows |
|---|---|---|
| about-application-dialog.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/interface/dialogs/aboutapplication/win2000pro.png) | About Notepad dialog (shared About-box template) |
| about-windows-dialog.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/interface/dialogs/aboutgui/win2000pro.png) | About Windows dialog, used for title bar/face color sampling |
| about-windows-dialog-vbox.png | https://commons.wikimedia.org/wiki/File:ABOUT_Windows_2000_DE,_VirtualBox_20230707.png | About Windows dialog (German), real VM capture, public domain, banner cropped |
| color-selector-dialog.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/interface/dialogs/colourselector/win2000pro.png) | Standard Color picker dialog |
| font-selection-dialog.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/interface/dialogs/font/win2000pro.png) | Standard Font dialog |
| open-file-common-dialog.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/interface/dialogs/openfile/win2000pro.png) | Common Open dialog with 5-icon Places Bar |
| wrong-password-messagebox.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/interface/misc/wrongpassword/win2000pro.png) | Message box, MB_ICONEXCLAMATION, single OK button |
| display-properties-background-tab.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/settings/appearance/win2000pro-1-1.png) | Display Properties sheet, Background tab selected (note: GUIdebook mislabels this file as "appearance") |
| display-properties-settings-tab.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/settings/display/win2000pro.png) | Display Properties sheet, Settings tab — confirms 6-tab order |
| display-properties-screensaver-tab.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/settings/screensaver/win2000pro.png) | Display Properties sheet, Screen Saver tab |
| control-panel-menu.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/settings/menu/win2000pro.png) | Control Panel in Explorer "web view" |
| run-dialog.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/system/features/run/win2000pro.png) | Run dialog |
| log-off-windows-dialog.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/startupshutdown/logout/win2000pro.png) | Log Off Windows confirmation (Yes/No message box) |
| shut-down-windows-dialog.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/startupshutdown/shutdownwindow/win2000pro.png) | Shut Down Windows dialog with action dropdown |
| shutting-down-screen.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/startupshutdown/shuttingdown/win2000pro.png) | Full-screen "shutting down" transition screen |
| accessibility-properties-sheet.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/settings/accessibility/win2000pro-1-1.png) | Accessibility Options property sheet (tab layout example) |
| mouse-properties-sheet.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/settings/mouse/win2000pro-1-1.png) | Mouse Properties sheet (tab layout example) |
| date-time-properties-sheet.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/settings/timedate/win2000pro-1-1.png) | Date/Time Properties sheet (tab layout example) |
| power-options-properties-sheet.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/settings/power/win2000pro-1-1.png) | Power Options property sheet (tab layout example) |
| wizard97-interior-page-example.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/installation/copying/win2000pro.png) | Windows 2000 Setup GUI phase — genuine Wizard97 interior page (header band + Back/Next) |

## Sources

- https://learn.microsoft.com/en-us/windows/win32/controls/wizards
- https://learn.microsoft.com/en-us/previous-versions/windows/desktop/mmc/adding-watermarks-to-wizard-97-pages
- https://learn.microsoft.com/en-us/previous-versions/ms738248(v=vs.85)
- https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bb226808(v=vs.85)
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-messagebox
- https://devblogs.microsoft.com/oldnewthing/?p=19773/
- https://guidebookgallery.org/screenshots/win2000pro
- https://guidebookgallery.org/screenshots/shutdownwindow
- https://commons.wikimedia.org/wiki/File:ABOUT_Windows_2000_DE,_VirtualBox_20230707.png
- https://commons.wikimedia.org/wiki/File:Message_box_Windows_2000_DE,_7-ZIP_Archivtest,_VirtualBox.png
- https://www.informit.com/articles/article.aspx?p=411736&seqNum=159
- https://www.informit.com/articles/article.aspx?p=411736&seqNum=160
- https://betawiki.net/wiki/Windows_Classic
- https://gist.github.com/Xarkam/0ae82779e25647700b1b7bfd59dee71c
