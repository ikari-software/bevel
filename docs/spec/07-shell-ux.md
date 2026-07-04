# Desktop, Taskbar, Start Menu & Systray UX

## Summary

This chapter specifies the observable behavior of the three shell surfaces — desktop, taskbar (with start menu and systray), and their popups — across the three era themes — Windows 2000 (v1 default), Windows XP/Luna (v1.1), Windows 11 (v1.2), staged per 05-theming.md — and the three platforms (macOS v1, Windows and Linux later). The governing principle is **era-faithfulness per theme**: a theme is not just pixels, it is behavior. The Win2000 theme has no taskbar button grouping, no tray overflow chevron, cascading Programs menus, and balloon tips; the XP theme adds grouping and the "hide inactive icons" chevron; the W11 theme is icon-only, centered, with an overflow flyout. All window/app/tray state flows through the PAL interfaces owned by 01-architecture.md §2.2 (`IWindowManager`, `IAppEnvironment`, `ISystemTrayHost`, `IDesktopEnvironment`); the code sketches in this chapter (`IWindowTracker`, `IAppCatalog`, `ITrayIconHost`) are **illustrative UX-facing pseudo-contracts** that map onto those canonical interfaces (mapping notes inline; gaps they expose are requirements *on* 01, tracked in Q7), so the UX layer is platform-blind. We explicitly do **not** replace Alt-Tab/Cmd-Tab, Mission Control, Spotlight, or the OS notification center; we render our own notifications only for shell-originated events.

---

## 1. Era-faithfulness rules (normative)

Behavioral differences between themes are part of the theme contract. A theme package declares a `BehaviorProfile` (an enum-keyed set of feature flags) alongside its resource dictionaries; the shell chrome reads flags, never theme names.

| Behavior | Win2000 (default) | Windows XP (Luna) | Windows 11 |
|---|---|---|---|
| Taskbar button style | Text + small icon, beveled | Text + icon, rounded Luna | Icon-only, centered |
| Button grouping | **Never** | Group when full (threshold) | Always grouped per app |
| Taskbar alignment | Left | Left | Centered (left = option) |
| Quick Launch toolbar | Yes (default on) | Yes (default off, as XP SP1+) | No (pinned icons instead) |
| Start button | `Start` + flag icon (lookalike) | Green rounded `start` | Centered logo button |
| Start menu layout | Single-column cascading | Two-column | Full-panel, pinned grid + search |
| Tray overflow | **None** — all icons visible | Chevron, "hide inactive icons" | Overflow flyout (`^`) |
| Clock format | `HH:mm` text only | `HH:mm` text only | Clock + date stacked |
| Clock click | Double-click → Date/Time dialog | Double-click → Date/Time dialog | Single-click → calendar flyout |
| Notifications | Balloon tips anchored to tray | Balloon tips (XP style) | Toast cards, bottom-right |
| Taskbar resize/drag | Draggable to any edge, resizable in row increments | Same, lockable ("Lock the Taskbar", default locked) | Bottom only |
| Desktop icon labels | Solid label background matching desktop color | Drop-shadowed transparent labels | Drop-shadowed transparent labels |

**R-ERA-1.** The UX layer MUST NOT special-case theme identity; all behavioral variation goes through `BehaviorProfile` flags so third-party themes can mix behaviors.

**R-ERA-2.** Per-flag user overrides are allowed (e.g., enable grouping under Win2000 theme) via Settings, but defaults MUST match the table above. Overrides live in user config, not the theme package.

