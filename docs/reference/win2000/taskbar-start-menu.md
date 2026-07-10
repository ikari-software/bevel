# Taskbar & Start Menu — Win2000 reference notes

## Overview
The Windows 2000 taskbar is a direct visual continuation of the Windows 95/98/Me taskbar: a single 28px-tall raised gray (`#D4D0C8`) strip docked to a screen edge (default: bottom), containing the Start button, an optional Quick Launch band with rebar grippers, a flexible row of task buttons, and a right-hand notification area (clock + tray icons) inset in a sunken well. There is no task grouping, no translucency, and no XP-style blue theme — everything renders in the classic 3D "Chicago" bevel style (raised = light-top/dark-bottom edges, sunken = inverted). The Start menu is the classic single-column cascading menu with a dark navy vertical sidebar banner reading "Windows 2000 Professional" (or the relevant edition name) rotated bottom-to-top; it introduces "Personalized Menus" (IntelliMenus), which hide rarely-used items behind a chevron, but it does **not** have the two-column, pinned "Most Frequently Used programs" layout that Windows XP introduced — that MFU list is absent in 2000. All measurements below were taken by direct pixel inspection of unscaled classic-theme screenshots (800×600 and 640×480, 96 DPI / small fonts), since taskbar chrome renders at fixed physical pixel sizes independent of desktop resolution.

