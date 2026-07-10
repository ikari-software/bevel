# Typography, Metrics & Effects — Win2000 reference notes

## Overview

Windows 2000's classic UI typography is a transitional moment: it is the first mainstream Windows release to ship **Tahoma** as the intended system font, replacing the bitmap **MS Sans Serif** used since Windows 3.1/95, but the transition was implemented inconsistently across Microsoft's own teams, so both fonts appear side-by-side depending on which dialog you're looking at. Font selection is indirected through the logical font names `MS Shell Dlg` and `MS Shell Dlg 2`, which the OS resolves via registry font-substitution rather than being hardcoded, and the resolved size for nearly all chrome text (menus, tooltips, message boxes, icon labels) is 8 point. Windows 2000 has no ClearType (that arrived in XP); its only text rendering toggle is the classic grayscale "Smooth edges of screen fonts" checkbox, which ships **off** by default. The Effects tab is also where menu/tooltip animation lives: Windows 2000 introduces a "Use transition effects for menus and tooltips" option defaulted **on**, with **Fade** as the default transition style (Windows 95/98 had no such setting). Bold is used sparingly and almost exclusively in Wizard97-style wizard headers, which have their own hardcoded typography spec independent of the user's theme.

## Key visual facts

- Windows 2000 changed the default UI font from Microsoft Sans Serif to Tahoma, but "not every team involved in developing Windows got the memo," so the change was inconsistent across dialogs and persisted as a mixed state through XP and into early Vista builds. [stealthpuppy.com](https://stealthpuppy.com/ui-consistency-and-microsoft-sans-serif/)
- Official Microsoft guidance for developers: "To target Windows XP and Windows 2000, use the 8 point MS Shell Dlg 2 pseudo font, which maps to Tahoma. To target earlier versions of Windows, use 8 point MS Shell Dlg pseudo font, which maps to Tahoma on Windows 2000 and Windows XP, and to MS Sans Serif on Windows 95, Windows 98, Windows Millennium Edition, and Windows NT 4.0." [learn.microsoft.com/uxguide/vis-fonts](https://learn.microsoft.com/en-us/windows/win32/uxguide/vis-fonts)
- `MS Shell Dlg 2` was introduced in Windows 2000 specifically "to support the look that was introduced with Windows 2000," and always resolves to Tahoma regardless of locale (unlike `MS Shell Dlg`, which is locale-dependent). [learn.microsoft.com/win32/intl/using-ms-shell-dlg](https://learn.microsoft.com/en-us/windows/win32/intl/using-ms-shell-dlg-and-ms-shell-dlg-2)
- `MS Shell Dlg` resolves to Microsoft Sans Serif for non-Japanese installs, MS UI Gothic for Japanese, Gulim for Korean, SimSun for Simplified Chinese, PMingLiU for Traditional Chinese, etc. — it exists to route dialog text through a font that actually covers the install locale's character set. [learn.microsoft.com/win32/intl/using-ms-shell-dlg](https://learn.microsoft.com/en-us/windows/win32/intl/using-ms-shell-dlg-and-ms-shell-dlg-2)
- Both logical font names are resolved via `HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\FontSubstitutes` — theming tools of the era hex-edited/registry-edited this key to force Tahoma or (on XP) Segoe UI, sometimes causing dialog text to wrap incorrectly because layouts were tuned for the original metrics. [stealthpuppy.com](https://stealthpuppy.com/ui-consistency-and-microsoft-sans-serif/)
- ClearType is not available in Windows 2000 — it is a Windows XP-era feature. Windows 2000's font smoothing is the older, non-subpixel grayscale-only method. [Experts Exchange](https://www.experts-exchange.com/questions/21960505/Is-there-a-way-to-have-Clear-Type-in-Windows-2000.html) / [AnandTech forums](https://forums.anandtech.com/threads/is-cleartype-available-for-windows-2000.748741/)
- Confirmed directly from a Windows 2000 Pro Display Properties → Effects tab screenshot: **"Smooth edges of screen fonts" ships unchecked (off) by default.** [GUIdebook](https://guidebookgallery.org/screenshots/win2000pro) (view 4, Effects tab)
- Same screenshot confirms **"Use transition effects for menus and tooltips" ships checked (on) by default**, with the adjacent dropdown set to **"Fade effect"** (the alternative being a scroll/slide effect). [GUIdebook](https://guidebookgallery.org/screenshots/win2000pro) (view 4, Effects tab)
- toastytech's own Windows 2000 review, captioned live: "Windows 2000 has several new special effects. Menus fade in and the mouse cursor has a shadow (both are optional)." — confirming fade-in menus and cursor shadow were both new, and both togglable, in this release. [toastytech.com/guis/w2k.html](http://toastytech.com/guis/w2k.html)
- The Effects tab also ships with, by default: "Use large icons" **off**, "Show icons using all possible colours" **on**, "Show window contents while dragging" **off**, "Hide keyboard navigation indicators until I use the Alt key" **on**. [GUIdebook](https://guidebookgallery.org/screenshots/win2000pro) (view 4, Effects tab)
- The Wizard97 UI spec — the header/body typography convention used across Windows 2000's own wizards (Found New Hardware, Add Printer, Network setup, etc.) — is explicitly scoped: "This specification is to be used for any wizard that will need to function on computers running Windows 2000 or later, including wizards in Internet Explorer 5... pertains to comctl32.dll, file version 5.00 ... that comes with IE 5 or NT 2000." [learn.microsoft.com/previous-versions/ms738248](https://learn.microsoft.com/en-us/previous-versions/ms738248(v=vs.85))
- Wizard97 typography table: Welcome/Completion page titles use **Verdana Bold 12pt**; interior-page titles use **MS Sans Serif Bold 10pt**; interior-page subtitles and all body text use **MS Shell Dialog 8pt**. Outside of that table, general UI guidance is explicit: "Use the default system font. Do not use font styles such as bold and italic." [learn.microsoft.com/previous-versions/bb246427](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bb246427(v=vs.85))
- Windows 2000's Mouse Properties dialog ships with **"Double-click to open an item (single-click to select)"** selected by default, not the single-click Web-style option — confirmed directly from the Buttons tab screenshot. [GUIdebook](https://guidebookgallery.org/screenshots/mouse)
- Folder Options exposes independent underline sub-choices once single-click/Web style is turned on: "Underline icon titles consistent with my browser" vs. "Underline icon titles only when I point at them" — these are off/moot under the classic double-click default. [Tom's Hardware forum](https://forums.tomshardware.com/threads/single-click-to-open-an-item-no-underline-possible-reg-hack.1311981/)
- The Font selection common dialog in Windows 2000 explicitly labels OpenType fonts: sample text reads "This is an OpenType font. This same font will be used on both your printer and your screen." — confirmed directly from a screenshot of the dialog. [GUIdebook](https://guidebookgallery.org/screenshots/font)
- Default tooltip background color resolves from system color `COLOR_INFOBK`, registry-backed at `HKEY_CURRENT_USER\Control Panel\Colors\InfoWindow`, defaulting to `RGB(255,255,225)` (pale yellow) — a value that predates Windows 2000 (Win95-era) and persists through the whole classic-theme lineage. [Mozilla Bugzilla #1299906](https://bugzilla.mozilla.org/show_bug.cgi?id=1299906)
- The menu show/hover delay exists specifically so a user navigating a nested flyout menu tree doesn't collapse the whole tree by moving the mouse slightly diagonally instead of straight into the next flyout. [Raymond Chen, The Old New Thing](https://devblogs.microsoft.com/oldnewthing/20080619-00/?p=21903)
- `SPI_GETMOUSEHOVERTIME` / registry `MouseHoverTime` (`HKEY_CURRENT_USER\Control Panel\Mouse`) has a long-standing Win32 default of 400 ms — the same value used for menu drop-down delay — governing how long the pointer must rest before a hover/tooltip action fires. [learn.microsoft.com/TRACKMOUSEEVENT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-trackmouseevent)
- Windows 2000's default sound scheme uses **"Windows Logon Sound.wav"** and **"Windows Logoff Sound.wav"** for session start/end — distinct, newly-composed cues replacing the Windows 95/98-era "The Microsoft Sound.wav" startup jingle (which is also still present in the file set for legacy/back-compat reasons). [winsounds.com](https://winsounds.com/windows-2000-sound-files/)
- The Sounds Properties "Sound Events" list is a two-level tree: a top-level **Windows** heading groups core shell events (opening/closing programs, minimize/maximize, session start/end), and a second top-level **Windows Explorer** heading groups additional shell-specific events, with further headings appearing per installed application. [flylib.com — Running Microsoft Windows 2000 Professional](https://flylib.com/books/en/3.229.1.43/1/)

## Measurements

| Element | Value | Confidence | Source |
|---|---|---|---|
| System UI font (menus, tooltips, message boxes, icon labels) | Tahoma, 8pt, via `MS Shell Dlg 2` | high | [MS Learn uxguide/vis-fonts](https://learn.microsoft.com/en-us/windows/win32/uxguide/vis-fonts) |
| Legacy-compat logical font | `MS Shell Dlg`, 8pt → Tahoma on Win2000/XP, MS Sans Serif on 95/98/ME/NT4 | high | [MS Learn uxguide/vis-fonts](https://learn.microsoft.com/en-us/windows/win32/uxguide/vis-fonts) |
| Wizard97 welcome/completion page title | Verdana Bold, 12pt | high | [MS Learn bb246427](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bb246427(v=vs.85)) |
| Wizard97 interior page title | MS Sans Serif Bold, 10pt | high | [MS Learn bb246427](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bb246427(v=vs.85)) |
| Wizard97 interior subtitle / body text | MS Shell Dialog, 8pt, not bold | high | [MS Learn bb246427](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bb246427(v=vs.85)) |
| Wizard97 orientation pane width | 160px fixed | medium (spec is Vista-refresh doc but pane concept carries from Win2000-era wizard 97) | [MS Learn bb246463](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bb246463(v=vs.85)) |
| "Item" font size — Title Bar / Palette Title | ~11pt | low — unclear if this is Win2000 classic default or XP Luna-era default; unverified for Win2000 specifically | [MS Q&A thread](https://learn.microsoft.com/en-us/answers/questions/2610596/what-are-the-default-font-sizes-for-title-bars-men) |
| "Item" font size — Message Box / Icon / ToolTip | ~9pt | low — same caveat as above | [MS Q&A thread](https://learn.microsoft.com/en-us/answers/questions/2610596/what-are-the-default-font-sizes-for-title-bars-men) |
| Tooltip hover delay (`SPI_GETMOUSEHOVERTIME`) | 400 ms | medium — longstanding Win32 default, not screenshot-verified for Win2000 specifically | [MS Learn TRACKMOUSEEVENT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-trackmouseevent) |
| Caret (text cursor) blink rate, Windows 2000 | ~1000 ms full cycle | low — single blog source, conflicts with the ~1060ms figure cited for XP/2003 and 530ms cited for later Windows | [redcircle.blog](https://redcircle.blog/2008/02/08/cursor-blinking/) |
| Double-click speed default | ~500 ms (registry `DoubleClickSpeed`, range 200–900) | low — sourced from modern-Windows discussions, not Win2000-specific; slider position in the Win2000 screenshot is consistent with a mid-range default | screenshot: [GUIdebook mouse](https://guidebookgallery.org/screenshots/mouse); value: general forum consensus |
| Appearance tab "Item" dropdown element count | 17 named elements (3D Objects, Active/Inactive Title Bar, Active/Inactive Window Border, Application Background, Caption Buttons, Desktop, Icon, Icon Spacing H/V, Menu, Message Box, Palette Title, Selected Items, ToolTip, Window) | medium — confirmed list is from an XP-era KB but the classic Appearance tab item set is shared across Win2000/XP classic mode | [KB 310543 mirror](https://ftp.zx.net.nz/pub/archive/ftp.microsoft.com/MISC/KB/en-us/310/543.HTM) |

## Colors

| Role | Hex / RGB | Source |
|---|---|---|
| Tooltip background (`COLOR_INFOBK` / registry `InfoWindow`) | `#FFFFE1` / RGB(255,255,225) | [Mozilla Bugzilla #1299906](https://bugzilla.mozilla.org/show_bug.cgi?id=1299906) |
| Wizard97 welcome/completion title text | Default system text color (no override specified) | [MS Learn bb246427](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bb246427(v=vs.85)) |

## Behavior notes

- Menu/tooltip transitions: the Effects tab's "Use transition effects for menus and tooltips" checkbox drives a single shared setting for **both** menus and tooltips — there is no separate toggle for the two — and the selectable styles are Fade vs. Scroll, with Fade being the shipped default. [GUIdebook Effects tab screenshot](https://guidebookgallery.org/screenshots/win2000pro)
- Font smoothing ("Smooth edges of screen fonts") is a simple grayscale anti-aliasing toggle in Win2000, off by default; there is no ClearType/subpixel option in the stock OS. [Experts Exchange](https://www.experts-exchange.com/questions/21960505/Is-there-a-way-to-have-Clear-Type-in-Windows-2000.html)
- Bold text usage is intentionally rare and reserved almost entirely for Wizard97 wizard titles; the general guidance explicitly discourages bold/italic elsewhere to preserve accessibility (users who override system fonts/colors for low vision must still get consistent, non-decorated text). [MS Learn bb246427](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bb246427(v=vs.85))
- Double-click remains the shipped default file-open affordance in Windows 2000's Mouse Properties, even though Windows 2000 folder views carry over "Web style" visual characteristics (Active Desktop integration) from the IE4/Windows 98 era; single-click-to-open with underlined titles is an opt-in, not a default. [informit.com](https://www.informit.com/articles/article.aspx?p=411736&seqNum=66), [GUIdebook mouse screenshot](https://guidebookgallery.org/screenshots/mouse)
- The menu/tooltip hover delay is deliberately generous to tolerate imperfect diagonal mouse movement through nested flyouts — a UX rationale documented after the fact by a Microsoft engineer who worked on the Windows shell. [Raymond Chen, Old New Thing](https://devblogs.microsoft.com/oldnewthing/20080619-00/?p=21903)
- Windows 2000 retained the legacy "The Microsoft Sound.wav" file in its sound asset set even though the default scheme's actual logon/logoff cues were re-composed as dedicated "Windows Logon Sound.wav" / "Windows Logoff Sound.wav" files — a scheme-level change from Windows 95/98's shared jingle approach. [winsounds.com](https://winsounds.com/windows-2000-sound-files/)

## Open questions

- Exact default point size and bold/regular weight for the **Title Bar** item specifically in Windows 2000's "Windows Standard" classic scheme could not be confirmed from a Win2000-specific primary source — the only concrete numbers found (11pt title bar / 9pt everything else) come from a 2014 Microsoft Q&A answer that does not specify OS version or theme (Luna vs. Classic), so it may reflect XP rather than Win2000.
- Could not verify whether Windows 2000's Appearance tab exposes independently, per-item Bold/Italic toggles in the Font style buttons next to each Item (visible as grayed-out "B" / "i" buttons in the Appearance screenshot when Desktop is selected) — need a screenshot with an actual text-bearing item (Menu, ToolTip, Title Bar) selected to see the enabled state and current weight.
- Caret blink rate default for Windows 2000 (~1000ms per the one source found) is not corroborated by a second independent source; the commonly-cited 530ms figure appears to be a later-Windows default, not Win2000's.
- Could not find a Win2000-specific primary source (KB, screenshot, or archived MSDN doc) for the exact double-click speed default in milliseconds — only general Win32 registry documentation from later eras.
- Could not locate a public archived copy of the original 1999 "Microsoft Windows User Experience" book/guidelines (the direct successor to "The Windows Interface Guidelines for Software Design") to cite Win2000-era guidance verbatim; only the Windows 7-era uxguide rewrite (which retroactively documents the Win2000/XP MS Shell Dlg 2 targeting rule) was accessible.
- No public screenshot of the Windows 2000 "Sounds and Multimedia Properties" dialog itself (Sound Events tree UI) was found via the permitted gallery sources — the sound-event facts above come from a book excerpt (flylib) and a sound-file archive (winsounds.com), not a screenshot.

## Images

| Filename | Source URL | Shows |
|---|---|---|
| `display-properties-appearance.png` | [GUIdebook — win2000pro Appearance](https://guidebookgallery.org/screenshots/win2000pro) | Display Properties → Appearance tab; Scheme dropdown = "Windows Standard"; Item/Font/Size/Color controls (Desktop item selected, font controls grayed out) |
| `display-properties-effects.png` | [GUIdebook — win2000pro Effects](https://guidebookgallery.org/screenshots/win2000pro) | Display Properties → Effects tab; shows all default checkbox states: transition effects on/Fade, font smoothing off, large icons off, all-colors icons on, drag contents off, hide keyboard indicators on |
| `font-selection-dialog.png` | [GUIdebook — font dialog](https://guidebookgallery.org/screenshots/font) | Common Font selection dialog (Arial shown), OpenType label text, Effects (Strikeout/Underline/Color), Script dropdown |
| `mouse-properties.png` | [GUIdebook — mouse](https://guidebookgallery.org/screenshots/mouse) | Mouse Properties → Buttons tab; default "Double-click to open" radio selected; double-click speed slider at mid position |
| `start-menu.png` | [toastytech.com/guis/w2k.html](http://toastytech.com/guis/w2k.html) | Start Menu open to Programs → Accessories flyout; live rendering of Tahoma-based menu typography, white-on-blue "Windows 2000 Professional" side banner |
| `menu-fade-transition-inprogress.png` | [toastytech.com/guis/w2k.html](http://toastytech.com/guis/w2k.html) | Paint's File menu caught mid-fade-in transition — direct visual evidence of the default Fade effect in motion |

## Sources

- https://stealthpuppy.com/ui-consistency-and-microsoft-sans-serif/
- https://learn.microsoft.com/en-us/windows/win32/uxguide/vis-fonts
- https://learn.microsoft.com/en-us/windows/win32/intl/using-ms-shell-dlg-and-ms-shell-dlg-2
- https://learn.microsoft.com/en-us/previous-versions/ms738248(v=vs.85)
- https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bb246427(v=vs.85)
- https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bb246463(v=vs.85)
- https://learn.microsoft.com/en-us/answers/questions/2610596/what-are-the-default-font-sizes-for-title-bars-men
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-trackmouseevent
- https://ftp.zx.net.nz/pub/archive/ftp.microsoft.com/MISC/KB/en-us/310/543.HTM
- https://www.experts-exchange.com/questions/21960505/Is-there-a-way-to-have-Clear-Type-in-Windows-2000.html
- https://forums.anandtech.com/threads/is-cleartype-available-for-windows-2000.748741/
- https://guidebookgallery.org/screenshots/win2000pro
- https://guidebookgallery.org/screenshots/mouse
- https://guidebookgallery.org/screenshots/font
- http://toastytech.com/guis/w2k.html
- https://forums.tomshardware.com/threads/single-click-to-open-an-item-no-underline-possible-reg-hack.1311981/
- https://www.informit.com/articles/article.aspx?p=411736&seqNum=66
- https://bugzilla.mozilla.org/show_bug.cgi?id=1299906
- https://devblogs.microsoft.com/oldnewthing/20080619-00/?p=21903
- https://winsounds.com/windows-2000-sound-files/
- https://flylib.com/books/en/3.229.1.43/1/
- https://redcircle.blog/2008/02/08/cursor-blinking/