**R-ERA-2b (branding placeholder — pending counsel).** The start-button marks named in the table above (the Win2000 flag-style glyph, the XP `start` pill, the W11 centered logo) and all user-facing theme names are **placeholder pending legal counsel** (05-theming.md §7; 00-master-plan.md #9). The product name is resolved — **Bevel** — but the start-button logo/mark and theme brand strings are not final; the engine references themes by internal id (`win2000`/`luna`/`win11`), never by user-facing name.

**R-ERA-3 (delivery scope).** The `BehaviorProfile` flag system (R-ERA-1) and the **Win2000 column** are the implementation baseline for milestones M1–M4 (09-engineering-plan.md) and are the only behavior set a shipped v1 theme exercises. The XP and W11 columns — XP grouping/`TaskbarGlomming` (R-TB-6), the XP chevron and W11 overflow flyout (R-TR-4), the W11 calendar flyout (R-CL-3), W11 per-monitor taskbars (R-MM-2), W11 toasts (R-NT-1, W11 half), and the XP/W11 start-menu layouts (§3.1) — ship **with their themes on the staged schedule 05-theming.md (authoritative) sets: Windows 2000 only at v1, Windows XP/Luna at v1.1, Windows 11 at v1.2.** The XP and W11 `BehaviorProfile` profiles nonetheless exist in the v1 engine from day one; until each theme ships, those flags are defined but no shipped theme sets them. Nothing from the XP/W11 columns may be scheduled into M2/M3.

Rejected alternative: a single "modern behavior, retro skin" model (like most Explorer skinners). Rejected because behavioral authenticity is the product's differentiator; grouping under a Win2000 skin reads as fake immediately.

---

## 2. Taskbar

### 2.1 Structure

Left→right (Win2000/XP): Start button · Quick Launch toolbar · window buttons · deskband area (future) · systray · clock. W11: widgets placeholder (not v1) · centered [start + pinned + running] · systray corner.

The taskbar is one Avalonia `Window` per participating monitor with:
- macOS: `NSWindow.level = .statusBar - 1` (above normal windows, below menu-bar-level panels), `collectionBehavior = [.canJoinAllSpaces, .stationary]`, no activation stealing (`NSWindowStyleMaskNonactivatingPanel` semantics via the helper). Work-area reservation via the Dock shim (Dock kept visible, tucked) / AX window-nudging fallback per 02-macos-platform.md §9 — normative there, not re-specified here.
- Windows: `WS_EX_TOOLWINDOW`, registered as an appbar via `SHAppBarMessage(ABM_NEW/ABM_SETPOS)` for work-area reservation.
- Linux X11: `_NET_WM_WINDOW_TYPE_DOCK` + `_NET_WM_STRUT_PARTIAL`. Wayland: `zwlr_layer_shell_v1` layer `top` with exclusive zone (position from the compositor chapter).

**R-TB-1.** Default taskbar height (1-row) comes from `Bevel.Metric.TaskbarHeight`, whose single source of truth is the metrics table in 05-theming.md §3 (at time of writing: 28 logical px Win2000, 30 XP/Luna, 48 W11); this chapter MUST NOT hardcode metric values (05 ENG-01/MET-01). 2000/XP resizable by dragging the inner edge in whole-row increments up to 50% of screen height; edge-docking (left/right/top/bottom) supported in 2000/XP profiles.

**R-TB-2.** Auto-hide supported in all profiles (2000: Taskbar Properties → "Auto hide"); when hidden, a 2 px reveal strip remains; reveal on pointer touch within 50 ms, hide 250 ms after pointer leaves and no popup is open.

### 2.2 Window list model (PAL)

> **Illustrative pseudo-contract — not the canonical PAL surface.** The canonical interface is `IWindowManager` in 01-architecture.md §2.2 (`ForeignWindow`/`ForeignWindowId` records, `ForegroundChanged` event). The sketch below shows what the taskbar view-model consumes; deltas it exposes against 01's shape — an active-window snapshot, `RestoreAsync`, a `DemandsAttention` flag, `AppId` as grouping key, and `SpaceOrDesktopId` — are requirements on 01's contract, tracked in Q7.

```csharp
// illustrative — canonical: 01-architecture.md §2.2 IWindowManager
public interface IWindowTracker
{
    IReadOnlyObservableCollection<ShellWindow> Windows { get; }   // z-order-independent, creation order
    ShellWindow? ActiveWindow { get; }
    event EventHandler<WindowChangedEventArgs> WindowChanged;      // Title/Icon/State/Attention deltas
    Task ActivateAsync(ShellWindow w);      // raise + focus, cross-Space on macOS
    Task MinimizeAsync(ShellWindow w);
    Task RestoreAsync(ShellWindow w);
    Task CloseAsync(ShellWindow w);         // polite close (WM_CLOSE / AXPress on close button / xdg close)
}

public sealed record ShellWindow(
    WindowKey Key,                // stable per-window id (HWND / (pid, AXUIElement hash) / toplevel handle)
    string AppId,                 // bundle id / exe path / .desktop id — grouping key
    string Title, ShellIcon Icon,
    WindowState State,            // Normal, Minimized, Maximized
    bool DemandsAttention,
    int MonitorIndex, string? SpaceOrDesktopId);
```

Per-platform sources (implemented in native helpers, see 01-architecture.md):

| Concern | macOS | Windows | Linux X11 | Linux Wayland |
|---|---|---|---|---|
| Enumerate | `CGWindowListCopyWindowInfo` (layer 0) + `AXUIElementCopyAttributeValue(kAXWindowsAttribute)` per app | `RegisterShellHookWindow` + `HSHELL_WINDOWCREATED/DESTROYED`; fallback `EnumWindows` | `_NET_CLIENT_LIST` root property + PropertyNotify | `ext-foreign-toplevel-list-v1` / `zwlr_foreign_toplevel_management_v1` |
| Activate | `NSRunningApplication.activate` + `AXRaise` action on the window element | `SwitchToThisWindow`/`SetForegroundWindow` (shell has foreground rights) | `_NET_ACTIVE_WINDOW` ClientMessage | `activate` request on the toplevel handle |
| Minimize/restore | `kAXMinimizedAttribute` set true/false | `SC_MINIMIZE`/`SC_RESTORE` via `WM_SYSCOMMAND` | `_NET_WM_STATE_HIDDEN` / `XIconifyWindow` | `set_minimized` / `unset_minimized` |
| Attention/flash | **Unsupported v1** (no public cross-app API for Dock-bounce observation) — see R-TB-8 | `HSHELL_FLASH` shell-hook message | `_NET_WM_STATE_DEMANDS_ATTENTION` + `WM_HINTS` urgency | `zwlr` state `activated`-adjacent; varies — best effort |
| Title/icon | `kAXTitleAttribute`; icon from `NSRunningApplication.icon` (app-level; macOS has no per-window icons) | `WM_GETTEXT`, `WM_GETICON`/class icon | `_NET_WM_NAME`, `_NET_WM_ICON` | toplevel `title`, `app_id` → icon theme lookup |

**R-TB-3.** One button per `ShellWindow`. macOS caveat: only windows exposed via AX with `kAXWindowRole` and subrole `kAXStandardWindowSubrole` become buttons; palettes/sheets are excluded. Requires the Accessibility TCC grant; without it the taskbar shows app-level buttons (one per `NSRunningApplication` with `activationPolicy == .regular`) as degraded mode, with an inline "grant permission" affordance.

**R-TB-4.** Button interactions: left-click on inactive → activate; left-click on active → minimize (toggle, exactly like Win2000). Middle-click: nothing in 2000 profile, close in W11 profile (authentic W11 closes on middle-click of a window preview; we accept the simplification). Right-click → window system menu: Restore / Move / Size / Minimize / Maximize / — / Close, with Move/Size disabled on platforms where we cannot drive them (macOS v1: disabled).

**R-TB-5.** Button sizing (2000): buttons shrink from max 160 px as the bar fills; below 60 px the title elides with `…`; below 34 px icon-only. No grouping, ever, in this profile — the bar simply gets crowded, and beyond icon-only minimum a vertical scroll arrow pair appears at the row edge (authentic multi-row alternative: user drags taskbar taller).

**R-TB-6.** Grouping (XP profile): when buttons would drop below 60 px, windows sharing `AppId` collapse into one button with a count badge and popup menu of window titles. Threshold and "always/never" as user options, matching XP's `TaskbarGlomming` semantics.

**R-TB-7.** Active button reflects `ActiveWindow` within 100 ms of a platform focus event. When our own windows (file manager) focus, they participate identically — the shell tracks them through the same PAL path, not a side channel.

**R-TB-8.** `DemandsAttention` renders as the era-correct flash: 2000/XP flashes the button caption (3 flashes then steady highlight, matching `FlashWindowEx` defaults); W11 amber pulse. On macOS v1 this feature is documented-unsupported; revisit if a viable Dock-notification observation technique emerges (Open question Q3).

### 2.3 Quick Launch

**R-TB-9.** Quick Launch (2000 profile, default visible): small-icon buttons backed by a user-ordered list of app references (`IAppCatalog` entries). Ships with: Show Desktop, file manager, default browser. "Show Desktop" minimizes all (macOS: iterate AX minimize; also offer "toggle" restore, like the Win2000 behavior after IE4 which restored on second click). Drag an app from Start menu/desktop onto Quick Launch to pin; drag off to remove (with the classic "poof"-free simple removal — no OS-specific animation).

---

## 3. Start menu

### 3.1 Per-theme structure

**Win2000 (single column, cascading, with the vertical side banner — recreated art, see legal chapter; the banner wordmark is the product name **Bevel**, but the banner mark and start-button logo are placeholder pending legal counsel per R-ERA-2b):**

| Item | Action / source |
|---|---|
| Windows Update *(hidden by default — anachronism)* | opens OS software-update (macOS: `open x-apple.systempreferences:com.apple.Software-Update-Settings.extension`) |
| **Programs ▸** | cascading tree from `IAppCatalog` (§3.2) |
| **Documents ▸** | 15 most recent files from PAL recents (macOS: `NSDocumentController`-adjacent — practically, our own MRU + `~/Library/Application Support/com.apple.sharedfilelist` parse is fragile → **our own MRU only**, fed by the file manager) |
| **Settings ▸** | → Control Panel (our settings app), Taskbar & Start Menu…, OS System Settings (deep link) |
| **Search ▸** | → For Files or Folders… (file manager search), On the Internet… |
| **Help** | opens our help/docs |
| **Run…** | run dialog: path/URL/app name; executes via `Process.Start` / `NSWorkspace.openApplication` |
| **Log Off <user>…** / **Shut Down…** | confirmation dialog → PAL session actions (macOS: Apple Events to loginwindow `kAELogOut`/`kAEShutDown`/`kAERestartRequest`; Windows: `ExitWindowsEx`; Linux: `org.freedesktop.login1.Manager` `PowerOff`/`Reboot`, session manager logout) |

**R-SM-1.** Cascade behavior: submenus open after 400 ms hover (`SPI_GETMENUSHOWDELAY` default) or immediately on click; "banana" triangle-safe diagonal pointer tracking so moving toward an open submenu does not close it.

**R-SM-2.** Win2000 "Personalized Menus" (hiding rarely used items behind a chevron) is **off by default and not implemented in v1**. Rationale: universally hated, high implementation cost (usage tracking), low authenticity value. Rejected alternative: faithful implementation — cut for v1, flag reserved in `BehaviorProfile`.

**XP (two column):** left column = pinned list (user-managed) + MFU list (frequency-tracked, top 6) + "All Programs ▸" cascading into the same catalog tree; right column = My Documents, My Pictures, My Computer (→ file manager roots), Control Panel, Search, Run…; header shows user display name + account picture (macOS: `NSUserName()` + user picture via `dscl . -read /Users/$USER JPEGPhoto` fallback generic); footer Log Off / Turn Off Computer.

**W11 (panel):** search box (filters catalog by name, fuzzy prefix), "Pinned" grid (6×3 page, paginated), "All apps" alphabetical list, "Recommended" (recent files from our MRU), user chip + power button. No web search integration, ever.

**R-SM-3.** Open triggers: click Start button; `Ctrl+Esc` on all platforms; Win/Super key where interceptable (Windows: `WH_KEYBOARD_LL` hook when we are the registered shell; Linux X11: XGrabKey on Super_L; Wayland: compositor keybinding; macOS: **no Cmd remap** — `Ctrl+Esc` and an optional user-configurable Carbon `RegisterEventHotKey` combo, default `⌥⌘Space`-free to avoid Spotlight collision: `⌃⌘Space` is emoji picker, so default **`⌥Esc`**). Escape closes; arrow keys navigate; typing in 2000/XP menus does first-letter navigation (no search box — authentic).

### 3.2 App catalog (PAL)

> **Illustrative pseudo-contract.** Canonical surface: `IAppEnvironment` in 01-architecture.md §2.2 (`EnumerateInstalledAppsAsync`, `LaunchAsync`). Deltas this sketch exposes — a live-watched observable collection, the hierarchical `ProgramsTree` view, and category metadata — are requirements on 01's contract, tracked in Q7.

```csharp
// illustrative — canonical: 01-architecture.md §2.2 IAppEnvironment
public interface IAppCatalog
{
    IReadOnlyObservableCollection<CatalogApp> Apps { get; }        // live: install/uninstall watched
    IReadOnlyList<CatalogFolder> ProgramsTree { get; }             // hierarchical view for cascading menu
    Task LaunchAsync(CatalogApp app, LaunchOptions? opts = null);
}
public sealed record CatalogApp(string AppId, string Name, ShellIcon Icon,
                                string? Category, string SourcePath);
```

| Platform | Source | Tree construction | Watch |
|---|---|---|---|
| macOS | `/Applications`, `/System/Applications`, `~/Applications`; metadata via `NSWorkspace`/`LSCopyDefaultApplicationURLForContentType`; bundle `Info.plist` for name/icon | No native hierarchy → synthesize: top-level alphabetical + folders mirroring actual subfolders of `/Applications` (e.g. `Utilities ▸`); optional curated category mapping from `LSApplicationCategoryType` | FSEvents on the three roots |
| Windows | `%APPDATA%` and `%ProgramData%\Microsoft\Windows\Start Menu\Programs` (.lnk shell links, resolved via `IShellLink`) | Mirror the folder tree verbatim — this *is* the Programs menu | `ReadDirectoryChangesW` |
| Linux | `.desktop` entries in `$XDG_DATA_DIRS/applications` per Desktop Entry Spec | freedesktop menu spec `Categories=` mapping to a fixed folder set (Accessories, Internet, Office, …) | inotify |

**R-SM-4.** Launch on macOS uses `NSWorkspace.openApplication(at:configuration:)` in the helper (not `Process.Start` on the binary) so LaunchServices activation, TCC attribution, and single-instance semantics behave normally.

---

## 4. Systray

### 4.1 Composition

The tray area = **mirrored native icons** + **first-party widgets**, visually indistinguishable. Mirroring mechanics (capture, click forwarding, menu-bar auto-hide on macOS; SNI/XEmbed on Linux; `Shell_TrayWnd` takeover + `TaskbarCreated` broadcast on Windows) are owned by the systray-capture chapter; this section owns UX.

> **Illustrative pseudo-contract.** Canonical surface: `ISystemTrayHost` in 01-architecture.md §2.2 (`ForwardActivationAsync` covers `ClickAsync`/`ShowNativeMenuAsync` below); first-party widgets (R-TR-3) are shell-internal, not PAL items. Any residual deltas are tracked in Q7.

```csharp
// illustrative — canonical: 01-architecture.md §2.2 ISystemTrayHost
public interface ITrayIconHost
{
    IReadOnlyObservableCollection<TrayIcon> Icons { get; }   // mirrored + first-party, unified
    Task ClickAsync(TrayIcon icon, TrayButton button, bool doubleClick, PixelPoint anchor);
    Task ShowNativeMenuAsync(TrayIcon icon, PixelPoint anchor);   // forwards to real item
}
```

**R-TR-1.** Icon size 16×16 logical (2000/XP), 16 with 24 px hit target (W11). Mirrored icons render the captured pixels; hover tooltip shows the native item's title (macOS: AX title of the status item; Windows: `NOTIFYICONDATA.szTip`; Linux: SNI `Title`/`ToolTip`).

**R-TR-2.** Click routing: left/right/double clicks forward to the native item at the *original* item's coordinates (synthesized events per the capture chapter). Latency budget: ≤ 120 ms from our click to native reaction, else show a busy cursor tick. Menus raised by native items appear at native coordinates on macOS v1 (they are real `NSMenu`s we cannot reparent) — the menu bar is momentarily revealed if hidden; accepted cosmetic compromise, documented.

**R-TR-3.** First-party widgets (all optional, Settings-toggleable): **Clock** (always, rightmost), **Volume**, **Network**, **Battery** (auto-hidden on desktops without battery), **Input/keyboard layout** (hidden by default). Data/actions per platform:

| Widget | macOS | Windows | Linux |
|---|---|---|---|
| Volume | CoreAudio `AudioObjectGet/SetPropertyData` (`kAudioHardwareServiceDeviceProperty_VirtualMainVolume`) | `IAudioEndpointVolume` (WASAPI) | PipeWire via `wpctl`/libpulse |
| Network | `NWPathMonitor` + `CWWiFiClient` (SSID needs Location TCC — degrade to generic icon) | `INetworkListManager` | NetworkManager DBus `org.freedesktop.NetworkManager` |
| Battery | `IOPSCopyPowerSourcesInfo` | `GetSystemPowerStatus` | UPower `org.freedesktop.UPower` |

Volume widget: left-click → era-styled slider popup (2000: vertical slider + Mute checkbox); double-click → full mixer (v1: deep link to OS sound settings); scroll wheel over icon adjusts ±2%.

**R-TR-4.** Overflow per profile: 2000 — none, tray grows leftward without limit (authentic). XP — chevron button collapsing icons idle > 60 s ("hide inactive"), per-icon always-show/always-hide overrides. W11 — `^` flyout grid; drag icons between flyout and bar to pin. Ordering: user drag-reorder persisted by icon identity (macOS: bundle id + AX identifier; Windows: GUID/`(hWnd,uID)`; Linux: SNI service name).

### 4.2 Clock & calendar

**R-CL-1.** Clock renders local time `HH:mm` (respects OS 12/24 h locale). Hover tooltip: full long date (`dddd, MMMM d, yyyy`). Timer aligned to minute boundaries (no per-second wakeups; ≤ 1 wake/min).

**R-CL-2.** 2000/XP: double-click opens our themed **Date/Time Properties** dialog — a faithful recreation (month grid, year spinner, analog clock second hand, timezone dropdown) in **read-only** mode; a "Change…" button deep-links to OS settings (`x-apple.systempreferences:com.apple.Date-Time-Settings.extension`, `ms-settings:dateandtime`, `gnome-control-center datetime`). Rationale: setting system time needs privilege escalation on every platform; recreating the dialog visually preserves the era feel without owning `systemsetup -settime`. Rejected: privileged write path via helper — cost/benefit fails for v1.

**R-CL-3.** W11: single-click opens calendar flyout (month view, today highlighted, prev/next month) above the tray. No agenda/calendar-account integration in v1.

---

## 5. Desktop

### 5.1 Surface & backing folder

One borderless window per monitor at desktop level (macOS `NSWindow.level = kCGDesktopWindowLevel + 1`, `.canJoinAllSpaces + .stationary`; Windows: parented under `Progman`/`WorkerW` when replacing, plain bottom-most `WS_EX_NOACTIVATE` otherwise; X11 `_NET_WM_WINDOW_TYPE_DESKTOP`; Wayland layer-shell `background`). Wallpaper rendering (per-theme defaults, fill modes) is specced in the theming chapter.

**R-DK-1.** Backing folder = the platform desktop directory (`~/Desktop`, `Environment.SpecialFolder.Desktop`, `$XDG_DESKTOP_DIR`), watched live (FSEvents / `ReadDirectoryChangesW` / inotify). Files appearing externally get the next free grid cell.

**R-DK-2.** macOS coexistence: when running alongside real Finder (dev mode / partial adoption), we set `defaults write com.apple.finder CreateDesktop -bool false && killall Finder` (with user consent prompt) so Finder stops drawing its own desktop icons behind ours; restored on uninstall. When we are the login shell (Finder not running), this is moot.

### 5.2 Icon grid

**R-DK-3.** Grid cell size comes from `Bevel.Metric.IconGrid.Cell` (single source of truth: 05-theming.md §3; at time of writing 75×75 logical px for Win2000/XP — matching the authentic `HKCU\Control Panel\Desktop\WindowMetrics` `IconSpacing` = −1125 twips = 75 px — and 76×76 for W11). Icons render 32×32 (2000/XP) / 48×48 (W11) within the cell, 2-line label with middle-ellipsis; flow top-to-bottom then left-to-right, origin top-left.

**R-DK-4.** Context menu (desktop background): Active Desktop ▸ *(absent — never shipping)*; **Arrange Icons ▸** (by Name/Type/Size/Date, **Auto Arrange** toggle, **Align to Grid** toggle — Align to Grid appeared in Win2000; both persisted), Line Up Icons, Refresh, —, Paste / Paste Shortcut, —, **New ▸** (Folder, Shortcut/Alias, text file; template list extensible), —, Properties (→ display/theme settings).

**R-DK-5.** Layout persistence: our own store at `{ConfigDir}/desktop-layout.json`, keyed by `(monitorSetHash, resolution)` so docking/undocking a laptop restores per-configuration layouts. We do **not** read or write `.DS_Store`. Positions survive rename (keyed by inode/file-id where available, path fallback).

**R-DK-6.** Selection: click, Ctrl-click (⌘ on macOS keyboards — modifier map follows host conventions), rubber-band; F2/Enter-rename per host convention (macOS: Return renames, ⌘O opens — we follow *host* muscle memory here, not era; era wins for visuals, host wins for typing/modifiers). Double-click opens via `IAppCatalog`/file associations (file manager chapter).

### 5.3 Special icons & shortcuts

| Concept | macOS | Windows | Linux |
|---|---|---|---|
| My Computer | virtual icon → file manager "Computer" root (volumes via `NSFileManager.mountedVolumeURLs`) | `::{20D04FE0-…}` equivalent, our own virtual root | virtual root from GVfs/udisks2 mounts |
| Recycle Bin | `~/.Trash` (Full Disk Access needed to list; else icon opens FM at Trash with permission prompt); trash via `NSWorkspace.recycle` | `SHQueryRecycleBin`/`SHEmptyRecycleBin` | freedesktop Trash spec `~/.local/share/Trash` |
| My Documents | `~` (home folder) — maps to the home directory, not `~/Documents` (00-master-plan.md #11b, 06-file-manager.md FM-040) | Known folder | `$XDG_DOCUMENTS_DIR` |
| Shortcut (new) | **Finder alias** created via `NSURL` bookmark data (interop with real Finder) — not symlink, decision: aliases survive moves; symlink offered in Advanced submenu | `.lnk` via `IShellLink` | `.desktop` `Type=Link` / symlink |
| Shortcut overlay | Our arrow-overlay badge on alias/lnk/desktop-link icons (recreated art) | same | same |

**R-DK-7.** Volumes/removable media appear as desktop icons on macOS (Finder-authentic *and* Win-era plausible) — toggleable; eject via context menu → `NSWorkspace.unmountAndEjectDevice`.

---

## 6. Drag-and-drop matrix

All internal DnD uses Avalonia's DnD with `DataFormats.Files`; the PAL bridges to native pasteboards so third-party apps interoperate.

| Source ↓ / Target → | Desktop | File manager | Taskbar button | Quick Launch/Pinned | Start menu | External app |
|---|---|---|---|---|---|---|
| **Desktop icon** | move (reposition) | move/copy per modifier | hover 500 ms → activate window, then drop into it (native forward) | pin | pin (creates entry in pinned list) | native file drag |
| **File manager item** | copy/move per modifier + drop-to-cell | per FM rules | hover-activate | pin | pin | native file drag |
| **External app file drag** | copy → backing folder + cell placement | per FM rules | hover-activate | — | — | n/a |
| **Text/URL from app** | creates `.webloc`/`.url`/`.desktop` internet shortcut | same | — | — | — | n/a |

Modifier rules (era-authentic Windows semantics, applied on all hosts): same-volume = move, cross-volume = copy; `Ctrl` forces copy, `Shift` forces move, `Alt`/`Ctrl+Shift` forces shortcut/alias; drag with right button (or ⌃-drag on macOS) shows Move/Copy/Create Shortcut menu on drop.

Native format bridging: macOS `NSPasteboard` `public.file-url` (+ `NSFilePromiseReceiver` for promised files from Mail/browsers — **required**, browsers drag promises); Windows `CF_HDROP` + `CFSTR_FILEDESCRIPTORW`/`CFSTR_FILECONTENTS` for virtual files; Linux XDND `text/uri-list`, Wayland `wl_data_device` with the same MIME.

**R-DND-1.** Drop onto a taskbar button never opens the file directly; it activates the window after 500 ms hover and continues the drag (authentic Windows behavior).

**R-DND-2.** Avalonia's macOS DnD path must be verified early for file-promise support; if missing, the native helper owns an `NSDraggingDestination` overlay view (spike scheduled in 09-engineering-plan.md).

---

## 7. Multi-monitor

**R-MM-1.** 2000/XP profiles: taskbar + Start menu on the **primary** monitor only (authentic); desktop icons on all monitors (primary monitor's grid fills first). Primary = OS main display (macOS: `NSScreen.screens[0]`, the one with the menu bar). An opt-in anachronistic secondary taskbar (UltraMon-style) is available under 2000/XP but is **off by default** (decided 2026-07-04; see Q1).

**R-MM-2.** W11 profile: "Show taskbar on all displays" option (default on, matching W11); secondary bars show that monitor's windows only or all windows (option, default "taskbar where window is open" + primary shows all). Clock on all bars, tray on primary only.

**R-MM-3.** Monitor topology changes (hotplug, resolution) re-anchor bars within 500 ms and re-run work-area reservation; desktop layouts swap per R-DK-5 keying.

**R-MM-4.** Popups (start menu, calendar, balloon) always open on the monitor of their anchor and clamp to its work area.

---

## 8. Notifications & balloon tips

**Decision:** the shell renders its **own** notifications **only for shell-originated events**; it does not intercept, mirror, or replace the OS notification system. macOS makes interception impossible anyway (`UNUserNotificationCenter` is per-app, Notification Center DB is SIP-adjacent private); on Windows/Linux, becoming the notification host (`org.freedesktop.Notifications` owner, toast host) is deferred to a post-v1 flag for Linux only, where it is standard practice for shells. Rejected for v1: full notification-host role — it doubles the surface area and macOS can't participate, breaking cross-platform parity of the pillar.

Shell-originated events that raise balloons: volume device changed, network joined/lost, media mounted/ejected ("You can now safely remove…" — yes, really), tray-mirror errors (item crashed / permission lost), updates available, TCC permission degradation ("Desktop icons unavailable — grant Full Disk Access").

**R-NT-1.** 2000/XP: balloon tip anchored to the relevant tray icon with pointer tail; max 1 visible, queue depth 5, timeout 10 s, click = action, close box present (XP style). W11: toast card slides from bottom-right, stack of 3.

**R-NT-2.** Balloons never steal focus and are excluded from the window list; they respect a "quiet mode" toggle in tray context menu.

**R-NT-3.** API: `IShellNotifier.Show(NotificationRequest)` internal only — no third-party API in v1 (prevents becoming an accidental notification daemon).

---

## 9. Keyboard navigation & accessibility

### 9.1 Keyboard map

| Action | Windows host | macOS host | Linux host |
|---|---|---|---|
| Open Start | `Ctrl+Esc`, `Win` | `Ctrl+Esc`, `⌥Esc` (configurable; Carbon `RegisterEventHotKey`) | `Ctrl+Esc`, `Super` (compositor binding) |
| Focus taskbar | `Win+T` | configurable, default `⌥⌘T` off | `Super+T` |
| Cycle taskbar buttons | arrows once focused; `Enter` activate; `Shift+F10`/menu-key context menu | same (Avalonia focus) | same |
| Show desktop | `Win+D` | hotkey configurable | `Super+D` |
| Run dialog | `Win+R` | `⌥⌘R` default-off | `Super+R` |
| Alt-Tab | **not ours** — OS keeps it | OS `⌘Tab` untouched | compositor's |

**R-KB-1.** Every shell surface is fully keyboard-operable: Start menu (arrows/Enter/Esc/first-letter), tray (arrows + Enter = left-click, Shift+F10 = right-click/menu), desktop (arrow-key grid navigation, Home/End, type-ahead selection).

**R-KB-2.** Global hotkeys on macOS must never require remapping ⌘; all defaults avoid collisions with Spotlight (`⌘Space`), input-source switch (`⌃Space`), and Mission Control keys.

### 9.2 Accessibility (owner-drawn shell)

Avalonia 11 exposes an automation tree (`AutomationPeer`) mapped to **UIA on Windows** and **NSAccessibility on macOS**; **Linux AT-SPI support is currently absent upstream** — this is a known, accepted v1 gap (Linux ships later; track upstream, budget for contribution in 09-engineering-plan.md).

**R-AX-1.** Every interactive element sets `AutomationProperties.Name` (localized), correct `ControlType` (Button for taskbar buttons and tray icons, MenuItem for start-menu entries, ListItem for desktop icons), and state (selected/expanded).

**R-AX-2.** Mirrored tray icons are pixels, but their automation node carries the *native* item's accessible title (from AX/SNI metadata), so VoiceOver reads "Dropbox, menu bar item, button" — not "image".

**R-AX-3.** VoiceOver smoke pass (taskbar traversal, start menu open→launch, desktop icon selection) is a release gate for every macOS release; Narrator equivalently on Windows.

**R-AX-4.** Honor OS reduced-motion (`NSWorkspace.accessibilityDisplayShouldReduceMotion`, `SPI_GETCLIENTAREAANIMATION`) — balloon/menu animations degrade to instant. High-contrast: the Win2000 theme family includes recreated "High Contrast Black/White" schemes, activated automatically when the OS signals increased-contrast.

---

## 10. What we do NOT replace (coexistence contract)

| OS feature | Position | Coexistence notes |
|---|---|---|
| macOS ⌘Tab switcher | Keep | Complements taskbar; both drive the same focus state |
| Mission Control / Spaces | Keep | Taskbar shows current-Space windows by default; "all Spaces" option; activating a window on another Space lets macOS switch Spaces naturally |
| Spotlight | Keep | Start menu search (W11 profile) searches apps/files via our index only; never intercepts ⌘Space |
| Notification Center | Keep | See §8 |
| macOS Dock | **Kept visible** (tucked/small on the taskbar edge) as the work-area reservation shim — 02-macos-platform.md §9, Req 9.1 | Never killed, and **never auto-hidden by default**: an auto-hidden Dock reserves no `visibleFrame` inset, which destroys the shim (02 Req 9.2). Auto-hide is valid only when the user explicitly selects the window-nudging or overlap-allowed strategy (02 Req 9.3). Dock position/size changes managed with consent |
| macOS menu bar | Auto-hidden per systray-capture chapter | Revealed on demand (pointer to top edge / native menu open) |
| Windows Alt-Tab, Task View | Keep (we are shell, these live in Explorer… when Explorer is gone, Alt-Tab is provided by the OS `Alt-Tab` legacy switcher — verify; fallback: minimal switcher post-v1) | Flagged risk R-5 |
| Linux compositor switcher/overview | Keep (X11/WM case); if we bundle a compositor (Wayland chapter's call), that chapter owns it | |
| Lock screen / login | Never | |

**Consistency note (normative pointer):** the macOS Dock strategy is specified in exactly one place — 02-macos-platform.md §9 (Req 9.1–9.3). This table, 08-os-interop.md §4.1, and 09-engineering-plan.md M2 are derived views and MUST be checked against 02 §9 whenever it changes; at last review, 08 §4.1 ("Dock auto-hide + shim") and 09 M2 ("auto-hide our bar in sync") still carried the pre-correction wording and need the same fix as this row.

---

## Risks

1. **macOS AX-based window list fragility.** Per-window control depends on the Accessibility TCC grant and per-app AX quality (Electron/Java apps expose windows inconsistently). Mitigation: app-level degraded mode (R-TB-3), per-app quirk table in the helper, telemetry on AX failures.
2. **Taskbar flash/attention parity on macOS is unsolved** (no public cross-app attention API). Shipping without it weakens era-authenticity; users may not notice, but spec honesty requires flagging it.
3. **Avalonia gaps:** macOS file-promise DnD (R-DND-2) and NSAccessibility completeness are unproven at this fidelity; Linux AT-SPI is absent upstream. Early spikes are scheduled in 09-engineering-plan.md; each has a helper-process fallback but at real cost.
4. **Tray click-forwarding latency/misses** (menu appears at native coordinates, hidden-menu-bar reveal flash) may read as jank; the 120 ms budget (R-TR-2) needs validation against ScreenCaptureKit + synthesized-event round trips.
5. **Windows-as-shell Alt-Tab:** with Explorer gone, some switcher/UX pieces vanish with it; inventory of Explorer-owned surfaces (Alt-Tab availability, Win+key handling) needed before the Windows port is scoped.
6. **Recents/MFU privacy:** our own MRU + launch-frequency tracking is a local privacy surface; must be documented, clearable, and off in a future "private mode".

## Open questions

1. **Q1 — Secondary taskbars under 2000/XP profiles:** **RESOLVED (2026-07-04):** shipped as an opt-in "UltraMon-style" secondary bar, **off by default** under 2000/XP profiles (authentic primary-only remains the default). Reflected in R-MM-1.
2. **Q2 — Win2000 theme Start-menu banner text:** product name **RESOLVED — "Bevel"** (00-master-plan.md #1); the banner wordmark is "Bevel". **Still open / blocked on legal counsel:** the banner *mark*, the start-button logo, and user-facing theme brand names (trade-dress distance sign-off) — placeholder until counsel clears them (05-theming.md §7, 00-master-plan.md #9; see R-ERA-2b).
3. **Q3 — macOS attention/bounce detection:** is a private-API or Dock-AX-observation technique (reading Dock icon bounce state via AX) acceptable under our private-API policy (01-architecture.md)? If yes, R-TB-8 upgrades from "unsupported".
4. **Q4 — Documents/Recommended sourcing on macOS:** ship with our-own-MRU only (spec'd), or additionally parse `sharedfilelist` recents (fragile, undocumented format) for day-one usefulness before the file manager has accumulated history?
5. **Q5 — Linux notification host:** post-v1 flag is spec'd; confirm whether "being the shell" on Linux makes owning `org.freedesktop.Notifications` effectively mandatory for credibility, pulling it into the Linux v1 scope.
6. **Q6 — Show Desktop semantics on macOS:** **RESOLVED (2026-07-04):** minimize-all (as spec'd in R-TB-9) is confirmed acceptable; the destructive window-state change is accepted. No macOS-native "reveal desktop" scatter (private-API-only path declined).
7. **Q7 — PAL interface consolidation (blocking on the 01 owner):** this chapter's sketches are now marked illustrative, but the deltas they expose must be absorbed into the canonical 01-architecture.md §2.2 contracts: `IWindowManager` needs an active-window snapshot, `RestoreAsync`, `DemandsAttention`, `AppId` grouping key, and Space/virtual-desktop identity; `IAppEnvironment` needs live install/uninstall watching, a `ProgramsTree` hierarchical view, and category metadata (or a separate app-catalog interface). Chapters 02/03/04/06/08 sketch further variants (`IWindowService`, a differently-shaped `IWindowTracker`, `IWorkAreaReserver`, `IAppRegistry`, `IRevealService`, `IShellAutomation`, …) — 01 must become the single definition point and every other chapter a reference, or each sketch must carry the same "illustrative" marker used here.
