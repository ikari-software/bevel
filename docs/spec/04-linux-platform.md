# Linux Platform Integration

Chapter 04 of the Bevel spec. Cross-references: overall process/PAL architecture in `01-architecture.md`, macOS platform in `02-macos-platform.md`, theming in `05-theming.md`, file manager behavior in `06-file-manager.md`, packaging/rollout schedule in `09-engineering-plan.md`. This chapter specs *how* Bevel integrates with Linux; *when* is the engineering plan's business.

## Summary

Linux ships after macOS, but the PAL contracts it needs must exist from day one. The headline positions taken here:

1. **X11 and Wayland are co-equal first-class targets** (owner directive, 2026-07-04 — supersedes the earlier X11-first stance). X11 is the lower-risk half: EWMH gives us everything a shell needs — `_NET_WM_WINDOW_TYPE_DESKTOP`/`DOCK` window types, `_NET_WM_STRUT_PARTIAL` work-area reservation, and full window management via client messages — on Avalonia's existing X11 backend. Wayland is co-equal in ambition but carries the heavier engineering (see position 3); both ship as first-class on the Linux track rather than Wayland trailing as a later gate.
2. **Bevel is not a window manager.** On X11 we require a host WM (any EWMH-compliant one); the optional "Bevel Session" bundles a reference WM (Openbox/labwc). On Wayland, "being the shell" without being the compositor is only possible on compositors that expose `zwlr_layer_shell_v1` + `zwlr_foreign_toplevel_management_v1` — i.e., the wlroots family and KWin, **not** GNOME Mutter. GNOME-on-Wayland is explicitly unsupported as a host.
3. **Wayland ships first-class on the Linux track, funded up front — not gated behind X11.** Avalonia has no production Wayland backend today and XWayland cannot create layer surfaces, so there is no shortcut: a real `Bevel.Pal.Linux.Wayland` backend on `zwlr_layer_shell_v1` (anchored panel/desktop surfaces + exclusive-zone work-area) and `zwlr_foreign_toplevel_management_v1` (taskbar window list + control) is a funded line item (contribute to/extend Avalonia's experimental Wayland work). Owning the *whole* Wayland session by bundling a compositor (wlroots/Smithay) is a later "Bevel Session" phase, out of v-Linux scope. Wayland host support is the wlroots family + KWin/Plasma; **GNOME/Mutter is unsupported as a host** (position 2) and no first-class commitment changes that.
4. **Tray = StatusNotifierItem host over DBus, with an XEmbed fallback** on X11 only. We own `org.kde.StatusNotifierWatcher` and render items ourselves; menus via `com.canonical.dbusmenu`.
5. **File-manager integration is all freedesktop standards:** `xdg-mime` default for `inode/directory`, ownership of the `org.freedesktop.FileManager1` DBus name for "reveal in file manager", the freedesktop Trash spec, and GVfs (via GIO) for network/virtual mounts.
6. **Packaging is native only** (.deb/.rpm/AUR/tarball). Flatpak and Snap cannot ship a shell — sandboxing, session-file installation, and DBus name ownership all break — and we do not pretend otherwise.

Throughout, requirements are numbered `LNX-nn`.

---

## 1. Display-server strategy: X11 and Wayland, co-equal

### 1.1 Decision

**LNX-01.** Linux supports X11 and Wayland as co-equal first-class session types (owner directive, 2026-07-04). Both are in scope for the first Linux release; Wayland is not deferred behind X11. Wayland host support covers the wlroots family and KWin/Plasma via `zwlr_layer_shell_v1` + `zwlr_foreign_toplevel_management_v1`; GNOME/Mutter is unsupported as a host (LNX-10, §1 position 2). The §3.4 material below is retained as the Wayland *implementation* detail, not as a deferral gate — its "v-later" framing is superseded by this requirement.

**Rationale:**

- **Avalonia reality.** Avalonia 11's Linux backend is X11. There is no production Wayland backend as of this writing (experimental work exists upstream; it does not implement `wlr-layer-shell`). Under a Wayland session, Avalonia apps run through XWayland — and XWayland clients *cannot* be layer surfaces, cannot set exclusive zones, and their EWMH struts/window-types are ignored or only partially honored by Wayland compositors. A panel via XWayland is a broken panel. There is no incremental path from XWayland to "real" Wayland support; it's a backend project either way.
- **X11 is fully specified for our use case.** EWMH (`_NET_WM_*`) was designed exactly for third-party desktops, panels, and pagers. Every behavior we need — desktop window, dock reservation, window list, activation, minimize/restore — has a standard, WM-agnostic protocol.
- **The audience overlap is favorable.** Users who install a retro-styled replacement shell skew toward distros/WMs where X11 remains first-class (Xfce, MATE, window-manager-only setups, XLibre/X11 forks). GNOME is dropping X11 sessions, but GNOME users are the least likely to replace their shell anyway.

**Rejected alternatives:**

- *Wayland-only (skip X11).* Rejected: X11 is the lower-risk half and remains first-class on many target distros/WMs, so dropping it forfeits the reachable near-term audience — and it still would not reach GNOME (Mutter has no layer-shell). Co-equal X11 + Wayland is the chosen path: both ship first-class on the Linux track, neither to the exclusion of the other.
- *Ship our own Wayland compositor up front.* Rejected for the first Linux release: writing/maintaining a compositor (even wlroots- or Smithay-based) is a product in itself. Reserved as the *session* strategy for Wayland later (§3.3 — the "Bevel Session" phase), by bundling an existing compositor, not writing one.
- *X11-only forever.* Rejected: X11 is in maintenance mode; distros are removing Xorg sessions. Wayland ships first-class up front alongside X11, and the PAL interfaces in this chapter are written so the Wayland implementation slots in without touching shell logic.

### 1.2 What this means structurally

Per `01-architecture.md`, all OS integration goes through the PAL. On Linux the PAL splits into:

```
Bevel.Pal.Linux/            C# — DBus (Tmds.DBus.Protocol), GIO P/Invoke, xdg-* glue
Bevel.Pal.Linux.X11/        C# — XLib/XCB P/Invoke: EWMH, struts, XEmbed
Bevel.Pal.Linux.Wayland/    C# + backend work — layer-shell, foreign-toplevel (funded up front)
bevel-helper-x11 (optional)      native helper only if XEmbed-in-Avalonia proves unstable (§5.3)
```

Unlike macOS (where accessibility and ScreenCaptureKit force a Swift helper — see `02-macos-platform.md`), Linux integration is almost entirely doable in-process from C#: DBus is just a socket protocol, and XLib/XCB are stable C ABIs. **LNX-02:** default to in-process C# with P/Invoke; spawn a native helper only where crash-isolation is empirically needed (XEmbed is the one candidate — legacy tray icons can kill their embedder with BadWindow errors; we mitigate in-process first with a dedicated X error handler, helper as fallback).

**Estimate impact.** Making Wayland co-equal and first-class adds up-front cost the earlier X11-first framing didn't carry: on top of the prior Linux estimate (24–34 ew), budget roughly **+8–14 ew** for the `Bevel.Pal.Linux.Wayland` backend (layer-shell + foreign-toplevel plumbing atop Avalonia's experimental Wayland work), to be firmed at Linux-track kickoff (see `09-engineering-plan.md`).

