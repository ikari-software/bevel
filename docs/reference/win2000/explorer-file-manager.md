# Explorer / File Manager — Win2000 reference notes

## Overview

Windows 2000 Explorer runs the "Desktop Update" (Active Desktop / IE4+ shell) chrome: a classic menu bar, a single-row rebar toolbar with small-icon buttons (the Back button carries a text label, added in the IE5.5-derived toolbar redesign that shrank the bar to about half its Windows 98 height), a combo-box address bar, and an optional tree ("Folders") or task ("Search"/"History") explorer band docked on the left. Folder windows default to "Web view": the right pane is a WebBrowser control hosting an HTML template (`folder.htt`) that renders a banner with the folder's icon/name over a pastel gradient/faded-square graphic, a blue horizontal rule, a description block, and a "See also" hyperlink list — with the classic ListView (icons/list/details) confined to a fixed-offset region to the right of that sidebar. All chrome elements share the standard 3D Button Face grey (#D4D0C8), and column headers in Details view use the classic raised button-style header control with per-click ascending/descending sort.

## Key visual facts

- Explorer folder windows carry a full classic menu bar: File, Edit, View, Favorites, Tools, Help. https://guidebookgallery.org/screenshots/win2000pro
- The single-row toolbar shows, left to right: Back (with visible text label) / Forward (icon-only, disabled-grey when no history) / Up-one-level, then Search / Folders / History task-pane toggle buttons (each with a text label), then Cut/Copy/Paste/Undo icon buttons, then a Views drop-down button (icon grid glyph) with its own chevron. https://gekk.info/articles/explorer.html ; https://guidebookgallery.org/screenshots/win2000pro
- This toolbar redesign ("side-labeled buttons") originates in IE5.5 and let Microsoft shrink the toolbar row to roughly half the height of the Windows 98-era toolbar; the cut/copy/paste cluster was shuffled forward to make room for the new Search/Folders/History buttons. https://gekk.info/articles/explorer.html
- A History button was added to Explorer's toolbar for the first time in Windows 2000, alongside a Search button that now launches an integrated task-pane search (previously a separate Find dialog/app). https://gekk.info/articles/explorer.html
- The Folders button toggles any window between "flat" folder view and classic two-pane "Explore" tree view. https://gekk.info/articles/explorer.html
- The Address bar is a labeled combo box ("Address:" caption, drop-down folder-path field with a small icon inside the field, dropdown arrow, and a "Go" button with a green circular-arrow icon at the right edge) sitting in its own rebar band below the toolbar. https://guidebookgallery.org/screenshots/win2000pro
- Toolbar buttons use the Win32 common-control toolbar default: `CreateWindowEx`-created toolbars size buttons to 24×22 px by default (`TB_SETBUTTONSIZE` to override). https://learn.microsoft.com/en-us/windows/win32/controls/toolbar-controls-overview
- Common Controls v5.80 (shipped with IE5/Windows 98 SE/Windows 2000) introduced the renamed `BTNS_*` toolbar button styles, including the list-style (`TBSTYLE_LIST`/`BTNS_SHOWTEXT`) buttons that place a text label to the right of the bitmap — the mechanism behind Explorer's labeled Back/Search/Folders/History buttons. https://learn.microsoft.com/en-us/windows/win32/controls/toolbar-controls-overview
- Web-view ("Explorer bar") is the default right-pane rendering for every folder in the Desktop Update; icon-underlining (the other classic "Web style" option) is off by default. https://learn.microsoft.com/en-us/archive/msdn-magazine/2000/june/more-windows-2000-ui-goodies-extending-explorer-views-by-customizing-hypertext-template-files
- Only two right-pane renderings exist in Windows 2000: the Web view (template-driven, whole-folder) and the Thumbnails view (content only, folders/directories only); large icons, small icons, list, and details remain classic ListView "View menu" options layered underneath either rendering. https://learn.microsoft.com/en-us/archive/msdn-magazine/2000/june/more-windows-2000-ui-goodies-extending-explorer-views-by-customizing-hypertext-template-files
- In the stock `folder.htt` template, the file-list ActiveX control (Shell DefView) is positioned a fixed 200 px from the window's left edge — i.e. the left info/banner sidebar is a fixed-width column, not something that reflows with window size. https://learn.microsoft.com/en-us/archive/msdn-magazine/2000/june/more-windows-2000-ui-goodies-extending-explorer-views-by-customizing-hypertext-template-files
- A close variant of the same template (`folder.htt` as republished/discussed by contemporaneous customization guides) sizes the left panel to `width:30%` and the file list to `left:30%; width:70%`, and hides the left panel entirely once the window narrows below ~400 px. http://www.virtualplastic.net/html/wv_fld.html
- The folder-title banner text uses a bold ~16pt heading (`p.Title{font:16pt;font-weight:bold}`) followed immediately by a horizontal rule image (`wvline.gif`, referred to as "the blue line") with `margin-bottom:20px` separating it from the body copy below. http://www.virtualplastic.net/html/wv_fld.html
- Body copy in the web-view sidebar uses 8pt/10pt Verdana on a white background; buttons injected into the template pick up `buttonface` system color. http://www.virtualplastic.net/html/wv_fld.html
- The web-view banner's decorative artwork behind the folder icon is a faded, multi-hue "pastel squares" pattern (oranges, cyans, light blues) rather than a flat gradient; screenshots of My Computer, Recycle Bin, Search Results, and Program Files all reuse the same style of banner graphic. https://guidebookgallery.org/screenshots/win2000pro ; https://gekk.info/articles/explorer.html
- My Computer's web-view sidebar includes a "See also" block with three blue hyperlinks (My Documents, My Network Places, Network and Dial-up Connections) beneath the description text — this "suggest related places" pattern recurs across most system folders. https://guidebookgallery.org/screenshots/win2000pro ; https://gekk.info/articles/explorer.html
- Opening a local disk's web view (e.g. "Local Disk (C:)") adds a used/free-space pie chart and capacity text to the sidebar beneath the description — new in Windows 2000, described by one retrospective as "extremely handy" for avoiding a trip to Properties. https://gekk.info/articles/explorer.html
- The Program Files folder is protected by a "scare link" pattern: the web view shows explanatory text ("This folder contains files that keep your system working properly...") plus an "Add/Remove Programs" hyperlink and a "Show Files" hyperlink instead of listing contents directly; going in via the Show Files link renders the file list with several toolbar buttons visibly disabled (greyed) even though items exist. https://gekk.info/articles/explorer.html
- Details view column headers are classic raised button-style header-control cells with vertical divider bevels; clicking toggles ascending/descending sort. https://guidebookgallery.org/screenshots/win2000pro (Recycle Bin, Search Results screenshots)
- Recycle Bin's Details view ships with columns: Name | Original Location | Date Deleted | Type | Size. https://guidebookgallery.org/screenshots/win2000pro
- Search Results' Details view ships with columns: Name | In Folder | Relevance | Size | Type | Modified (truncated in the 774px-wide screenshot as "Modi..."). https://guidebookgallery.org/screenshots/win2000pro
- The Search task pane (Explorer band) is a left-docked panel titled "Search" with a close (X) button in its own mini title strip, a "New"/scroll-icon button row beneath it, then a "Search for Files and Folders" sub-heading, a "Search for files or folders named:" text box, a "Containing text:" text box, a "Look in:" drive combo, Search Now / Stop Search buttons, a "Search Options >>" expander link, and a "Search for other items" list of blue links (Computers, People, Internet — Files or Folders is greyed out as the active mode). https://guidebookgallery.org/screenshots/win2000pro
- The status bar is a single flat strip divided into panels by sunken 3D dividers; on My Computer it shows only an object count ("4 object(s)") on the left and an icon+label of the current folder on the right; on Recycle Bin/disk views it can add extra panels (e.g. total bytes) between them. https://guidebookgallery.org/screenshots/win2000pro
- All chrome bands (menu bar, toolbar row, address-bar row, status bar) render on the same flat Button Face grey background, sampled at RGB(212,208,200) / #D4D0C8 directly from a Windows 2000 Pro "My Computer" screenshot. https://guidebookgallery.org/screenshots/win2000pro (local pixel measurement)
- The address-bar combo field and the file-list content pane are pure white, RGB(255,255,255), in the same screenshot. https://guidebookgallery.org/screenshots/win2000pro (local pixel measurement)