## Key visual facts
- Taskbar default height is **28px** at 96 DPI/small-fonts — measured identically (600−572=28) on an 800×600 screenshot and (480−452=28) on an independently-sourced 640×480 screenshot of the same UI, confirming the metric is a fixed pixel value, not resolution-relative. https://guidebookgallery.org/screenshots/win2000pro ; http://toastytech.com/guis/w2k3.html
- Taskbar face color is the classic `BTNFACE` gray, measured as RGB (212,208,200) = `#D4D0C8`, consistent across all sampled screenshots. https://guidebookgallery.org/screenshots/win2000pro
- Taskbar top edge is a 2-line raised bevel: 1px white highlight (`#FFFFFF`) immediately followed by flat face color, i.e. the standard "raised" 3D border rendering used throughout Win2000 classic chrome. https://guidebookgallery.org/screenshots/win2000pro
- Start button spans the full taskbar height minus ~2–4px top/bottom margin and is **not fixed-width** — it auto-sizes to fit the Windows flag icon + "Start" label; measured ~144–150px wide (icon ~9–21px, "Start" text glyphs ending ~x=135, button border at ~x=144) in the sampled screenshots. Width is directly a function of the rendered label/font, so it will vary with font/DPI settings. https://guidebookgallery.org/screenshots/win2000pro ; http://toastytech.com/guis/w2k3.html
- Start button icon is the classic flat 4-quadrant Windows flag (no wavy/gradient XP flag), immediately followed by the "Start" label in the system UI font (bold). http://toastytech.com/guis/w2k3.html
- Immediately right of the Start button is a two-line vertical "gripper" (rebar drag handle) marking the start of the Quick Launch toolbar band, seen as two parallel dark/light column pairs a few px apart. https://guidebookgallery.org/screenshots/win2000pro
- Default Quick Launch toolbar ships with 4 icons: Internet Explorer, Outlook Express, Show Desktop, and View Channels — the latter two are special shell items, not plain shortcuts, stored in `...\Application Data\Microsoft\Internet Explorer\Quick Launch`. https://www.itprotoday.com/systems-management/how-can-i-change-icons-quick-launch-toolbar
- Task buttons are separated by thin 2px etched divider lines (dark line + light line, not full box borders) when in the normal/unpressed state; a button for the foreground/active window renders with a visible sunken 3D border instead. Directly observed in a 3-button strip (Control Panel/Calculator/NetMeeting) where "Control Panel" (foreground) shows a pressed/sunken box while the other two are flat with only divider lines. https://guidebookgallery.org/screenshots/win2000pro (system/managers/running crop)
- Task buttons have variable width (auto-sized to icon+label, truncated with ellipsis when the taskbar is crowded) — no fixed per-button width; in the 800px-wide sample, three buttons ("On-Screen Keyboard", "Administrative Tools", "Computer Management") each measured ~163px. https://guidebookgallery.org/screenshots/win2000pro
- Notification area (tray) icons sit inside a sunken well: measured a 1px dark top/left edge and implied light bottom/right edge bracketing each icon region, e.g. the speaker icon's well spanned x≈704–718 in an 800px-wide sample. https://guidebookgallery.org/screenshots/win2000pro
- The clock is right-aligned inside the notification area and shows time only (no seconds, no date) by default, e.g. "1:15 PM", "10:29 PM", "9:53 PM" across multiple samples — 12-hour format with AM/PM, no leading zero on the hour. https://guidebookgallery.org/screenshots/win2000pro ; http://toastytech.com/guis/w2k3.html
- Start menu items are **20px tall** per row at 640×480/96 DPI, measured from repeating icon-row boundaries in a Programs submenu (rows recur every 20px: y=194, 214, 234...). http://toastytech.com/guis/w2k3.html
- The Start menu's left sidebar banner is dark navy blue, measured background ≈ RGB(0,0,132) = `#000084`, running the full height of the menu with white rotated text reading the OS edition name (e.g. "Windows 2000 Professional"). http://toastytech.com/guis/w2k3.html
- Sidebar banner width measured at ~26px at 640×480 (icon-column offset before menu items begin). http://toastytech.com/guis/w2k3.html
- Menu item highlight/selection color measured as RGB(10,36,106) = `#0A246A` (classic `COLOR_HIGHLIGHT` blue), used both for the selected Start menu item and the "Windows 2000 Professional" gradient in some captures. https://guidebookgallery.org/screenshots/win2000pro
- Submenus (e.g. Programs → Accessibility) open as flyout cascades to the right, each with its own icon column, matching Win9x-style single-column cascading menus — no two-column "pinned MFU + All Programs" layout (that's XP-only). http://toastytech.com/guis/w2k3.html
- Items with further submenus show a small black right-pointing triangle arrow glyph at the row's right edge (e.g. "Programs", "Accessibility", "Settings"). http://toastytech.com/guis/w2k3.html
- Separator lines appear as thin horizontal etched (sunken) rules between logical groups in the Start menu, e.g. between "Windows Update"/"Windows 2000 Support" links and "Programs"/"Documents"/etc, and again above "Shut Down". https://guidebookgallery.org/screenshots/win2000pro
- Personalized ("Intelli") Menus are on by default: Programs submenu items not recently used are hidden beneath a downward chevron/arrow at the bottom of the list; clicking it (or hovering, after a delay) expands the full list. A balloon-tip explains the feature the first time it's used. http://toastytech.com/guis/w2k3.html
- The Taskbar & Start Menu Properties dialog exposes: "Always on top", "Auto hide" (taskbar collapses to a thin sliver until pointed at), "Show small icons in Start menu" (removes the sidebar banner when enabled), "Show Clock", and "Use Personalized Menus". https://www.informit.com/articles/article.aspx?p=411736&seqNum=161
- "Favorites" and "Log Off" Start menu entries are optional/toggleable via the Properties dialog's Advanced tab — in the toastytech reference screenshot both are hidden, i.e. not shown by default in that configuration. http://toastytech.com/guis/w2k3.html
- Right-clicking empty taskbar space opens a context menu offering Toolbars, Cascade Windows, Tile Windows Horizontally/Vertically, Minimize All Windows, and (after a cascade/tile) Undo Cascade/Undo Tile, plus Properties. https://www.informit.com/articles/article.aspx?p=411736&seqNum=49 ; https://www.informit.com/articles/article.aspx?p=411736&seqNum=163
- Four built-in toolbars are selectable from the taskbar's Toolbars submenu (Quick Launch plus others), and users can also create custom folder-backed toolbars. https://www.informit.com/articles/article.aspx?p=411736&seqNum=163
- It is easy to accidentally drag Start menu items out of their list (turning them into a floating/relocated shortcut) with no obvious undo affordance — a UX rough edge noted contemporaneously. http://toastytech.com/guis/w2k3.html
- The notification area does **not** have Windows XP's "hide inactive icons" chevron/collapse feature — all tray icons are always visible in Windows 2000; icon-hiding was introduced in XP. https://www.pssoftlab.com/blog/hide-system-tray-icons-methods (background/context on XP-only chevron feature)
- Balloon-tip notifications from the notification area were introduced in Windows 2000 (new vs. Windows 98/Me). https://en.wikipedia.org/wiki/Taskbar
- Classic bevel colors used throughout taskbar/menu chrome: highlight `#FFFFFF`, light shadow `#808080` (128,128,128), dark shadow `#404040` (64,64,64) — directly measured on button/divider edges. https://guidebookgallery.org/screenshots/win2000pro

## Measurements
| Element | Value | Confidence | Source |
|---|---|---|---|
| Taskbar height (default, single row, 96 DPI) | 28px | measured (2 independent screenshots) | https://guidebookgallery.org/screenshots/win2000pro ; http://toastytech.com/guis/w2k3.html |
| Taskbar top bevel highlight | 1px white line | measured | https://guidebookgallery.org/screenshots/win2000pro |
| Start button width | ~144–150px (label-dependent, not fixed) | measured, font-dependent | https://guidebookgallery.org/screenshots/win2000pro |
| Start button icon (flag) width | ~9–21px glyph span | measured | https://guidebookgallery.org/screenshots/win2000pro |
| Quick Launch gripper width | ~4–5px (two hairlines) | measured | https://guidebookgallery.org/screenshots/win2000pro |
| Task button width (auto-sized sample) | ~163px per button (3-button 800px sample) | measured, sample-dependent | https://guidebookgallery.org/screenshots/win2000pro |
| Task button divider | 2px etched line (dark+light) | measured | https://guidebookgallery.org/screenshots/win2000pro |
| Notification icon sunken well | ~14px wide box per icon (704–718 sample) | measured | https://guidebookgallery.org/screenshots/win2000pro |
| Start menu item row height | 20px (640×480, 96 DPI) | measured | http://toastytech.com/guis/w2k3.html |
| Start menu sidebar banner width | ~26px (640×480) | measured | http://toastytech.com/guis/w2k3.html |
| Start menu submenu arrow | small black right-triangle, right-aligned in row | measured/visual | http://toastytech.com/guis/w2k3.html |
| Clock format | 12-hour, no seconds, e.g. "1:15 PM" | measured/visual | https://guidebookgallery.org/screenshots/win2000pro |

## Colors
| Role | Hex | Source |
|---|---|---|
| Taskbar/button face (`BTNFACE`) | `#D4D0C8` (212,208,200) | https://guidebookgallery.org/screenshots/win2000pro |
| 3D highlight (`BTNHIGHLIGHT`) | `#FFFFFF` | https://guidebookgallery.org/screenshots/win2000pro |
| 3D light shadow (`BTNSHADOW`) | `#808080` (128,128,128) | https://guidebookgallery.org/screenshots/win2000pro |
| 3D dark shadow | `#404040` (64,64,64) | https://guidebookgallery.org/screenshots/win2000pro |
| Selected menu item / highlight (`COLOR_HIGHLIGHT`) | `#0A246A` (10,36,106) | https://guidebookgallery.org/screenshots/win2000pro |
| Start menu sidebar banner background | `#000084` (0,0,132) ~estimate, palette-quantized source image | http://toastytech.com/guis/w2k3.html |
| Default desktop wallpaper teal (context, not chrome) | `#3A6EA5` (58,110,165) | https://guidebookgallery.org/screenshots/win2000pro |

## Behavior notes
- Auto-hide: taskbar collapses to a ~1–2px sliver at the screen edge when not focused; reappears when the mouse touches that edge. https://www.informit.com/articles/article.aspx?p=411736&seqNum=161
- Always on top: keeps the taskbar visible above maximized windows (default on). https://www.informit.com/articles/article.aspx?p=411736&seqNum=161
- Personalized Menus (IntelliMenus): Start menu hides items "not used in a while" behind a chevron/down-arrow at the bottom of long menus; expandable by click or hover; requires user-tracking (`Intellimenus`/`NoInstrumentation` registry policy can disable it). http://toastytech.com/guis/w2k3.html ; https://learn.microsoft.com/en-us/previous-versions/ms813183(v=msdn.10)
- "Show small icons in Start menu" removes the vertical sidebar banner entirely and shrinks item icons — a distinct visual mode from the default large-icon + banner layout. https://www.informit.com/articles/article.aspx?p=411736&seqNum=161
- Dragging a Start menu item (e.g. by accident) relocates/removes it from the menu; there is no obvious in-UI undo, a contemporaneously-noted usability issue. http://toastytech.com/guis/w2k3.html
- Right-click on empty taskbar area → Cascade Windows / Tile Windows Horizontally / Tile Windows Vertically / Minimize All Windows / Undo Cascade|Tile / Toolbars / Properties. https://www.informit.com/articles/article.aspx?p=411736&seqNum=49
- Balloon notifications (new in Windows 2000) pop up from the notification area for system events (e.g. "found new hardware", low battery); behavior described as appearing somewhat inconsistently/unpredictably by contemporary reviewers. https://en.wikipedia.org/wiki/Taskbar ; http://toastytech.com/guis/w2k3.html
- No tray-icon auto-hide/chevron collapsing — every notification icon stays visible at all times (unlike XP's "hide inactive icons"). https://www.pssoftlab.com/blog/hide-system-tray-icons-methods
- Menu fade transitions are a configurable visual effect (Display Properties → Effects tab) affecting both Start menu and application menus — screenshot shows a partially-faded Paint "File" menu mid-transition. http://toastytech.com/guis/w2k3.html (w2kfademenus.png)
- Taskbar can be docked to any of the four screen edges and resized by dragging its inner edge (standard Win9x-lineage behavior; not independently re-verified against a Win2000-specific source in this pass — see Open questions).

## Open questions
- Exact fixed pixel width of the Start button when NOT auto-sized (i.e. whether Win2000 enforces any minimum/maximum) was not found in a citable public source — only measured instances.
- Precise resize-drag increment for taskbar height (whether it snaps to fixed row-height multiples) not confirmed from a public source in this pass.
- Full default Quick Launch icon *order* (left-to-right) for a stock install was inferred from a secondary source, not cross-checked pixel-by-pixel against an unmodified out-of-box screenshot.
- Did not find a citable measurement for the taskbar's minimum/maximum multi-row height increment, or for context-menu item heights specifically (only Start-menu item heights were measured).
- The exact RGB of the sidebar banner gradient (whether it's a flat fill or a subtle vertical gradient) is uncertain — the source PNG is 8-bit palette-indexed, which can distort anti-aliased/gradient sampling; a true-color source would be needed to confirm.
- Did not locate a still-working archived MSDN "Windows Interface Guidelines"/"Microsoft Windows User Experience" (1999) page with taskbar-specific pixel specs; the physical book exists on archive.org but its content wasn't extractable via automated fetch in this pass.

## Images
| Filename | Source URL | Shows |
|---|---|---|
| w2k-desktop-full-guidebook.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/desktop/full/win2000pro.png) | Full 800×600 Win2000 Pro desktop with taskbar, Start button, Quick Launch, task buttons, tray — primary measurement source |
| w2k-desktop-empty-guidebook.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/desktop/empty/win2000pro.png) | Empty desktop + taskbar baseline, no task buttons |
| w2k-desktop-firstrun-guidebook.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/desktop/firstrun/win2000pro.png) | First-run desktop state + taskbar |
| w2k-advserv-desktop-full-guidebook.png | https://guidebookgallery.org/screenshots/win2000advserv (pics/gui/desktop/full/win2000advserv.png) | Windows 2000 Advanced Server desktop + taskbar, cross-check sample |
| w2k-running-apps-guidebook.png | https://guidebookgallery.org/screenshots/win2000pro (system/managers/running crop, 800×28) | Taskbar strip with 3 task buttons showing active/pressed vs normal states |
| w2k-start-menu-toastytech.png | http://toastytech.com/guis/w2k3.html (w2kstartmenu.png) | Full Start menu open with Programs → Accessibility submenu cascade, sidebar banner, submenu arrows |
| w2k-where-are-programs-toastytech.png | http://toastytech.com/guis/w2k3.html (w2kwhereprogs.png) | Personalized Menus chevron/expand-arrow at bottom of a hidden-items list |
| w2k-menu-drag-toastytech.png | http://toastytech.com/guis/w2k3.html (w2kmenudrag.png) | Dragging an item out of the Start menu |
| w2k-fademenus-toastytech.png | http://toastytech.com/guis/w2k3.html (w2kfademenus.png) | Taskbar + Quick Launch + active Paint task button + tray, alongside a fading application menu |
| w2k-taskbar-zoomed-left-guidebook-crop.png | https://guidebookgallery.org/screenshots/win2000pro (local 3x crop of desktop/full) | Zoomed Start button, Quick Launch grip+icons, first task button |
| w2k-taskbar-zoomed-mid-guidebook-crop.png | https://guidebookgallery.org/screenshots/win2000pro (local 3x crop of desktop/full) | Zoomed middle task buttons with dividers |
| w2k-taskbar-zoomed-right-guidebook-crop.png | https://guidebookgallery.org/screenshots/win2000pro (local 3x crop of desktop/full) | Zoomed tray well, speaker icon, clock |

