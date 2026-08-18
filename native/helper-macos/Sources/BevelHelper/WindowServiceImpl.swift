import AppKit
import ApplicationServices
import CoreGraphics
import Foundation
import GRPCCore
import GRPCProtobuf
import ScreenCaptureKit

// MARK: - Private API declarations

/// Private SPI: map an AXUIElement to its CGWindowID.
/// Returns `kAXErrorSuccess` (0) when the mapping succeeds.
@_silgen_name("_AXUIElementGetWindow")
func _AXUIElementGetWindow(_ element: AXUIElement, _ windowId: inout CGWindowID) -> AXError

/// Private SPI: reduce AX messaging timeout to avoid hangs on unresponsive apps.
/// Default is often 6s; we cap at ~1s per the spec (R18).
@_silgen_name("AXUIElementSetMessagingTimeout")
func _AXUIElementSetMessagingTimeout(_ element: AXUIElement, _ timeoutInSeconds: Float) -> AXError

// MARK: - AXObserver C callback

/// Global C callback invoked by the AXObserver run loop.
/// Bridges back to the WindowServiceImpl via the `refcon` pointer.
private func axObserverCallback(
    observer: AXObserver,
    element: AXUIElement,
    notification: CFString,
    refcon: UnsafeMutableRawPointer?
) {
    guard let refcon else { return }
    let svc = Unmanaged<WindowServiceImpl>.fromOpaque(refcon).takeUnretainedValue()
    svc.enqueueAXEvent(observer: observer, element: element, notification: notification)
}

// MARK: - AXNotification → WindowChange.Kind helper

func axNotificationToChangeKind(_ notification: String) -> Bevel_Helper_V1_WindowChange.Kind? {
    switch notification {
    case kAXFocusedWindowChangedNotification: return .focused
    case kAXTitleChangedNotification:         return .titleChanged
    case kAXWindowMiniaturizedNotification:   return .minimized
    case kAXWindowDeminiaturizedNotification: return .deminimized
    case kAXMovedNotification:                return .moved
    case kAXResizedNotification:              return .moved
    case kAXWindowCreatedNotification:        return .opened
    case kAXUIElementDestroyedNotification:   return .closed
    default: return nil
    }
}

// MARK: - Geometry sanitizer

/// Clamp a foreign-process-supplied CGFloat to Int32. A plain `Int32(...)` cast TRAPS on NaN,
/// infinity, or out-of-range values — and window geometry here comes from other apps' AX servers
/// and CGWindowList entries, so one hostile/buggy app reporting absurd geometry would crash-loop
/// the helper (review: adversarial, validated).
private func clampToInt32(_ v: CGFloat) -> Int32 {
    v.isFinite ? Int32(clamping: Int64(v.rounded())) : 0
}

// MARK: - WindowServiceImpl

/// Window enumeration and control service backed by CGWindowList + AXUIElement.
///
/// Implements the `WindowService` gRPC contract:
/// - `ListWindows`     — enumerate all on-screen layer-0 windows
/// - `Activate`        — bring a window to the foreground
/// - `Minimize`        — minimize a window into the Dock
/// - `Restore`         — restore a minimized window
/// - `Close`           — close a window
/// - `Changes`         — snapshot-then-deltas stream (server-streaming)
///
/// Correlation between CGWindowIDs and AXUIElements uses the private
/// `_AXUIElementGetWindow` SPI with a frame-comparison fallback.
/// AXObserver notifications drive the change stream, backstopped by a
/// mandatory ≥1s reconciliation poll (AX notifications are unreliable).
final class WindowServiceImpl: RegistrableRPCService, @unchecked Sendable {
    let expectedKey: String

    /// The shell process (Bevel.App) that launched this helper. Its own windows
    /// (desktop, taskbar, file manager) must be excluded from enumeration — the
    /// helper runs in a SEPARATE process, so `getpid()` only excludes the helper,
    /// not the shell whose windows we'd otherwise mirror back onto its own taskbar.
    let parentPID: pid_t

    /// Last observed frontmost application that is not the shell itself.
    /// Read and written from both the reconciliation poll and gRPC handler
    /// threads, so every access MUST hold `stateLock`.
    private var lastForeignFrontmostPID: pid_t?

    // MARK: - State

    /// Current window snapshot keyed by CGWindowID, updated by reconciliation poll.
    private let stateLock = NSLock()
    private var windowStore: [CGWindowID: Bevel_Helper_V1_TaskbarWindow] = [:]

    /// Running-but-windowless regular apps (bevel-ww71), keyed by bundle id. Each is a synthetic
    /// TaskbarWindow (windowID "app:<bundle>", isAppPresence=true, no frame) diffed + emitted on the
    /// Changes stream exactly like windows. Guarded by `stateLock` alongside `windowStore`.
    private var appStore: [String: Bevel_Helper_V1_TaskbarWindow] = [:]

    /// App-icon PNG cache keyed by bundle id. The reconciliation poll now builds
    /// full descriptors (icons included) every ~1s, so re-rendering each app's icon
    /// from disk on every tick would be wasteful — cache the PNG once per bundle id.
    /// Guarded by its own lock so the slow render never blocks `stateLock`.
    private let iconCacheLock = NSLock()
    private var iconCache: [String: Data] = [:]

    /// Per-pid AXObservers.
    private var axObservers: [pid_t: AXObserver] = [:]

    /// Dedicated CFRunLoop that actually pumps the AXObservers and the NSWorkspace
    /// app-launch hook. Without this, observer sources were added to `CFRunLoopGetCurrent()`
    /// inside the poll `Task` (a cooperative-pool thread that never runs a run loop), so AX
    /// notifications never fired and the 1s poll was the ONLY detector — the "new windows
    /// appear ~1s late" bug. Set once on the AX thread; read after `axRunLoopReady`.
    private var axRunLoop: CFRunLoop?
    private let axRunLoopReady = DispatchSemaphore(value: 0)
    /// Token for the NSWorkspace didLaunchApplication observer, so it can be removed
    /// on shutdown. Assigned on the AX thread before `axRunLoopReady` is signalled and
    /// read after `shutdown()`, so the semaphore provides the necessary ordering.
    private var launchObserverToken: NSObjectProtocol?
    /// NSWorkspace app-activation hook token. Fires the instant the frontmost app changes — the
    /// signal AX's per-app window notifications miss on an app-to-app switch (Cmd-Tab, Dock, clicking
    /// another app's window), so the taskbar pressed-state can follow focus that didn't originate
    /// from a taskbar click. Same lifetime/threading contract as `launchObserverToken`.
    private var activateObserverToken: NSObjectProtocol?
    /// Serializes observer registration, which can now come from two threads (the poll's
    /// `ensureObservers` and the launch hook's `addObserver`).
    private let observerLock = NSLock()

    /// Active Changes subscribers. Each holds the gRPC response writer directly,
    /// so the stream stays open until cancelled (no AsyncStream lifecycle pitfalls).
    /// Writes are serialized via `_writeQueue` (gRPC writers are not concurrent-safe).
    private var subscribers: [UUID: RPCWriter<Bevel_Helper_V1_WindowChange>] = [:]
    private let writeQueue = DispatchQueue(label: "bevel.window.changes.write")
    private var pollTask: Task<Void, Never>?

    /// Whether the service has been shut down.
    private var isShutdown = false

    /// Debug window enumeration to stderr (relayed to the app log) when
    /// BEVEL_DEBUG_WINDOWS=1. Explains why each candidate window is kept or dropped.
    private let debugWindows = ProcessInfo.processInfo.environment["BEVEL_DEBUG_WINDOWS"] == "1"

    private func dbg(_ msg: @autoclosure () -> String) {
        guard debugWindows else { return }
        FileHandle.standardError.write(Data(("[BEVEL-WIN] " + msg() + "\n").utf8))
    }

    // MARK: - Init / Deinit

    init(expectedKey: String, parentPID: pid_t = 0) {
        self.expectedKey = expectedKey
        self.parentPID = parentPID
        startAXRunLoopThread() // must be ready before any observer is registered
        startReconciliationPoll()
    }

    deinit {
        shutdown()
    }

    func shutdown() {
        stateLock.lock()
        isShutdown = true
        stateLock.unlock()

        pollTask?.cancel()

        // Remove the NSWorkspace launch hook so it can't invoke addObserver after
        // shutdown. Nil'd out so a second shutdown()/deinit is a no-op.
        if let token = launchObserverToken {
            NSWorkspace.shared.notificationCenter.removeObserver(token)
            launchObserverToken = nil
        }
        if let token = activateObserverToken {
            NSWorkspace.shared.notificationCenter.removeObserver(token)
            activateObserverToken = nil
        }

        // Tear down AXObservers (and their run-loop sources) BEFORE the run loop is
        // stopped and before the instance can be deallocated — see removeAllObservers.
        removeAllObservers()
        if let runLoop = axRunLoop { CFRunLoopStop(runLoop) }
        finishAllSubscribers()
    }

    // MARK: - RegistrableRPCService

