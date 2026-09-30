# Solution & Process Architecture

Status: DRAFT for review · Chapter 01 of the Bevel spec · Cross-references sibling chapters by number (02–09; see 09-engineering-plan.md for sequencing).

## Summary

Bevel is a UI process plus a native helper daemon *where the platform demands one*: a single cross-platform **Avalonia UI process** (`Bevel.App`, C#/.NET 9) that owns every pixel the user sees, and — on macOS and Linux — a small per-platform **native helper daemon** that owns every fragile OS integration (accessibility, screen capture, event synthesis). Windows runs single-process: all interop is in-proc P/Invoke/COM (ch. 03, D-W9), with Winlogon's `AutoRestartShell` plus a crash-count circuit breaker (ch. 03 §1.4) as the containment story instead of process isolation. Where a helper exists, the two processes talk over **gRPC on Unix domain sockets** (named pipes, should the contingency Windows helper ever materialize — §3.4), with a shared-memory side channel for pixel data. All platform behavior is reached through a **Platform Abstraction Layer** (`Bevel.Pal.Abstractions`) of narrow C# interfaces; the UI layer never references a platform API directly. The solution is a single .NET solution plus one native sub-project per platform, wired together with `Microsoft.Extensions.DependencyInjection`/Hosting, Serilog logging, offline-first crash reporting (local crash logs + export), Velopack self-update, and a layered JSON settings system. Themes are data-only packages (AXAML + assets), not code, and are loaded without plugin-level trust.

---

## 1. Repository and solution layout

One repository (monorepo), one .NET solution, native helpers as sibling sub-projects built by the same build pipeline.

```
bevel/
├── Bevel.sln
├── Directory.Build.props            # LangVersion, nullable enable, analyzers, version
├── Directory.Packages.props         # central package management (CPM)
├── global.json                      # pinned SDK (9.0.x)
├── src/
│   ├── Bevel.App/                   # Avalonia entry point, composition root, lifetime
│   ├── Bevel.Core/                  # domain: models, services, settings, no Avalonia refs
│   ├── Bevel.UI/                    # shared controls, chrome primitives, view infrastructure
│   ├── Bevel.Desktop/               # desktop surface module (icon grid, wallpaper) → ch. 07
│   ├── Bevel.Taskbar/               # taskbar module (window list, start menu, tray, clock) → ch. 07
│   ├── Bevel.FileManager/           # Explorer-style file manager module → ch. 06
│   ├── Bevel.Pal.Abstractions/      # PAL interfaces + DTOs, zero dependencies beyond BCL
│   ├── Bevel.Pal.MacOS/             # macOS PAL impl (helper client + direct P/Invoke where safe)
│   ├── Bevel.Pal.Windows/           # Windows PAL impl
│   ├── Bevel.Pal.Linux/             # Linux PAL impl (X11 + Wayland strategies) → ch. 04
│   ├── Bevel.Pal.Fake/              # deterministic in-memory PAL for tests & UI dev mode
│   ├── Bevel.Ipc/                   # .proto contracts + generated C# client/server stubs
│   ├── Bevel.Themes.Win2000/        # default theme (fork/extend Classic.Avalonia) → ch. 05
│   ├── Bevel.Themes.Luna/
│   └── Bevel.Themes.Win11/
├── native/
│   ├── helper-macos/                # Swift Package "BevelHelper" (AX, SCKit, CGEvent, Apple Events)
│   ├── helper-windows/              # CONTINGENCY ONLY — empty in v1; Windows is in-proc (ch. 03 D-W9), see §3.4
│   └── helper-linux/                # C# helper w/ DBus (Tmds.DBus) + thin C shims if needed
├── proto/                           # single source of truth for IPC (.proto files)
├── tests/
│   ├── Bevel.Core.Tests/
│   ├── Bevel.UI.Tests/              # Avalonia.Headless
│   ├── Bevel.Pal.ContractTests/     # one suite, run against Fake + each real PAL
│   └── Bevel.Integration.Tests/     # shell↔helper IPC round-trips
├── build/                           # NUKE build project (compile, sign, notarize, package)
├── assets/                          # recreated icon/cursor/sound sources (SVG masters) → ch. 05
└── docs/spec/                       # this spec
```

Naming and conventions:

- **ARCH-01** Root namespace is `Bevel.*`. Product name is **Bevel**, frozen at M0 (2026-07-04); reverse-DNS identifier `pl.ikari.bevel`, URL scheme `bevel://`.
- **ARCH-02** `Bevel.Core`, `Bevel.Pal.Abstractions`, and `Bevel.Ipc` MUST NOT reference Avalonia. Enforced by an architecture test (NetArchTest) in CI.
- **ARCH-03** Feature modules (`Bevel.Desktop`, `Bevel.Taskbar`, `Bevel.FileManager`) reference `Bevel.UI` + `Bevel.Core` + `Bevel.Pal.Abstractions` only — never a concrete PAL project. Only `Bevel.App` (the composition root) references concrete PALs.
- **ARCH-04** Central Package Management (`Directory.Packages.props`) pins every package version; Renovate keeps them fresh.
- **ARCH-05** Build orchestration is NUKE (C# build scripts) so macOS codesign/notarize, Swift build, protoc generation, and .NET publish live in one typed pipeline instead of a bash/yaml sprawl.

Rejected: separate repos for native helpers (version-skew hell across the IPC contract); Cake/Make (NUKE keeps us in C#).

## 2. Platform Abstraction Layer (PAL)

### 2.1 Design rules

- **PAL-01** Interfaces are *capability-oriented*, not platform-shaped: they describe what the shell needs ("reserve work area", "mirror tray items"), never how a platform does it.
- **PAL-02** Every interface exposes a `Capabilities` object; UI code feature-detects instead of `if (OperatingSystem.IsMacOS())`. Platform checks in UI/feature modules are a CI failure (analyzer ban on `OperatingSystem.Is*` outside PAL projects).
- **PAL-03** All PAL calls that cross into the helper are `async` and accept a `CancellationToken`. Event push uses .NET events backed by the gRPC streaming layer; handlers are marshaled to the Avalonia UI thread by a `IPalDispatcher` decorator, so consumers never touch threading.
- **PAL-04** DTOs are immutable records in `Bevel.Pal.Abstractions`; the mapping to/from protobuf lives in the concrete PAL, so the abstraction never leaks wire types.
- **PAL-05** Every PAL surface must pass `Bevel.Pal.ContractTests` (behavioral contract: ordering guarantees, event delivery after subscribe, cancellation semantics, error taxonomy) against `Bevel.Pal.Fake` and, on matching CI runners, real implementations.

### 2.2 Interface sketches

```csharp
namespace Bevel.Pal.Abstractions;

/// Foreign (non-shell) top-level windows: enumeration, control, thumbnails.
public interface IWindowManager
{
    WindowManagerCapabilities Capabilities { get; }               // thumbnails? zOrder? perSpace?
    ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default);
    event EventHandler<ForeignWindowEvent> WindowOpened;
    event EventHandler<ForeignWindowEvent> WindowClosed;
    event EventHandler<ForeignWindowEvent> WindowChanged;         // title/icon/minimize/focus
    event EventHandler<ForeignWindowEvent> ForegroundChanged;
    Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default);
    Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default);
    Task CloseAsync(ForeignWindowId id, CancellationToken ct = default);
    Task<PalImage?> GetThumbnailAsync(ForeignWindowId id, PixelSize maxSize, CancellationToken ct = default);
}

/// Host-OS tray/status-item capture and mirroring (pillar 3). Mac impl → ch. 02.
public interface ISystemTrayHost
{
    TrayHostCapabilities Capabilities { get; }                    // pixelMirror? sniProtocol? menuExtraction?
    ValueTask<IReadOnlyList<TrayItem>> GetItemsAsync(CancellationToken ct = default);
    event EventHandler<TrayItemEvent> ItemAdded;
    event EventHandler<TrayItemEvent> ItemRemoved;
    event EventHandler<TrayItemEvent> ItemUpdated;                // icon pixels, tooltip, menu
    /// Pixel stream for mirrored items; frames arrive via shared memory (§4.4).
    Task<ITrayFrameSubscription> SubscribeFramesAsync(TrayItemId id, CancellationToken ct = default);
    Task ForwardActivationAsync(TrayItemId id, TrayActivation kind, PixelPoint atShellPos, CancellationToken ct = default);
    /// macOS: hide/show the real menu bar status area while we mirror it.
    Task SetNativeTrayHiddenAsync(bool hidden, CancellationToken ct = default);
}

/// Wallpaper, monitors, work-area reservation, desktop metrics.
public interface IDesktopEnvironment
{
    DesktopCapabilities Capabilities { get; }                     // canReserveWorkArea? perMonitorWallpaper?
    IReadOnlyList<MonitorInfo> Monitors { get; }
    event EventHandler MonitorsChanged;
    Task<WorkAreaReservation> ReserveWorkAreaAsync(MonitorId monitor, DockEdge edge, int thicknessPx, CancellationToken ct = default);
    Task SetWallpaperVisibleToHostAsync(bool hostWallpaperHidden, CancellationToken ct = default);
    Task<PalWindowHandle> ConfigureShellSurfaceAsync(ShellSurfaceKind kind /*Desktop|Taskbar|Overlay*/, PalWindowHandle window, CancellationToken ct = default);
}

/// Being (and stopping being) the user's shell; login/session integration.
public interface IShellSession
{
    ShellSessionCapabilities Capabilities { get; }
    ValueTask<ShellInstallState> GetInstallStateAsync(CancellationToken ct = default);
    Task<ShellInstallResult> RegisterAsShellAsync(ShellRegistrationOptions opts, CancellationToken ct = default);
    Task UnregisterAsync(CancellationToken ct = default);         // always restorable — see SAFE-01
    Task SetRunAtLoginAsync(bool enabled, CancellationToken ct = default);
    Task LogOutAsync(LogoutKind kind, CancellationToken ct = default);  // logout/restart/shutdown/lock
    event EventHandler<SessionEvent> SessionChanged;              // lock/unlock, display sleep
}

/// File operations with progress, undo, and trash semantics (file manager backend).
public interface IFileOperations
{
    FileOpsCapabilities Capabilities { get; }                     // trash? cloneCopy(APFS)? undo?
    IFileOperationJob Copy(IReadOnlyList<string> sources, string destDir, FileOpOptions opts);
    IFileOperationJob Move(IReadOnlyList<string> sources, string destDir, FileOpOptions opts);
    IFileOperationJob Delete(IReadOnlyList<string> paths, DeleteMode mode /*Trash|Permanent*/, FileOpOptions opts);
    Task RenameAsync(string path, string newName, CancellationToken ct = default);
    Task<TrashInfo> GetTrashInfoAsync(CancellationToken ct = default);
    Task EmptyTrashAsync(CancellationToken ct = default);
    IFileSystemWatch Watch(string path, WatchOptions opts);       // FSEvents/inotify/ReadDirectoryChangesW
}

public interface IFileOperationJob : IAsyncDisposable
{
    FileOpJobId Id { get; }
    IObservable<FileOpProgress> Progress { get; }                 // bytes, items, current path, speed
    Task Completion { get; }
    Task PauseAsync(); Task ResumeAsync(); Task CancelAsync();
    Task RespondAsync(FileOpConflictResponse response);           // replace/skip/keep-both, per conflict
}

/// Icons for files, folders, apps, devices — at multiple sizes, themable overrides first.
public interface IIconProvider
{
    ValueTask<PalImage> GetIconAsync(IconRequest request, CancellationToken ct = default);
    // IconRequest: path | fileExtension | specialFolder | appBundleId, size (16/32/48/256), overlays (link, shared)
    event EventHandler<IconInvalidatedEvent> IconInvalidated;     // app installed/uninstalled, theme change
}

/// App launching, URL/document opening, running-app registry ("what's in the taskbar").
public interface IAppEnvironment
{
    ValueTask<IReadOnlyList<RunningApp>> GetRunningAppsAsync(CancellationToken ct = default);
    event EventHandler<RunningAppEvent> AppLaunched;
    event EventHandler<RunningAppEvent> AppTerminated;
    Task LaunchAsync(LaunchRequest request, CancellationToken ct = default);   // app, doc, url
    ValueTask<IReadOnlyList<InstalledApp>> EnumerateInstalledAppsAsync(CancellationToken ct = default); // start menu
}

/// TCC / permission brokering — mac-heavy, no-op elsewhere.
public interface IPermissionBroker
{
    ValueTask<PermissionState> GetStateAsync(ShellPermission p, CancellationToken ct = default);
    Task<PermissionState> RequestAsync(ShellPermission p, CancellationToken ct = default);  // may deep-link to Settings
    event EventHandler<PermissionChangedEvent> PermissionChanged;
}
// ShellPermission: Accessibility, ScreenRecording, FullDiskAccess, AppleEvents, InputMonitoring
```

### 2.3 PAL implementation split (in-proc vs helper)

| Concern | macOS | Windows | Linux |
|---|---|---|---|
| Window enumeration/control | helper (AX API + CGWindowList) | in-proc P/Invoke (EnumWindows etc.) | in-proc (X11 via XCB binding) / helper on Wayland |
| Tray capture/mirror | helper (ScreenCaptureKit + CGEvent) | in-proc (Shell_TrayWnd takeover — ch. 03 §3–4, D-W9) | in-proc (SNI over Tmds.DBus), helper for XEmbed |
| Event synthesis (click forward) | helper (CGEventPost, needs AX trust) | in-proc (SendInput) | helper |
| File ops, watching, icons | in-proc (BCL + P/Invoke, NSWorkspace via objc bridge for icons) | in-proc (IFileOperation COM) | in-proc (gio/trash spec) |
| Apple Events scripting suite (pillar 4) | helper (NSAppleEventManager) → ch. 02/08 | n/a | n/a |
| Session/shell registration | in-proc (`defaults`/SMAppService) | in-proc (registry) | in-proc (session files) |

Rule of thumb (**PAL-06**): anything that (a) requires a TCC grant, (b) runs callbacks on OS-owned threads, or (c) has a history of hard-crashing processes (AX, SCKit, hooks) lives in the helper. Cheap, safe syscalls stay in-proc to avoid IPC latency. On Windows this rule resolves to *everything in-proc* (**ch. 03 D-W9**): there is no TCC analog, the Win32 surface is stable and prompt-free, and 25 years of prior-art shells (Cairo, RetroBar, LiteStep) run it in-proc. Crash containment on Windows comes from Winlogon `AutoRestartShell` + the crash-count circuit breaker (ch. 03 §1.4), and the helper-process harness stays available as a contingency if Windows interop proves crashy in practice (ch. 03 §10).

## 3. Process model

```
                    ┌────────────────────────────────────────────┐
                    │  Bevel.App (C# / Avalonia, .NET 9)         │
                    │  desktop · taskbar · file manager · themes │
                    │  composition root · settings · logging     │
                    └───────△───────────────────────△────────────┘
                            │ gRPC over UDS /        │ shared-memory
                            │ named pipe (§4)        │ frame ring (§4.4)
                    ┌───────▽───────────────────────▽────────────┐
                    │  BevelHelper (macOS & Linux daemon)        │
                    │  macOS: Swift · linux: C# (+C shims)       │
                    │  AX / SCKit / CGEvent / Apple Events       │
                    │  server / XEmbed / watchdog                │
                    └────────────────────────────────────────────┘

     Windows: no helper process (ch. 03 D-W9) — Bevel.App alone, all
     interop in-proc P/Invoke/COM; Winlogon AutoRestartShell + crash
     circuit breaker (ch. 03 §1.4) provide the containment instead.
```

### 3.1 Why two processes where we run two (and one on Windows)

- **Crash isolation**: AX callbacks and ScreenCaptureKit streams are the crashiest code we will run. A helper crash costs us tray mirroring for ~2 s; it never takes down the desktop.
- **TCC identity**: on macOS, Accessibility/Screen Recording grants attach to the *responsible* code-signed binary. Keeping all TCC-gated work in one signed helper means one stable prompt story (details in ch. 02) and lets us rev the UI app without re-prompting.
- **Runtime freedom**: the macOS helper is Swift, so ScreenCaptureKit/AX/Apple Events are used first-class instead of through brittle objc-runtime marshaling from C#.

**Windows is the deliberate exception (ch. 03 D-W9):** none of the three rationales above applies there — no TCC analog, no OS-owned callback threads with a crash history, and every needed API (Shell_TrayWnd protocol, `EnumWindows`, `SendInput`, `IFileOperation`) is stable in-proc P/Invoke/COM proven by Cairo/RetroBar/LiteStep. A Windows helper would add IPC latency and a second failure mode for zero isolation benefit. Crash containment there is Winlogon `AutoRestartShell` plus the pre-Avalonia crash-count circuit breaker (ch. 03 §1.4, REQ-W4/W5).

Rejected: one process with try/catch around native calls (native crashes are not catchable); helper-per-feature (N processes = N sockets, N update targets — split later only if one subsystem proves unstable, the supervisor supports multiple children already); a mandatory helper on Windows (see above — kept only as a contingency harness, §3.4).

### 3.2 Supervision and restart policy

`Bevel.Core.Supervision.HelperSupervisor` (runs inside Bevel.App). SUP-01…07 apply where a helper exists (macOS, Linux); on Windows there is no helper (ch. 03 D-W9) and the equivalents are Winlogon `AutoRestartShell` + the crash-count circuit breaker (ch. 03 §1.4):

| Rule | Behavior |
|---|---|
| **SUP-01** launch | Bevel.App spawns the helper at startup with `--socket <path> --token <nonce> --parent-pid <pid>`; helper exits if parent PID dies (poll + kqueue/`WaitForSingleObject`). |
| **SUP-02** health | Bidirectional `Ping` RPC every 5 s; 3 missed pings ⇒ treat as hung, SIGKILL, restart. |
| **SUP-03** backoff | Restart delays 0 s, 1 s, 2 s, 5 s, 10 s (cap). Counter resets after 120 s healthy. |
| **SUP-04** crash-loop | >5 restarts in 120 s ⇒ **degraded mode**: dependent features (tray mirror, window list on mac) show an inline "integration offline — retry" affordance; core desktop/taskbar/file manager keep working. |
| **SUP-05** state resync | Helper is stateless between runs; on (re)connect the shell replays subscriptions and the helper re-sends full snapshots (tray items, windows). All event streams are snapshot+delta so a restart is a re-snapshot, not corruption. |
| **SUP-06** shell watchdog | Reverse direction: the helper watches the UI process. If Bevel.App dies while registered as the OS shell, the helper relaunches it (debounce 2 s, max 3 attempts) and, on final failure, executes the platform **escape hatch** (SAFE-01): mac — restore Finder's desktop (`CreateDesktop=true`), delete the loginwindow `Finder` key if the Advanced opt-in ever set it, relaunch Finder/Dock; linux — exec fallback session. Windows needs no reverse watchdog process: Winlogon `AutoRestartShell` relaunches the registered shell, and the circuit breaker restores `explorer.exe` on crash-loop (ch. 03 §1.4). |
| **SUP-07** boot resilience | The helper itself is registered with the OS keep-alive facility (macOS LaunchAgent `KeepAlive=true`; Linux systemd user unit `Restart=on-failure`) so the pair can never both stay dead. Windows: n/a — no helper, and Run keys are not a keep-alive facility (they execute once at logon); fresh-start-at-logon comes from the Winlogon `Shell` value itself. |

- **SAFE-01** Every mechanism that makes us the shell MUST have a no-network, no-our-code-required rollback documented and also automated: a standalone `shell-rescue` script installed at first run (macOS: `defaults write com.apple.finder CreateDesktop -bool true`, `defaults delete com.apple.loginwindow Finder` if the Advanced opt-in ever set it, then `open -a Finder` + `killall Finder` — this is ch. 02's `restore-finder.sh`, driven by the mutation journal of ch. 02 Req 2.3; Windows: restore `Shell=explorer.exe`; Linux: restore session file). Details per platform in ch. 02/03/04.

### 3.3 Threading model (shell process)

- Avalonia UI thread owns all visual state. PAL events arrive on gRPC threads and are dispatched via `IPalDispatcher` (wraps `Dispatcher.UIThread.Post` with coalescing for high-frequency streams like tray frames — latest-wins per item, ≤ display refresh rate).
- File operation jobs and IPC run on the thread pool; no dedicated threads except the shared-memory frame reader (one, blocking on a futex/eventfd-style signal).

### 3.4 Native helper implementation choices

- **macOS: Swift** (SwiftPM executable, signed + notarized alongside the app, embedded in `Bevel.app/Contents/Library/LoginItems` or `Contents/MacOS`). Uses grpc-swift for the IPC server. Rationale: SCKit/AX/NSAppleEventManager are Swift/ObjC-native; fighting them from C# costs more than a second language.
- **Windows: no helper process in v1** (ch. 03 D-W9). Everything needed (Shell_TrayWnd takeover, `SetWindowsHookEx`, `SendInput`, `IFileOperation`) is stable, prompt-free P/Invoke/COM callable in-proc from `Bevel.Pal.Windows`; a helper would add IPC latency and a second failure mode without buying isolation Windows actually needs. Crash containment: Winlogon `AutoRestartShell` + the pre-init crash circuit breaker (ch. 03 §1.4). **Contingency:** if in-proc interop proves crashy in practice (ch. 03 §10, risk 6), the tray/appbar server moves into a C# NativeAOT helper under `native/helper-windows/` speaking the same `.proto` contract over a named pipe — the supervisor and IPC layer of §3.2/§4 already support it, which is why the harness stays in the repo layout.
- **Linux: C#** (CoreCLR or NativeAOT), DBus via Tmds.DBus for SNI; thin C shims only if XEmbed demands them.

## 4. IPC

### 4.1 Decision: gRPC over Unix domain sockets / named pipes

**ARCH-06** IPC between Bevel.App and helpers is gRPC (HTTP/2) over a Unix domain socket (macOS/Linux) or named pipe (Windows — used only if the contingency helper of §3.4 is ever built; v1 Windows has no helper, ch. 03 D-W9), with protobuf contracts in `proto/` and a shared-memory side channel for pixel frames.

| Option | Verdict | Why |
|---|---|---|
| **gRPC over UDS/pipe** | **chosen** | Streaming RPCs map exactly to our event-push model; first-class codegen for C# (`Grpc.AspNetCore`/`Grpc.Net.Client` support UDS + named pipes) *and* Swift (grpc-swift); versionable protobuf contracts; deadline/cancellation propagation built in; testable over loopback. |
| Raw protobuf over socket | rejected | Reinvents framing, request correlation, streaming, cancellation, backpressure — exactly the bugs gRPC already fixed. |
| macOS XPC | rejected as the *contract* | No supported C# client; would make the PAL contract platform-specific, forking every interface. (The Swift helper may still use XPC internally toward its own sub-services if ever needed.) |
| JSON-RPC / StreamJsonRpc | rejected | Fine for control, poor for binary payloads and Swift-side codegen; weaker schema evolution story. |
| Shared memory only | rejected | Great for frames, terrible as an RPC substrate. Used as a *complement* (§4.4). |

### 4.2 Contract shape

- Package `bevel.helper.v1`. Services split by capability, mirroring the PAL: `WindowService`, `TrayService`, `InputService`, `SessionService`, `ScriptingService` (mac), plus `SupervisionService` (Ping, GetHelperInfo).
- Event delivery: one server-streaming RPC per event domain (`TrayService.StreamEvents`), always beginning with a `Snapshot` message then `Delta`s (**IPC-01**), so reconnect = resubscribe.
- **IPC-02** Versioning: additive-only within `v1`; `GetHelperInfo` returns `{protocolVersion, helperVersion, capabilities[]}` and the shell refuses helpers with a lower major protocol version (then triggers self-update repair).
- **IPC-03** Deadlines: every unary call carries a deadline (default 5 s; 30 s for file-sized transfers); the supervisor treats `DEADLINE_EXCEEDED` bursts like missed pings.

### 4.3 Transport security

- Socket at `~/Library/Application Support/Bevel/run/helper.sock` (mac), `$XDG_RUNTIME_DIR/bevel/helper.sock` (linux), `\\.\pipe\bevel-helper-<user-sid>` (win). Parent dir `0700`.
- **IPC-04** Peer verification on accept: `getpeereid`/`SO_PEERCRED`/`GetNamedPipeClientProcessId` must match the expected UID and the launch nonce passed via `--token` must be presented in the first handshake call. No TCP listener, ever.

### 4.4 Pixel plane: shared memory ring

Tray-icon mirrors (and later window thumbnails/live previews) are pixel streams; base64-in-protobuf would burn CPU. Design:

- Helper allocates a memory-mapped file per subscription (`MemoryMappedFile` / `mmap`), ring of N=3 BGRA frame slots + header (write index, per-slot seqlock).
- gRPC carries only control: `FrameRingInfo{path,size,format}` on subscribe, then lightweight `FrameSignal{slot,seq,dirtyRect}` messages on the stream.
- Reader validates seq before/after copy (torn-frame rejection). Latest-wins; no backpressure needed for UI mirroring.
- **IPC-05** Ring files live under the same `0700` runtime dir and are unlinked on unsubscribe.

## 5. Settings & configuration

- **CFG-01** Format: JSON with `System.Text.Json` source-generated serializers (AOT-friendly, fast startup). Written atomically (temp + rename).
- **CFG-02** Location: `~/Library/Application Support/Bevel/` (mac), `%APPDATA%\Bevel\` (win), `$XDG_CONFIG_HOME/bevel/` (linux) — resolved by `IAppPaths` in `Bevel.Core`.
- **CFG-03** Layering (highest wins): built-in defaults (code) → machine/managed layer (`/Library/Application Support/Bevel/managed.json` etc., for future MDM) → user `settings.json` → session overrides (never persisted). Merged view exposed as `ISettings` with typed sections: `settings.Get<TaskbarSettings>()`.
- **CFG-04** Live reload: file watcher + debounce; sections raise `Changed` events; UI binds through observable wrappers. External edits are legal and supported.
- **CFG-05** Schema versioning: top-level `"schemaVersion": n`; ordered migration steps in `Bevel.Core.Settings.Migrations`; unknown keys are preserved on rewrite (round-trip via `JsonObject`), so newer files survive older builds.
- Per-theme and per-module settings nest under their own keys (`"theme:win2000": {...}`), avoiding a central god-schema.

Rejected: `Microsoft.Extensions.Configuration` (read-oriented; write/round-trip support is DIY anyway); a database (overkill; users should be able to git their config); platform-native stores (`defaults`/registry) as primary (kills cross-platform sync and debuggability — but `IShellSession` still writes the OS keys the OS itself demands).

## 6. Themes and plugins

### 6.1 Themes are data, not code

- **THM-01** A theme package (`.beveltheme`, a zip) contains: `theme.json` manifest (id, name, version, minBevelVersion, metrics block), AXAML resource dictionaries + control themes, raster/vector assets, fonts, sounds. **No assemblies.** AXAML is parsed at load time with `AvaloniaRuntimeXamlLoader`; `x:Class`/code-behind is rejected by the loader configuration. **Distribution (2026-09-30):** no Bevel-hosted theme gallery; third-party themes may be installed from **git repositories** the user supplies. Install-UI warranties / licence blur for that path are **TBD** (`docs/legal/open-questions-parked.md`).
- **THM-02** First-party themes (Win2000 default — building on Classic.Avalonia (MIT), see ch. 03 — Luna, Win11) ship *compiled* inside `Bevel.Themes.*` assemblies for startup speed, but are also exported as `.beveltheme` packages so the third-party pipeline is exercised by our own themes.
- **THM-03** Theme switch is live: swap resource-dictionary/control-theme scopes, re-render; no restart. Target < 500 ms on the reference machine (perf budget in 09-engineering-plan.md).
- **THM-04** Theme asset licensing gate: CI runs an asset-manifest check; every shipped asset must carry provenance metadata (`created-by`, `license`) — Microsoft-original bitmaps/fonts/sounds are build failures (pillar: IP constraint; asset plan in ch. 03).

### 6.2 Plugins (post-v1, but architected now)

- **PLG-01** Plugin API surface is `Bevel.Sdk` (curated subset: tray widgets, start-menu providers, file-manager columns/context-menu items). Not the PAL, not internals.
- **PLG-02** In-proc plugins load into a collectible `AssemblyLoadContext` per plugin with an explicit shared-assembly allowlist (`Bevel.Sdk`, Avalonia). Unload on disable; a plugin exception disables the plugin, never the shell (top-level handler per plugin dispatch).
- **PLG-03** ALC is isolation, not security. Anything needing real sandboxing runs out-of-proc speaking a public gRPC surface (same infra as §4). v1 ships zero third-party plugin support; the decision here just prevents architecture that would preclude it.

## 7. Logging, diagnostics, crash reporting

- **LOG-01** Serilog everywhere in C#; sinks: rolling files (`<data-dir>/logs/bevel-.log`, 7 days / 50 MB cap), in-memory ring buffer (last 2000 events, dumped into crash reports), console in dev. Helper logs (os_log on mac, Serilog in C# helpers) are tailed by the supervisor and merged into the same directory with a `helper/` prefix.
- **LOG-02** Structured event IDs for supervision, IPC, and shell-registration paths (the "3 a.m. debugging" surfaces) — greppable, documented in `docs/ops/log-events.md`.
- **LOG-03** Crash reporting is **fully offline in v1**: crashes and diagnostics are written to local crash logs on disk (native crash handler enabled for the NativeAOT helpers; the Swift helper writes its crash reports into the same directory), surfaced in-app with an **export button** so the user can attach them to a bug report. **No network telemetry and no consent UI in v1** — nothing leaves the machine without an explicit user action. Scrubbing still applies to anything exported: no file paths from user volumes, no window titles, no tray-icon pixels. Network crash reporting (e.g., Sentry) is a post-v1 revisit, not the v1 plan.
- **LOG-04** `bevelctl diag` (a `Bevel.App --cli` verb): produces a redacted zip of logs + settings + capability matrix + helper state for bug reports.
- **LOG-05** UI-thread hang watchdog: 250 ms heartbeat, 5 s stall triggers a stack dump to logs (`dotnet-stack` style via `EventPipe`), because a hung shell is as bad as a crashed one.

## 8. Self-update

- **UPD-01** Updater: **Velopack** (cross-platform successor to Squirrel; MIT). One framework for all three platforms, delta updates, channels (`stable`, `beta`), staged rollouts. Rejected: Sparkle (mac-only, ObjC integration burden), MSIX/App Store models (fights shell replacement), hand-rolled (no).
- **UPD-02** The app bundle is the unit of update — helper binaries ship inside it, so shell/helper versions can never skew (see IPC-02 as the backstop for the restart window).
- **UPD-03** Being the shell changes the rules: never auto-apply mid-session. Flow: download + verify in background → badge on start menu → user picks "Restart shell to update" → on macOS/Linux the helper (watchdog, SUP-06) supervises the swap: shell exits, Velopack applies, helper relaunches new shell; on Windows (no helper, ch. 03 D-W9) Velopack's own `Update.exe` performs the apply-and-relaunch, with Winlogon `AutoRestartShell` as backstop. If relaunch fails, escape hatch SAFE-01 fires. On macOS the update must re-satisfy notarization/Gatekeeper and avoid app-translocation (install to `/Applications`, quarantine-clear via notarized ticket).
- **UPD-04** All packages signed (Developer ID + notarization on mac, Authenticode on win, minisign manifest on linux); updater verifies before staging. Distribution details in 09-engineering-plan.md.

## 9. Startup & session registration abstraction

`IShellSession` (§2.2) fronts these platform mechanisms:

| Platform | "Run at login" | "Be the shell" | Restore |
|---|---|---|---|
| macOS | `SMAppService.mainApp.register()` (LaunchAgent for helper) | **Coexist (default, ch. 02 §2.2):** `defaults write com.apple.finder CreateDesktop -bool false` + `killall Finder` — Finder stays alive but stops drawing the desktop layer; our windows take over, Apple Events/`NSWorkspace` keep working. **Advanced ("Replace Finder", opt-in, best-effort):** additionally attempts the loginwindow `Finder` key — unreliable-to-inert on modern macOS (viability spike tracked in ch. 09). Dock handling + AX work-area mitigation per ch. 02 | `defaults write com.apple.finder CreateDesktop -bool true`; delete loginwindow `Finder` key if set; relaunch Finder (ch. 02 `restore-finder.sh`) |
| Windows | `HKCU\...\Run` | `HKCU\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\Shell = Bevel.exe` (per-user shadow of HKLM value) | delete value → explorer.exe |
| Linux | XDG autostart `.desktop` | X11: own session `.desktop` in `/usr/share/xsessions/`; Wayland: session entry launching bundled compositor arrangement (position taken in ch. 07) | remove session entry |

- **SES-01** Registration is always: (1) install rescue script, (2) verify escape hatch works, (3) flip the key, (4) verify on next login via a first-run beacon; any failure auto-rolls back.
- **SES-02** Three install tiers exposed in onboarding: *Try it* (runs as an app over the host shell), *Default shell* (registered), *Kiosk* (future). On macOS, *Default shell* means **Coexist** mode (hidden-but-alive Finder, ch. 02); loginwindow replacement lives one level deeper behind the Advanced toggle. The whole product must be demoable in tier 1 — this is also our dev loop.

## 10. Dependency injection & testability

- **DI-01** `Microsoft.Extensions.Hosting` generic host inside Bevel.App: one composition root (`Program.cs` → `BevelHostBuilder`), `IServiceCollection` registrations grouped per module via `IModule.ConfigureServices(IServiceCollection, HostBuilderContext)`. Feature modules self-register; `Bevel.App` just lists modules.
- **DI-02** Concrete PAL selection at the composition root only: `services.AddPal(PlatformDetector.Current)` → registers `Bevel.Pal.MacOS` etc.; `--pal=fake` CLI switch swaps in `Bevel.Pal.Fake` for demo/dev/UI-test runs on any OS.
- **DI-03** ViewModels are constructor-injected, resolved through a lightweight `ViewModelLocator` backed by the container; no service-locator calls outside infrastructure. Avalonia views stay logic-free.
- **DI-04** Test pyramid: (a) `Bevel.Core.Tests` pure unit tests; (b) `Bevel.UI.Tests` on **Avalonia.Headless** with `Bevel.Pal.Fake` — full desktop/taskbar/file-manager scenario tests run on every CI OS with zero OS integration; (c) `Bevel.Pal.ContractTests` against Fake always, and against real PALs on matching self-hosted runners (macOS runner with TCC pre-granted — provisioning in 09-engineering-plan.md); (d) `Bevel.Integration.Tests` boots a real helper over a real socket and exercises supervision (kill -9 the helper mid-stream, assert resync per SUP-05).
- **DI-05** `Bevel.Pal.Fake` is a product feature, not just a test double: deterministic scripted desktops (fake windows, fake tray items, fake file system via injected `IFileSystem` abstraction) power screenshot/theming review tooling (ch. 03) and reproducible bug repros.
- **DI-06** Time, randomness, and the file system are injected (`TimeProvider`, `IFileSystem` from TestableIO/System.IO.Abstractions) in `Bevel.Core` — no static clocks in domain code.

## Risks

1. **gRPC-on-UDS friction in the Swift helper.** grpc-swift v2 is solid but heavier than the task; if binary size or startup cost offends, fallback is grpc-swift v1 or a hand-rolled HTTP/2-free transport — mitigated by keeping contracts in `.proto` so only the transport binding would change.
2. **Helper/shell mutual supervision deadlocks or restart storms** (SUP-03/06 interacting badly, e.g., both processes restarting each other) — macOS/Linux only; Windows has no process pair (ch. 03 D-W9) and its analog, `AutoRestartShell` crash-looping, is handled by ch. 03 §1.4's circuit breaker. Mitigation: restart authority is asymmetric — the helper only restarts the shell when the shell is registered as OS shell; integration tests must cover the matrix.
3. **Two-language tax (Swift + C#)** on a small team: every macOS tray/AX feature touches proto + Swift + C#. Accepted deliberately (§3.4); watch for contract churn in early milestones and batch proto changes.
4. **Runtime-parsed AXAML themes (THM-01) may hit Avalonia `RuntimeXamlLoader` gaps** (features that only work compiled). Mitigation: our own themes ship both compiled and packaged (THM-02), so gaps surface in-house first; keep a documented "supported AXAML subset" for theme authors.
5. **NativeAOT for the Linux helper** (and the contingency Windows helper, if it is ever built) could collide with a needed dependency (reflection-heavy libs). Mitigation: helpers are intentionally tiny; if AOT blocks, self-contained CoreCLR publish is a config flip.
6. **Settings live-reload + live theme swap + supervision** interacting during an update restart is a combinatorial corner; the integration suite needs an explicit "update while theme switching while helper crashed" chaos test.
7. **Crash-reporting privacy**: a desktop shell sees everything (window titles, file names). One scrubbing bug is a trust incident; treat LOG-03 scrubbing rules as security-reviewed code.

## Open questions

1. **Product name / root namespace** — RESOLVED (2026-07-04): product name is **Bevel**, root namespace `Bevel.*`, identifiers `pl.ikari.bevel` / `bevel://`, frozen at M0 (ARCH-01).
2. **Crash reporting backend** — RESOLVED (2026-07-04): v1 is **fully offline** — local crash logs + in-app export, no network telemetry and no consent UI (LOG-03). Network reporting (Sentry SaaS/self-hosted) is a post-v1 revisit.
3. **Update feed hosting & staged-rollout policy** — GitHub Releases is fine for beta; is there budget for a CDN + rollout percentage control at 1.0?
4. **Should the macOS helper eventually become two helpers** (one TCC-privileged for AX/capture, one unprivileged for Apple Events scripting), to minimize the blast radius of the Screen Recording grant? Supervisor supports it (§3.1); needs a call on prompt UX before the first public build.
5. **Managed/MDM configuration layer (CFG-03)** — RESOLVED (2026-07-04): the managed-config hook is **kept in v1**. The config-source abstraction (built-in → managed → user → session, CFG-03) stays so a managed layer slots in without a schema change; MDM *delivery* of that layer is the future piece.
6. **Contract-test hardware** — do we fund a self-hosted macOS CI runner with pre-granted TCC permissions at project start (recommended), or accept real-PAL tests being manual until beta? Interacts with 09-engineering-plan.md milestones.
7. **Windows contingency helper harness** — keep the empty `native/helper-windows/` directory (plus its named-pipe transport binding) in-tree from day one so the D-W9 escape route stays cheap, or add it only if ch. 03 §10's in-proc-crashiness risk actually materializes? Leaning keep-empty; costs nothing, documents intent.
8. **macOS Advanced ("Replace Finder") mode fate** — if the ch. 09 loginwindow-key spike concludes the key is fully inert on supported OS versions, does the Advanced toggle ship at all in v1, or is Coexist the only mode? Owner call after the spike; §9 and SAFE-01 are written to survive either answer.