## Measurements

| Element | Value | Confidence | Source |
|---|---|---|---|
| Toolbar button default size (Win32 common control) | 24×22 px | high (spec) | https://learn.microsoft.com/en-us/windows/win32/controls/toolbar-controls-overview |
| Toolbar bitmap default image size (`CreateWindowEx`) | 16×15 px | high (spec) | https://learn.microsoft.com/en-us/windows/win32/controls/toolbar-controls-overview |
| Web-view sidebar width (stock `folder.htt`) | 200 px fixed offset for file-list control | high (MS-published source excerpt) | https://learn.microsoft.com/en-us/archive/msdn-magazine/2000/june/more-windows-2000-ui-goodies-extending-explorer-views-by-customizing-hypertext-template-files |
| Web-view sidebar width (alt. template variant) | 30% of window width, collapses under ~400 px window width | medium (secondary source, differs from MSDN figure above — may be a later/98-era variant of the template) | http://www.virtualplastic.net/html/wv_fld.html |
| Folder-title banner font size | ~16 pt bold | medium (secondary source) | http://www.virtualplastic.net/html/wv_fld.html |
| Web-view body font | 8pt/10pt Verdana | medium (secondary source) | http://www.virtualplastic.net/html/wv_fld.html |
| Menu bar band height (in a 552×382 reference screenshot) | ~22 px | low (estimate, scaled screenshot, own pixel measurement) | https://guidebookgallery.org/screenshots/win2000pro |
| Toolbar row height (same screenshot) | ~22 px | low (estimate, own pixel measurement) | https://guidebookgallery.org/screenshots/win2000pro |
| Address-bar row height (same screenshot) | ~20 px | low (estimate, own pixel measurement) | https://guidebookgallery.org/screenshots/win2000pro |
| Status bar height (same screenshot) | ~20 px | low (estimate, own pixel measurement) | https://guidebookgallery.org/screenshots/win2000pro |
| Details-view column header row height (Recycle Bin screenshot, 733×306) | ~15 px | low (estimate, own pixel measurement) | https://guidebookgallery.org/screenshots/win2000pro |
| Thumbnail preview size (web-view thumbnail control) | 160×160 px | medium (secondary source) | http://www.virtualplastic.net/html/wv_fld.html |
| Disk-space pie chart size (web-view sidebar) | ~100×50 px | medium (secondary source) | http://www.virtualplastic.net/html/wv_fld.html |