    func registerMethods<Transport: ServerTransport>(with router: inout RPCRouter<Transport>) {
        let serviceName = "bevel.helper.v1.WindowService"

        // ── ListWindows ──────────────────────────────────────────────────
        router.registerHandler(
            forMethod: MethodDescriptor(fullyQualifiedService: serviceName, method: "ListWindows"),
            deserializer: ProtobufDeserializer<Bevel_Helper_V1_ListWindowsRequest>(),
            serializer: ProtobufSerializer<Bevel_Helper_V1_ListWindowsReply>(),
            handler: { [weak self] request, context in
                guard let self else {
                    throw RPCError(code: .internalError, message: "WindowService deallocated")
                }
                try AuthInterceptor.authenticate(
                    request.metadata,
                    expectedKey: self.expectedKey,
                    expectedCapability: "window"
                )
                _ = try await ServerRequest(stream: request)
                let windows = self.enumerateWindows()
                var reply = Bevel_Helper_V1_ListWindowsReply()
                reply.windows = windows
                return StreamingServerResponse(single: ServerResponse(message: reply))
            }
        )

        // ── Activate ─────────────────────────────────────────────────────
        router.registerHandler(
            forMethod: MethodDescriptor(fullyQualifiedService: serviceName, method: "Activate"),
            deserializer: ProtobufDeserializer<Bevel_Helper_V1_WindowRef>(),
            serializer: ProtobufSerializer<Bevel_Helper_V1_ActivateReply>(),
            handler: { [weak self] request, context in
                guard let self else {
                    throw RPCError(code: .internalError, message: "WindowService deallocated")
                }
                try AuthInterceptor.authenticate(
                    request.metadata,
                    expectedKey: self.expectedKey,
                    expectedCapability: "window"
                )
                let req = try await ServerRequest(stream: request)
                try self.activateWindow(windowID: req.message.windowID)
                return StreamingServerResponse(
                    single: ServerResponse(message: Bevel_Helper_V1_ActivateReply())
                )
            }
        )

        // ── Minimize ─────────────────────────────────────────────────────
        router.registerHandler(
            forMethod: MethodDescriptor(fullyQualifiedService: serviceName, method: "Minimize"),
            deserializer: ProtobufDeserializer<Bevel_Helper_V1_WindowRef>(),
            serializer: ProtobufSerializer<Bevel_Helper_V1_MinimizeReply>(),
            handler: { [weak self] request, context in
                guard let self else {
                    throw RPCError(code: .internalError, message: "WindowService deallocated")
                }
                try AuthInterceptor.authenticate(
                    request.metadata,
                    expectedKey: self.expectedKey,
                    expectedCapability: "window"
                )
                let req = try await ServerRequest(stream: request)
                try self.minimizeWindow(windowID: req.message.windowID)
                return StreamingServerResponse(
                    single: ServerResponse(message: Bevel_Helper_V1_MinimizeReply())
                )
            }
        )

        // ── Restore ──────────────────────────────────────────────────────
        router.registerHandler(
            forMethod: MethodDescriptor(fullyQualifiedService: serviceName, method: "Restore"),
            deserializer: ProtobufDeserializer<Bevel_Helper_V1_WindowRef>(),
            serializer: ProtobufSerializer<Bevel_Helper_V1_RestoreReply>(),
            handler: { [weak self] request, context in
                guard let self else {
                    throw RPCError(code: .internalError, message: "WindowService deallocated")
                }
                try AuthInterceptor.authenticate(
                    request.metadata,
                    expectedKey: self.expectedKey,
                    expectedCapability: "window"
                )
                let req = try await ServerRequest(stream: request)
                try self.restoreWindow(windowID: req.message.windowID)
                return StreamingServerResponse(
                    single: ServerResponse(message: Bevel_Helper_V1_RestoreReply())
                )
            }
        )

        // ── Close ────────────────────────────────────────────────────────
        router.registerHandler(
            forMethod: MethodDescriptor(fullyQualifiedService: serviceName, method: "Close"),
            deserializer: ProtobufDeserializer<Bevel_Helper_V1_WindowRef>(),
            serializer: ProtobufSerializer<Bevel_Helper_V1_CloseReply>(),
            handler: { [weak self] request, context in
                guard let self else {
                    throw RPCError(code: .internalError, message: "WindowService deallocated")
                }
                try AuthInterceptor.authenticate(
                    request.metadata,
                    expectedKey: self.expectedKey,
                    expectedCapability: "window"
                )
                let req = try await ServerRequest(stream: request)
                try self.closeWindow(windowID: req.message.windowID)
                return StreamingServerResponse(
                    single: ServerResponse(message: Bevel_Helper_V1_CloseReply())
                )
            }
        )

        // ── TerminateApp ─────────────────────────────────────────────────
        router.registerHandler(
            forMethod: MethodDescriptor(fullyQualifiedService: serviceName, method: "TerminateApp"),
            deserializer: ProtobufDeserializer<Bevel_Helper_V1_TerminateAppRequest>(),
            serializer: ProtobufSerializer<Bevel_Helper_V1_TerminateAppReply>(),
            handler: { [weak self] request, context in
                guard let self else {
                    throw RPCError(code: .internalError, message: "WindowService deallocated")
                }
                try AuthInterceptor.authenticate(
                    request.metadata,
                    expectedKey: self.expectedKey,
                    expectedCapability: "window"
                )
                let req = try await ServerRequest(stream: request)
                let ok = self.terminateApp(bundleID: req.message.bundleID, force: req.message.force)
                var reply = Bevel_Helper_V1_TerminateAppReply()
                reply.ok = ok
                return StreamingServerResponse(single: ServerResponse(message: reply))
            }
        )

        // ── Reposition ────────────────────────────────────────────────
        router.registerHandler(
            forMethod: MethodDescriptor(fullyQualifiedService: serviceName, method: "Reposition"),
            deserializer: ProtobufDeserializer<Bevel_Helper_V1_RepositionRequest>(),
            serializer: ProtobufSerializer<Bevel_Helper_V1_RepositionReply>(),
            handler: { [weak self] request, context in
                guard let self else {
                    throw RPCError(code: .internalError, message: "WindowService deallocated")
                }
                try AuthInterceptor.authenticate(
                    request.metadata,
                    expectedKey: self.expectedKey,
                    expectedCapability: "window"
                )
                let req = try await ServerRequest(stream: request)
                try self.repositionWindow(
                    windowID: req.message.windowID,
                    target: req.message.target
                )
                return StreamingServerResponse(
                    single: ServerResponse(message: Bevel_Helper_V1_RepositionReply())
                )
            }
        )

        // ── CaptureWindow ─────────────────────────────────────────────────
        router.registerHandler(
            forMethod: MethodDescriptor(fullyQualifiedService: serviceName, method: "CaptureWindow"),
            deserializer: ProtobufDeserializer<Bevel_Helper_V1_CaptureWindowRequest>(),
            serializer: ProtobufSerializer<Bevel_Helper_V1_CaptureWindowReply>(),
            handler: { [weak self] request, context in
                guard let self else {
                    throw RPCError(code: .internalError, message: "WindowService deallocated")
                }
                try AuthInterceptor.authenticate(
                    request.metadata,
                    expectedKey: self.expectedKey,
                    expectedCapability: "window"
                )
                let req = try await ServerRequest(stream: request)
                var reply = Bevel_Helper_V1_CaptureWindowReply()
                if let cgID = CGWindowID(req.message.windowID) {
                    reply.png = await self.captureWindowThumbnail(
                        windowID: cgID,
                        maxWidth: Int(req.message.maxWidth),
                        maxHeight: Int(req.message.maxHeight)
                    )
                }
                return StreamingServerResponse(single: ServerResponse(message: reply))
            }
        )

        // ── Changes (server-streaming) ───────────────────────────────────
        router.registerHandler(
            forMethod: MethodDescriptor(fullyQualifiedService: serviceName, method: "Changes"),
            deserializer: ProtobufDeserializer<Bevel_Helper_V1_ChangesRequest>(),
            serializer: ProtobufSerializer<Bevel_Helper_V1_WindowChange>(),
            handler: { [weak self] request, context in
                guard let self else {
                    throw RPCError(code: .internalError, message: "WindowService deallocated")
                }
                try AuthInterceptor.authenticate(
                    request.metadata,
                    expectedKey: self.expectedKey,
                    expectedCapability: "window"
                )
                _ = try await ServerRequest(stream: request)

                return StreamingServerResponse(
                    of: Bevel_Helper_V1_WindowChange.self
                ) { [weak self] writer in
                    guard let self else {
                        return [:]
                    }
                    // 1. Emit full snapshot, then register the writer as a
                    //    live subscriber. reconcile()/AXObserver feed it until the
                    //    client disconnects (the producer Task is cancelled).
                    let windows = self.enumerateWindows()
                    for win in windows {
                        var change = Bevel_Helper_V1_WindowChange()
                        change.kind = .snapshot
                        change.window = win
                        try await writer.write(change)
                    }
                    let subID = self.addSubscriber(writer)
                    // Remove this subscriber whenever the producer ends — whether the
                    // loop below falls through on cancellation or Task.sleep throws when
                    // the client disconnects. Without this, disconnected clients' writers
                    // accumulate in `subscribers` forever.
                    defer { self.removeSubscriber(subID) }
                    // Hold the writer open until the client disconnects; the producer
                    // Task is cancelled on disconnect, ending this loop.
                    while !Task.isCancelled {
                        try await Task.sleep(nanoseconds: 1_000_000_000)
                    }
                    return [:]
                }
            }
        )
    }

    // MARK: - Window enumeration