---

## 2. X11 integration

### 2.1 Window roles via EWMH

Bevel's top-level surfaces on X11 and the properties they must set:

| Surface | `_NET_WM_WINDOW_TYPE` | Other properties | Notes |
|---|---|---|---|
| Desktop (wallpaper + icon grid) | `_NET_WM_WINDOW_TYPE_DESKTOP` | `_NET_WM_STATE_SKIP_TASKBAR`, `_NET_WM_STATE_SKIP_PAGER`, `_NET_WM_STATE_STICKY`; no decorations (`_MOTIF_WM_HINTS`) | One per monitor, or one root-spanning window; sized to full screen. WM keeps DESKTOP windows at the bottom of the stack. |
| Taskbar | `_NET_WM_WINDOW_TYPE_DOCK` | `_NET_WM_STRUT_PARTIAL`, `_NET_WM_STRUT` (legacy dual-set), SKIP_TASKBAR, SKIP_PAGER, STICKY | Struts per §2.2. |
| Start menu / popups | `_NET_WM_WINDOW_TYPE_POPUP_MENU` or `_NET_WM_WINDOW_TYPE_DIALOG` with override-redirect as Avalonia does natively | — | Avalonia's popup layer already handles this; verify focus behavior with `_NET_ACTIVE_WINDOW`. |
| File manager windows | `_NET_WM_WINDOW_TYPE_NORMAL` | — | Ordinary app windows. |

**LNX-03.** Avalonia does not expose `_NET_WM_WINDOW_TYPE` or struts in its public API. `Bevel.Pal.Linux.X11` obtains the native `XID` from Avalonia (`IPlatformHandle` on the `TopLevel`) and sets these properties directly via `XChangeProperty`. This is supported and stable; document it as an internal contract test (property survives window re-map).

### 2.2 Work-area reservation: `_NET_WM_STRUT_PARTIAL`

The taskbar reserves screen edge space so maximized windows don't cover it — the thing macOS lacks entirely (see the Dock-shim discussion in `02-macos-platform.md`) and Linux does best.

**LNX-04.** The taskbar window sets both `_NET_WM_STRUT_PARTIAL` (12 CARD32s: `left, right, top, bottom, left_start_y, left_end_y, right_start_y, right_end_y, top_start_x, top_end_x, bottom_start_x, bottom_end_x`) and legacy `_NET_WM_STRUT` (4 CARD32s) for pre-partial WMs. Values are in *root-window coordinates*, which matters on multi-monitor: a bottom taskbar on a monitor above another monitor must NOT set a bottom strut (it would carve into the lower monitor). Strut edges may only be used when the taskbar sits on the corresponding edge of the *root* bounding box.

