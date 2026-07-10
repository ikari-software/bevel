# Icons / Pixel Art Style — Win2000 reference notes

## Overview

Windows 2000 icons are hand-illustrated bitmaps, not vector renders: every pixel is placed deliberately, edges are hard (1-bit mask transparency, no anti-aliased alpha), and detail is exaggerated at small sizes rather than simply downscaled. The shipped format is three fixed sizes — 16×16, 32×32, 48×48 — each authored twice, once for 16-color (VGA-safe) displays and once for 256-color displays, with the system picking whichever fits the current display mode. Objects that are physically 3-D (devices, computers, cabinets) are drawn in a raised, oblique "desk-toy" perspective with a consistent upper-left light source; objects that are physically flat (documents, pages) are drawn straight-on. A small, fixed vocabulary of overlay badges (shortcut arrow, shared hand, and later CD-burn/offline/tape badges) is composited into the bottom-left or bottom-right corner of the base icon rather than being drawn as part of it. This whole system pre-dates Windows XP's alpha-blended 32-bit icons: Windows 2000 icon transparency is strictly binary (a pixel is either fully opaque or fully transparent), which is the single most load-bearing constraint for a pixel-accurate clone.

## Key visual facts

- Application/document/object icons ship in three fixed pixel sizes: 16×16, 32×32, and 48×48, each supplied in both a 16-color and a 256-color version (four bitmaps minimum per logical icon). https://archive.org/details/windows_uxdocs (MS-Official-GUI-2001.pdf, "Icon Design," p.349-350)
- The 256-color icon variant is what actually renders in 16-bit and 24-bit ("high color"/"true color") display modes — there is no separate true-color bitmap resource in the base spec. https://archive.org/details/windows_uxdocs (MS-Official-GUI-2001.pdf, p.349-350)
- Displaying icons at 48×48 requires the registry value `Shell Icon Size` under `HKEY_CURRENT_USER\Desktop\WindowMetrics` to be set to 48 (default is 32); displaying icons above 16 colors requires `Shell Icon BPP` to be 8 or higher in the same key. https://archive.org/details/windows_uxdocs (MS-Official-GUI-2001.pdf, p.350)
- Icon colors should be drawn from the system palette so the icon renders correctly across all color configurations; the OS auto-remaps colors for monochrome displays, but designers are told to manually check/author a monochrome fallback if the auto-map looks bad. https://archive.org/details/windows_uxdocs (MS-Official-GUI-2001.pdf, p.350)
- Canonical light-source rule: light comes from the upper left. To reinforce it, use a black edge on the bottom and right of a shape and a dark-gray edge (or a darker shade of the object's own hue) on the left and top. https://archive.org/details/windows_uxdocs (MS-Official-GUI-2001.pdf, p.351)
- 16×16 icons are not naive downscales of the 32×32 art — the guide explicitly says "simply scaling down... does not work" and instructs designers to re-draw small icons to preserve general silhouette and the single most distinctive detail, exaggerating that detail if needed. https://archive.org/details/windows_uxdocs (MS-Official-GUI-2001.pdf, p.351); reinforced generally by https://learn.microsoft.com/en-us/windows/win32/uxguide/vis-icons ("Level of detail")
- 48×48 icons may use full 256-color richness for a "realistic" look, but designers are told to stay simple; if targeting a 256-color display, only the system's standard 256-color palette may be used, while 65,000+-color ("high color") targets may use any RGB combination. https://archive.org/details/windows_uxdocs (MS-Official-GUI-2001.pdf, p.351)
- Icon design guidance explicitly forbids depicting people, faces, gender markers, or body parts (for international/cultural-neutrality reasons); if a human figure is unavoidable, render it as generically as possible. https://archive.org/details/windows_uxdocs (MS-Official-GUI-2001.pdf, p.351)
- Overlay badges (shortcut, offline, etc.) are composited on top of the base icon; guidance is to keep them clear of the icon's most important visual area, and to flip the base icon horizontally (adjusting the light source accordingly) if the badge would otherwise obscure it. https://archive.org/details/windows_uxdocs (MS-Official-GUI-2001.pdf, p.352)
- Toolbar button glyphs (a visually related but distinct icon family from desktop/Explorer icons) ship at 16×16 and 20×20, each in 16-color and 256-color, plus optional grayscale hot-track variants; 16-color toolbar glyphs get a black outline (except for arrow/X glyphs) and stay flat with minimal shading, while 256-color toolbar glyphs get a gray/color top-left + black bottom-right icon-style border and more shading. https://archive.org/details/windows_uxdocs (MS-Official-GUI-2001.pdf, "Toolbar Button Image Design," p.352-353)
- Windows 2000-era icon transparency is a binary 1-bit AND-mask (a pixel is either fully opaque or fully see-through) — there is no soft/anti-aliased alpha edge. 32-bit RGBA icons with an 8-bit (256-level) alpha channel were introduced later, in Windows XP. https://www.gdgsoft.com/gconvert/help/XP_icons.htm
- The Windows 16-color icon/UI palette (shared with EGA/VGA text-mode heritage) is closed: all 16 slots are system-defined and none are free for custom hues, so 16-color icons must dither within exactly these 16 colors. https://en.wikipedia.org/wiki/List_of_software_palettes
- In 256-color mode, Windows reserves 20 palette slots as system colors (the first 10 and the last 10 palette indices); the remaining 236 indices are free for an application/icon's own colors. The system's 20 reserved slots = the 16 VGA colors plus 4 extra gray shades; from Windows 95 onward those 4 extra grays can shift when the active color scheme changes, so icons shouldn't rely on their exact values. https://en.wikipedia.org/wiki/List_of_software_palettes
- Icon design for the era is described as "illustrative" (hand-drawn/painted look, not photoreal) — Microsoft's own later contrast (written for the Vista redesign) explicitly frames Windows XP-style icons as more illustrative/symbolic and less "rendered," which by extension characterizes the visually similar, slightly plainer Windows 2000 set. https://learn.microsoft.com/en-us/windows/win32/uxguide/vis-icons ("Design concepts")
- Documented perspective convention for this icon generation (Microsoft's post-2000 icon guide, describing the style Vista was moving away from): 3-D objects are drawn as solid forms in oblique perspective from a low bird's-eye view with two vanishing points; flat real-world objects (paper, documents) are drawn straight-on/front-facing; at 16×16 and below, even normally-3-D subjects are simplified to a straight-on rendering because perspective doesn't read at that size. Toolbar icons are always front-facing regardless of size. https://learn.microsoft.com/en-us/windows/win32/uxguide/vis-icons ("Perspective") — ~apply with caution to Windows 2000 specifically; this document is written for the XP/Vista transition, not Windows 2000 itself.
- Overlay badge sizing/position convention documented in the same later guide: badges scale with the base icon (10×10 over a 16×16 icon, 16×16 over a 32×32 icon, 24×24 over a 48×48 icon) and are meant to fill about 25% of the icon's area, with "annotations" (e.g. read-only marks) placed bottom-right and "overlays" (e.g. shortcut, shared) placed bottom-left, one per icon. https://learn.microsoft.com/en-us/windows/win32/uxguide/vis-icons ("Size requirements" / "Annotations and overlays") — ~carry over cautiously to Win2000; exact Win2000-era badge pixel footprints are not independently confirmed.
- Windows XP (documented) shipped five standard overlay badges: a small arrow for shortcuts, an open upward-palm hand for a shared folder, a downward blue arrow for files queued to burn to CD, blue swirling arrows for offline-available items, and a black clock for items archived to tape. The shortcut-arrow and shared-hand overlays are the two that were already established on Windows 95/98/2000, predating this list. https://devblogs.microsoft.com/oldnewthing/20030827-00/?p=42783
- Windows 2000 gave several major desktop/system icons (e.g. My Computer, Recycle Bin) visual "facelifts" with more detail and color depth versus Windows 98/NT4, while keeping the same 32×32/48×48, 256-color technical ceiling as Windows 98. https://www.howtogeek.com/733912/a-visual-history-of-windows-icons-from-windows-1-to-11/
- Per Wikipedia, Windows 2000's new/updated icon set (My Computer, Recycle Bin, and others) first appeared during development in Beta 3, build 1964. https://en.wikipedia.org/wiki/Windows_2000
- Windows 98 was the release that added the 48×48 icon size on top of the earlier 32×32 standard; Windows 95 shipped mainly 32×32 16-color icons by default even though the underlying Win32 icon resource format technically allowed up to 256×256 with millions of colors (rarely used until much later). https://www.howtogeek.com/733912/a-visual-history-of-windows-icons-from-windows-1-to-11/
- General mouse pointer/cursor guidance from the same 2001 Microsoft interaction guide: every pointer shape has a defined hot spot (the exact pixel that registers the click/hover), and pointer shape should make that hot spot visually obvious (e.g. a crosshair's hot spot is the line intersection); Windows also defines a "hot zone" — the area around an object where its hot spot is still considered "over" that object. https://archive.org/details/windows_uxdocs (MS-Official-GUI-2001.pdf, "Pointer Design," p.356 / "Mouse Pointers," p.42-43)

## Measurements

| Element | Value | Confidence | Source |
| --- | --- | --- | --- |
| Standard icon sizes | 16×16, 32×32, 48×48 px | confirmed | https://archive.org/details/windows_uxdocs (MS-Official-GUI-2001.pdf) |
| Icon color variants required | 16-color + 256-color per size | confirmed | https://archive.org/details/windows_uxdocs (MS-Official-GUI-2001.pdf) |
| Toolbar glyph sizes | 16×16, 20×20 px | confirmed | https://archive.org/details/windows_uxdocs (MS-Official-GUI-2001.pdf) |
| Registry default shell icon size | 32 px (raise `Shell Icon Size` to 48 for large icons) | confirmed | https://archive.org/details/windows_uxdocs (MS-Official-GUI-2001.pdf) |
| Icon transparency | 1-bit AND-mask (binary opaque/transparent, no alpha) | confirmed | https://www.gdgsoft.com/gconvert/help/XP_icons.htm |
| 16-color palette size | 16 colors, 0 free for custom hues | confirmed | https://en.wikipedia.org/wiki/List_of_software_palettes |
| 256-color mode system-reserved slots | 20 of 256 (first 10 + last 10 indices); 236 free | confirmed | https://en.wikipedia.org/wiki/List_of_software_palettes |
| Overlay badge footprint (later-documented convention) | ~10×10 over 16×16 icon; ~16×16 over 32×32 icon; ~24×24 over 48×48 icon; ~25% of icon area | ~estimate (documented post-2000, applied cautiously back to Win2000) | https://learn.microsoft.com/en-us/windows/win32/uxguide/vis-icons |
| Flat-icon shadow angle (later-documented convention) | light/shadow at 120–130° from upper-left | ~estimate (Vista-era doc; win2000 icons visibly use upper-left light too, per MS-Official-GUI-2001.pdf) | https://learn.microsoft.com/en-us/windows/win32/uxguide/vis-icons |
| First appearance of Win2000's refreshed system icon set | Beta 3, build 1964 | confirmed | https://en.wikipedia.org/wiki/Windows_2000 |

## Colors

| Role | Hex | Source |
| --- | --- | --- |
| Windows 16-color palette — black | #000000 | https://lospec.com/palette-list/microsoft-windows |
| Windows 16-color palette — dark gray | #7e7e7e | https://lospec.com/palette-list/microsoft-windows |
| Windows 16-color palette — light gray | #bebebe | https://lospec.com/palette-list/microsoft-windows |
| Windows 16-color palette — white | #ffffff | https://lospec.com/palette-list/microsoft-windows |
| Windows 16-color palette — dark red / maroon | #7e0000 | https://lospec.com/palette-list/microsoft-windows |
| Windows 16-color palette — bright red | #fe0000 | https://lospec.com/palette-list/microsoft-windows |
| Windows 16-color palette — dark green | #047e00 | https://lospec.com/palette-list/microsoft-windows |
| Windows 16-color palette — bright green | #06ff04 | https://lospec.com/palette-list/microsoft-windows |
| Windows 16-color palette — dark yellow/olive | #7e7e00 | https://lospec.com/palette-list/microsoft-windows |
| Windows 16-color palette — bright yellow | #ffff04 | https://lospec.com/palette-list/microsoft-windows |
| Windows 16-color palette — navy | #00007e | https://lospec.com/palette-list/microsoft-windows |
| Windows 16-color palette — bright blue | #0000ff | https://lospec.com/palette-list/microsoft-windows |
| Windows 16-color palette — dark magenta/purple | #7e007e | https://lospec.com/palette-list/microsoft-windows |
| Windows 16-color palette — bright magenta | #fe00ff | https://lospec.com/palette-list/microsoft-windows |
| Windows 16-color palette — dark cyan/teal | #047e7e | https://lospec.com/palette-list/microsoft-windows |
| Windows 16-color palette — bright cyan | #06ffff | https://lospec.com/palette-list/microsoft-windows |
| Icon shading — dark edge (bottom/right, reinforces light source) | black, or a darker shade of the object's own hue | https://archive.org/details/windows_uxdocs (MS-Official-GUI-2001.pdf, p.351) |
| Icon shading — mid edge (top/left) | dark gray | https://archive.org/details/windows_uxdocs (MS-Official-GUI-2001.pdf, p.351) |
| Toolbar/bitmap transparency key color (documented for the later XP/Vista toolbar spec; long-standing GDI convention) | magenta, R255 G0 B255 | https://learn.microsoft.com/en-us/windows/win32/uxguide/vis-icons |

## Behavior notes

- Icon color/size resource selection is driven by the current display's color depth and the `Shell Icon BPP`/`Shell Icon Size` registry values under `HKEY_CURRENT_USER\Desktop\WindowMetrics`, not hardcoded per-app — the same .ico resource block supplies the right bitmap for 16-color VGA, 256-color, and (from Windows 98/2000) 48×48 "large icon" views. https://archive.org/details/windows_uxdocs (MS-Official-GUI-2001.pdf, p.350)
- Overlay badges are a compositing layer applied by the shell on top of a resolved base icon (not baked into the file's own icon resource), which is why the same shortcut arrow or shared hand can appear over any file/folder/app icon uniformly. https://devblogs.microsoft.com/oldnewthing/20030827-00/?p=42783
- A documented shell bug/behavior: if code calls `SetFileAttributes` with an unchecked, failed `GetFileAttributes` result (0xFFFFFFFF), it can accidentally set `FILE_ATTRIBUTE_OFFLINE`, causing the black "archived to tape" clock overlay to appear on files that were never actually archived — an example of overlay state being attribute-driven rather than purely file-type-driven. https://devblogs.microsoft.com/oldnewthing/20030827-00/?p=42783
- Pointer (cursor) feedback should be scoped to the region where its meaning applies — e.g. only show the busy/hourglass pointer over non-interactive windows, and only show link/hand-style pointers over the actual interactive hot zone, not the whole window. https://archive.org/details/windows_uxdocs (MS-Official-GUI-2001.pdf, "Pointer Design," p.356)
- Non-animated pointer variants must always be available even when animated cursors are used, because animation can be distracting for some users — implying the OS ships a static fallback for every animated cursor state. https://archive.org/details/windows_uxdocs (MS-Official-GUI-2001.pdf, p.357)

## Open questions

- The exact Windows-2000-specific overlay badge pixel footprint and corner placement (as opposed to the later, more formalized XP/Vista-era guide values used here as a proxy) was not independently confirmed from a Windows-2000-dated source.
- Whether Windows 2000's Explorer ever rendered icons using the 4-bit (16-color) variant in normal (256-color+) desktop use, or whether that fallback was effectively dead by 2000 except for remote/Terminal Services low-color sessions, is not confirmed with a primary source.
- Specific named hex values Microsoft actually used inside real Windows 2000 system icons (e.g. the folder-yellow, the Recycle-Bin blue) were not found in any public design-guideline text; only the generic 16-color system palette and the "236 free slots in 256-color mode" constraint are documented. Would need direct pixel sampling of legitimately-owned, non-DLL screenshot images (not extracted icon resources) to pin down.
- Betawiki build-specific icon change logs (e.g. build 1983's Control Panel applet icon updates, build 1964's My Computer/Recycle Bin refresh, build 1965's Recycle Bin change) could not be fetched directly — betawiki.net returned HTTP 403 to automated fetches on both attempts — so those facts are omitted rather than cited secondhand.
- Exact drop-shadow opacity/feathering values for genuinely Windows-2000-era (not Vista-era) flat file/document icons were not found in a Win2000-dated source.

## Images

| Filename | Source URL | Shows |
| --- | --- | --- |
| desktop-full-icons.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/desktop/full/win2000pro.png) | Windows 2000 Professional desktop with app windows open — desktop icon set at 32px in context |
| desktop-empty-icons.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/desktop/empty/win2000pro.png) | Clean Win2000 desktop showing default desktop icons (My Computer, Recycle Bin, etc.) |
| control-panel-classic-view.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/settings/menu/win2000pro.png) | Control Panel classic view — grid of 32px applet icons, good for cross-icon style comparison |
| explorer-file-manager.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/system/managers/filemanager/win2000pro.png) | Windows Explorer tree/list view — folder and file icons at list scale |
| application-manager-view1.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/system/managers/applicationmanager/win2000pro-1-1.png) | Add/Remove Programs-style application manager list with small app icons |
| recycle-bin-dialog.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/system/features/trashcan/win2000pro.png) | Recycle Bin window — empty/full bin icon states |
| search-results-icons.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/system/features/search/win2000pro.png) | Search Results pane — file-type icons in a results list |
| about-windows-dialog.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/interface/dialogs/aboutgui/win2000pro.png) | "About Windows" dialog — large Windows 2000 logo/badge icon rendering |
| open-file-dialog-icons.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/interface/dialogs/openfile/win2000pro.png) | Common Open File dialog — small-icon list view of files/folders |
| display-appearance-dialog.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/settings/appearance/win2000pro-1-1.png) | Display Properties Appearance tab — preview pane showing window chrome + icon label text rendering |
| address-book-icons.png | https://guidebookgallery.org/screenshots/win2000pro (pics/gui/applications/office/addressbook/win2000pro.png) | Windows Address Book toolbar and contact-list icon set |
| toastytech-start-menu-icons.png | http://toastytech.com/guis/w2k3.html (w2kstartmenu.png) | Start Menu open, showing Programs submenu icon set at native 16px/32px scale |
| toastytech-shutdown-dialog.png | http://toastytech.com/guis/w2k3.html (w2kshutdown.png) | Shut Down Windows dialog with its dropdown icon |
| winworldpc-explorer-icons.png | https://winworldpc.com/screenshot/c3ac5d3a-c3b5-1a09-11c3-a4e284a2c3a5/c3922cc3-aa1f-23c2-b411-c3a4e284a2ef | Windows Explorer window, alternate capture — folder tree + file list icon rendering |
| winworldpc-start-menu-icons.png | https://winworldpc.com/screenshot/c3ac5d3a-c3b5-1a09-11c3-a4e284a2c3a5/0dc39d07-4a23-c2b5-11c3-a4e284a2c3a5 | Start Menu, alternate capture — corroborates toastytech capture of the same UI |

## Sources

- https://archive.org/details/windows_uxdocs (MS-Official-GUI-2001.pdf — "Official Guidelines for User Interface Developers and Designers," dated 5/3/01, targets Windows 2000)
- https://learn.microsoft.com/en-us/windows/win32/uxguide/vis-icons
- https://www.gdgsoft.com/gconvert/help/XP_icons.htm
- https://en.wikipedia.org/wiki/List_of_software_palettes
- https://lospec.com/palette-list/microsoft-windows
- https://devblogs.microsoft.com/oldnewthing/20030827-00/?p=42783
- https://www.howtogeek.com/733912/a-visual-history-of-windows-icons-from-windows-1-to-11/
- https://en.wikipedia.org/wiki/Windows_2000
- https://guidebookgallery.org/screenshots/win2000pro
- http://toastytech.com/guis/w2k3.html
- https://winworldpc.com/screenshot/c3ac5d3a-c3b5-1a09-11c3-a4e284a2c3a5/c3922cc3-aa1f-23c2-b411-c3a4e284a2ef
- https://winworldpc.com/screenshot/c3ac5d3a-c3b5-1a09-11c3-a4e284a2c3a5/0dc39d07-4a23-c2b5-11c3-a4e284a2c3a5
