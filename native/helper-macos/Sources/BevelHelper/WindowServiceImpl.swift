import AppKit
import ApplicationServices
import Foundation
import GRPCCore
import GRPCProtobuf

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

private func axNotificationToChangeKind(_ notification: String) -> Bevel_Helper_V1_WindowChange.Kind? {
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

    // MARK: - State

    /// Current window snapshot keyed by CGWindowID, updated by reconciliation poll.
    private let stateLock = NSLock()
    private var windowStore: [CGWindowID: Bevel_Helper_V1_TaskbarWindow] = [:]

    /// App-icon PNG cache keyed by bundle id. The reconciliation poll now builds
    /// full descriptors (icons included) every ~1s, so re-rendering each app's icon
    /// from disk on every tick would be wasteful — cache the PNG once per bundle id.
    /// Guarded by its own lock so the slow render never blocks `stateLock`.
    private let iconCacheLock = NSLock()
    private var iconCache: [String: Data] = [:]

    /// Per-pid AXObservers.
    private var axObservers: [pid_t: AXObserver] = [:]

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
        removeAllObservers()
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
                    // Hold the writer open: writer.write throws when the client
                    // disconnects, which ends this producer.
                    while true {
                        try await Task.sleep(nanoseconds: 1_000_000_000)
                    }
                    // Unreachable, but satisfies the return type.
                    self.removeSubscriber(subID)
                    return [:]
                }
            }
        )
    }

    // MARK: - Window enumeration

    /// Fetch layer-0 CGWindowList entries (excluding our own process and desktop
    /// elements). `.optionAll` includes minimized/off-screen windows, which is
    /// required so minimized windows stay listed (bevel-m2.3).
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
                && owner != parentPID   // exclude the shell's own windows
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
        frontmostPID: pid_t?
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
            rect.x = Int32((bounds["X"] as? CGFloat) ?? 0)
            rect.y = Int32((bounds["Y"] as? CGFloat) ?? 0)
            rect.width = Int32((bounds["Width"] as? CGFloat) ?? 0)
            rect.height = Int32((bounds["Height"] as? CGFloat) ?? 0)
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
            var subroleVal: CFTypeRef?
            if !win.isMinimized,
               AXUIElementCopyAttributeValue(axWin, kAXSubroleAttribute as CFString, &subroleVal) == .success,
               let subrole = subroleVal as? String,
               subrole != kAXStandardWindowSubrole {
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

        win.isFocused = (pid == frontmostPID) && isFocusedWindow(cgID: cgID, axMap: axMap, pid: pid)

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

        dbg("keep cg=\(cgID) '\(win.appName)' title='\(win.title)' min=\(win.isMinimized) frame=\(win.frame.width)x\(win.frame.height) hasAX=\(axMap[cgID] != nil)")
        return win
    }

    /// Enumerate all layer-0 windows (including minimized), each with a full
    /// AX-correlated descriptor. Backs ListWindows and the Changes snapshot.
    func enumerateWindows() -> [Bevel_Helper_V1_TaskbarWindow] {
        let entries = layer0Entries(options: .optionAll)
        let pidSet = Set(entries.compactMap { $0[kCGWindowOwnerPID as String] as? pid_t })
        let axMap = correlateAXElements(forPIDs: pidSet)
        let frontmostPID = NSWorkspace.shared.frontmostApplication?.processIdentifier
        return entries.compactMap { describe(entry: $0, axMap: axMap, frontmostPID: frontmostPID) }
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

        // Render into a real 16x16 bitmap. NSImage.tiffRepresentation ignores the
        // logical `size` and emits the icon's LARGEST native representation (often
        // 512x512 → hundreds of KB). With one icon per window, that pushes the
        // aggregated ListWindows reply past gRPC's 4 MB limit and every enumeration
        // fails with ResourceExhausted. Drawing into a fixed 16x16 bitmap keeps each
        // PNG ~1 KB — taskbar buttons never need more.
        let target = NSSize(width: 16, height: 16)
        guard let rep = NSBitmapImageRep(
            bitmapDataPlanes: nil,
            pixelsWide: 16, pixelsHigh: 16,
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
    private func correlateAXElements(forPIDs pids: Set<pid_t>) -> [CGWindowID: AXUIElement] {
        var map: [CGWindowID: AXUIElement] = [:]

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
                }
            }
        }

        return map
    }

    /// Determine whether a CGWindowID is the focused window of its owning app.
    private func isFocusedWindow(cgID: CGWindowID, axMap: [CGWindowID: AXUIElement], pid: pid_t) -> Bool {
        // Try the AXUIElement approach first.
        if axMap[cgID] != nil {
            var focused: CFTypeRef?
            let axApp = AXUIElementCreateApplication(pid)
            let result = AXUIElementCopyAttributeValue(axApp, kAXFocusedWindowAttribute as CFString, &focused)
            if result == .success, let focusedElem = focused {
                // Compare CFEqual
                let focusedAX = focusedElem as! AXUIElement
                var focusedCGID: CGWindowID = 0
                if _AXUIElementGetWindow(focusedAX, &focusedCGID) == .success {
                    return focusedCGID == cgID
                }
            }
        }
        return false
    }

    // MARK: - Window control actions

    private func axWindow(for windowID: String) throws -> AXUIElement {
        guard let cgID = CGWindowID(windowID) else {
            throw RPCError(code: .invalidArgument, message: "Invalid window_id: \(windowID)")
        }

        // Re-enumerate AX elements for the PID associated with this window.
        // `.optionAll` (not just on-screen) so a MINIMIZED window can still be
        // resolved — otherwise Restore/Activate on a minimized window would fail
        // with "not found" (bevel-m2.3).
        guard let entry = layer0Entries(options: .optionAll).first(where: {
            ($0[kCGWindowNumber as String] as? CGWindowID) == cgID
        }), let pid = entry[kCGWindowOwnerPID as String] as? pid_t else {
            throw RPCError(code: .notFound, message: "Window \(windowID) not found")
        }

        let axMap = correlateAXElements(forPIDs: [pid])
        guard let axWin = axMap[cgID] else {
            throw RPCError(code: .notFound, message: "AXUIElement for window \(windowID) not available")
        }
        return axWin
    }

    /// Distinguish `.invalidUIElement` from `.cannotComplete` — the former
    /// means the window no longer exists, the latter is a transient failure.
    private func axErrorToRPC(_ error: AXError, windowID: String) -> RPCError {
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
        let axWin = try axWindow(for: windowID)
        let result = AXUIElementPerformAction(axWin, kAXRaiseAction as CFString)
        guard result == .success else {
            throw axErrorToRPC(result, windowID: windowID)
        }

        // AXRaise only reorders the window *within* its own app. A taskbar-button click must
        // also make the owning app frontmost — otherwise, with Bevel's taskbar at a high window
        // level (holding key focus after the click), the window comes forward but its app never
        // becomes active, so it "doesn't always come up". Setting kAXFrontmostAttribute is the
        // accessibility-native way to activate the app (works from this permitted helper, and
        // isn't deprecated like NSRunningApplication.activate(options:)). Best-effort: the raise
        // already succeeded, so a frontmost failure isn't fatal.
        var pid: pid_t = 0
        if AXUIElementGetPid(axWin, &pid) == .success {
            let appElement = AXUIElementCreateApplication(pid)
            AXUIElementSetAttributeValue(appElement, kAXFrontmostAttribute as CFString, kCFBooleanTrue)
        }
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
        guard attrResult == .success, let button = closeButton else {
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
    /// Synchronous: copies the subscriber set under the lock, then dispatches
    /// each write onto `writeQueue` (gRPC writers are not concurrent-safe).
    private func broadcast(_ change: Bevel_Helper_V1_WindowChange) {
        stateLock.lock()
        let active = subscribers
        stateLock.unlock()
        guard !active.isEmpty else { return }
        for (_, writer) in active {
            writeQueue.async {
                _ = Task { [writer] in
                    _ = try? await writer.write(change)
                }
            }
        }
    }

    // MARK: - AXObserver management

    /// Enqueue an AX event into the change stream. Called from the global C callback.
    func enqueueAXEvent(observer: AXObserver, element: AXUIElement, notification: CFString) {
        let notifStr = notification as String
        guard let kind = axNotificationToChangeKind(notifStr) else { return }

        // Extract the CGWindowID from the element.
        var cgID: CGWindowID = 0
        let result = _AXUIElementGetWindow(element, &cgID)
        if result != .success || cgID == 0 {
            // For app-level notifications (created/destroyed/focused), try to
            // get the focused window of the app.
            // For element-level notifications, we expect a valid CGWindowID.
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
                if AXUIElementCopyAttributeValue(element, kAXTitleAttribute as CFString, &title) == .success,
                   let titleStr = title as? String {
                    updated.title = titleStr
                }
            case .moved, .focused:
                // Frame/focus will be refreshed by the reconciliation poll.
                break
            default:
                break
            }
            change.window = updated
        } else {
            // Window not in store — try to build a descriptor from element.
            if let fresh = buildTaskbarWindowFromAX(element: element, cgID: cgID) {
                change.window = fresh
            }
        }

        broadcast(change)
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

        var position: CFTypeRef?
        var size: CFTypeRef?
        var rect = Bevel_Helper_V1_PixelRect()
        if AXUIElementCopyAttributeValue(element, kAXPositionAttribute as CFString, &position) == .success,
           AXUIElementCopyAttributeValue(element, kAXSizeAttribute as CFString, &size) == .success {
            if let posVal = position {
                var pt = CGPoint.zero
                AXValueGetValue(posVal as! AXValue, .cgPoint, &pt)
                rect.x = Int32(pt.x)
                rect.y = Int32(pt.y)
            }
            if let sizeVal = size {
                var sz = CGSize.zero
                AXValueGetValue(sizeVal as! AXValue, .cgSize, &sz)
                rect.width = Int32(sz.width)
                rect.height = Int32(sz.height)
            }
        }
        win.frame = rect

        return win
    }

    /// Register per-app AXObservers for all currently visible window owners.
    private func ensureObservers(for pids: Set<pid_t>) {
        let ownPID = getpid()
        let currentPIDs: Set<pid_t> = stateLock.withLock { Set(axObservers.keys) }

        // Add observers for new PIDs.
        let newPIDs = pids.subtracting(currentPIDs).subtracting([ownPID])
        for pid in newPIDs {
            var observer: AXObserver?
            let refcon = Unmanaged.passUnretained(self).toOpaque()
            let result = AXObserverCreate(pid, axObserverCallback, &observer)
            guard result == .success, let observer else { continue }

            // Register notifications.
            let appElement = AXUIElementCreateApplication(pid)
            let appNotifications: [String] = [
                kAXFocusedWindowChangedNotification,
                kAXWindowCreatedNotification,
                kAXUIElementDestroyedNotification,
            ]
            for notif in appNotifications {
                AXObserverAddNotification(observer, appElement, notif as CFString, refcon)
            }

            // Register per-window notifications on existing windows.
            var windows: CFTypeRef?
            if AXUIElementCopyAttributeValue(appElement, kAXWindowsAttribute as CFString, &windows) == .success,
               let windowList = windows as? [AXUIElement] {
                for axWin in windowList {
                    let windowNotifications: [String] = [
                        kAXTitleChangedNotification,
                        kAXWindowMiniaturizedNotification,
                        kAXWindowDeminiaturizedNotification,
                        kAXMovedNotification,
                        kAXResizedNotification,
                    ]
                    for notif in windowNotifications {
                        AXObserverAddNotification(observer, axWin, notif as CFString, refcon)
                    }
                }
            }

            CFRunLoopAddSource(
                CFRunLoopGetCurrent(),
                AXObserverGetRunLoopSource(observer),
                .defaultMode
            )

            stateLock.withLock { axObservers[pid] = observer }
        }

        // Remove observers for PIDs that are no longer visible.
        let stalePIDs = currentPIDs.subtracting(pids)
        for pid in stalePIDs {
            if let observer = stateLock.withLock({ axObservers.removeValue(forKey: pid) }) {
                CFRunLoopRemoveSource(
                    CFRunLoopGetCurrent(),
                    AXObserverGetRunLoopSource(observer),
                    .defaultMode
                )
            }
        }
    }

    private func removeAllObservers() {
        let all: [pid_t: AXObserver] = stateLock.withLock {
            let copy = axObservers
            axObservers.removeAll()
            return copy
        }
        for (_, observer) in all {
            CFRunLoopRemoveSource(
                CFRunLoopGetCurrent(),
                AXObserverGetRunLoopSource(observer),
                .defaultMode
            )
        }
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
                try? await Task.sleep(nanoseconds: 1_000_000_000) // 1s
            }
        }
    }

    /// Full reconciliation: enumerate windows, diff against the store, emit deltas.
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
        let axMap = correlateAXElements(forPIDs: currentPIDs.subtracting([ownPID]))
        let frontmostPID = NSWorkspace.shared.frontmostApplication?.processIdentifier

        var newStore: [CGWindowID: Bevel_Helper_V1_TaskbarWindow] = [:]
        for entry in entries {
            guard let cgID = entry[kCGWindowNumber as String] as? CGWindowID,
                  let win = describe(entry: entry, axMap: axMap, frontmostPID: frontmostPID)
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
    }
}