**LNX-05.** Multi-monitor policy: taskbar on primary monitor by default (`_NET_PRIMARY` isn't a thing; use RandR primary output via `XRRGetOutputPrimary`), optional per-monitor taskbars later. For a taskbar on a non-root-edge monitor edge, fall back to no strut + `_NET_WM_STATE_ABOVE` and accept overlap (same mitigation class as macOS).

**LNX-06.** On taskbar hide/auto-hide, struts must be updated (set to 0-height sliver or removed) so the work area is reclaimed; on exit (including crash — see supervisor in `01-architecture.md`), the window destruction removes struts automatically, which is the nice property of X11: no persistent state to clean up.

### 2.3 Window management for the taskbar (we are a pager, not a WM)

**Decision (LNX-07): Bevel does not manage windows on X11.** It requires a running EWMH/ICCCM-compliant WM and acts as a *pager* in EWMH terms. Rationale: writing a WM is out of scope and unnecessary — every behavior the taskbar needs is a client message to the root window. Rejected alternative: embedding a WM library (e.g., forking a small WM) — needless coupling; users in WM-only setups already have strong WM preferences.

Taskbar data sources (root window properties, watched via `PropertyNotify`):

- `_NET_CLIENT_LIST` / `_NET_CLIENT_LIST_STACKING` — window list.
- `_NET_ACTIVE_WINDOW` — highlight active task.
- Per-window: `_NET_WM_NAME` (UTF8), `WM_CLASS` (app identity for grouping + icon lookup via .desktop `StartupWMClass`), `_NET_WM_ICON` (ARGB icon data), `_NET_WM_STATE` (contains `_NET_WM_STATE_HIDDEN` for minimized, `SKIP_TASKBAR` to filter), `_NET_WM_DESKTOP` (virtual desktop filtering), `WM_HINTS` urgency flag for taskbar flash.

Taskbar actions (client messages to root, `SubstructureRedirectMask|SubstructureNotifyMask`):

- Activate: `_NET_ACTIVE_WINDOW` (source indication = 2, "pager").
- Minimize: ICCCM `WM_CHANGE_STATE` → `IconicState`.
- Restore: `_NET_ACTIVE_WINDOW`; Maximize/unmaximize: `_NET_WM_STATE` toggle of `_NET_WM_STATE_MAXIMIZED_VERT/_HORZ`.
- Close: `_NET_CLOSE_WINDOW`.
- Virtual desktops: `_NET_NUMBER_OF_DESKTOPS`, `_NET_CURRENT_DESKTOP`, switch via `_NET_CURRENT_DESKTOP` client message.

**LNX-08.** The PAL contract (shared with macOS/Windows implementations):

```csharp
public interface IWindowTracker
{
    IReadOnlyList<ForeignWindow> Windows { get; }          // filtered: !SKIP_TASKBAR, managed, normal type
    ForeignWindow? ActiveWindow { get; }
    event EventHandler<WindowChangedEventArgs> Changed;     // open/close/title/icon/state/desktop
    Task ActivateAsync(ForeignWindow w);
    Task MinimizeAsync(ForeignWindow w);
    Task CloseAsync(ForeignWindow w);
}

public interface IWorkAreaReserver
{
    // Screen edge + thickness; implementation maps to struts (X11),
    // exclusive_zone (Wayland layer-shell), AppBar (Windows), Dock shim (macOS).
    IDisposable Reserve(Screen screen, ScreenEdge edge, int thicknessPx);
}
```

`sealed record ForeignWindow(nint Handle, string Title, string AppId, Bitmap? Icon, WindowState State, int Desktop)`.

### 2.4 X11 event loop

**LNX-09.** EWMH watching runs on a dedicated thread with its own `XOpenDisplay` connection (never share Avalonia's display connection; XLib is not thread-safe across uncoordinated users). Use XCB or XLib with `XInitThreads` + a separate Display; marshal events onto the Avalonia dispatcher. Install `XSetErrorHandler` that logs and swallows `BadWindow` (windows die between event and query — routine, not exceptional).

---

## 3. Wayland strategy (first-class, funded up front)

### 3.1 The two protocols we need, and who has them

Being a shell *under someone else's compositor* requires, at minimum:

1. **`zwlr_layer_shell_v1`** (wlr-layer-shell) — surfaces in the `background` layer (desktop) and `top`/`bottom` layer (taskbar), with `set_anchor`, `set_exclusive_zone` (the strut equivalent), `set_keyboard_interactivity`.
2. **`zwlr_foreign_toplevel_management_v1`** (or the newer `ext_foreign_toplevel_list_v1` + `ext_foreign_toplevel_state` family as it stabilizes) — enumerate/activate/minimize/close other apps' windows for the taskbar.

Compositor support matrix (verify at implementation time; this is the well-known landscape):

| Compositor | layer-shell | foreign-toplevel | Verdict as host |
|---|---|---|---|
| Sway, Hyprland, river, Wayfire, labwc, niri (wlroots/smithay family) | Yes | Yes (zwlr) | **Supported host** |
| KDE KWin (Plasma) | Yes | Partial — has its own `org_kde_plasma_window_management`; zwlr foreign-toplevel not exposed | **Supported host** — desktop/taskbar placement via layer-shell; taskbar window-list uses the KDE-specific `org_kde_plasma_window_management` as a second backend (implementation detail, not a scope question) |
| GNOME Mutter | **No** (explicitly rejected upstream) | No | **Unsupported.** No layer-shell, no foreign toplevel; only GNOME Shell extensions get this power. |

**LNX-10.** GNOME-on-Wayland is documented as unsupported for shell duty. We do not chase Mutter workarounds (extensions injecting JS into GNOME Shell to host our surfaces is a maintenance tarpit and a different product).

### 3.2 The Avalonia backend problem

Avalonia has no stable Wayland backend; the community/experimental backend does not do layer-shell. Options considered:

- **(a) Custom "layer host" native helper:** a small C/wlroots-client program creates layer surfaces and shares buffers with the C# process. Rejected: we'd be reimplementing half a windowing backend (input routing, popups, IME, DPI) over IPC — worse than writing the backend properly.
- **(b) Fork/extend Avalonia's Wayland backend with a layer-shell extension API.** **Chosen.** The rendering side (Skia on EGL/wl_shm) is the part Avalonia upstream is already building; our delta is surface-role plumbing (`zwlr_layer_surface_v1` instead of `xdg_toplevel`) plus the two shell protocols. Upstreamable pieces get upstreamed; the layer-shell extension can live in our tree (precedent: GTK has gtk-layer-shell as an out-of-tree library — same shape).
- **(c) Wait for upstream indefinitely.** Rejected as a plan; acceptable as sequencing (the funded Wayland backend work starts by evaluating upstream state first — see `09-engineering-plan.md`).

**LNX-11.** Wayland backend kickoff gate: the funded Wayland work opens with a de-risking spike proving an Avalonia `TopLevel` rendered as a `zwlr_layer_surface_v1` with working input + popups on Sway and KWin. This gates *implementation sequencing* within the funded Wayland line item — not whether Wayland ships (it is co-equal and funded up front); no shell feature work on Wayland lands before the spike passes.

### 3.3 Run-under vs. ship-a-session on Wayland

**Decision (LNX-12): both, in this order —**

1. **Companion mode (first):** run under the user's existing wlroots-family/KWin compositor via layer-shell. This is the Wayland analog of "X11 with your own WM."
2. **Bevel Session (later):** ship a `wayland-sessions` entry that launches **labwc** (wlroots, Openbox-flavored — thematically on-brand, actively maintained, permissively licensed) configured for Bevel, then Bevel itself. We *bundle and configure* a compositor; we do not write one. (Owning the whole Wayland session this way is a later phase, out of first-Linux scope — distinct from companion mode, which is first-class up front.) Rejected alternatives: writing a Smithay-based compositor (huge scope), bundling Sway (tiling defaults are wrong for a Win2000-style desktop).

### 3.4 Tray, reveal, trash on Wayland

SNI/DBus (§5), FileManager1 (§6.2), Trash (§6.3), and xdg-mime (§6.1) are display-server-agnostic — they carry over unchanged. XEmbed fallback does not exist on Wayland (LNX-22). Global pointer position and click synthesis are unavailable by design; nothing in the Linux feature set requires them (unlike macOS tray mirroring — Linux tray is protocol-based, no pixel capture needed).

---

## 4. Session integration

### 4.1 Modes

**LNX-13.** Bevel runs in one of three modes, auto-detected with manual override in settings:

| Mode | What runs | Target user |
|---|---|---|
| **Companion** | Bevel desktop+taskbar+FM inside an existing session (Xfce, KDE, WM-only). User disables the host's panel/desktop (we document per-DE recipes; e.g., Xfce: `xfconf-query -c xfce4-session` autostart edits; KDE: remove panel via Plasma config). The same recipes cover tray-watcher and FileManager1 name hand-off — see LNX-19/LNX-24: D-Bus names transfer by disabling the incumbent, not by force. | Tinkerers, first-contact users |
| **Bevel Session (X11)** | `/usr/share/xsessions/bevel.desktop` → `bevel-session` script: starts a WM (bundled Openbox with our rc.xml, or user-configured via `$BEVEL_WM`), then `bevel-desktop` | Committed users |
| **Bevel Session (Wayland, later phase)** | `/usr/share/wayland-sessions/bevel.desktop` → labwc + Bevel | Committed users, post-LNX-11 |

Session file sketch:

```ini
# /usr/share/xsessions/bevel.desktop
[Desktop Entry]
Name=Bevel
Comment=Bevel desktop session
Exec=bevel-session
Type=Application
DesktopNames=Bevel
```

**LNX-14.** `bevel-session` sets `XDG_CURRENT_DESKTOP=Bevel` and `XDG_SESSION_DESKTOP=bevel`, imports the environment into systemd user manager (`systemctl --user import-environment DISPLAY XAUTHORITY XDG_CURRENT_DESKTOP`), and starts components as systemd user units rather than a shell script chain:

- `bevel-wm.service` (Openbox/labwc), `bevel-desktop.service` (the C# process), both `PartOf=graphical-session.target`, `bevel-desktop.service` with `Restart=on-failure` and `After=bevel-wm.service`.
- The session script then does `systemctl --user start bevel-session.target` and waits on it. Non-systemd distros (Void, Alpine, Gentoo/OpenRC) get the plain script path as fallback; systemd units are the primary supported mechanism.

**LNX-15.** Crash behavior: `Restart=on-failure` with `StartLimitBurst` tuned so a crash-looping shell falls back to a minimal "recovery" mode (a barebones terminal + logout dialog launched by `OnFailure=bevel-recovery.service`), never a dead black screen. This is the Linux analog of the supervisor design in `01-architecture.md`.

**LNX-16.** We *coexist with* GNOME/KDE at the display-manager level (extra session entry beside theirs); we do not attempt to hijack or replace `gnome-session`/`plasma-session` internals (GNOME's required-components machinery fights back and changes per release; rejected). XDG autostart: in Bevel Session mode, Bevel itself processes `/etc/xdg/autostart` + `~/.config/autostart` entries per the Desktop Application Autostart spec, honoring `OnlyShowIn=`/`NotShowIn=` against `XDG_CURRENT_DESKTOP=Bevel`. Also implement `org.freedesktop.impl.portal` *usage* (we are a portal client, not a portal backend, in companion mode; Bevel Session mode configures `xdg-desktop-portal-gtk` as backend via `/usr/share/xdg-desktop-portal/bevel-portals.conf`).

---

## 5. System tray

### 5.1 StatusNotifierItem host (primary)

**LNX-17.** Bevel's tray implements the **StatusNotifierWatcher + StatusNotifierHost** sides of the SNI spec over the session bus, in C#, using `Tmds.DBus.Protocol` (the low-level, source-generated-friendly API; not the reflection-based legacy `Tmds.DBus`):

- Own the well-known name **`org.kde.StatusNotifierWatcher`**, object `/StatusNotifierWatcher`, interface `org.kde.StatusNotifierWatcher`: methods `RegisterStatusNotifierItem(s)`, `RegisterStatusNotifierHost(s)`; properties `RegisteredStatusNotifierItems (as)`, `IsStatusNotifierHostRegistered (b)`, `ProtocolVersion (i)`; signals `StatusNotifierItemRegistered/Unregistered`, `StatusNotifierHostRegistered`.
- Register ourselves as host: `org.kde.StatusNotifierHost-{pid}`.
- For each item (service like `org.kde.StatusNotifierItem-{pid}-{n}` or a unique name + object path), read `org.kde.StatusNotifierItem` properties: `Category`, `Id`, `Title`, `Status` (Passive/Active/NeedsAttention → hide/show/flash), `IconName`/`IconPixmap` (ARGB32, network byte order — note: needs byte-swap to Skia BGRA on little-endian), `OverlayIcon*`, `AttentionIcon*`, `ToolTip`, `ItemIsMenu`, `Menu` (object path). Watch `New*` signals.
- Interactions: `Activate(x,y)` on left-click (unless `ItemIsMenu`), `SecondaryActivate` on middle-click, `ContextMenu(x,y)` or dbusmenu on right-click, `Scroll(delta, orientation)` on wheel.
- **Compat quirk:** accept both `org.kde.*` and `org.freedesktop.*` interface names (libappindicator vs. spec drift); resolve icons named via `IconThemePath` plus standard XDG icon theme lookup.

**LNX-18.** Menus: implement a **`com.canonical.dbusmenu`** client (`GetLayout`, `Event`, `AboutToShow`, `LayoutUpdated`/`ItemsPropertiesUpdated` signals) rendering into native Avalonia `MenuFlyout`s styled by the active theme (a dbusmenu → Avalonia adapter is a reusable component; budget for the spec's warts: dynamic layouts, toggle types, icon-data properties).

**LNX-19.** Watcher conflict policy. First, the D-Bus reality this design must respect: `RequestName` with `DBUS_NAME_FLAG_REPLACE_EXISTING` succeeds **only if the current owner acquired the name with `DBUS_NAME_FLAG_ALLOW_REPLACEMENT`** — the requester cannot force a steal. Plasma (`plasmashell` owns `org.kde.StatusNotifierWatcher`) and snixembed do *not* set ALLOW_REPLACEMENT, so a REPLACE_EXISTING request against them simply fails or queues; there is no one-click takeover primitive on D-Bus. Policy:

- Bevel requests the name with `DBUS_NAME_FLAG_ALLOW_REPLACEMENT` on its *own* acquisition (so a restarting host DE can reclaim it cleanly) and **without** `DBUS_NAME_FLAG_DO_NOT_QUEUE`, so if the name is taken we sit in the bus's name queue.
- We subscribe to `NameOwnerChanged` for the watcher name: when the incumbent releases it or exits, the bus promotes us from the queue automatically and we start serving items; on `NameLost` (host DE reclaimed it) we drop to passive mode and surface that state in settings.
- "Take over tray" in Companion mode is therefore **guided disabling of the competing host component**, with per-DE recipes rather than a bus operation: Plasma — remove the System Tray widget / quit `plasmashell` (Companion mode already implies disabling the host panel, LNX-13) plus `systemctl --user mask plasma-xembedsniproxy` for the XEmbed proxy; Xfce/MATE — remove the panel's status/tray plugin; snixembed — stop it and disable its autostart entry. The settings UI shows live ownership state ("tray currently owned by: plasmashell") and walks the user through the recipe; the button never promises a replacement the bus won't perform.
- In Bevel Session mode we own the name unconditionally (no competing component is started).

(Contrast with Windows, where the `Shell_TrayWnd` takeover really can be forced by the newcomer — the D-Bus name model has no equivalent; see the Windows chapter.)

### 5.2 XEmbed fallback (X11 only)

Legacy apps (some Java apps, old Wine trays) use the XEmbed system tray spec.

**LNX-20.** X11 tray fallback: acquire the manager selection `_NET_SYSTEM_TRAY_S{screen}` (via `XSetSelectionOwner` + `MANAGER` client message broadcast), accept `SYSTEM_TRAY_OPCODE`/`SYSTEM_TRAY_REQUEST_DOCK` messages, and reparent the icon's window into a native child X window of the taskbar (`XReparentWindow` + XEmbed handshake `_XEMBED_INFO`, `XEMBED_EMBEDDED_NOTIFY`). Set `_NET_SYSTEM_TRAY_VISUAL` to a 32-bit visual for ARGB icons. Avalonia can't composite foreign X windows into its Skia scene; the icons live as small native child windows positioned over reserved gaps in the tray area (same "hole punching" technique every non-GTK tray uses). If BadWindow storms from misbehaving clients destabilize the process in testing, move XEmbed hosting to `bevel-helper-x11` per LNX-02.

**LNX-21.** Alternative rejected: bundling `snixembed` (XEmbed→SNI proxy) instead of native XEmbed. Tempting (less code), but it adds an external runtime dep and a pixel-scraping layer; keep it as a documented user workaround, not a dependency.

**LNX-22.** On Wayland there is no XEmbed; SNI-only, documented.

---

## 6. File manager integration

### 6.1 Becoming the default file manager

**LNX-23.** Ship `bevel-files.desktop` (installed to `/usr/share/applications/`) with `MimeType=inode/directory;` and register as default:

```
xdg-mime default bevel-files.desktop inode/directory
```

The installer offers this (never silently); the settings UI has "Make Bevel the default file manager" / "Restore previous default" (we record the previous value from `xdg-mime query default inode/directory` before overwriting). This routes `xdg-open ~/Documents`, `gio open`, and most "Open folder" buttons to us.

### 6.2 `org.freedesktop.FileManager1` — the reveal standard

**LNX-24.** Own the well-known session-bus name **`org.freedesktop.FileManager1`**, object path `/org/freedesktop/FileManager1`, interface `org.freedesktop.FileManager1`:

```
ShowFolders(as uris, s startup_id)
ShowItems(as uris, s startup_id)          # the "reveal in file manager" call — select item in parent
ShowItemProperties(as uris, s startup_id)
```

URIs are `file://` (percent-encoded); `ShowItems` must open the *parent* folder with the item selected — this is what browsers' "Show in folder", Electron's `shell.showItemInFolder`, and IDEs call. This is the Linux counterpart of the macOS `NSWorkspace`/Apple Events reveal work in `02-macos-platform.md` and shares the same internal `IRevealService`. DBus activation file (`/usr/share/dbus-1/services/org.freedesktop.FileManager1.service` → `Exec=bevel-files --gapplication-service`-style activation) so reveal works even when Bevel FM isn't running. Conflict note: Nautilus/Dolphin also claim this name, and neither acquires it with `DBUS_NAME_FLAG_ALLOW_REPLACEMENT` — so we cannot force-replace them (same D-Bus semantics as LNX-19). We request with ALLOW_REPLACEMENT + queueing and watch `NameOwnerChanged`; opting into default-FM status includes the guided step of stopping the incumbent (e.g., `nautilus -q` to kill the lingering `nautilus --gapplication-service` instance, and noting that its D-Bus activation file will resurrect it on the next reveal call unless ours wins activation). The DBus service file must only be installed when the user opts in (bundle it with LNX-23's toggle) — two activation files for one name makes the race worse, not better.

### 6.3 Trash

**LNX-25.** Implement the **freedesktop.org Trash specification v1.0** natively in C# (no GIO dependency for the core):

- Home trash: `$XDG_DATA_HOME/Trash/` (`files/`, `info/*.trashinfo` with `[Trash Info]`, `Path=` (percent-encoded, absolute or relative-to-topdir), `DeletionDate=` ISO 8601 local time).
- Foreign volumes: `$topdir/.Trash/$uid/` if `.Trash` exists with sticky bit; else create `$topdir/.Trash-$uid/`. Same-filesystem `rename(2)` moves only — never copy-delete silently; if no valid trash dir on that volume, prompt "Delete permanently?" (matches Nautilus behavior).
- `directorysizes` cache file maintained per spec for fast trash-size display.
- Trash *view* in the FM: virtual `trash:` location aggregating all trash dirs, restore via `Path` key, empty-trash with confirmation. Interop requirement: items trashed by Nautilus/Dolphin must appear and restore correctly, and vice versa (integration tests against GIO's implementation).

### 6.4 GVfs, mounts, and volumes

**LNX-26.** Two-tier VFS behind the FM's `IFileSystemProvider` (see file-manager chapter):

1. **Local tier (pure .NET):** POSIX filesystems via `System.IO` + P/Invoke for the gaps (xattrs, `statvfs`, file modes).
2. **Virtual tier (GIO):** network shares (`smb://`, `sftp://`, `dav://`), MTP/GPhoto devices, and `computer://`-style locations via **libgio-2.0 P/Invoke** (`g_file_new_for_uri`, `g_file_enumerate_children`, `g_file_mount_enclosing_volume`, `GVolumeMonitor` for the sidebar's devices/volumes; mounts appear at the FUSE bridge `/run/user/$uid/gvfs/` for handing paths to non-GIO apps). GVfs daemons are an optional runtime dependency: if absent, the virtual tier reports "not available" and local browsing is unaffected. Rejected: reimplementing SMB/SFTP clients in C# (enormous, worse), and hard-depending on GVfs (breaks minimal WM-only installs).

**LNX-27.** Removable drives: prefer `GVolumeMonitor` (which wraps UDisks2); mount/unmount/eject through GIO so polkit prompts flow through the portal/agent normally. Direct `org.freedesktop.UDisks2` DBus is the fallback path if GIO is unavailable.

**LNX-28.** Desktop icons for mounted volumes and the "trash full/empty" icon state follow the same providers, so the desktop grid and FM sidebar share one model.

### 6.5 Misc freedesktop conformance

**LNX-29.** The FM and start menu honor: XDG Base Directory spec; `user-dirs.dirs` (XDG_DESKTOP_DIR is the desktop grid's root — not hardcoded `~/Desktop`); Desktop Entry spec for launchers (including `TryExec`, `DBusActivatable`, actions); Icon Theme spec + `hicolor` fallback for all app/mime icons; `shared-mime-info` for type detection (via GIO `g_content_type_guess` or a managed reimplementation — decide at implementation by benchmarking); Thumbnail Managing Standard (`$XDG_CACHE_HOME/thumbnails/`, MD5-of-URI naming, `normal`/`large` sizes) so thumbnails are shared with other FMs, and external thumbnailers via `/usr/share/thumbnailers/*.thumbnailer`.

---

## 7. Packaging & distribution

**LNX-30.** Distribution channels, in priority order:

1. **.deb** (Ubuntu/Debian, own apt repo), **.rpm** (Fedora/openSUSE, copr/OBS), **AUR** package (Arch), generic **tarball** with an installer script. Builds are .NET NativeAOT-or-self-contained single-dir (see `09-engineering-plan.md` for the AOT decision); runtime deps kept to: glibc, X11 libs, fontconfig, glib/gio (optional tier), systemd (optional).
2. Distro-native packaging by maintainers once the project is public (we keep packaging scripts distro-friendly: no network at build time, reproducible).

**LNX-31. Flatpak/Snap: not for the shell.** Recorded reasons (this comes up constantly, so the spec takes the position once):

- A flatpak cannot install `/usr/share/xsessions/` or `/usr/share/wayland-sessions/` entries — no session integration, period.
- Sandbox blocks owning arbitrary well-known DBus names cleanly across the board we need (`org.kde.StatusNotifierWatcher`, `org.freedesktop.FileManager1`), raw X11 selection/reparenting games (XEmbed, `_NET_SYSTEM_TRAY_S0`), writing systemd user units, and `xdg-mime` system-level defaults.
- `--filesystem=host` + every hole punched ≈ no sandbox, at which point flatpak is only a worse package format for us.

A *flatpak of the file manager alone* (Companion-lite: FM + no shell duties) is a plausible future funnel; noted in Open questions, not committed.

**LNX-32.** Theme asset licensing applies identically on Linux (recreated icons/sounds/fonts only — see the theming chapter); additionally, packages must not depend on `ttf-ms-fonts`/`msttcorefonts` — bundle the chosen metric-compatible free fonts.

---

## 8. Requirement rollup (Linux v1 exit criteria)

| Area | Must pass |
|---|---|
| Desktop & taskbar | LNX-03..06 verified on Openbox, Xfwm, KWin (X11), Marco, i3 (strut honored; desktop stays bottom) |
| Window tracking | LNX-07..09; taskbar reflects open/close/title/icon/active/minimize within 100 ms on the WMs above |
| Session | LNX-13..16; Bevel Session boots from GDM/SDDM/LightDM to usable desktop < 5 s after login on reference hardware; crash → recovery mode, never black screen |
| Tray | LNX-17..22; validated against: nm-applet (SNI), Telegram, Discord, blueman, a libappindicator app, one XEmbed-only app. Companion-mode name handling verified on Plasma (X11): Bevel queues behind plasmashell, is promoted via `NameOwnerChanged` after the guided disable, and yields the name back when plasmashell restarts |
| File manager | LNX-23..29; Trash interop with Nautilus round-trip; `ShowItems` from Firefox and VS Code reveals correctly |
| Packaging | LNX-30..32; clean install/uninstall on Ubuntu LTS, Fedora current, Arch |

## Risks

- **Avalonia Wayland backend never materializes upstream** → our fork-and-extend estimate (LNX-11 spike, +8–14 ew) balloons into owning a full windowing backend. Mitigation: the co-equal X11 half still ships full value if the Wayland line slips; the kickoff spike de-risks and bounds the estimate before feature work commits; labwc/wlroots keep X11 viable via XWayland for *apps* even in our future Bevel Session.
- **X11 relevance decay outpaces Wayland backend delivery.** GNOME removing Xorg sessions is fine (GNOME isn't our host anyway), but if Fedora/Ubuntu drop Xorg *packages* entirely before the co-equal Wayland backend ships, the reachable near-term audience shrinks to Xfce/MATE/WM distros. Mitigation: Wayland is funded up front precisely to limit this exposure; monitor annually; re-sequence if needed.
- **EWMH compliance variance across WMs.** Struts and `_NET_ACTIVE_WINDOW` source-indication handling differ subtly (KWin focus-stealing prevention, i3's partial EWMH). Mitigation: the WM matrix in §8 is a CI-tested matrix (Xvfb + each WM headless), not a hope.
- **XEmbed instability** (foreign windows dying mid-reparent, visual mismatches causing BadMatch). Mitigation: LNX-02 error-handler containment; helper-process escape hatch; XEmbed is a shrinking legacy surface — acceptable to mark "best effort".
- **dbusmenu complexity underestimated.** Real-world dbusmenu trees (Electron apps, Qt apps via appmenu) are quirky. Budget an adapter test corpus early; degraded fallback = `ContextMenu(x,y)` call letting the app render its own menu (loses theming for that menu, acceptable).
- **FileManager1/watcher name conflicts in Companion mode** produce user-visible flakiness (reveal opens Nautilus sometimes). Mitigation: LNX-19/LNX-24 queue-plus-`NameOwnerChanged` acquisition with guided disable-the-incumbent recipes and explicit ownership-state display in settings. Residual risk: the recipes are per-DE and drift with DE releases (Open question 7), and a D-Bus-activated incumbent (Nautilus) can be resurrected by any client call while its activation file is installed.
- **GIO P/Invoke lifetime bugs** (GObject ref-counting from C#) are a classic crash source. Mitigation: single audited interop layer with `SafeHandle`s; the virtual tier is isolatable to a worker process if it proves crashy (same pattern as native helpers in `01-architecture.md`).

## Open questions

1. **Plasma-on-Wayland — resolved as in-scope (2026-07-04).** With Wayland now co-equal and first-class, and KWin exposing both `zwlr_layer_shell_v1` and `org_kde_plasma_window_management` (§3.1), KWin/Plasma is a supported Wayland host, not a scope question. The KDE-specific second taskbar backend for `org_kde_plasma_window_management` is an implementation detail funded within the Wayland line item. (No longer open; retained for numbering.)
2. **Reference WM default for Bevel Session (X11): Openbox vs. bundling nothing** and hard-requiring the user's WM. Openbox is unmaintained-but-stable; labwc is Wayland-only. Owner call on bundling an effectively frozen upstream vs. the setup friction of "bring your own WM."
3. **Flatpak'd file-manager-only build** as a discovery funnel (LNX-31 note): worth the packaging matrix cost?
4. **Minimum distro floor**: do we support non-systemd distros at v1 (script-based session path, LNX-14 fallback) or document systemd as required and accept the Void/Gentoo/Alpine noise?
5. **`ext_foreign_toplevel_*` vs `zwlr_foreign_toplevel_management_v1`**: the ext-protocol family may be the portable future (and the only thing some compositors adopt). Decide at Wayland-milestone time which is primary; needs a fresh ecosystem check then.
6. **Icon-grid desktop on Wayland background layer**: layer-shell background surfaces can take input on click-through-free regions; confirm drag-and-drop *onto* the desktop (from FM windows) works acceptably across wlroots compositors, or whether desktop DnD is X11-only at first.
7. **Per-DE takeover recipes as a maintenance surface**: the guided-disable steps in LNX-19/LNX-24 (Plasma tray-widget removal, `plasma-xembedsniproxy` masking, Nautilus service deactivation, panel-plugin removal on Xfce/MATE) depend on DE internals that shift between releases. Do we CI-test the recipes (containerized DE sessions in the §8 matrix) or ship them as documentation-only best-effort?
