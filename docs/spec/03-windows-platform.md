# Windows Platform Integration

Status: Draft v1 — chapter 03 of the Bevel spec. Cross-references: [01-architecture.md](01-architecture.md) (PAL contracts, helper-process model, IPC), [02-macos-platform.md](02-macos-platform.md) (the hard platform this chapter contrasts against), [04-linux-platform.md](04-linux-platform.md), [05-theming.md](05-theming.md), [06-file-manager.md](06-file-manager.md), [09-engineering-plan.md](09-engineering-plan.md).

## Summary

Windows is the only platform where replacing the shell is a *documented, supported* operation: Winlogon reads a registry value to decide what to launch instead of `explorer.exe`, and third-party shells (LiteStep, bbLean, Cairo Shell, RetroBar) have exercised this path for 25 years. Almost everything we need — window tracking, tray protocol, work-area reservation, shell namespace — is reachable via P/Invoke from C#, so **Windows does not need a native helper process for core function** (unlike macOS, where Swift helpers are mandatory for ScreenCaptureKit/AX). The genuinely hard parts are: (a) faithfully re-implementing the `Shell_TrayWnd` wire protocol so 25 years of `Shell_NotifyIcon` callers keep working, (b) acting as the *appbar server* for third-party docked bars, and (c) the undocumented, per-build-GUID-churning virtual desktop internals. This chapter specs shell-replacement mode and a lower-risk *companion mode* (running alongside Explorer as an appbar), decides on per-user (HKCU) replacement with a watchdog and multiple escape hatches, and decides on a classic MSI installer (MSIX cannot legally write the Winlogon key from its container).

Decisions made in this chapter:

| # | Decision | Section |
|---|----------|---------|
| D-W1 | Replace shell via **HKCU** Winlogon `Shell`, never HKLM, in v1 | §1.2 |
| D-W2 | Watchdog process + boot-safe rollback (crash-count circuit breaker) | §1.4 |
| D-W3 | Implement the full `Shell_TrayWnd`/`TrayNotifyWnd` window-class contract, including WM_COPYDATA appbar server and NOTIFYICON_VERSION_4 semantics | §3, §4 |
| D-W4 | Window tracking primary source: `RegisterShellHookWindow` + `SetWinEventHook`; UIA only as enrichment | §5 |
| D-W5 | Virtual desktops: public `IVirtualDesktopManager` + synthesized Win+Ctrl+arrow only in v1; no undocumented `IVirtualDesktopManagerInternal` dependency | §6 |
| D-W6 | Companion mode is the **default install mode** on Windows; replacement is opt-in (decided by owner 2026-07-04; default may be revisited once macOS replace-rollout data is in) | §7 |
| D-W7 | Classic installer (WiX MSI); MSIX rejected for shell mode | §8 |
| D-W8 | x64 + ARM64 first-class from day one; no x86 | §9 |
| D-W9 | No native *interop* helper process on Windows; all OS interop in-proc P/Invoke/COM, isolated in `Bevel.Pal.Windows` with a crash-containment policy instead. Supersedes the `helper-windows` component sketched in 01-architecture.md (§10.1) | §10 |

---

## 1. Shell replacement via the Winlogon `Shell` value

### 1.1 Mechanics

Winlogon launches the shell named by the `Shell` value at logon:

- Machine-wide default: `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon` → `Shell` (REG_SZ), default `explorer.exe`.
- Per-user override: `HKCU\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon` → `Shell` (REG_SZ). If present, it wins for that user. (On some managed configurations the `System\Shell` policy value under `HKCU\...\Policies\System` also applies; we do not touch policy keys.)
- `AutoRestartShell` (REG_DWORD, HKLM Winlogon, default 1): Winlogon relaunches the shell process if it exits unexpectedly. This applies to any shell named in `Shell`, not just Explorer — we get crash-relaunch for free.

The value is a command line, not just an EXE name, so we install as:

```
Shell = "C:\Program Files\Bevel\BevelHost.exe" --mode=shell
```

**REQ-W1.** The installer MUST write only `HKCU\...\Winlogon\Shell` when enabling replacement mode, and MUST record the prior value (usually absent) in `HKCU\SOFTWARE\Bevel\Rollback` before writing.

**REQ-W2.** Enabling replacement MUST be a separate, explicit post-install step in the app's own settings UI ("Make Bevel my Windows shell"), never a silent installer default. Disabling it deletes the HKCU value, restoring Explorer at next logon.

### 1.2 HKLM vs HKCU (D-W1)

Decision: **HKCU only.**

Rationale:
- Per-user scope means a broken install strands one account, not the machine; another admin account still boots into Explorer.
- No UAC elevation needed to enable/disable — the settings toggle works without admin.
- HKLM replacement affects service accounts, other users, and safe-mode-adjacent flows; the blast radius is not worth it for v1.