## Colors

| Role | Hex/RGB | Source |
|---|---|---|
| Chrome band background (menu bar / toolbar / address-bar row / status bar) | #D4D0C8 (212,208,200) — standard Button Face | https://guidebookgallery.org/screenshots/win2000pro (own pixel measurement) |
| Address-bar field / file-list content background | #FFFFFF (255,255,255) | https://guidebookgallery.org/screenshots/win2000pro (own pixel measurement) |
| 3D bevel highlight (raised-edge top/left) | #FFFFFF (255,255,255) | https://guidebookgallery.org/screenshots/win2000pro (own pixel measurement) |
| 3D bevel shadow (raised-edge bottom/right, outer) | #808080 (128,128,128) | https://guidebookgallery.org/screenshots/win2000pro (own pixel measurement) |
| 3D bevel shadow (inner, darkest) | #404040 (64,64,64) | https://guidebookgallery.org/screenshots/win2000pro (own pixel measurement) |
| Selection highlight ("Hilight", default Windows Standard scheme) | #000080 (0,0,128) navy | https://techshelps.github.io/MSDN/DNWINNT/HTML/D1E/S8538.HTM (Microsoft NT/2000 SDK docs excerpt) |
| Selected-item text ("HilightText") | #FFFFFF | https://techshelps.github.io/MSDN/DNWINNT/HTML/D1E/S8538.HTM |
| Active title bar ("ActiveTitle", default scheme) | #000080 (0,0,128) navy per SDK docs — but sampled from an actual Win2000 Pro screenshot title bar the rendered color is closer to RGB(10,36,106)/#0A246A, likely due to the gradient-caption blend | https://techshelps.github.io/MSDN/DNWINNT/HTML/D1E/S8538.HTM (doc) vs. https://guidebookgallery.org/screenshots/win2000pro (own pixel measurement) |
| Inactive title bar | #C0C0C0 (192,192,192) | https://techshelps.github.io/MSDN/DNWINNT/HTML/D1E/S8538.HTM |
| Window frame | #000000 | https://techshelps.github.io/MSDN/DNWINNT/HTML/D1E/S8538.HTM |
| Hyperlink text (web-view "See also" links) | classic blue, underlined | https://guidebookgallery.org/screenshots/win2000pro (visual, no hex extracted) |

## Behavior notes