## Sources
- https://guidebookgallery.org/screenshots/win2000pro
- https://guidebookgallery.org/screenshots/win2000advserv
- http://toastytech.com/guis/w2k3.html
- http://toastytech.com/guis/w2k.html
- http://toastytech.com/guis/w2k2.html
- https://en.wikipedia.org/wiki/Taskbar
- https://en.wikipedia.org/wiki/Start_menu
- https://www.informit.com/articles/article.aspx?p=411736&seqNum=12
- https://www.informit.com/articles/article.aspx?p=411736&seqNum=49
- https://www.informit.com/articles/article.aspx?p=411736&seqNum=161
- https://www.informit.com/articles/article.aspx?p=411736&seqNum=163
- https://www.itprotoday.com/systems-management/how-can-i-change-icons-quick-launch-toolbar
- https://learn.microsoft.com/en-us/previous-versions/ms813183(v=msdn.10)
- https://www.pssoftlab.com/blog/hide-system-tray-icons-methods
- https://archive.org/details/microsoftwindows00micr_0 (consulted, not extractable — see Open questions)
- https://winworldpc.com/screenshot/c3ac5d3a-c3b5-1a09-11c3-a4e284a2c3a5/0dc39d07-4a23-c2b5-11c3-a4e284a2c3a5 (consulted, appears to mirror the toastytech Start menu screenshot; not independently downloaded)