Rejected: HKLM machine-wide replacement (kiosk/fleet scenario). Revisit post-v1 behind an enterprise flag; requires MDM/GPO story anyway.

### 1.3 Escape hatches

Even with rollback logic, users must always have a way out:

1. **Ctrl+Alt+Del → Task Manager** works regardless of shell (Winlogon owns the secure desktop). From Task Manager: *Run new task* → `explorer.exe` gives a full Explorer session; `regedit` allows manual repair.
2. **Safe Mode** ignores nothing here — HKCU `Shell` is honored even in safe mode on modern Windows — so the watchdog (§1.4) is the real safety net, plus:
3. **`--restore-explorer` CLI verb** on `BevelHost.exe` that deletes the HKCU value and starts `explorer.exe`, runnable from Task Manager's Run dialog.
4. A **start-menu-visible "Restore Windows Explorer shell"** shortcut installed alongside the app (works because Start menu shortcuts are just files; our own start menu lists them).

**REQ-W3.** `BevelHost.exe --restore-explorer` MUST work with no dependencies beyond the registry APIs (no config files, no IPC), and MUST be documented in the installer's final page.

### 1.4 Watchdog and crash circuit breaker (D-W2)

`AutoRestartShell` relaunches us on crash, but a crash-loop at logon is a bricked session. Spec:

**REQ-W4.** On startup in shell mode, `BevelHost.exe` increments a crash counter in `HKCU\SOFTWARE\Bevel\Health` (`StartCount`, `LastStartTime`). After 60 s of healthy uptime it resets the counter. If `StartCount ≥ 3` within 5 minutes, the process MUST (a) delete the HKCU `Shell` value, (b) spawn `explorer.exe`, (c) write a crash report path to the Health key, (d) show a plain Win32 `MessageBox` (`user32!MessageBoxW` — deliberately not Avalonia, which may be the crashing component) explaining what happened, then exit.

**REQ-W5.** The circuit breaker MUST run before Avalonia, DI container, or theme loading is initialized — first ~50 lines of `Main`.

This mirrors the macOS chapter's helper-crash containment philosophy (01-architecture.md) but is simpler: there is only one process to babysit.

---

## 2. Life without Explorer: what breaks, what we must provide

Killing/never-starting `explorer.exe` removes more than the taskbar. Critically, most of the *shell namespace* does **not** break: `shell32.dll` is an in-process library, so `IShellFolder`, `SHGetKnownFolderPath`, `ShellExecuteEx`, `SHChangeNotify`, icon extraction, and shell property stores all work inside any process that calls them — including ours and third-party apps. DWM (`dwm.exe`) is a separate service: compositing, thumbnails (`DwmRegisterThumbnail`), and Aero peek primitives keep working.

What actually breaks, and our position on each:

| Explorer-provided facility | Breaks without Explorer? | Our obligation |
|---|---|---|
| Desktop window (`Progman`/`WorkerW`), icons, wallpaper | Yes | We provide (our desktop layer; see 01-architecture.md desktop surface). We create our own bottom-most desktop window; we do NOT recreate `Progman` class (apps that FindWindow("Progman") for wallpaper tricks get degraded behavior — accepted). |
| Taskbar, `Shell_TrayWnd`, tray, clock | Yes | We provide, wire-compatible (§3–4). |
| Start menu (`StartMenuExperienceHost.exe`) | Yes (child of Explorer) | Our start menu; app enumeration from Start Menu folders + `AppsFolder` (`shell:AppsFolder` via `IShellFolder` enum gives packaged apps too). |
| `TaskbarCreated` broadcast | Yes | We broadcast it when our tray is ready (§4.3). |
| Appbar server (SHAppBarMessage receiver) | Yes | We implement (§3.2). |
| `IShellWindows` ROT registration (`CLSID_ShellWindows`), used by `SHOpenFolderAndSelectItems` and automation | Yes | Best-effort: our file manager registers a ShellWindows-compatible object (06-file-manager.md); fallback: register as handler for `Directory`/`Drive` shell verbs so "reveal in folder" launches us. Full IShellWindows fidelity is stretch. |
| Toast notifications / Action Center (`ShellExperienceHost.exe`) | Partially — toasts raised via WinRT may not display | v1: accepted degradation, documented. v2 option: our own notification listener via `UserNotificationListener` (WinRT, needs capability) to render toasts in our UI. |
| Clipboard history UI (Win+V), emoji panel (Win+.) | Yes | Accepted loss in v1 (input service `ctfmon` keeps IME working; only the shell-hosted panels die). |
| AutoPlay / device arrival UI | Yes | We handle `WM_DEVICECHANGE`/`RegisterDeviceNotification` and show our own prompt (file-manager scope). |
| Win+E, Win+D, Win+number hotkeys | Yes (Explorer registers them) | We register via `RegisterHotKey`; Win+E opens our file manager. |
| File-type associations / `ShellExecute` | No — works in-proc | Nothing to do. |
| OLE drag & drop between apps | No | Nothing to do. |
| DWM composition, Alt+Tab (the built-in one is Explorer's on Win11; the legacy XP-style Alt+Tab in `AltTab.dll` still exists) | Alt+Tab partially | We provide our own switcher via window tracker (§5); legacy fallback exists regardless. |
| Windows Search indexer, SettingsApp, UWP lifetime (`ApplicationFrameHost`) | No — separate services | Nothing to do. |

**REQ-W6.** In shell mode, Bevel MUST register the standard shell hotkeys (Win+E, Win+D, Win+L passthrough is OS-level, Win+number for taskbar slots) via `RegisterHotKey` and release them on exit.

**REQ-W7.** Bevel MUST call `SHChangeNotify(SHCNE_ASSOCCHANGED, ...)` and standard shell APIs rather than private mechanisms wherever possible, so in-proc shell state stays coherent for other apps.

Third-party expectations worth naming: apps commonly `FindWindow("Shell_TrayWnd", NULL)` to (a) position themselves relative to the taskbar, (b) detect "is Explorer the shell", (c) send appbar/tray messages. Providing a real `Shell_TrayWnd` (§3) satisfies nearly all of them.

---

## 3. Taskbar: work area and the appbar protocol

### 3.1 Reserving work area

Two roles, depending on mode:

- **shell mode (we are the taskbar):** we do what Explorer does — set the work area directly with `SystemParametersInfo(SPI_SETWORKAREA, 0, &rect, SPIF_SENDCHANGE)`. The call applies to the monitor containing the rect, so we issue one call per monitor where a taskbar edge is docked. `SPIF_SENDCHANGE` broadcasts `WM_SETTINGCHANGE` so maximized windows re-layout.
- **Companion mode (Explorer is the shell):** we are a client appbar: `SHAppBarMessage(ABM_NEW/ABM_QUERYPOS/ABM_SETPOS/ABM_REMOVE, &APPBARDATA)`, exactly like any docked toolbar. Explorer arbitrates.

**REQ-W8.** Work-area math MUST be per-monitor and DPI-aware (`GetDpiForMonitor`, `MonitorFromRect`); taskbar height is defined in DIPs by the theme (05-theming.md) and converted per-monitor.

**REQ-W9.** On clean shutdown or mode switch, Bevel MUST restore the full work area (`SPI_SETWORKAREA` with the monitor rect) for every monitor it modified.

### 3.2 Being the appbar *server*

Third-party appbars (docked launcher bars, some IM sidebars) call `SHAppBarMessage`, which internally sends `WM_COPYDATA` (with `COPYDATASTRUCT.dwData = 0`, payload an `APPBARMSGDATA` containing the `APPBARDATA` and the ABM code) to whatever window has class `Shell_TrayWnd`. If nobody answers, appbars break. Since we own `Shell_TrayWnd` in shell mode, we must answer.

**REQ-W10.** The `Shell_TrayWnd` window MUST implement the appbar server: `ABM_NEW`, `ABM_REMOVE`, `ABM_QUERYPOS`, `ABM_SETPOS`, `ABM_GETSTATE`, `ABM_GETTASKBARPOS`, `ABM_ACTIVATE`, `ABM_WINDOWPOSCHANGED`, `ABM_SETAUTOHIDEBAR`, `ABM_GETAUTOHIDEBAR`, `ABM_SETSTATE`. Position arbitration: maintain a registry of appbars per monitor-edge, shrink work area accordingly, send `ABN_POSCHANGED` notifications (via each appbar's registered callback message) when arrangement changes.

**REQ-W11.** `ABM_GETTASKBARPOS` MUST return our taskbar's rect and edge, since utilities use it for positioning (e.g., volume flyouts, launchers).

Prior art to mine during implementation: **Cairo Shell** (C#, MIT) implements this exact server in `ManagedShell` (github.com/cairoshell/ManagedShell); we should evaluate depending on it or vendoring the message plumbing rather than re-deriving struct layouts. Its license is compatible.

### 3.3 Fullscreen & rude apps

Explorer hides the taskbar when a fullscreen app is foreground. We detect via `RegisterShellHookWindow` message `HSHELL_RUDEAPPACTIVATED` (§5) plus geometry check (`window rect == monitor rect` and no WS_CAPTION), and auto-conceal per monitor.

---

## 4. Tray takeover

### 4.1 The wire protocol

`Shell_NotifyIcon` in `shell32.dll` marshals the caller's `NOTIFYICONDATA` into a `WM_COPYDATA` message sent to the `Shell_TrayWnd` window:

- `dwData = 0`: appbar message (§3.2).
- `dwData = 1`: tray notification — payload `SHELLTRAYDATA` (magic dword, message id NIM_ADD/NIM_MODIFY/NIM_DELETE/NIM_SETFOCUS/NIM_SETVERSION, then a 32/64-bit `NOTIFYICONDATA`).
- `dwData = 3`: icon-position query from `Shell_NotifyIconGetRect` — payload `NOTIFYICONIDENTIFIER`; we must reply with the icon's screen rect (utilities use this to anchor flyouts).

The expected child window hierarchy that some software probes: `Shell_TrayWnd` → `TrayNotifyWnd` → `SysPager` → `ToolbarWindow32` ("Notification Area"/"User Promoted Notification Area"). We create real Win32 windows with these class names as a *façade*; rendering happens in our Avalonia surface, but the façade windows keep `FindWindowEx` probes and accessibility tooling from finding nothing.

**REQ-W12.** Tray host MUST accept both ANSI (`NOTIFYICONDATAA`) and Unicode layouts and all historical struct sizes (Win2k through NOTIFYICONDATA_V3 and current), keyed off `cbSize`, for both 32-bit and 64-bit senders (a 32-bit process sends the 32-bit layout — sizes and offsets differ; ManagedShell has the dual definitions).
**REQ-W13.** Icon pixels: `hIcon` is a shared USER object, directly loadable cross-process; render via icon → bitmap → Avalonia `Bitmap`. `NIF_TIP` tooltips, `NIF_INFO` balloons (rendered as our own toast UI), and `NIF_GUID` identity MUST be honored.
**REQ-W14.** Click forwarding MUST respect the icon's negotiated `uVersion` (from `NIM_SETVERSION`): legacy (≤3) receives `WM_LBUTTONDOWN`-style codes in `lParam`; `NOTIFYICON_VERSION_4` receives `WM_CONTEXTMENU`/`NIN_SELECT`/`NIN_KEYSELECT` with anchor coordinates packed in `wParam`. Before posting the callback we MUST call `AllowSetForegroundWindow(pid)` so the app's menu can take focus and dismiss correctly.
**REQ-W15.** Dead icons: the tray MUST poll `IsWindow(callbackHwnd)` (or use the window tracker's destroy events) and garbage-collect icons whose owner died without `NIM_DELETE` — Explorer parity.

### 4.2 Taking over from a running Explorer

Companion→shell transitions and debugging sessions need takeover while Explorer runs. Explorer's tray must be dethroned:

The supported path is: write the HKCU `Shell` value, then **log off and back on** — Winlogon launches us cleanly and no runtime fight over the `Shell_TrayWnd` class name ever happens. In-session takeover exists only behind a developer/power-user flag (`--take-over`) and works as follows:

1. Locate the window of class `Shell_TrayWnd`; resolve its owning process via `GetWindowThreadProcessId` + `OpenProcess`/`QueryFullProcessImageName`.
2. Write `HKCU\...\Winlogon\Shell` *first*, so any restart logic resolves to us.
3. Ask Explorer's shell instance to exit gracefully: there is no documented "quit shell" message, so post `WM_CLOSE` to `Shell_TrayWnd`, wait, then `TerminateProcess` the owning `explorer.exe` on timeout. (The hidden "Exit Explorer" context-menu path uses undocumented messages we won't depend on.)
4. Create our `Shell_TrayWnd`, start the appbar server, broadcast `TaskbarCreated` (§4.3).

This ordering (registry-then-kill) is the pattern proven by Cairo Shell and RetroBar. Note that a manually terminated Explorer is relaunched by nothing (Winlogon's `AutoRestartShell` watches only the process *it* started), so step 3 does not race a respawn.

**REQ-W16.** `--take-over` MUST verify the process owning `Shell_TrayWnd` is `explorer.exe` (image path under `%WINDIR%`) before terminating anything (see also user-level rule: never kill unidentified processes).

### 4.3 `TaskbarCreated`

**REQ-W17.** When the tray window is created and ready (shell mode or post-takeover), Bevel MUST `RegisterWindowMessage(L"TaskbarCreated")` and `SendNotifyMessage(HWND_BROADCAST, msg, 0, 0)`. Every well-behaved tray app re-adds its icon in response. Apps started *before* us at logon are covered by this; apps that never handle `TaskbarCreated` (rare, buggy) will be missing until restarted — Explorer has the same behavior.

**REQ-W18.** In companion mode Bevel MUST NOT create `Shell_TrayWnd` and MUST NOT broadcast `TaskbarCreated`. Companion mode gets no native tray capture (§7) — mirroring another shell's tray on Windows has no supported mechanism short of scraping, and unlike macOS there is no product need: Explorer's tray is already visible.

---

## 5. Window enumeration and control

### 5.1 Event sources (D-W4)

Primary: `RegisterShellHookWindow(hwnd)` + `RegisterWindowMessage(L"SHELLHOOK")` delivers `HSHELL_WINDOWCREATED`, `HSHELL_WINDOWDESTROYED`, `HSHELL_WINDOWACTIVATED`, `HSHELL_RUDEAPPACTIVATED`, `HSHELL_REDRAW` (title/flash changes), `HSHELL_FLASH`, `HSHELL_GETMINRECT`. This is precisely the feed Explorer's taskbar uses and only reports *taskbar-eligible* top-level windows — pre-filtered for us.

Supplementary: `SetWinEventHook(WINEVENT_OUTOFCONTEXT)` for `EVENT_OBJECT_NAMECHANGE` (title updates), `EVENT_SYSTEM_MINIMIZESTART/END`, `EVENT_OBJECT_LOCATIONCHANGE` (thumbnail invalidation), and `EVENT_OBJECT_UNCLOAKED/CLOAKED` (virtual-desktop and UWP visibility).

Initial population: `EnumWindows` filtered by the standard eligibility rules — visible, no owner (`GetWindow(GW_OWNER) == 0`) or `WS_EX_APPWINDOW`, not `WS_EX_TOOLWINDOW`, and not DWM-cloaked (`DwmGetWindowAttribute(DWMWA_CLOAKED)` — filters UWP zombies and other-desktop windows).

UI Automation is **not** in the hot path — it's slower and heavier. We use it only for enrichment where Win32 lacks data (e.g., reading modern app names) and for our own accessibility exposure (Avalonia's UIA support covers our UI; see 01-architecture.md a11y section).

### 5.2 Control operations

| PAL operation (see `IWindowController`, 01-architecture.md) | Win32 implementation |
|---|---|
| Activate | `SwitchToThisWindow`/`SetForegroundWindow` after `AllowSetForegroundWindow`; restore if minimized (`ShowWindow(SW_RESTORE)`) |
| Minimize / Maximize / Restore / Close | `ShowWindowAsync`; `WM_SYSCOMMAND SC_CLOSE` for close (lets apps prompt to save) |
| Live thumbnails (taskbar hover) | `DwmRegisterThumbnail` — real live previews, zero pixel copying on our side |
| Icon | `WM_GETICON`/`GCLP_HICON`, fallback to exe icon via `SHGetFileInfo`; packaged apps via AUMID → `shell:AppsFolder` item icon |
| Group identity | `GetApplicationUserModelId` (packaged) else exe path; matches Explorer grouping |
| Minimize animation targeting | Answer `HSHELL_GETMINRECT` and call the undocumented-but-stable `SetTaskmanWindow` so minimize animations fly to our taskbar button |

Contrast with macOS (02-macos-platform.md): everything above needs no permission prompt, no helper process, no private API risk (except `SetTaskmanWindow`, which degrades to "animation goes to screen corner" if it ever breaks). This is the PAL's easiest backend.

### 5.3 C# sketch

```csharp
// Bevel.Pal.Windows — in-proc, no helper (D-W9)
internal sealed class Win32WindowTracker : IWindowTracker, IDisposable
{
    // One hidden message-only... NOT message-only: shell hook requires a real
    // top-level window; we reuse the (invisible in companion mode) tray host window.
    public event EventHandler<WindowEventArgs>? WindowAdded, WindowRemoved, WindowChanged, ForegroundChanged;

    public Win32WindowTracker(TrayHostWindow host)
    {
        Native.RegisterShellHookWindow(host.Hwnd);
        _shellHookMsg = Native.RegisterWindowMessage("SHELLHOOK");
        host.AddMessageFilter(OnShellHook);
        _winEventHooks = HookWinEvents(); // NAMECHANGE, CLOAK, MINIMIZE*
        foreach (var w in EnumEligibleTopLevel()) Publish(w);
    }
}
```

---

## 6. Virtual desktops (D-W5)

Documented surface: `IVirtualDesktopManager` (CLSID `VirtualDesktopManager`, `shobjidl_core.h`) offers exactly three methods: `IsWindowOnCurrentVirtualDesktop`, `GetWindowDesktopId`, `MoveWindowToDesktop` (own-process windows only). Everything richer — enumerating desktops, names, switching, moving *other* apps' windows — lives in `IVirtualDesktopManagerInternal`, whose IIDs and vtables **change across Windows builds** (the VirtualDesktopAccessor / Slions.VirtualDesktop projects maintain per-build GUID tables and still break on Insider updates).

Decision: v1 uses **only** the public API plus pragmatic tricks:

- Taskbar filtering "current desktop only": `IsWindowOnCurrentVirtualDesktop` per window, re-evaluated on `EVENT_OBJECT_CLOAKED/UNCLOAKED` (desktop switches cloak/uncloak windows — this event storm *is* our switch signal, no internal API needed).
- Desktop switch command (pager buttons): synthesize `Win+Ctrl+Left/Right` via `SendInput`. Crude but build-proof.
- Move-window-to-desktop for arbitrary windows, desktop names, create/destroy desktop: **not offered in v1.** Feature-flagged `IVirtualDesktopManagerInternal` adapter (per-build GUID table, hard-disabled on unknown builds) is a v1.x candidate.

Rationale: a shell that breaks on every Windows feature update because of vtable drift violates the reliability bar; the cloak-event trick covers the 90% case (correct taskbar contents per desktop).

**REQ-W19.** All virtual-desktop calls MUST be behind `IVirtualDesktopService` in the PAL with a capability flags property (`CanSwitch`, `CanMoveOtherWindows`, `CanEnumerate`) so UI degrades declaratively (same pattern the macOS Spaces backend needs — Spaces is also undocumented; see 02-macos-platform.md).

---

## 7. Companion mode (alongside Explorer) — default on Windows (D-W6)

Running as a non-replacement gives users the theming/taskbar/file-manager experience with zero risk, and is our low-friction wedge. Decision (owner, 2026-07-04): **companion mode is the default install experience on Windows**; replacement mode is a settings opt-in with big red letters. The default may be revisited once macOS replace-rollout data is in.

Companion-mode behavior matrix:

| Component | shell mode | Companion mode |
|---|---|---|
| Desktop icons + wallpaper | Ours | Off by default (Explorer draws desktop); optional "cover desktop" toggle creates our desktop window above Explorer's |
| Taskbar | Ours; `Shell_TrayWnd` owner; appbar server | Ours as **appbar client** (`ABM_NEW`); Explorer taskbar can be set to auto-hide by user, or ours docks to a different edge |
| Tray | Full takeover | Not shown (REQ-W18); clock/quick-settings still available in our bar |
| Start menu | Ours (Win key via `RegisterHotKey` — note: Win key alone is not grabbable while Explorer runs; use click/configurable hotkey) | Ours, on click or hotkey |
| File manager | Default handler for folders (opt-in file association) | Available, not default |
| Alt+Tab | Ours optional | OS's |

**REQ-W20.** Mode MUST be a runtime flag (`--mode=shell|companion`) resolved at startup by checking whether *we* were launched by Winlogon as the shell (`GetShellWindow() == NULL` and no `Shell_TrayWnd` present is the practical detection), so one binary serves both.

**REQ-W21.** Switching modes from settings MUST: write/delete the Winlogon value, explain that logoff is required, and offer "log off now" (`ExitWindowsEx(EWX_LOGOFF, ...)`).

Shell-mode session responsibilities that companion mode skips: as the shell we also handle `WM_QUERYENDSESSION`/`WM_ENDSESSION` ordering gracefully and expose Shut down / Restart / Log off in the start menu via `ExitWindowsEx` (needs `SE_SHUTDOWN_NAME` privilege adjustment) and `LockWorkStation()`.

---

## 8. Packaging: MSIX vs classic installer (D-W7)

Decision: **WiX-built MSI** (classic Win32 installer), per-user install (`ALLUSERS=""`) matching the HKCU strategy. Optional winget manifest points at the MSI.

| Criterion | MSIX | Classic MSI (chosen) |
|---|---|---|
| Write `HKCU\...\Winlogon\Shell` | Registry virtualization redirects writes; even with `RegistryWriteVirtualization=disabled` (needs `runFullTrust` + special capability review), shipping a shell replacement through Store policy is a non-starter | Direct, honest |
| Launch at Winlogon as shell | Packaged apps launch via activation pipeline; Winlogon `Shell` wants a plain command line — packaged exe paths under `WindowsApps` have ACL headaches | Plain `Program Files`-style path (per-user: `%LOCALAPPDATA%\Programs\Bevel`) works |
| Watchdog/registry rollback freedom | Container friction | Full |
| Auto-update | Store/MSIX pipeline | We ship our own updater (**Velopack**, per 01-architecture.md UPD-01 / 00-master-plan.md R7) — needed anyway for macOS parity (09-engineering-plan.md release engineering) |
| Enterprise deployment | Fine | Fine (MSI is the enterprise lingua franca) |

Rejected: MSIX (container vs. shell-replacement is fundamentally at odds); also rejected Inno Setup — fine tool, but MSI gives us MDM deployability and component-level repair, and WiX v5 integrates with `dotnet` builds.

Code signing: Authenticode with a **separate OV/EV cert** is **mandatory** — an unsigned binary that rewrites Winlogon `Shell` is indistinguishable from malware to SmartScreen/Defender heuristics. This is a dedicated Windows cert obtained at Windows-track start (the macOS build signs with its own existing individual Developer ID — a mac concern, not shared). Budget for cert + Defender false-positive submission process in 09-engineering-plan.md.

**REQ-W22.** Every shipped binary MUST be Authenticode-signed; the installer MUST be timestamped; CI enforces this on release builds.

---

## 9. Architectures: x64 and ARM64 (D-W8)

- Targets: `win-x64` and `win-arm64`, self-contained .NET 9 publishes. **No x86 build** — Windows 11 has no 32-bit SKU.
- Avalonia and SkiaSharp both ship ARM64 natives; no blockers.
- The 32-bit concern that remains on x64/ARM64: **32-bit *senders*** of tray messages (WM_COPYDATA payloads with 32-bit struct layout, §4 REQ-W12) — a data-format issue, not a build-target issue.
- ARM64: x64 tray apps run under emulation and still speak the same window-message protocol — no special handling. Avoid any dependency that would force x64-only (none identified).

**REQ-W23.** CI MUST build and smoke-test (VM) both `win-x64` and `win-arm64` from day one of Windows work, even while Windows ships after macOS — the PAL contract tests (01-architecture.md) run per-RID.

---

## 10. Where Windows is easier, and what the PAL exploits (D-W9)

| Concern | macOS (02-macos-platform.md) | Windows | PAL consequence |
|---|---|---|---|
| Becoming the shell | loginwindow `Finder` defaults key, SIP constraints, unsupported | Documented Winlogon value | `IShellRegistration` trivial on Windows |
| Permissions | TCC prompts: Accessibility, Screen Recording, FDA | None for any of our core APIs | `IPermissionBroker` returns `AllGranted` constant on Windows |
| Tray capture | Pixel-scraping NSStatusItems via ScreenCaptureKit + synthesized clicks | Apps *push* icons to us via a documented-enough message protocol | `ITrayHost` on Windows is authoritative (owns icon lifecycle), on macOS it is a mirror. The PAL contract must model both: `TrayCapability.Authoritative` vs `.Mirrored` |
| Work area | No API; Dock shim / AX mitigation | `SPI_SETWORKAREA` + appbar protocol | `IWorkAreaReservation` real on Windows/X11, shimmed on macOS |
| Window control | AX API, per-app trust, fragile | Win32 HWND messages, universal | `IWindowController` full-fidelity on Windows |
| Live window previews | ScreenCaptureKit per-window streams (permission + CPU) | `DwmRegisterThumbnail` (free, composited) | Thumbnail PAL should be *surface-based* (give me a preview at rect), not *pixels-based*, so Windows can use DWM zero-copy |
| Native helper process | Required (Swift: SCK, AX, CGEvent) | **Not required** — everything is P/Invoke/COM from C# | Helper-process IPC layer is *optional per platform*; Windows backend links in-proc (crash containment via the §1.4 watchdog instead) |
| Event synthesis for tray clicks | CGEvent posting, Accessibility permission | `PostMessage` to icon's own callback window — by design | — |

Design rule extracted for 01-architecture.md: **PAL interfaces are shaped by the *hardest* platform's constraints but must not tax the easiest.** Concretely: async-everything (macOS helpers are out-of-proc) but Windows implementations may complete synchronously; capability flags everywhere (`TrayCapability`, virtual-desktop caps, work-area caps) instead of lowest-common-denominator feature cuts.

### 10.1 D-W9 is normative: no `helper-windows` (resolution of the 01/03 conflict)

Earlier drafts of 01-architecture.md sketched a `native/helper-windows` component ("C# NativeAOT helper — tray takeover, hooks"), included a Windows helper in the process diagram and §3.4, and assigned tray capture ("helper (Shell_TrayWnd takeover)") and event synthesis ("helper (SendInput)") to it in the PAL split table. **This chapter overrides that sketch.** The technical case:

- Every Windows integration in this chapter — `Shell_TrayWnd`/appbar server (§3–4), `RegisterShellHookWindow`/`SetWinEventHook` (§5), `SPI_SETWORKAREA`, virtual-desktop calls (§6) — is plain P/Invoke/COM callable from the shell process. There is no Windows equivalent of macOS's "this API only works with a TCC-granted, entitlement-carrying native process" constraint that motivated helpers in the first place.
- A helper would *hurt* here: `Shell_TrayWnd` and the shell hook window want to live where the message pump and the UI are; marshaling `WM_COPYDATA` payloads over IPC adds a copy, a failure mode, and latency to the hottest path.
- Tray click forwarding on Windows is `PostMessage` to the icon owner's own callback window after `AllowSetForegroundWindow` (REQ-W14) — not `SendInput` into foreign windows. The only `SendInput` use is the Win+Ctrl+arrow desktop-switch synthesis (§6), which is unprivileged and in-proc. The PAL split table's "helper (SendInput)" row was wrong on mechanism as well as on process placement.
- Crash containment is provided by Winlogon's `AutoRestartShell` plus the circuit breaker (§1.4) instead of helper isolation; if in-proc interop proves crashy in practice, the escape valve in Risk 6 (move tray/appbar into the existing helper harness) remains open because the PAL keeps the helper IPC layer *optional per platform*.

Required edits in sibling chapters (tracked here so the spec converges):

1. **01-architecture.md:** delete `native/helper-windows` from the repo layout; remove the Windows helper from the process diagram and §3.4; change the PAL split table's Windows column to in-proc for window enumeration/control, tray capture, and event synthesis, with a note pointing at this section.
2. **07-shell-ux.md §2.2:** the blanket "window-list sources are implemented in native helpers" becomes "per the platform chapters" — the Windows source (`RegisterShellHookWindow`, §5) is in-proc.

Scope note: D-W9 bans native *interop* helpers. It does not ban auxiliary plain-C# processes with no OS-integration code — specifically the update supervisor of 01-architecture.md UPD-03/SUP-06, which must be a separate process by definition (it swaps the shell binary while the shell is down). That supervisor and the §1.4 circuit breaker are complementary, not in conflict.

---

## Risks

1. **Tray protocol fidelity.** The WM_COPYDATA payload layouts (`SHELLTRAYDATA`, 32/64-bit `NOTIFYICONDATA` variants, `NOTIFYICONIDENTIFIER`) are undocumented-but-frozen. Risk is medium-low (frozen since forever; multiple OSS implementations to cross-check) but a subtle offset bug silently drops icons for some apps. Mitigation: adopt/vendor ManagedShell's battle-tested marshaling; matrix-test against a zoo of tray apps (Steam, Discord, OneDrive, corporate VPN clients).
2. **Windows 11 servicing changes shell internals.** Microsoft is actively rewriting taskbar/tray internals (e.g., Win11 tray churn already broke ExplorerPatcher repeatedly). We don't hook Explorer's code (safer than ExplorerPatcher), but apps could start preferring new registration paths (e.g., packaged apps using `Windows.UI.Notifications`-adjacent tray APIs) that bypass `Shell_TrayWnd`. Watch item; no action until observed.
3. **Defender/SmartScreen flagging.** Writing Winlogon `Shell` is a classic persistence TTP (MITRE T1547.004). Even signed, expect EDR flags in corporate environments. Mitigation: signing (REQ-W22), Defender vendor submission, HKCU-only writes, and companion-default install (most users never touch the key).
4. **Toast notification degradation** in shell mode (§2) may surprise users of chat apps that rely on toasts. `UserNotificationListener` remediation is unproven for a Win32 app without package identity — needs a spike before promising it.
5. **`SetTaskmanWindow` / `RegisterShellHookWindow` semantics** are exported-but-underdocumented; behavior verified by every alternative shell for two decades, but a future Windows could gate them. Fallback paths (WinEvent hooks) exist for everything except minimize-animation targeting.
6. **Crash containment without a helper process.** In-proc P/Invoke (D-W9, §10.1) means a bug in interop code takes down the whole shell — mitigated by the watchdog (REQ-W4) but a crash-loop UX is worse than macOS's helper-restart UX. If Windows interop proves crashy in practice, we can move the tray/appbar server into the same helper-process harness the other platforms use (the PAL already allows it).
7. **Cross-chapter drift on D-W9.** Until the 01-architecture.md and 07-shell-ux.md edits listed in §10.1 land, the spec set contains a stale Windows helper in 01's repo layout/process diagram/PAL table. §10.1 is the normative statement; treat conflicting text in 01/07 as pending deletion, not as an alternative design.

## Open questions

1. **How far to take `IShellWindows`/`SHOpenFolderAndSelectItems` emulation?** Registering a ShellWindows-compatible ROT object affects third-party automation (installers that "open the folder"). Full emulation is meaningful work in the file-manager chapter's scope (06-file-manager.md) — needs an owner decision on priority vs. the verb-handler fallback.
2. **Per-build `IVirtualDesktopManagerInternal` adapter (v1.x)** — is desktop pager richness (names, drag-to-desktop) worth an update-fragile dependency? Deferred; revisit after v1 telemetry on pager usage.

## Resolved (owner decisions, 2026-07-04)

- **Companion-default vs replacement-default (D-W6):** companion mode is the default install experience on Windows; replacement is a settings opt-in. Supersedes the earlier "engineering recommends / product call / needs sign-off" framing. The default may be revisited once macOS replace-rollout data is in.
- **Windows 10 support:** dropped — **Win11 22H2+ only** (saves ~2 ew). Every API in this chapter exists on Win10, so the cost was mainly the test matrix; not worth it now. Revisit only if early Windows demand skews Win10-heavy.
- **Updater choice:** **Velopack** (cross-platform, per 01-architecture.md UPD-01 / 00-master-plan.md R7). Resolves the earlier deferral of "Velopack vs. custom" to 09-engineering-plan.md — the choice is settled here and there.