    /// Fetch layer-0 CGWindowList entries (excluding our own process and desktop
    /// elements). `.optionAll` includes minimized/off-screen windows, which is
    /// required so minimized windows stay listed (bevel-m2.3).
    /// Captures a PNG thumbnail of the window with the given CGWindowID via ScreenCaptureKit, scaled to
    /// fit maxWidth x maxHeight (0 = a 240x160 default), preserving aspect. Empty Data if unavailable
    /// (no Screen Recording permission, window gone, or capture error) — the caller shows no preview.
    private func captureWindowThumbnail(windowID: CGWindowID, maxWidth: Int, maxHeight: Int) async -> Data {
        guard CGPreflightScreenCaptureAccess() else { return Data() }
        // Bound the whole capture (cold SCShareableContent + screenshot) so a slow first-call CGS/SCK
        // init after a Screen-Recording grant can never hang the RPC (bevel-1275). On timeout the caller
        // keeps the limited-mode app icon — identical to any other capture failure. The .NET side carries
        // its own deadline too; this is the belt-and-suspenders leg inside the helper.
        let result = await Self.withTimeout(seconds: 2.5) {
            // onScreenWindowsOnly:false so MINIMIZED windows are still capturable — the rest of the helper
            // deliberately supports minimized windows (bevel-m2.3), and the taskbar hovers them too. Otherwise
            // hovering a minimized item silently falls back to title-only (review: swift-ios).
            guard let content = try? await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false),
                  let scWindow = content.windows.first(where: { $0.windowID == windowID }) else {
                return Data()
            }
            let w = scWindow.frame.width, h = scWindow.frame.height
            guard w > 1, h > 1 else { return Data() }
            let maxW = maxWidth > 0 ? Double(maxWidth) : 240
            let maxH = maxHeight > 0 ? Double(maxHeight) : 160
            let fit = min(maxW / w, maxH / h, 1.0)      // never upscale past the window's point size
            let scale = fit * 2                          // capture at 2x the fitted size → crisp downscale in the UI
            let config = SCStreamConfiguration()
            config.width = max(2, Int(w * scale))
            config.height = max(2, Int(h * scale))
            config.showsCursor = false
            config.ignoreShadowsSingleWindow = true
            let filter = SCContentFilter(desktopIndependentWindow: scWindow)
            guard let cgImage = try? await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config) else {
                return Data()
            }
            return NSBitmapImageRep(cgImage: cgImage).representation(using: .png, properties: [:]) ?? Data()
        }
        return result ?? Data()
    }

    /// Runs `operation` but returns nil if it hasn't finished within `seconds` — a bound around a
    /// cold/wedged ScreenCaptureKit init so a slow first capture can't hang the RPC (bevel-1275). The
    /// losing branch is cancelled; the caller treats nil as "capture unavailable" (limited-mode icon).
    static func withTimeout<T: Sendable>(seconds: Double, _ operation: @escaping @Sendable () async -> T?) async -> T? {
        await withTaskGroup(of: T?.self) { group in
            group.addTask { await operation() }
            group.addTask {
                try? await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
                return nil
            }
            let first = await group.next() ?? nil
            group.cancelAll()
            return first
        }
    }

    private func layer0Entries(options: CGWindowListOption) -> [[String: Any]] {
        let ownPID = getpid()
        guard let cgWindows = CGWindowListCopyWindowInfo(
            [options, .excludeDesktopElements],
            kCGNullWindowID
        ) as? [[String: Any]] else {
            return []
        }
        return cgWindows.filter {
            let owner = $0[kCGWindowOwnerPID as String] as? pid_t ?? -1
            return ($0[kCGWindowLayer as String] as? Int32 ?? 99) == 0
                && owner != ownPID
        }
    }

    /// Build a full `TaskbarWindow` descriptor for a CGWindowList entry, correlating
    /// to AX for minimized/focus state and loading the app icon.
    ///
    /// Shared by BOTH `enumerateWindows()` (ListWindows + Changes snapshot) AND the
    /// reconciliation poll so the two paths produce identical rich descriptors.
    /// Previously the poll path built its own icon-less, `isMinimized`-hardcoded
    /// descriptors, so windows opened after startup showed no icon (bevel-m2.1) and
    /// minimized windows were diffed as closed and dropped from the taskbar
    /// (bevel-m2.3). One builder keeps the paths from drifting again.
    private func describe(
        entry: [String: Any],
        axMap: [CGWindowID: AXUIElement],
        effectiveFrontmost: pid_t?
    ) -> Bevel_Helper_V1_TaskbarWindow? {
        guard let cgID = entry[kCGWindowNumber as String] as? CGWindowID else { return nil }
        let pid = entry[kCGWindowOwnerPID as String] as? pid_t ?? 0
        let ownerName = entry[kCGWindowOwnerName as String] as? String ?? "?"

        // Only apps that appear in the Dock (regular activation policy) belong on the
        // taskbar. This drops menu-bar-only agents (.accessory) and background daemons
        // (.prohibited), which were flooding the bar with non-window entries.
        guard let app = NSRunningApplication(processIdentifier: pid) else {
            dbg("drop cg=\(cgID) '\(ownerName)' pid=\(pid) reason=no-running-app")
            return nil
        }
        guard app.activationPolicy == .regular else {
            dbg("drop cg=\(cgID) '\(app.localizedName ?? ownerName)' pid=\(pid) reason=activationPolicy=\(app.activationPolicy.rawValue)")
            return nil
        }

        var win = Bevel_Helper_V1_TaskbarWindow()
        win.windowID = String(cgID)
        win.title = entry[kCGWindowName as String] as? String ?? ""
        win.appName = app.localizedName ?? (entry[kCGWindowOwnerName as String] as? String ?? "")
        win.appBundleID = app.bundleIdentifier ?? ""
        win.pid = Int32(pid)

        // App icon as PNG (best-effort; empty when unavailable), cached per bundle id.
        if !win.appBundleID.isEmpty {
            win.appIconPng = cachedAppIconPNG(bundleID: win.appBundleID)
        }

        if let bounds = entry[kCGWindowBounds as String] as? NSDictionary {
            var rect = Bevel_Helper_V1_PixelRect()
            rect.x = clampToInt32((bounds["X"] as? CGFloat) ?? 0)
            rect.y = clampToInt32((bounds["Y"] as? CGFloat) ?? 0)
            rect.width = clampToInt32((bounds["Width"] as? CGFloat) ?? 0)
            rect.height = clampToInt32((bounds["Height"] as? CGFloat) ?? 0)
            win.frame = rect
        }

        if let axWin = axMap[cgID] {
            // isMinimized — read FIRST, because the subrole/title filters below must exempt
            // minimized windows (bevel-m2.3). Minimized windows are off-screen, so `.optionAll`
            // on the CGWindowList query is required; CGWindowList alone cannot report it.
            var minVal: CFTypeRef?
            let minRes = AXUIElementCopyAttributeValue(axWin, kAXMinimizedAttribute as CFString, &minVal)
            win.isMinimized = (minRes == .success) && (minVal as? Bool == true)

            // Standard windows only — excludes panels, sheets, popovers, tooltips and other
            // non-standard subroles that regular apps also expose at layer 0. Minimized windows
            // are exempt: some apps (e.g. Jump Desktop) report a non-standard subrole such as
            // AXDialog for a *miniaturized* window, but it was a real taskbar window and must
            // stay listed while minimized (bevel-m2.3) — otherwise its button vanishes on minimize.
            if !win.isMinimized, let subrole = nonStandardSubrole(of: axWin) {
                dbg("drop cg=\(cgID) '\(win.appName)' reason=subrole=\(subrole)")
                return nil
            }

            // Window title from AX. kCGWindowName needs Screen Recording (usually not
            // granted → empty, so the label fell back to the bundle id); AX titles need
            // only Accessibility, so read from AX whenever the CG title is blank.
            if win.title.isEmpty {
                var titleVal: CFTypeRef?
                if AXUIElementCopyAttributeValue(axWin, kAXTitleAttribute as CFString, &titleVal) == .success,
                   let t = titleVal as? String {
                    win.title = t
                }
            }
        } else {
            win.isMinimized = false
        }

        // Closed-window removal (bevel-m2.3): a window that has just been closed can leave a brief
        // CGWindowList tombstone — its name and bounds survive for a moment after the NSWindow is torn
        // down, so kCGWindowName still yields a title and the frame is non-zero. Such an entry has NO live
        // AX element, so we cannot confirm it as MINIMIZED (which must stay listed) and it is off-screen
        // (`.optionAll` surfaced it; it is absent from the on-screen list). That makes it a closed/ghost
        // window, not a real taskbar window — otherwise its title-bearing tombstone sails past every drop
        // gate below and the button lingers for seconds. On-screen windows (real, even when AX is
        // unavailable) and AX-correlated minimized windows both keep a live signal and are unaffected.
        if axMap[cgID] == nil, (entry[kCGWindowIsOnscreen as String] as? Bool) != true {
            dbg("drop cg=\(cgID) '\(win.appName)' reason=offscreen-no-ax (closed/ghost)")
            return nil
        }

        // AX/CG can expose an empty title for one reconciliation tick while an existing
        // window is being renamed. Identity is the stable CGWindowID, not its mutable title:
        // retain the last accepted title while that same PID/window remains AX-correlated.
        // Without this, the no-title gate below emits CLOSED and then OPENED on the next
        // 500ms poll, making the taskbar button shrink and regrow instead of updating in place.
        let previousTitle: String? = stateLock.withLock {
            guard let previous = windowStore[cgID], previous.pid == win.pid else { return nil }
            return previous.title
        }
        win.title = titleForStableDiff(
            current: win.title,
            previous: previousTitle,
            hasAXWindow: axMap[cgID] != nil
        )

        win.isFocused = isTaskbarFocusedWindow(
            cgID: cgID,
            pid: pid,
            axMap: axMap,
            effectiveFrontmost: effectiveFrontmost
        )

        // Drop phantom windows: a layer-0 CGWindow with a zero-area frame is not a
        // real user window (system overlays, off-screen scaffolding) and would show
        // as an empty taskbar button. Minimized windows are kept regardless — they
        // must stay on the taskbar (bevel-m2.3) and may report no on-screen frame.
        if (win.frame.width <= 0 || win.frame.height <= 0) && !win.isMinimized {
            dbg("drop cg=\(cgID) '\(win.appName)' reason=zero-frame")
            return nil
        }

        // A real taskbar window has a title. Every genuine user window exposes one
        // (via AX or CGWindowList); the flood of title-less layer-0 windows — full-width
        // 30px strips, 1x1/64x64/500x500 placeholders with no AX — are not real windows.
        // Minimized windows are exempt so they stay listed (bevel-m2.3).
        if win.title.isEmpty && !win.isMinimized {
            dbg("drop cg=\(cgID) '\(win.appName)' reason=no-title hasAX=\(axMap[cgID] != nil)")
            return nil
        }

        // Bevel hosts both shell chrome and ordinary applications (File Manager/Explorer) in
        // the parent process. Excluding the whole PID hid every first-party app from its own
        // taskbar. Only reserved shell surfaces and transient Avalonia popups (tooltips, menus)
        // are dropped; File Manager / Explorer windows are legal taskbar windows.
        if isShellChrome(pid: pid, title: win.title) {
            dbg("drop cg=\(cgID) '\(win.appName)' reason=shell-chrome title='\(win.title)'")
            return nil
        }
        if isBevelTransient(
            pid: pid,
            cgID: cgID,
            axMap: axMap,
            isMinimized: win.isMinimized,
            frameWidth: Int(win.frame.width),
            frameHeight: Int(win.frame.height)
        ) {
            dbg("drop cg=\(cgID) '\(win.appName)' reason=bevel-transient title='\(win.title)'")
            return nil
        }

        dbg("keep cg=\(cgID) '\(win.appName)' title='\(win.title)' min=\(win.isMinimized) frame=\(win.frame.width)x\(win.frame.height) hasAX=\(axMap[cgID] != nil)")
        return win
    }

    /// Internal for focused unit coverage via `@testable import BevelHelper`.
    func isShellChrome(pid: pid_t, title: String) -> Bool {
        guard pid == parentPID else { return false }
        return title == "Bevel Desktop" || title == "Bevel Taskbar"
    }

    /// If `element` exposes a subrole that is NOT the standard document-window subrole, return it
    /// (for logging); otherwise nil. A missing or unreadable subrole counts as standard (nil) — the
    /// caller keeps the window. Centralizes the AXSubrole read shared by `describe()`,
    /// `isBevelTransient()`, and the AX-event fast-path so the three checks cannot drift.
    private func nonStandardSubrole(of element: AXUIElement) -> String? {
        var subroleVal: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXSubroleAttribute as CFString, &subroleVal) == .success,
              let subrole = subroleVal as? String,
              subrole != kAXStandardWindowSubrole else { return nil }
        return subrole
    }

    /// Avalonia tooltip/menu popups from the shell process are layer-0 but not standard
    /// document windows. Without this filter, hovering a taskbar button spawns a tooltip
    /// window that becomes a phantom taskbar button and reflows the strip.
    func isBevelTransient(
        pid: pid_t,
        cgID: CGWindowID,
        axMap: [CGWindowID: AXUIElement],
        isMinimized: Bool,
        frameWidth: Int = 0,
        frameHeight: Int = 0
    ) -> Bool {
        guard pid == parentPID, !isMinimized else { return false }
        // Tooltip popups are small; File Manager / Explorer windows are not.
        if frameWidth > 0, frameHeight > 0, frameWidth < 320, frameHeight < 160 {
            return true
        }
        guard let axWin = axMap[cgID] else {
            // No AX correlation: treat as transient only when the surface is tooltip-sized
            // or the frame is unknown (common for ephemeral Avalonia popups).
            return frameWidth == 0 || frameHeight == 0
                || (frameWidth < 320 && frameHeight < 160)
        }
        return nonStandardSubrole(of: axWin) != nil
    }

    /// Resolve the effective frontmost *foreign* app for the current snapshot: the frontmost app
    /// when it is a REAL Dock app (.regular activation policy), else the last such app so our own
    /// .accessory shell chrome (taskbar/desktop) becoming frontmost does not clear the projection.
    ///
    /// In the split shell the taskbar/desktop run as separate .accessory processes; keying off
    /// `parentPID` (the headless core) misclassified them as foreign, so every taskbar interaction
    /// reset the projection and the snapshot reported nothing focused (SNAPSHOT focus=[]),
    /// collapsing the pressed state. Activation policy is the split-safe signal (bevel-nji
    /// follow-up). Computed ONCE per snapshot — the NSRunningApplication policy lookup and the
    /// shared-state update must not run per window.
    private func effectiveForeignFrontmost(_ frontmostPID: pid_t?) -> pid_t? {
        let frontmostIsForeign: Bool = frontmostPID.map { fpid in
            NSRunningApplication(processIdentifier: fpid)?.activationPolicy == .regular
        } ?? false
        return stateLock.withLock {
            if frontmostIsForeign, let frontmostPID {
                lastForeignFrontmostPID = frontmostPID
            }
            return frontmostIsForeign ? frontmostPID : lastForeignFrontmostPID
        }
    }

    /// Pressed-state: true when `cgID` is the focused window of the snapshot's effective frontmost
    /// foreign app (resolved once via `effectiveForeignFrontmost`).
    private func isTaskbarFocusedWindow(
        cgID: CGWindowID,
        pid: pid_t,
        axMap: [CGWindowID: AXUIElement],
        effectiveFrontmost: pid_t?
    ) -> Bool {
        guard let effectiveFrontmost, pid == effectiveFrontmost else { return false }
        return isFocusedWindow(cgID: cgID, axMap: axMap, pid: pid)
    }

    /// Preserve presentation data across a transient observation gap without changing identity.
    /// Internal for focused unit coverage via `@testable import BevelHelper`.
    func titleForStableDiff(current: String, previous: String?, hasAXWindow: Bool) -> String {
        guard current.isEmpty,
              hasAXWindow,
              let previous,
              !previous.isEmpty
        else { return current }
        return previous
    }

    /// Enumerate all layer-0 windows (including minimized), each with a full
    /// AX-correlated descriptor. Backs ListWindows and the Changes snapshot.
    func enumerateWindows() -> [Bevel_Helper_V1_TaskbarWindow] {
        let entries = layer0Entries(options: .optionAll)
        let pidSet = Set(entries.compactMap { $0[kCGWindowOwnerPID as String] as? pid_t })
        let axMap = correlateAXElements(forPIDs: pidSet, entries: entries)
        let frontmostPID = NSWorkspace.shared.frontmostApplication?.processIdentifier
        let effectiveFrontmost = effectiveForeignFrontmost(frontmostPID)
        var windows = entries.compactMap { describe(entry: $0, axMap: axMap, effectiveFrontmost: effectiveFrontmost) }
        // Append the last-computed app-presence entries (bevel-ww71) so ListWindows + the Changes snapshot
        // carry windowless-running apps too. The 500ms reconcile keeps `appStore` current.
        windows.append(contentsOf: stateLock.withLock { Array(appStore.values) })
        return windows
    }

    // MARK: - App icon

    /// Return the app-icon PNG for a bundle id, rendering it once and caching the
    /// result. The slow disk render happens outside `iconCacheLock`.
    private func cachedAppIconPNG(bundleID: String) -> Data {
        if bundleID.isEmpty { return Data() }
        iconCacheLock.lock()
        if let cached = iconCache[bundleID] {
            iconCacheLock.unlock()
            return cached
        }
        iconCacheLock.unlock()

        let png = appIconPNG(bundleID: bundleID)
        iconCacheLock.lock()
        iconCache[bundleID] = png
        iconCacheLock.unlock()
        return png
    }

    /// Loads the owning app's icon and returns it as PNG bytes, or empty Data on failure.
    private func appIconPNG(bundleID: String) -> Data {
        guard let appPath = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID)?.path else {
            return Data()
        }
        let icon = NSWorkspace.shared.icon(forFile: appPath)

        // Render into a fixed 64x64 bitmap. NSImage.tiffRepresentation ignores the logical `size` and
        // emits the icon's LARGEST native representation (often 512x512 → hundreds of KB); with one icon
        // per window that pushes the aggregated ListWindows reply past gRPC's 4 MB limit → ResourceExhausted.
        // 64x64 is the sweet spot: the taskbar button is 16pt = 32px on a 2x display (48px on 3x), so 64
        // gives a crisp source with headroom (downscaled, never upscaled — 16px looked blocky on Retina),
        // yet each PNG is only ~4-8 KB and icons are cached per bundle id, so the aggregate stays tiny.
        let target = NSSize(width: 64, height: 64)
        guard let rep = NSBitmapImageRep(
            bitmapDataPlanes: nil,
            pixelsWide: 64, pixelsHigh: 64,
            bitsPerSample: 8, samplesPerPixel: 4,
            hasAlpha: true, isPlanar: false,
            colorSpaceName: .deviceRGB,
            bytesPerRow: 0, bitsPerPixel: 0
        ) else {
            return Data()
        }
        rep.size = target

        guard let ctx = NSGraphicsContext(bitmapImageRep: rep) else { return Data() }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = ctx
        icon.draw(in: NSRect(origin: .zero, size: target),
                  from: .zero, operation: .copy, fraction: 1.0)
        NSGraphicsContext.restoreGraphicsState()

        return rep.representation(using: .png, properties: [:]) ?? Data()
    }

    // MARK: - AXUIElement correlation

    /// Build a `[CGWindowID: AXUIElement]` map for the given PIDs using
    /// `_AXUIElementGetWindow`, with a frame-comparison fallback.
    ///
    /// The fallback is load-bearing: `_AXUIElementGetWindow` does NOT resolve a
    /// CGWindowID for every AX window. Finder's folder/browser windows are the known
    /// offender — the SPI returns a non-success/zero id for them, so without a fallback
    /// they never enter `axMap`. `describe()` then sees `hasAX=false`, cannot read the
    /// window's AXTitle (the folder name), finds `kCGWindowName` empty (needs Screen
    /// Recording), and drops the window at the no-title gate — no taskbar button ever
    /// appears (bevel-3rs). The fallback matches such an AX window to a same-PID CG
    /// window by frame so it lands in `axMap` keyed by its real CGWindowID, letting the
    /// existing AX-title fallback fill in the folder name. Restricting to the same PID
    /// (and rejecting zero-area AX frames) keeps this from mis-correlating phantom junk.
    ///
    /// `entries` are the CGWindowList records the caller already fetched — passed in so
    /// the fallback has the CGWindowID↔frame candidates without a second CG query.
    private func correlateAXElements(
        forPIDs pids: Set<pid_t>,
        entries: [[String: Any]]
    ) -> [CGWindowID: AXUIElement] {
        var map: [CGWindowID: AXUIElement] = [:]

        // Group CG candidates by owner PID for the frame-comparison fallback. Only same-PID
        // windows are compared so identical frames across different apps can't cross-correlate.
        var cgByPID: [pid_t: [(cgID: CGWindowID, frame: CGRect)]] = [:]
        for entry in entries {
            guard let cgID = entry[kCGWindowNumber as String] as? CGWindowID,
                  let pid = entry[kCGWindowOwnerPID as String] as? pid_t else { continue }
            let bounds = entry[kCGWindowBounds as String] as? NSDictionary
            let rect = CGRect(
                x: (bounds?["X"] as? CGFloat) ?? 0,
                y: (bounds?["Y"] as? CGFloat) ?? 0,
                width: (bounds?["Width"] as? CGFloat) ?? 0,
                height: (bounds?["Height"] as? CGFloat) ?? 0
            )
            cgByPID[pid, default: []].append((cgID: cgID, frame: rect))
        }

        for pid in pids {
            let axApp = AXUIElementCreateApplication(pid)
            _ = _AXUIElementSetMessagingTimeout(axApp, 1.0)

            var axWindows: CFTypeRef?
            let result = AXUIElementCopyAttributeValue(axApp, kAXWindowsAttribute as CFString, &axWindows)
            guard result == .success, let windowList = axWindows as? [AXUIElement] else {
                continue
            }

            for axWin in windowList {
                var cgId: CGWindowID = 0
                let mapResult = _AXUIElementGetWindow(axWin, &cgId)
                if mapResult == .success, cgId != 0 {
                    map[cgId] = axWin
                    continue
                }
                // SPI could not resolve the CGWindowID (observed for Finder folder windows).
                // Recover the correlation by frame so the window still gets an AX title.
                if let candidates = cgByPID[pid],
                   let axFrame = axWindowFrame(axWin),
                   let matched = frameMatchedCGID(
                       axFrame: axFrame,
                       candidates: candidates,
                       claimed: Set(map.keys)
                   ) {
                    map[matched] = axWin
                }
            }
        }

        return map
    }

    /// Read an AX window's on-screen frame (top-left origin, global screen coordinates —
    /// the SAME coordinate space as `kCGWindowBounds`, so the two are directly comparable).
    /// Returns nil if either attribute is missing or is not a well-formed `AXValue`.
    private func axWindowFrame(_ axWin: AXUIElement) -> CGRect? {
        var posVal: CFTypeRef?
        var sizeVal: CFTypeRef?
        guard AXUIElementCopyAttributeValue(axWin, kAXPositionAttribute as CFString, &posVal) == .success,
              AXUIElementCopyAttributeValue(axWin, kAXSizeAttribute as CFString, &sizeVal) == .success,
              let posVal, CFGetTypeID(posVal) == AXValueGetTypeID(),
              let sizeVal, CFGetTypeID(sizeVal) == AXValueGetTypeID()
        else { return nil }
        var pt = CGPoint.zero
        var sz = CGSize.zero
        AXValueGetValue(posVal as! AXValue, .cgPoint, &pt)
        AXValueGetValue(sizeVal as! AXValue, .cgSize, &sz)
        return CGRect(origin: pt, size: sz)
    }

    /// Frame-comparison correlation core. Given an AX window's frame and the same-PID CG
    /// candidate windows, return the CGWindowID of the first not-yet-claimed CG window whose
    /// frame matches within a small tolerance. A zero-area AX frame never matches: it cannot
    /// disambiguate and would otherwise latch onto the title-less phantom strips the no-title
    /// gate exists to reject — so junk stays out of `axMap`. Pure and deterministic; internal
    /// for focused unit coverage via `@testable import BevelHelper`.
    func frameMatchedCGID(
        axFrame: CGRect,
        candidates: [(cgID: CGWindowID, frame: CGRect)],
        claimed: Set<CGWindowID>
    ) -> CGWindowID? {
        guard axFrame.width > 0, axFrame.height > 0 else { return nil }
        let tolerance: CGFloat = 2.0
        for cand in candidates where !claimed.contains(cand.cgID) {
            if abs(cand.frame.origin.x - axFrame.origin.x) <= tolerance,
               abs(cand.frame.origin.y - axFrame.origin.y) <= tolerance,
               abs(cand.frame.width - axFrame.width) <= tolerance,
               abs(cand.frame.height - axFrame.height) <= tolerance {
                return cand.cgID
            }
        }
        return nil
    }

    /// Determine whether a CGWindowID is the focused window of its owning app.
    private func isFocusedWindow(cgID: CGWindowID, axMap: [CGWindowID: AXUIElement], pid: pid_t) -> Bool {
        // Query the app's focused window via AX — do not require axMap[cgID]; correlation
        // can fail for one tick while the window is still the real foreground surface.
        var focused: CFTypeRef?
        let axApp = AXUIElementCreateApplication(pid)
        // R18 convention: bound the AX round-trip — this runs per window per 500ms reconcile tick,
        // and without the cap a beachballing frontmost app stalls each query for the default AX
        // timeout (~6s), freezing the poll (review: correctness+adversarial, validated).
        _ = _AXUIElementSetMessagingTimeout(axApp, 1.0)
        let result = AXUIElementCopyAttributeValue(axApp, kAXFocusedWindowAttribute as CFString, &focused)
        guard result == .success, let focusedElem = focused else { return false }
        // A misbehaving app's AX server can return an unexpected CFType here; verify the
        // runtime type before casting so a bad value falls back to "not focused" instead
        // of crashing the whole helper. (`as?` on CF types is a compile error — the static
        // cast "always succeeds" — so guard on the CFTypeID, then the cast cannot fail.)
        guard CFGetTypeID(focusedElem) == AXUIElementGetTypeID() else { return false }
        let focusedAX = focusedElem as! AXUIElement

        // Element identity is authoritative when we have a correlated AX element for this window.
        // axMap already worked around `_AXUIElementGetWindow`'s unreliability (via the frame-match
        // fallback), so a SECOND SPI round-trip here — resolving the focused element back to a
        // CGWindowID — just reintroduces that flakiness and can mis-map focus to the wrong window
        // of a multi-window app. Compare the app's focused-window element to axMap[cgID] directly.
        if let axWin = axMap[cgID] {
            let match = CFEqual(axWin, focusedAX)
            if debugWindows {
                var fcg: CGWindowID = 0
                let ok = _AXUIElementGetWindow(focusedAX, &fcg) == .success
                dbg("focus? cg=\(cgID) identity=\(match) spiFocusedCG=\(ok ? String(fcg) : "n/a")")
            }
            return match
        }
        // No correlated AX element for this CGWindowID (the SPI could not map it at all) — fall
        // back to the focused-window SPI round-trip as a best effort.
        var focusedCGID: CGWindowID = 0
        if _AXUIElementGetWindow(focusedAX, &focusedCGID) == .success {
            return focusedCGID == cgID
        }
        return false
    }

    // MARK: - Window control actions

    /// Resolve the owning PID (always) and the window-level AXUIElement (best-effort) for a CGWindowID.
    /// Some apps (notably Apple Music) expose no correlated AX window element, so callers that only
    /// need to bring the app forward must not hard-fail on a missing window element.
    private func resolveWindow(windowID: String) throws -> (pid: pid_t, axWin: AXUIElement?) {
        guard let cgID = CGWindowID(windowID) else {
            throw RPCError(code: .invalidArgument, message: "Invalid window_id: \(windowID)")
        }
        // `.optionAll` (not just on-screen) so a MINIMIZED window can still be resolved — otherwise
        // Restore/Activate on a minimized window would fail with "not found" (bevel-m2.3).
        guard let entry = layer0Entries(options: .optionAll).first(where: {
            ($0[kCGWindowNumber as String] as? CGWindowID) == cgID
        }), let pid = entry[kCGWindowOwnerPID as String] as? pid_t else {
            throw RPCError(code: .notFound, message: "Window \(windowID) not found")
        }
        let axMap = correlateAXElements(forPIDs: [pid], entries: [entry])
        return (pid, axMap[cgID])
    }

    /// The window-level AX element, required by callers that act ON the window itself (minimize,
    /// restore, close). Throws when it can't be correlated — those actions have no app-level fallback.
    private func axWindow(for windowID: String) throws -> AXUIElement {
        guard let axWin = try resolveWindow(windowID: windowID).axWin else {
            throw RPCError(code: .notFound, message: "AXUIElement for window \(windowID) not available")
        }
        return axWin
    }

    /// Distinguish `.invalidUIElement` from `.cannotComplete` — the former
    /// means the window no longer exists, the latter is a transient failure.
    func axErrorToRPC(_ error: AXError, windowID: String) -> RPCError {
        switch error {
        case .invalidUIElement:
            return RPCError(code: .notFound, message: "Window \(windowID) no longer exists")
        case .cannotComplete:
            return RPCError(code: .unavailable, message: "Request for window \(windowID) cannot be completed right now")
        default:
            return RPCError(code: .internalError, message: "AX error \(error.rawValue) for window \(windowID)")
        }
    }

    func activateWindow(windowID: String) throws {
        // App-presence entry (bevel-ww71): windowID is "app:<bundle>", there's no window to resolve.
        // Activating the app IS the reopen — macOS fires applicationShouldHandleReopen, which makes a
        // window (the app then flows in as a normal window button and the presence entry drops).
        if windowID.hasPrefix("app:") {
            let bundle = String(windowID.dropFirst("app:".count))
            let app = NSWorkspace.shared.runningApplications.first {
                $0.bundleIdentifier == bundle && $0.activationPolicy == .regular
            }
            guard let app else {
                throw RPCError(code: .notFound, message: "app-presence target not running: \(bundle)")
            }
            let appElement = AXUIElementCreateApplication(app.processIdentifier)
            _ = _AXUIElementSetMessagingTimeout(appElement, 1.0)   // R18: never block on a hung target
            AXUIElementSetAttributeValue(appElement, kAXFrontmostAttribute as CFString, kCFBooleanTrue)
            app.activate()
            return
        }

        let (pid, axWin) = try resolveWindow(windowID: windowID)

        // AXRaise reorders the window WITHIN its own app. Best-effort: some apps (Apple Music) expose
        // no correlated window element, so skip the raise there rather than failing the whole
        // activation — the app-frontmost step below still brings the app (and its window) forward.
        if let axWin {
            _ = AXUIElementPerformAction(axWin, kAXRaiseAction as CFString)
        }

        // Make the owning app frontmost. This is what actually brings a window up when Bevel's taskbar
        // sits at a high window level (holding key focus after the click) — and it is the ONLY step
        // available for apps without a window AX element. kAXFrontmostAttribute is the
        // accessibility-native activation and works for most apps.
        let appElement = AXUIElementCreateApplication(pid)
        _ = _AXUIElementSetMessagingTimeout(appElement, 1.0)   // R18: never block on a hung target
        AXUIElementSetAttributeValue(appElement, kAXFrontmostAttribute as CFString, kCFBooleanTrue)

        // Belt-and-suspenders for apps whose app-level AX is ALSO restricted, so kAXFrontmostAttribute
        // silently no-ops (Apple Music is the canonical case — no window AX element AND no app-AX
        // activation). NSRunningApplication.activate is not accessibility-dependent, so it brings such
        // apps forward when the AX path can't. Harmless for the apps AX already handled.
        NSRunningApplication(processIdentifier: pid)?.activate()
    }

    /// Quit (or force-quit) every running instance of an app by bundle id (bevel-ww71). Graceful
    /// `terminate()` posts the standard quit (apps may prompt to save); `forceTerminate()` is the
    /// SIGKILL-equivalent "Force Quit". Returns true if at least one instance was asked to quit.
    func terminateApp(bundleID: String, force: Bool) -> Bool {
        let apps = NSWorkspace.shared.runningApplications.filter { $0.bundleIdentifier == bundleID }
        var any = false
        for app in apps {
            let quit = force ? app.forceTerminate() : app.terminate()
            any = any || quit
        }
        return any
    }

    func minimizeWindow(windowID: String) throws {
        let axWin = try axWindow(for: windowID)
        let result = AXUIElementSetAttributeValue(axWin, kAXMinimizedAttribute as CFString, true as CFTypeRef)
        guard result == .success else {
            throw axErrorToRPC(result, windowID: windowID)
        }
    }

    func restoreWindow(windowID: String) throws {
        let axWin = try axWindow(for: windowID)
        let result = AXUIElementSetAttributeValue(axWin, kAXMinimizedAttribute as CFString, false as CFTypeRef)
        guard result == .success else {
            throw axErrorToRPC(result, windowID: windowID)
        }
    }

    func closeWindow(windowID: String) throws {
        let axWin = try axWindow(for: windowID)
        // Find the close button and press it (kAXCloseAction is not a standard constant).
        var closeButton: CFTypeRef?
        let attrResult = AXUIElementCopyAttributeValue(
            axWin, kAXCloseButtonAttribute as CFString, &closeButton)
        // Verify the CFTypeID before casting — a hostile AX server could return a
        // non-AXUIElement CFType, and a force cast would crash the whole helper.
        guard attrResult == .success, let button = closeButton,
              CFGetTypeID(button) == AXUIElementGetTypeID() else {
            throw RPCError(code: .internalError,
                           message: "Close button not found for window \(windowID)")
        }
        let result = AXUIElementPerformAction(button as! AXUIElement, kAXPressAction as CFString)
        guard result == .success else {
            throw axErrorToRPC(result, windowID: windowID)
        }
    }

    /// Reposition/resize a window to an absolute target rect (device pixels, AppKit
    /// bottom-left origin) via AXPosition + AXSize.
    func repositionWindow(windowID: String, target: Bevel_Helper_V1_PixelRect) throws {
        let axWin = try axWindow(for: windowID)

        var point = CGPoint(x: CGFloat(target.x), y: CGFloat(target.y))
        guard let posValue = AXValueCreate(.cgPoint, &point) else {
            throw RPCError(code: .internalError,
                           message: "Failed to create AX position value for \(windowID)")
        }
        let posResult = AXUIElementSetAttributeValue(
            axWin, kAXPositionAttribute as CFString, posValue)
        guard posResult == .success else {
            throw axErrorToRPC(posResult, windowID: windowID)
        }

        var size = CGSize(width: CGFloat(target.width), height: CGFloat(target.height))
        guard let sizeValue = AXValueCreate(.cgSize, &size) else {
            throw RPCError(code: .internalError,
                           message: "Failed to create AX size value for \(windowID)")
        }
        let sizeResult = AXUIElementSetAttributeValue(
            axWin, kAXSizeAttribute as CFString, sizeValue)
        guard sizeResult == .success else {
            throw axErrorToRPC(sizeResult, windowID: windowID)
        }
    }

    // MARK: - Change stream subscribers

    private func addSubscriber(_ writer: RPCWriter<Bevel_Helper_V1_WindowChange>) -> UUID {
        let id = UUID()
        stateLock.lock()
        subscribers[id] = writer
        stateLock.unlock()
        return id
    }

    private func removeSubscriber(_ id: UUID) {
        stateLock.lock()
        subscribers.removeValue(forKey: id)
        stateLock.unlock()
    }

    private func finishAllSubscribers() {
        stateLock.lock()
        subscribers.removeAll()
        stateLock.unlock()
    }

    /// Broadcast a `WindowChange` to all active Changes subscribers.
    /// Copies the subscriber set under the lock, then serializes every write on
    /// `writeQueue` (gRPC writers are not concurrent-safe). Each async write is
    /// bridged back to the serial queue via a semaphore so the next write does not
    /// start until the current one finishes — the actual serialization the previous
    /// fire-and-forget `Task` never provided. `.async` (not `.sync`) is used so a
    /// caller already running on `writeQueue` could never deadlock.
    private func broadcast(_ change: Bevel_Helper_V1_WindowChange) {
        stateLock.lock()
        let active = subscribers
        stateLock.unlock()
        guard !active.isEmpty else { return }
        for (id, writer) in active {
            writeQueue.async { [weak self] in
                let done = DispatchSemaphore(value: 0)
                // The write is async; run it on the cooperative pool and block this
                // serial-queue slot until it completes so writes stay strictly ordered.
                // A write throws once the client is gone — drop its dead writer so the
                // subscriber set does not grow without bound.
                Task { [writer] in
                    do {
                        try await writer.write(change)
                    } catch {
                        self?.removeSubscriber(id)
                    }
                    done.signal()
                }
                // BOUNDED wait: a subscriber that stalls without throwing (suspended client,
                // exhausted HTTP/2 flow control) would otherwise park this serial-queue slot
                // forever and wedge every future broadcast for every subscriber (review:
                // reliability+adversarial, validated). A client that cannot drain one write in
                // 5s is evicted like a dead one — the C# side resubscribes on stream loss.
                if done.wait(timeout: .now() + 5) == .timedOut {
                    self?.removeSubscriber(id)
                }
            }
        }
    }

    // MARK: - AXObserver management

    /// Enqueue an AX event into the change stream. Called from the global C callback.
    func enqueueAXEvent(observer: AXObserver, element: AXUIElement, notification: CFString) {
        let notifStr = notification as String
        guard let kind = axNotificationToChangeKind(notifStr) else { return }

        // Resolve the CGWindowID. App-level notifications (focus changed) arrive on the
        // application AX element, not the focused window — map through kAXFocusedWindow.
        var cgID: CGWindowID = 0
        var elementForBuild = element
        if _AXUIElementGetWindow(element, &cgID) != .success || cgID == 0 {
            if notifStr == kAXFocusedWindowChangedNotification as String {
                var focused: CFTypeRef?
                if AXUIElementCopyAttributeValue(element, kAXFocusedWindowAttribute as CFString, &focused) == .success,
                   let focusedElem = focused,
                   CFGetTypeID(focusedElem) == AXUIElementGetTypeID() {
                    elementForBuild = focusedElem as! AXUIElement
                    _ = _AXUIElementGetWindow(elementForBuild, &cgID)
                }
            }
            if cgID == 0 { return }
        }

        // Apply the SAME app-eligibility gate the snapshot path (`describe`) uses: only apps with a
        // regular activation policy own taskbar windows. The AX-observer stream otherwise broadcasts
        // an `.opened`/change for EVERY window an observed app creates — including the tooltip/menu
        // popups our OWN `.accessory` shell chrome (taskbar, desktop) spawns on hover. Those are a
        // flood of empty-title 'Avalonia Application' windows that `describe` correctly drops
        // (activationPolicy != .regular), so without this guard the two paths disagree: the event
        // stream registers each popup as a phantom taskbar button (shifting the bar and dismissing
        // the very tooltip) until the 2s snapshot reconcile prunes it (bevel-nji follow-up).
        var eventPID: pid_t = 0
        if AXUIElementGetPid(elementForBuild, &eventPID) == .success,
           let app = NSRunningApplication(processIdentifier: eventPID),
           app.activationPolicy != .regular {
            return
        }

        // Build a TaskbarWindow for this CGWindowID from the current store.
        stateLock.lock()
        let win = windowStore[cgID]
        stateLock.unlock()

        var change = Bevel_Helper_V1_WindowChange()
        change.kind = kind

        if let win {
            var updated = win
            // Refresh mutable fields for the notification type.
            switch kind {
            case .minimized:
                updated.isMinimized = true
            case .deminimized:
                updated.isMinimized = false
            case .titleChanged:
                // Try to read the current title from AX.
                var title: CFTypeRef?
                if AXUIElementCopyAttributeValue(elementForBuild, kAXTitleAttribute as CFString, &title) == .success,
                   let titleStr = title as? String {
                    updated.title = titleStr
                }
            case .focused:
                updated.isFocused = true
            case .moved:
                break
            default:
                break
            }
            change.window = updated
        } else {
            // Window not in store — try to build a descriptor from element.
            if var fresh = buildTaskbarWindowFromAX(element: elementForBuild, cgID: cgID) {
                if kind == .focused { fresh.isFocused = true }
                change.window = fresh
            }
        }

        broadcast(change)
    }

    /// Broadcast a `.focused` change for the currently-focused window of `pid`, driven by the
    /// NSWorkspace app-activation hook. Mirrors the `.focused` handling in `enqueueAXEvent`: prefer
    /// the enumerated `windowStore` descriptor (already fully filtered), else best-effort build one
    /// through the eligibility-gated `buildTaskbarWindowFromAX`. Only real Dock apps own taskbar
    /// focus — our own `.accessory` shell chrome activating (taskbar/desktop) must not reset it.
    private func broadcastFocusForApp(pid: pid_t) {
        guard let app = NSRunningApplication(processIdentifier: pid),
              app.activationPolicy == .regular else { return }

        let axApp = AXUIElementCreateApplication(pid)
        // Bound the AX round-trip: a hung/slow app must not stall the shared AX run-loop thread.
        _ = _AXUIElementSetMessagingTimeout(axApp, 1.0)

        var focused: CFTypeRef?
        guard AXUIElementCopyAttributeValue(axApp, kAXFocusedWindowAttribute as CFString, &focused) == .success,
              let focusedElem = focused,
              CFGetTypeID(focusedElem) == AXUIElementGetTypeID() else { return }
        let focusedAX = focusedElem as! AXUIElement

        var cgID: CGWindowID = 0
        guard _AXUIElementGetWindow(focusedAX, &cgID) == .success, cgID != 0 else { return }

        guard let change = focusedWindowChange(cgID: cgID, element: focusedAX) else { return }
        dbg("activate-focus pid=\(pid) cg=\(cgID) '\(change.window.appName)' title='\(change.window.title)'")
        broadcast(change)
    }

    /// Build a `.focused` WindowChange for `cgID`, preferring the enumerated `windowStore`
    /// descriptor (already fully filtered) and falling back to an eligibility-gated
    /// `buildTaskbarWindowFromAX`. Shared by the app-activation hook and any caller that has
    /// already resolved the focused window's `(cgID, element)`. Returns nil when the window is
    /// ineligible — nothing to broadcast.
    private func focusedWindowChange(cgID: CGWindowID, element: AXUIElement) -> Bevel_Helper_V1_WindowChange? {
        stateLock.lock()
        let stored = windowStore[cgID]
        stateLock.unlock()

        var change = Bevel_Helper_V1_WindowChange()
        change.kind = .focused
        if let stored {
            var updated = stored
            updated.isFocused = true
            change.window = updated
        } else if var fresh = buildTaskbarWindowFromAX(element: element, cgID: cgID) {
            fresh.isFocused = true
            change.window = fresh
        } else {
            return nil
        }
        return change
    }

    /// Build a minimal TaskbarWindow from an AXUIElement + CGWindowID.
    private func buildTaskbarWindowFromAX(element: AXUIElement, cgID: CGWindowID) -> Bevel_Helper_V1_TaskbarWindow? {
        var win = Bevel_Helper_V1_TaskbarWindow()
        win.windowID = String(cgID)

        var pid: pid_t = 0
        guard AXUIElementGetPid(element, &pid) == .success else { return nil }
        win.pid = Int32(pid)

        if let app = NSRunningApplication(processIdentifier: pid) {
            win.appName = app.localizedName ?? ""
            win.appBundleID = app.bundleIdentifier ?? ""
        }

        var title: CFTypeRef?
        if AXUIElementCopyAttributeValue(element, kAXTitleAttribute as CFString, &title) == .success,
           let titleStr = title as? String {
            win.title = titleStr
        }

        // Apply the snapshot path's (`describe`) eligibility gates so this AX-event fast-path can't
        // emit a window `describe()` would reject. Firefox fires kAXFocusedWindowChangedNotification
        // for an empty-title, non-standard Gecko utility window; without these gates it becomes a
        // phantom *focused* taskbar button that steals the pressed state from the real window it
        // shadows (the multi-window pressed-state bug). A brand-new real window still passes here
        // and the fast-path shows it instantly; the 500ms snapshot reconciles everything else.
        if let subrole = nonStandardSubrole(of: element) {
            dbg("event-drop cg=\(cgID) '\(win.appName)' reason=subrole=\(subrole)")
            return nil
        }
        if win.title.isEmpty {
            dbg("event-drop cg=\(cgID) '\(win.appName)' reason=no-title")
            return nil
        }
        // Mirror describe()'s shell-chrome gate too (review: maintainability, validated — this
        // path lacked it, the third recurrence of the two-paths-drift bug class): without it a
        // shell chrome window can flash as a phantom button until the snapshot prunes it.
        if isShellChrome(pid: pid, title: win.title) {
            dbg("event-drop cg=\(cgID) '\(win.appName)' reason=shell-chrome")
            return nil
        }

        var position: CFTypeRef?
        var size: CFTypeRef?
        var rect = Bevel_Helper_V1_PixelRect()
        if AXUIElementCopyAttributeValue(element, kAXPositionAttribute as CFString, &position) == .success,
           AXUIElementCopyAttributeValue(element, kAXSizeAttribute as CFString, &size) == .success {
            // Guard on the CFTypeID before casting — a misbehaving AX server could hand
            // back a non-AXValue CFType, which a force cast would crash on.
            if let posVal = position, CFGetTypeID(posVal) == AXValueGetTypeID() {
                var pt = CGPoint.zero
                AXValueGetValue(posVal as! AXValue, .cgPoint, &pt)
                rect.x = clampToInt32(pt.x)
                rect.y = clampToInt32(pt.y)
            }
            if let sizeVal = size, CFGetTypeID(sizeVal) == AXValueGetTypeID() {
                var sz = CGSize.zero
                AXValueGetValue(sizeVal as! AXValue, .cgSize, &sz)
                rect.width = clampToInt32(sz.width)
                rect.height = clampToInt32(sz.height)
            }
        }
        win.frame = rect

        // Mirror describe()'s transient gate (same review finding), now that the frame is known:
        // the shell's own tooltip/menu popups must never become taskbar buttons via the fast path.
        if isBevelTransient(pid: pid, cgID: cgID, axMap: [cgID: element], isMinimized: false,
                            frameWidth: Int(rect.width), frameHeight: Int(rect.height)) {
            dbg("event-drop cg=\(cgID) '\(win.appName)' reason=bevel-transient")
            return nil
        }

        return win
    }

    /// Dedicated CFRunLoop thread that pumps the AXObservers and the app-launch hook (see
    /// `axRunLoop`). Blocks until the run loop reference is published, so `init` can start the
    /// poll — which registers observers — knowing the run loop exists.
    private func startAXRunLoopThread() {
        let thread = Thread { [weak self] in
            guard let self else { return }
            self.axRunLoop = CFRunLoopGetCurrent()

            // App-launch hook: register a launching app's observer BEFORE it draws its first
            // window, so kAXWindowCreatedNotification fires instantly instead of waiting for the
            // next poll tick. Delivered on this thread's run loop.
            self.launchObserverToken = NSWorkspace.shared.notificationCenter.addObserver(
                forName: NSWorkspace.didLaunchApplicationNotification,
                object: nil, queue: nil
            ) { [weak self] note in
                guard let self,
                      let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication
                else { return }
                self.addObserver(for: app.processIdentifier)
            }

            // App-activation hook: the frontmost app changed. AX's per-app window notifications do
            // NOT fire on an app-to-app switch (the new app's internal focused window is unchanged),
            // so without this the taskbar could only catch external switches on the 2s snapshot poll
            // — clicking a taskbar button updated instantly, but Cmd-Tab / Dock / clicking a window
            // felt dead. Broadcast the newly-frontmost app's focused window immediately.
            self.activateObserverToken = NSWorkspace.shared.notificationCenter.addObserver(
                forName: NSWorkspace.didActivateApplicationNotification,
                object: nil, queue: nil
            ) { [weak self] note in
                guard let self,
                      let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication
                else { return }
                self.broadcastFocusForApp(pid: app.processIdentifier)
            }

            self.axRunLoopReady.signal()

            // A no-op port keeps the run loop from returning immediately when it has no sources.
            RunLoop.current.add(NSMachPort(), forMode: .common)
            CFRunLoopRun()
        }
        thread.name = "bevel.ax.runloop"
        thread.start()
        axRunLoopReady.wait()
    }

    /// Register per-app AXObservers for all currently visible window owners, and drop observers
    /// for owners that are gone. Called from the reconciliation poll.
    private func ensureObservers(for pids: Set<pid_t>) {
        let ownPID = getpid()
        let currentPIDs: Set<pid_t> = stateLock.withLock { Set(axObservers.keys) }

        for pid in pids.subtracting(currentPIDs).subtracting([ownPID]) {
            addObserver(for: pid)
        }

        // Remove observers for PIDs that are no longer visible.
        guard let runLoop = axRunLoop else { return }
        for pid in currentPIDs.subtracting(pids) {
            if let observer = stateLock.withLock({ axObservers.removeValue(forKey: pid) }) {
                CFRunLoopRemoveSource(runLoop, AXObserverGetRunLoopSource(observer), .defaultMode)
            }
        }
    }

    /// Register an AXObserver for a single app (idempotent), adding its run-loop source to the AX
    /// run loop so its notifications actually fire. Safe to call from the poll thread AND the
    /// launch hook — `observerLock` serializes the check-create-insert.
    private func addObserver(for pid: pid_t) {
        guard pid != getpid(), let runLoop = axRunLoop else { return }
        // Never (re)register an observer after shutdown — the launch hook or an
        // in-flight poll could otherwise resurrect observers we just tore down.
        if (stateLock.withLock { isShutdown }) { return }

        observerLock.lock()
        defer { observerLock.unlock() }
        if (stateLock.withLock { axObservers[pid] }) != nil { return }

        var observer: AXObserver?
        let refcon = Unmanaged.passUnretained(self).toOpaque()
        guard AXObserverCreate(pid, axObserverCallback, &observer) == .success, let observer else { return }

        let appElement = AXUIElementCreateApplication(pid)
        for notif in [
            kAXFocusedWindowChangedNotification,
            kAXWindowCreatedNotification,
            kAXUIElementDestroyedNotification,
        ] {
            AXObserverAddNotification(observer, appElement, notif as CFString, refcon)
        }

        // Register per-window notifications on existing windows.
        var windows: CFTypeRef?
        if AXUIElementCopyAttributeValue(appElement, kAXWindowsAttribute as CFString, &windows) == .success,
           let windowList = windows as? [AXUIElement] {
            for axWin in windowList {
                for notif in [
                    kAXTitleChangedNotification,
                    kAXWindowMiniaturizedNotification,
                    kAXWindowDeminiaturizedNotification,
                    kAXMovedNotification,
                    kAXResizedNotification,
                ] {
                    AXObserverAddNotification(observer, axWin, notif as CFString, refcon)
                }
            }
        }

        CFRunLoopAddSource(runLoop, AXObserverGetRunLoopSource(observer), .defaultMode)
        CFRunLoopWakeUp(runLoop)
        stateLock.withLock { axObservers[pid] = observer }
    }

    private func removeAllObservers() {
        let all: [pid_t: AXObserver] = stateLock.withLock {
            let copy = axObservers
            axObservers.removeAll()
            return copy
        }
        guard let runLoop = axRunLoop, !all.isEmpty else { return }
        // Snapshot the sources into a plain array so the teardown closure captures
        // only locals (never self — that would resurrect a deallocating instance).
        let sources = all.values.map { AXObserverGetRunLoopSource($0) }
        // Remove the run-loop sources ON the AX run-loop thread. AXObserver callbacks
        // fire on that same thread, so performing removal there guarantees no callback
        // is executing when the source disappears. Combined with dropping our strong
        // refs to the observers above (ARC then invalidates them), this closes the race
        // where a callback could fire with a now-dangling `passUnretained` refcon while
        // the instance is being torn down in shutdown()/deinit.
        performOnAXRunLoopSync {
            for source in sources {
                CFRunLoopRemoveSource(runLoop, source, .defaultMode)
            }
        }
    }

    /// Run `work` synchronously on the AX run-loop thread. AXObserver callbacks execute
    /// on that thread, so work scheduled here can never overlap an in-flight callback.
    /// When already on that thread (teardown triggered from within a callback), run inline
    /// to avoid waiting on ourselves. Callers must ensure the run loop is still running
    /// (i.e. before CFRunLoopStop) or the block would never execute.
    private func performOnAXRunLoopSync(_ work: @escaping () -> Void) {
        guard let runLoop = axRunLoop else { work(); return }
        if CFEqual(CFRunLoopGetCurrent(), runLoop) {
            work()
            return
        }
        let done = DispatchSemaphore(value: 0)
        CFRunLoopPerformBlock(runLoop, CFRunLoopMode.commonModes.rawValue) {
            work()
            done.signal()
        }
        CFRunLoopWakeUp(runLoop)
        done.wait()
    }

    // MARK: - Reconciliation poll (LOAD-BEARING)

    /// Start the mandatory reconciliation poll. AX notifications are unreliable
    /// (especially for windows that are closed without going through AX), so this
    /// poll is the authoritative backstop. Runs at ≥1s interval.
    private func startReconciliationPoll() {
        pollTask = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { break }
                self.reconcile()
                // 500ms backstop. The AX run loop now delivers window-created notifications
                // near-instantly for observed apps, so this only covers the gap before an app's
                // observer is registered and AX events macOS drops (halved from 1s — bevel latency).
                try? await Task.sleep(nanoseconds: 500_000_000) // 500ms
            }
        }
    }

    /// Full reconciliation: enumerate windows, diff against the store, emit deltas.
    /// Windowless-running regular apps (bevel-ww71): every `.regular` (Dock) app that is running but does
    /// NOT own a kept window. Same activation-policy gate as `describe()` (so `.accessory` menu-bar agents,
    /// already surfaced via the systray mirror, aren't double-represented), minus Bevel's own processes.
    /// Keyed by bundle id — stable across the app's window lifecycle. `windowPIDs` is the set of PIDs that
    /// own a real kept window this poll.
    private func computeAppPresence(windowPIDs: Set<pid_t>) -> [String: Bevel_Helper_V1_TaskbarWindow] {
        let ownPID = getpid()
        var result: [String: Bevel_Helper_V1_TaskbarWindow] = [:]
        for app in NSWorkspace.shared.runningApplications {
            guard app.activationPolicy == .regular else { continue }
            let pid = app.processIdentifier
            if pid == ownPID || pid == parentPID { continue }
            if windowPIDs.contains(pid) { continue }              // owns a window → shown as a window button
            guard let bundle = app.bundleIdentifier, !bundle.isEmpty else { continue }
            var win = Bevel_Helper_V1_TaskbarWindow()
            win.windowID = "app:" + bundle
            win.appBundleID = bundle
            win.appName = app.localizedName ?? bundle
            win.pid = pid
            win.isAppPresence = true
            win.appIconPng = cachedAppIconPNG(bundleID: bundle)
            result[bundle] = win
        }
        return result
    }

    private func reconcile() {
        let ownPID = getpid()

        // `.optionAll` so minimized windows stay in the set — otherwise they would
        // be diffed as CLOSED and dropped from the taskbar every poll (bevel-m2.3).
        let entries = layer0Entries(options: .optionAll)

        // Ensure AXObservers are registered for all window owners.
        let currentPIDs = Set(entries.compactMap { $0[kCGWindowOwnerPID as String] as? pid_t })
        ensureObservers(for: currentPIDs)

        // Build fresh descriptors via the SAME builder ListWindows uses, so the poll
        // path carries icons + minimized state (bevel-m2.1 / bevel-m2.3).
        let axMap = correlateAXElements(forPIDs: currentPIDs.subtracting([ownPID]), entries: entries)
        let frontmostPID = NSWorkspace.shared.frontmostApplication?.processIdentifier
        let effectiveFrontmost = effectiveForeignFrontmost(frontmostPID)

        var newStore: [CGWindowID: Bevel_Helper_V1_TaskbarWindow] = [:]
        for entry in entries {
            guard let cgID = entry[kCGWindowNumber as String] as? CGWindowID,
                  let win = describe(entry: entry, axMap: axMap, effectiveFrontmost: effectiveFrontmost)
            else { continue }
            newStore[cgID] = win
        }

        // Diff against the store using only the windows we actually keep — describe()
        // drops phantom zero-frame windows, so opened/closed reflect the taskbar's view.
        let currentSet = Set(newStore.keys)
        let previousSet: Set<CGWindowID> = stateLock.withLock { Set(windowStore.keys) }
        let opened = currentSet.subtracting(previousSet)
        let closed = previousSet.subtracting(currentSet)

        // Emit OPENED events.
        for cgID in opened {
            guard let win = newStore[cgID] else { continue }
            var change = Bevel_Helper_V1_WindowChange()
            change.kind = .opened
            change.window = win
            broadcast(change)
        }

        // Emit CLOSED events.
        for cgID in closed {
            let win = stateLock.withLock { windowStore[cgID] }
            var change = Bevel_Helper_V1_WindowChange()
            change.kind = .closed
            if let win {
                change.window = win
            } else {
                var stub = Bevel_Helper_V1_TaskbarWindow()
                stub.windowID = String(cgID)
                change.window = stub
            }
            broadcast(change)
        }

        // Emit MOVED and TITLE_CHANGED for windows whose frame or title changed.
        for (cgID, newWin) in newStore {
            guard let oldWin = stateLock.withLock({ windowStore[cgID] }) else { continue }

            let frameChanged = oldWin.frame.x != newWin.frame.x
                || oldWin.frame.y != newWin.frame.y
                || oldWin.frame.width != newWin.frame.width
                || oldWin.frame.height != newWin.frame.height

            let titleChanged = oldWin.title != newWin.title
            let focusChanged = oldWin.isFocused != newWin.isFocused
            let minimizedChanged = oldWin.isMinimized != newWin.isMinimized

            if frameChanged {
                var change = Bevel_Helper_V1_WindowChange()
                change.kind = .moved
                change.window = newWin
                broadcast(change)
            }

            if titleChanged {
                var change = Bevel_Helper_V1_WindowChange()
                change.kind = .titleChanged
                change.window = newWin
                broadcast(change)
            }

            if focusChanged {
                var change = Bevel_Helper_V1_WindowChange()
                change.kind = .focused
                change.window = newWin
                broadcast(change)
            }

            // Minimize/restore backstop: the AXObserver may miss the miniaturize
            // notification, so the poll re-derives it from AX and emits the delta
            // itself (bevel-m2.3). The window stays listed either way.
            if minimizedChanged {
                var change = Bevel_Helper_V1_WindowChange()
                change.kind = newWin.isMinimized ? .minimized : .deminimized
                change.window = newWin
                broadcast(change)
            }
        }

        // Update the store atomically.
        stateLock.withLock { windowStore = newStore }

        // App-presence (bevel-ww71): windowless-running apps = running .regular apps minus those owning a
        // kept window this poll. Diffed + emitted as OPENED/CLOSED synthetic windows, exactly like windows,
        // so the merge transition is just: app opens a window → its PID enters windowPIDs → the presence
        // entry drops (CLOSED) while the real window arrives (OPENED); the C# projector suppresses the
        // presence entry for that bundle in the overlap frame so there's never a visible duplicate.
        let windowPIDs = Set(newStore.values.map { $0.pid })
        let newApps = computeAppPresence(windowPIDs: windowPIDs)
        let prevAppKeys: Set<String> = stateLock.withLock { Set(appStore.keys) }
        let curAppKeys = Set(newApps.keys)

        for bundle in curAppKeys.subtracting(prevAppKeys) {
            guard let app = newApps[bundle] else { continue }
            var change = Bevel_Helper_V1_WindowChange()
            change.kind = .opened
            change.window = app
            broadcast(change)
        }
        for bundle in prevAppKeys.subtracting(curAppKeys) {
            let app = stateLock.withLock { appStore[bundle] }
            var change = Bevel_Helper_V1_WindowChange()
            change.kind = .closed
            if let app {
                change.window = app
            } else {
                var stub = Bevel_Helper_V1_TaskbarWindow()
                stub.windowID = "app:" + bundle
                change.window = stub
            }
            broadcast(change)
        }
        stateLock.withLock { appStore = newApps }
    }
}