- Web view is the default folder rendering in the Windows 2000 Desktop Update; it can be switched to classic view via Folder Options → General → "Use Windows classic folders." https://learn.microsoft.com/en-us/archive/msdn-magazine/2000/june/more-windows-2000-ui-goodies-extending-explorer-views-by-customizing-hypertext-template-files
- The window-class hierarchy differs by view: classic view is a plain `SHELLDLL_DefView` hosting a ListView filling the whole right pane; Web view instead hosts an `Internet Explorer_Server` (WebBrowser control) that in turn contains the `SHELLDLL_DefView`/ListView as just one element among the HTML template's other content. https://learn.microsoft.com/en-us/archive/msdn-magazine/2000/june/more-windows-2000-ui-goodies-extending-explorer-views-by-customizing-hypertext-template-files
- Explorer's four classic ListView states — large icons, small icons, list, details — are unified across folder windows and the desktop itself; per the MSDN article it is "hard to say" whether the shell's views or the underlying listview control's UI conventions (item underlining, hot-tracking, full-row select, background bitmap) came first, since both evolved together and the same listview implementation backs folders, the desktop, and the thumbnails view. https://learn.microsoft.com/en-us/archive/msdn-magazine/2000/june/more-windows-2000-ui-goodies-extending-explorer-views-by-customizing-hypertext-template-files
- A folder's Web-view template is chosen per-folder via a hidden `Folder Settings` subdirectory plus a `desktop.ini` entry (`PersistMoniker=file://Folder Settings/folder.htt`), created through the Folder Customization Wizard from the folder's context menu; different system folder types (file folders vs. Recycle Bin vs. My Network Places vs. My Pictures) ship distinct default `.htt` templates. https://learn.microsoft.com/en-us/archive/msdn-magazine/2000/june/more-windows-2000-ui-goodies-extending-explorer-views-by-customizing-hypertext-template-files
- My Pictures' template (`imgview.htt`) adds an image-specific toolbar (zoom, print, preview, size adjustment) inside the web-view content area, distinct from the standard `folder.htt` chrome. https://learn.microsoft.com/en-us/archive/msdn-magazine/2000/june/more-windows-2000-ui-goodies-extending-explorer-views-by-customizing-hypertext-template-files
- Creating a new folder through the normal Explorer UI immediately inserts the new item into the ListView already in rename/edit mode at the bottom of the list; the directory is only actually created on disk once the user exits edit mode and confirms a name. https://learn.microsoft.com/en-us/archive/msdn-magazine/2000/june/more-windows-2000-ui-goodies-extending-explorer-views-by-customizing-hypertext-template-files
- Explorer listens for file-system change notifications via a per-open-folder "file notification object" (FNO); the UI auto-refreshes when the FNO signals a change in the monitored subtree, without requiring the originating process to explicitly notify Explorer (though `SHChangeNotify` can speed up the refresh). https://learn.microsoft.com/en-us/archive/msdn-magazine/2000/june/more-windows-2000-ui-goodies-extending-explorer-views-by-customizing-hypertext-template-files
- The Views toolbar button and its dropdown/chevron let the user switch among the four classic ListView layouts without opening the View menu. https://guidebookgallery.org/screenshots/win2000pro (visual)
- Toolbar customization ("Enabling Customization" in Win32 common controls) lets a toolbar with `CCS_ADJUSTABLE` allow the user to drag buttons to reposition them, drag a button off to remove it, or double-click the toolbar to open a Customize Toolbar dialog — this is the general mechanism Explorer's own customizable toolbar builds on. https://learn.microsoft.com/en-us/windows/win32/controls/toolbar-controls-overview
- Rebar-hosted toolbars (as Explorer's are) must use `CCS_NORESIZE`/`CCS_NOPARENTALIGN` because the rebar control — not the toolbar itself — governs the band's size and position; this rebar hosting is what enables Explorer's toolbar and address-bar rows to be independently draggable/rearrangeable bands. https://learn.microsoft.com/en-us/windows/win32/controls/toolbar-controls-overview

## Open questions

- Could not confirm an exact hex value for the web-view banner's faded pastel-square graphic (it appears to be a bitmap sourced from the shell32/webview resource files, not a flat CSS gradient) — no public source describes it numerically; only visual description obtained from screenshots.
- Could not find a public archived copy of the actual Windows 2000 stock `folder.htt`/`shellstyle.css` source to confirm the exact banner region pixel width in the shipped final build; the two secondary sources found (MSDN Magazine, virtualplastic.net) disagree (200px fixed vs. 30%-relative), and it's unclear whether the 30%-relative version is a Windows 98/early-Desktop-Update variant rather than the final Windows 2000 template.
- No confirmed screenshot was found of Explorer's in-place rename (F2) edit box, drag-and-drop ghost/insertion-line visuals, or the rubber-band/marquee multi-select rectangle in Windows 2000 specifically — behavior is inferred from general Win32 ListView/comctl32 documentation and cross-version knowledge, not verified against a Windows 2000 screenshot.
- Could not verify precise chevron (overflow arrow) pixel dimensions or its exact trigger width for the Explorer toolbar under Windows 2000; only the general `RBBS_USECHEVRON` rebar mechanism was confirmed via search snippets, not fetched in full.
- toastytech.com/guis was unreachable during this session (server misconfiguration error on two attempts) — its Windows 2000 gallery page was not consulted; may hold additional screenshots worth revisiting later.
- The two candidate ActiveTitle colors (#000080 documented default vs. #0A246A sampled from a screenshot) were not reconciled — could not determine whether this is a gradient-caption blend artifact, a different default introduced specifically for Windows 2000 (vs. the NT4-era default reused in generic SDK docs), or screenshot/JPEG-style compression drift (image is PNG here, so compression drift is unlikely).
- Details view's exact column-resize/drag-handle cursor and double-click-to-autofit behavior were not verified against a Windows 2000-specific source.

## Images

| Filename | Source URL | Shows |
|---|---|---|
| win2000pro-file-manager-guidebook.png | https://guidebookgallery.org/pics/gui/system/managers/filemanager/win2000pro.png | My Computer window in Web view: full toolbar, address bar, banner, "See also" links, status bar |
| win2000pro-desktop-full-guidebook.png | https://guidebookgallery.org/pics/gui/desktop/full/win2000pro.png | Desktop with multiple overlapping windows incl. Administrative Tools Explorer window, Computer Management MMC, Start menu |
| win2000pro-search-guidebook.png | https://guidebookgallery.org/pics/gui/system/features/search/win2000pro.png | Search Results window: Search explorer band (task pane) + Details view column headers |
| win2000pro-trashcan-guidebook.png | https://guidebookgallery.org/pics/gui/system/features/trashcan/win2000pro.png | Recycle Bin window in Web view with empty Details view column headers |
| win2000pro-openfile-guidebook.png | https://guidebookgallery.org/pics/gui/interface/dialogs/openfile/win2000pro.png | Open file common dialog (shares Explorer chrome conventions: toolbar, list) |
| win2000-explorer-winworld.png | https://winworldpc.com/res/img/screenshots/final-3e8aaaa2b1c65587e5249551445642da-w2kfilebrowser.png | My Computer window in Web view, alternate capture (with desktop icons visible) |
| win2k-explorer-gekk.png | https://gekk.info/articles/images/explorer/2k_explorer_small.png | Local Disk (C:) Web view with disk-space pie chart in sidebar |
| win2k-explorer-related-links-gekk.png | https://gekk.info/articles/images/explorer/win2k_explorer_related_small.png | My Computer Web view showing "See also" related-links block |
| win2k-explorer-programfiles-gekk.png | https://gekk.info/articles/images/explorer/win2k_explorer_programfiles.png | Program Files "scare link" Web view with disabled toolbar buttons and gear-graphic banner |

## Sources

- https://guidebookgallery.org/screenshots/win2000pro
- https://guidebookgallery.org/screenshots/filemanager
- https://guidebookgallery.org/screenshots/win2000advserv
- https://gekk.info/articles/explorer.html
- https://winworldpc.com/screenshot/c3ac5d3a-c3b5-1a09-11c3-a4e284a2c3a5/c3922cc3-aa1f-23c2-b411-c3a4e284a2ef
- https://winworldpc.com/screenshot/c3ac5d3a-c3b5-1a09-11c3-a4e284a2c3a5
- https://learn.microsoft.com/en-us/archive/msdn-magazine/2000/june/more-windows-2000-ui-goodies-extending-explorer-views-by-customizing-hypertext-template-files
- http://www.virtualplastic.net/html/wv_fld.html
- https://learn.microsoft.com/en-us/windows/win32/controls/toolbar-controls-overview
- https://techshelps.github.io/MSDN/DNWINNT/HTML/D1E/S8538.HTM
- https://www.quppa.net/syscol/ (consulted for comparison; Windows 2000-specific row not present, XP Classic values used only as cross-check, not cited as fact)
