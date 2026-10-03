import AppKit
import ApplicationServices
import CoreGraphics
import Foundation
import GRPCCore
import GRPCProtobuf
import ScreenCaptureKit

private extension CGEventField {
    /// The private CGEventField carrying the window ID an event targets (bevel-6fin, from Ice: 0x33).
    static let windowID = CGEventField(rawValue: 0x33)!   // swiftlint:disable:this force_unwrapping
}

/// Menu-bar status-item mirroring — the "Ice technique" (docs/spec/02-macos-platform.md §5).
///
/// M3-A (this file) implements **discovery + limited mode (§5.5)**: it enumerates the menu-bar
/// status-item windows via `CGWindowListCopyWindowInfo` (status-item layer, menu-bar Y-band; Req
/// 5.1), keys each by `ownerPID:windowNumber` (Req 5.2), correlates it to its `NSRunningApplication`
/// for name + icon, and returns them in left-to-right menu-bar order. Icons are the owning app's
/// icon (non-live) — no Screen Recording is required. Live ScreenCaptureKit capture (§5.3) and
/// click-forwarding (§5.5) arrive in M3-B / M3-C.
///
/// macOS 26 (Tahoe) note: `CGWindowListCopyWindowInfo` reports many status items as owned by
/// Control Center rather than their real app (FB18327911), so owner attribution can be coarse here;
/// the self-test + attribution fallback live in M3-E.
final class TrayServiceImpl: RegistrableRPCService, @unchecked Sendable {
    let expectedKey: String
    /// The shell process that launched us; its own menu-bar presence (if any) is excluded.
    let parentPID: pid_t

    /// The status-item window level. Menu-bar extras sit at `kCGStatusWindowLevel` (25); the main
    /// menu bar itself is one below (24). We filter to this layer within the menu-bar Y-band.
    private let statusWindowLayer = 25

    /// Case-insensitive substrings; a status item whose identity (kCGWindowName / owner) matches any
    /// is NOT mirrored (bevel-m3.4). Defaults cover iStat Menus (live graphs — belong in the native
    /// bar), Control Center's own menu chrome (BentoBox), Ice's control separators, and modules Bevel
    /// already provides (its clock / Siri). Extend at launch via BEVEL_TRAY_DENY (comma-separated).
    private let denyList: [String] = {   // eager (not lazy) — read concurrently from ListTrayItems + the Changes poll (review: swift-ios)
        var list = ["istatmenus", "bentobox", "ice.controlitem", "clock", "siri"]
        if let extra = ProcessInfo.processInfo.environment["BEVEL_TRAY_DENY"] {
            list += extra.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces).lowercased() }
                        .filter { !$0.isEmpty }
        }
        return list
    }()

    /// True when a status item should be hidden from Bevel's tray per the denylist.
    func isDenied(windowName: String, ownerName: String) -> Bool {
        let hay = (windowName + " " + ownerName).lowercased()
        return denyList.contains { hay.contains($0) }
    }

    /// App-icon PNG cache keyed by bundle id — the icon render is off the hot path (limited mode).
    private let iconCacheLock = NSLock()
    private var iconCache: [String: Data] = [:]

    private let debug = ProcessInfo.processInfo.environment["BEVEL_DEBUG_TRAY"] == "1"
    private func dbg(_ msg: @autoclosure () -> String) {
        guard debug else { return }
        FileHandle.standardError.write(Data(("[BEVEL-TRAY] " + msg() + "\n").utf8))
    }

    /// Always logged (not just under debug) — this is the offline systray diagnostic (§5.10).
    private func log(_ msg: String) {
        FileHandle.standardError.write(Data(("[BEVEL-TRAY] " + msg + "\n").utf8))
    }

    /// TEMP (bevel-6fin): always-on file trace for the reveal path (stderr isn't captured in this run).
    private func rlog(_ msg: String) {
        let line = msg + "\n"
        if let h = FileHandle(forWritingAtPath: "/tmp/bevel-reveal.log") {
            h.seekToEndOfFile(); h.write(Data(line.utf8)); try? h.close()
        } else {
            try? line.write(toFile: "/tmp/bevel-reveal.log", atomically: true, encoding: .utf8)
        }
    }

    /// Hard feature flag: BEVEL_TRAY_DISABLE=1 turns the systray subsystem off entirely (empty tray),
    /// leaving the rest of the shell untouched (Req 10.1). The escape hatch when a build breaks it.
    private let subsystemDisabled = ProcessInfo.processInfo.environment["BEVEL_TRAY_DISABLE"] == "1"

    // Self-test gate (§5.10): live pixel capture is enabled only once a launch-time self-test proves
    // the pipeline works on the current OS build; otherwise the tray degrades to limited mode (§5.5)
    // rather than showing black/wrong frames. Guarded because enumerateWithCapture runs concurrently.
    private let selfTestLock = NSLock()
    private var selfTestDone = false
    private var liveMirroringEnabled = true

    init(expectedKey: String, parentPID: pid_t = 0) {
        self.expectedKey = expectedKey
        self.parentPID = parentPID
    }

    // MARK: - RPC registration

    /// The menu-bar control item (Strategy A), set once at startup by main(). Reached from the
    /// SetConsolidation handler (via a main-actor hop) and the enumerator's self-exclusion (U3/U5).
    nonisolated(unsafe) var controlItem: MenuBarControlItem?

    /// Click forwarding (bevel-6fin). When `park` is set, the caller (app) has just collapsed its control
    /// item so THIS item is momentarily on-screen; the helper session-tap Cmd-drags it to a parked slot
    /// right of the control (so it survives the caller's re-hide) and presses it → the owning app's menu
    /// opens at the top. Non-park is a plain forward to the item's current on-screen position.
    func forwardClickWithReveal(
        itemID: String, button: Bevel_Helper_V1_ForwardClickRequest.Button, modifiers: UInt32,
        park: Bool) async -> Bool {
        rlog("forwardClickWithReveal park=\(park) item=\(itemID)")
        if park { return revealParkAndClick(itemID: itemID, button: button, modifiers: modifiers) }
        return forwardClick(itemID: itemID, button: button, modifiers: modifiers)
    }

    /// Single-item reveal via Ice's SELF-ADDRESSED-event move (bevel-6fin; studied from jordanbaird/Ice).
    /// The item is hidden OFF-SCREEN and we do NOT reveal the bar. We craft mouse events addressed to the
    /// item's window + owning process (not to a screen point), un-suppress synthetic events during remote-
    /// drag, and post them to the SESSION tap — so the move lands regardless of cursor position and survives
    /// remote desktop (unlike a cursor-warp HID-tap drag). Move this one item to a slot left of the Clock
    /// (right of the expanded control → it stays visible), then AX-press it so its menu opens at the top.
    /// Restore-to-origin is the next step (first cut leaves the clicked item parked).
    private func revealParkAndClick(
        itemID: String, button: Bevel_Helper_V1_ForwardClickRequest.Button, modifiers: UInt32) -> Bool {
        let parts = itemID.split(separator: ":")
        guard parts.count == 2, let pid = pid_t(parts[0]), let win = Int(parts[1]) else { rlog("  bad itemID"); return false }
        guard let (clockRect, clockWin) = clockWindow() else { rlog("  no clock anchor"); return false }
        rlog("  clock win=\(clockWin) x=\(Int(clockRect.origin.x)); moving item win=\(win) pid=\(pid)")
        let dropPoint = CGPoint(x: clockRect.origin.x - 1, y: clockRect.midY)   // just LEFT of the clock
        let moved = moveItemTargeted(windowID: win, pid: pid, to: dropPoint, targetWindowID: clockWin)
        usleep(150_000)   // let the bar reflow the moved item on-screen
        guard let (rect, _) = onScreenRect(itemID: itemID) else {
            rlog("  item \(win) NOT on-screen after move (moved=\(moved))"); return false
        }
        let point = CGPoint(x: rect.midX, y: rect.midY)
        let ok = pressViaAX(at: point, itemID: itemID, rightClick: button == .right)
            || clickViaCGEvent(at: point, itemID: itemID, button: button, modifiers: modifiers)
        rlog("  item \(win) parked at x=\(Int(rect.origin.x)); press ok=\(ok); scheduling restore")
        scheduleRestore(windowID: win, pid: pid)
        return ok
    }

    /// Auto-restore (bevel-6fin): after time to use the menu, move the revealed item back into the hidden
    /// region so the tray returns to its consolidated state. Ice re-hides on a timer too.
    private func scheduleRestore(windowID: Int, pid: pid_t) {
        Task { [weak self] in
            try? await Task.sleep(nanoseconds: 4_000_000_000)
            self?.restoreItem(windowID: windowID, pid: pid)
        }
    }

    /// Moves a parked item back to the LEFT of our BevelTrayControl — which, expanded, keeps everything to
    /// its left off-screen, so the item hides again. The same self-addressed move, in reverse.
    private func restoreItem(windowID: Int, pid: pid_t) {
        guard let (ctrlRect, ctrlWin) = controlWindow() else { rlog("  restore: no control anchor"); return }
        let dropPoint = CGPoint(x: ctrlRect.origin.x - 1, y: ctrlRect.midY)
        _ = moveItemTargeted(windowID: windowID, pid: pid, to: dropPoint, targetWindowID: ctrlWin)
        rlog("  restored item \(windowID) to left of control")
    }

    /// Our BevelTrayControl status item's frame + window number (by window name), even off-screen.
    private func controlWindow() -> (CGRect, Int)? {
        guard let wins = CGWindowListCopyWindowInfo([.excludeDesktopElements], kCGNullWindowID)
                as? [[String: Any]] else { return nil }
        for w in wins where (w[kCGWindowName as String] as? String) == "BevelTrayControl" {
            if let num = w[kCGWindowNumber as String] as? Int,
               let bd = w[kCGWindowBounds as String] as? [String: Any],
               let r = CGRect(dictionaryRepresentation: bd as CFDictionary) { return (r, num) }
        }
        return nil
    }

    /// The item's current on-screen bounds + window number, matched by the windowNumber half of item_id.
    private func onScreenRect(itemID: String) -> (CGRect, Int)? {
        let parts = itemID.split(separator: ":")
        guard parts.count == 2, let num = Int(parts[1]),
              let wins = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID)
                as? [[String: Any]] else { return nil }
        for w in wins where (w[kCGWindowNumber as String] as? Int) == num {
            if let bd = w[kCGWindowBounds as String] as? [String: Any],
               let r = CGRect(dictionaryRepresentation: bd as CFDictionary), r.origin.y <= 40 { return (r, num) }
        }
        return nil
    }

    /// The menu-bar Clock's frame + window number — the park anchor (rightmost always-visible system item).
    private func clockWindow() -> (CGRect, Int)? {
        guard let wins = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID)
                as? [[String: Any]] else { return nil }
        for w in wins where (w[kCGWindowName as String] as? String) == "Clock" {
            if let num = w[kCGWindowNumber as String] as? Int,
               let bd = w[kCGWindowBounds as String] as? [String: Any],
               let r = CGRect(dictionaryRepresentation: bd as CFDictionary), r.origin.y <= 40 { return (r, num) }
        }
        return nil
    }

    /// Ice's move (bevel-6fin): reposition a menu-bar item by posting mouse events SELF-ADDRESSED to its
    /// window + process, not to a screen location — so it works on an off-screen item and through remote
    /// desktop. Cmd-down "grabs" the item (location irrelevant → 20000,20000); the up is addressed to the
    /// TARGET window at the drop point, dropping the item there. Session tap + remote-drag un-suppression.
    private func moveItemTargeted(windowID: Int, pid: pid_t, to dropPoint: CGPoint, targetWindowID: Int) -> Bool {
        guard let source = CGEventSource(stateID: .hidSystemState) else { return false }
        permitRemoteEvents()
        let grabAnywhere = CGPoint(x: 20_000, y: 20_000)
        guard let down = itemEvent(.leftMouseDown, at: grabAnywhere, windowID: windowID, pid: pid, source: source, cmd: true, click: false),
              let up = itemEvent(.leftMouseUp, at: dropPoint, windowID: targetWindowID, pid: pid, source: source, cmd: false, click: false)
        else { return false }
        // Deliver directly to the owning process (Ice's ultimate delivery is postToPid). Post to the
        // session tap too, since Ice's relay routes through it. Longer holds let the drag-rearrange settle.
        down.post(tap: .cgSessionEventTap)
        down.postToPid(pid)
        usleep(90_000)
        up.post(tap: .cgSessionEventTap)
        up.postToPid(pid)
        usleep(90_000)
        return true
    }

    /// Builds a mouse CGEvent addressed to a specific menu-bar item window + process (Ice's menuBarItemEvent):
    /// the window/process fields make macOS route it to that item regardless of cursor position and let it
    /// ride the session tap instead of the HID stream remote desktop owns. `cmd` = Cmd-held (the move grab);
    /// `click` sets clickState so a down/up opens the item's menu.
    private func itemEvent(_ mouseType: CGEventType, at location: CGPoint, windowID: Int, pid: pid_t,
                           source: CGEventSource, cmd: Bool, click: Bool) -> CGEvent? {
        guard let e = CGEvent(mouseEventSource: source, mouseType: mouseType, mouseCursorPosition: location, mouseButton: .left)
        else { return nil }
        e.flags = cmd ? .maskCommand : []
        e.setIntegerValueField(.eventTargetUnixProcessID, value: Int64(pid))
        e.setIntegerValueField(.eventSourceUserData, value: Int64(truncatingIfNeeded: ObjectIdentifier(e).hashValue))
        e.setIntegerValueField(.mouseEventWindowUnderMousePointer, value: Int64(windowID))
        e.setIntegerValueField(.mouseEventWindowUnderMousePointerThatCanHandleThisEvent, value: Int64(windowID))
        e.setIntegerValueField(.windowID, value: Int64(windowID))
        if click { e.setIntegerValueField(.mouseEventClickState, value: 1) }
        return e
    }

    /// Un-suppress synthetic events during remote-mouse-drag / suppression-interval states (Ice's
    /// permitAllEvents) — the other half of surviving remote desktop.
    private func permitRemoteEvents() {
        guard let src = CGEventSource(stateID: .combinedSessionState) else { return }
        let permitAll: CGEventFilterMask = [.permitLocalMouseEvents, .permitLocalKeyboardEvents, .permitSystemDefinedEvents]
        src.setLocalEventsFilterDuringSuppressionState(permitAll, state: .eventSuppressionStateRemoteMouseDrag)
        src.setLocalEventsFilterDuringSuppressionState(permitAll, state: .eventSuppressionStateSuppressionInterval)
    }

    func registerMethods<Transport: ServerTransport>(with router: inout RPCRouter<Transport>) {
        let serviceName = "bevel.helper.v1.TrayService"

        // ── ListTrayItems ────────────────────────────────────────────────
        router.registerHandler(
            forMethod: MethodDescriptor(fullyQualifiedService: serviceName, method: "ListTrayItems"),
            deserializer: ProtobufDeserializer<Bevel_Helper_V1_ListTrayItemsRequest>(),
            serializer: ProtobufSerializer<Bevel_Helper_V1_ListTrayItemsReply>(),
            handler: { [weak self] request, context in
                guard let self else { throw RPCError(code: .internalError, message: "TrayService deallocated") }
                try AuthInterceptor.authenticate(
                    request.metadata, expectedKey: self.expectedKey, expectedCapability: "tray")
                _ = try await ServerRequest(stream: request)
                var reply = Bevel_Helper_V1_ListTrayItemsReply()
                reply.items = await self.enumerateWithCapture()
                return StreamingServerResponse(single: ServerResponse(message: reply))
            }
        )

        // ── Changes (server-streaming) ───────────────────────────────────
        // Emits a SNAPSHOT burst, then re-enumerates on a throttled poll and streams
        // ADDED/REMOVED/UPDATED deltas until the client disconnects. Each subscriber diffs against
        // its own last-sent set — there's normally one subscriber (the taskbar), so no shared state.
        router.registerHandler(
            forMethod: MethodDescriptor(fullyQualifiedService: serviceName, method: "Changes"),
            deserializer: ProtobufDeserializer<Bevel_Helper_V1_TrayChangesRequest>(),
            serializer: ProtobufSerializer<Bevel_Helper_V1_TrayChange>(),
            handler: { [weak self] request, context in
                guard let self else { throw RPCError(code: .internalError, message: "TrayService deallocated") }
                try AuthInterceptor.authenticate(
                    request.metadata, expectedKey: self.expectedKey, expectedCapability: "tray")
                _ = try await ServerRequest(stream: request)

                return StreamingServerResponse(of: Bevel_Helper_V1_TrayChange.self) { [weak self] writer in
                    guard let self else { return [:] }

                    // 1. Full snapshot (with live capture when Screen Recording is granted).
                    var lastByID: [String: Bevel_Helper_V1_TrayItem] = [:]
                    for item in await self.enumerateWithCapture() {
                        var change = Bevel_Helper_V1_TrayChange()
                        change.kind = .snapshot
                        change.item = item
                        try await writer.write(change)
                        lastByID[item.itemID] = item
                    }

                    // 2. Poll + diff until cancelled (client disconnect cancels the producer Task).
                    // Adaptive cadence (U4): refresh faster while consolidated (the tray IS the menu bar
                    // then, so liveness matters most), slower in plain mirror mode to save CPU.
                    while !Task.isCancelled {
                        let interval: UInt64 = (self.controlItem?.isHidingItems ?? false) ? 900_000_000 : 2_000_000_000
                        try await Task.sleep(nanoseconds: interval)
                        let current = await self.enumerateWithCapture()
                        var currentByID: [String: Bevel_Helper_V1_TrayItem] = [:]
                        for item in current { currentByID[item.itemID] = item }

                        // Added / updated. The signature excludes icon bytes so a live re-capture
                        // (whose pixels can differ each frame) doesn't spam UPDATE for every item.
                        for item in current {
                            if let prev = lastByID[item.itemID] {
                                // Update on identity change OR icon-content change (U4). The legacy-CG
                                // capture (U1) reads the backing store deterministically, so a static
                                // icon yields identical bytes and only a real change (battery %, spinner)
                                // differs — live icons without the per-pixel-jitter spam that led
                                // signature() to exclude bytes. If a capture path ever reintroduces
                                // jitter, quantize/debounce here (see freshness open question).
                                if self.signature(prev) != self.signature(item) || prev.iconPng != item.iconPng {
                                    try await writer.write(self.change(.updated, item))
                                }
                            } else {
                                try await writer.write(self.change(.added, item))
                            }
                        }
                        // Removed.
                        for (id, item) in lastByID where currentByID[id] == nil {
                            try await writer.write(self.change(.removed, item))
                        }
                        lastByID = currentByID
                    }
                    return [:]
                }
            }
        )

        // ── ForwardClick ─────────────────────────────────────────────────
        router.registerHandler(
            forMethod: MethodDescriptor(fullyQualifiedService: serviceName, method: "ForwardClick"),
            deserializer: ProtobufDeserializer<Bevel_Helper_V1_ForwardClickRequest>(),
            serializer: ProtobufSerializer<Bevel_Helper_V1_ForwardClickReply>(),
            handler: { [weak self] request, context in
                guard let self else { throw RPCError(code: .internalError, message: "TrayService deallocated") }
                try AuthInterceptor.authenticate(
                    request.metadata, expectedKey: self.expectedKey, expectedCapability: "tray")
                let req = try await ServerRequest(stream: request)
                var reply = Bevel_Helper_V1_ForwardClickReply()
                reply.delivered = await self.forwardClickWithReveal(
                    itemID: req.message.itemID, button: req.message.button, modifiers: req.message.modifiers,
                    park: req.message.park)
                return StreamingServerResponse(single: ServerResponse(message: reply))
            }
        )

        // ── SetConsolidation ─────────────────────────────────────────────
        // Hide (enabled) or reveal (disabled) the real status items via the control item (Strategy A).
        router.registerHandler(
            forMethod: MethodDescriptor(fullyQualifiedService: serviceName, method: "SetConsolidation"),
            deserializer: ProtobufDeserializer<Bevel_Helper_V1_SetConsolidationRequest>(),
            serializer: ProtobufSerializer<Bevel_Helper_V1_SetConsolidationReply>(),
            handler: { [weak self] request, context in
                guard let self else { throw RPCError(code: .internalError, message: "TrayService deallocated") }
                try AuthInterceptor.authenticate(
                    request.metadata, expectedKey: self.expectedKey, expectedCapability: "tray")
                let req = try await ServerRequest(stream: request)
                let enabled = req.message.enabled
                consolidationLog("SetConsolidation RPC received enabled=\(enabled) controlItem=\(self.controlItem != nil)")
                // Non-blocking: set the flag; the control item's main-run-loop timer applies it. Never
                // hop to the main thread from here (that deadlocks in the status-bar IPC). Reply at once.
                self.controlItem?.requestHidden(enabled)
                var reply = Bevel_Helper_V1_SetConsolidationReply()
                reply.applied = true
                return StreamingServerResponse(single: ServerResponse(message: reply))
            }
        )
    }

    private func change(_ kind: Bevel_Helper_V1_TrayChange.Kind, _ item: Bevel_Helper_V1_TrayItem)
        -> Bevel_Helper_V1_TrayChange {
        var c = Bevel_Helper_V1_TrayChange()
        c.kind = kind
        c.item = item
        return c
    }

    // MARK: - Click forwarding (§5.5)

    /// Forwards a click to the real status item so it reveals its menu/popover. Re-reads the item's
    /// live on-screen centre, then tries AX-press FIRST (Req 5.8) — it drives the element directly and,
    /// crucially, works for the Control-Center-hosted items on macOS 26 that ignore synthetic mouse
    /// clicks. Falls back to CGEvent synthesis for older OS / non-CC items. Needs Accessibility.
    func forwardClick(itemID: String, button: Bevel_Helper_V1_ForwardClickRequest.Button, modifiers: UInt32) -> Bool {
        let parts = itemID.split(separator: ":")
        guard parts.count == 2, let expectedPID = Int(parts[0]), let windowNumber = Int(parts[1]) else { return false }
        // Validate the resolved window is STILL the intended status item before synthesizing input: match
        // the windowNumber AND the owning pid AND the status-item layer + menu-bar band. macOS reuses
        // CGWindowNumbers after a window is destroyed, so a stale/reused id must not drive a click into an
        // unrelated on-screen window (review: security least-privilege + adversarial id-reuse).
        guard let windows = CGWindowListCopyWindowInfo(
            [.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]],
              let w = windows.first(where: {
                  ($0[kCGWindowNumber as String] as? Int) == windowNumber
                      && ($0[kCGWindowOwnerPID as String] as? Int) == expectedPID
                      && ($0[kCGWindowLayer as String] as? Int) == statusWindowLayer
              }),
              let boundsDict = w[kCGWindowBounds as String] as? [String: Any],
              let rect = CGRect(dictionaryRepresentation: boundsDict as CFDictionary),
              rect.origin.y <= 40, rect.height >= 8, rect.height <= 40, rect.width >= 8, rect.width <= 400 else {
            return false
        }
        let point = CGPoint(x: rect.midX, y: rect.midY)

        // 1. AX-press (preferred, and the only thing that works for CC-hosted items on macOS 26).
        if pressViaAX(at: point, itemID: itemID, rightClick: button == .right) { return true }

        // 2. Fallback: synthesize a real mouse click at the item's coordinates.
        return clickViaCGEvent(at: point, itemID: itemID, button: button, modifiers: modifiers)
    }

    /// Finds the accessibility element at the item's screen point and performs its press/show-menu
    /// action — reveals the real menu without faking the mouse (no cursor teleport). Walks up a couple
    /// of parents if the deepest element under the point doesn't itself support the action.
    private func pressViaAX(at point: CGPoint, itemID: String, rightClick: Bool) -> Bool {
        let systemWide = AXUIElementCreateSystemWide()
        // Cap AX messaging at 1s (the convention WindowServiceImpl uses) so an unresponsive target app
        // can't hang this synchronous call on the shared concurrency pool (review: swift-ios).
        _ = _AXUIElementSetMessagingTimeout(systemWide, 1.0)
        var element: AXUIElement?
        guard AXUIElementCopyElementAtPosition(systemWide, Float(point.x), Float(point.y), &element) == .success,
              var el = element else { return false }

        // Prefer the modifier-appropriate verb: a menu extra opens its menu on AXPress; some expose
        // an explicit AXShowMenu (esp. for the right-click menu).
        let actions: [CFString] = rightClick
            ? ["AXShowMenu" as CFString, kAXPressAction as CFString]
            : [kAXPressAction as CFString, "AXShowMenu" as CFString]

        for _ in 0..<3 {   // el, then up to two ancestors
            for action in actions {
                if AXUIElementPerformAction(el, action) == .success {
                    dbg("AX-pressed item \(itemID) via \(action) at \(point)")
                    return true
                }
            }
            var parent: CFTypeRef?
            guard AXUIElementCopyAttributeValue(el, kAXParentAttribute as CFString, &parent) == .success,
                  let p = parent, CFGetTypeID(p) == AXUIElementGetTypeID() else { break }
            el = (p as! AXUIElement)
        }
        return false
    }

    /// Best-effort human name for a status item whose window name is the generic AppKit "Item-0" (empty
    /// → "Menu item"): the app's real label lives in accessibility. Reads AXTitle → AXDescription →
    /// AXHelp of the element at the item's centre. Short messaging timeout so an unresponsive app can't
    /// stall enumeration (same convention as pressViaAX).
    private func axTitle(at point: CGPoint) -> String? {
        let systemWide = AXUIElementCreateSystemWide()
        _ = _AXUIElementSetMessagingTimeout(systemWide, 0.3)
        var element: AXUIElement?
        guard AXUIElementCopyElementAtPosition(systemWide, Float(point.x), Float(point.y), &element) == .success,
              let el = element else { return nil }
        for attr in [kAXTitleAttribute, kAXDescriptionAttribute, kAXHelpAttribute] as [CFString] {
            var v: CFTypeRef?
            if AXUIElementCopyAttributeValue(el, attr, &v) == .success, let s = v as? String {
                let trimmed = s.trimmingCharacters(in: .whitespacesAndNewlines)
                if !trimmed.isEmpty { return trimmed }
            }
        }
        return nil
    }

    private func clickViaCGEvent(at point: CGPoint, itemID: String,
                                 button: Bevel_Helper_V1_ForwardClickRequest.Button, modifiers: UInt32) -> Bool {
        let (downType, upType, cgButton): (CGEventType, CGEventType, CGMouseButton) =
            button == .right ? (.rightMouseDown, .rightMouseUp, .right) : (.leftMouseDown, .leftMouseUp, .left)
        let flags = cgFlags(modifiers)
        let source = CGEventSource(stateID: .combinedSessionState)
        CGWarpMouseCursorPosition(point)
        usleep(15_000)
        guard let move = CGEvent(mouseEventSource: source, mouseType: .mouseMoved,
                                 mouseCursorPosition: point, mouseButton: cgButton),
              let down = CGEvent(mouseEventSource: source, mouseType: downType,
                                 mouseCursorPosition: point, mouseButton: cgButton),
              let up = CGEvent(mouseEventSource: source, mouseType: upType,
                               mouseCursorPosition: point, mouseButton: cgButton) else {
            return false
        }
        move.post(tap: .cghidEventTap)
        down.flags = flags; up.flags = flags
        down.post(tap: .cghidEventTap)
        usleep(60_000)
        up.post(tap: .cghidEventTap)
        dbg("forwarded \(button) click (CGEvent) to item \(itemID) at \(point)")
        return true
    }

    /// Maps the wire modifier bitmask (shift=1, control=2, option=4, command=8) to CGEventFlags.
    private func cgFlags(_ modifiers: UInt32) -> CGEventFlags {
        var f = CGEventFlags()
        if modifiers & 1 != 0 { f.insert(.maskShift) }
        if modifiers & 2 != 0 { f.insert(.maskControl) }
        if modifiers & 4 != 0 { f.insert(.maskAlternate) }
        if modifiers & 8 != 0 { f.insert(.maskCommand) }
        return f
    }

    // MARK: - Enumeration

    /// Discovers the current menu-bar status items (Req 5.1/5.2). Left-to-right menu-bar order.
    func enumerateTrayItems() -> [Bevel_Helper_V1_TrayItem] {
        if subsystemDisabled { return [] }   // hard feature flag (§10.1)
        let ownPID = getpid()
        // Include OFF-SCREEN windows: Strategy A hides the real items by pushing them off-screen (the
        // app-side control item expands), and the tray must still show them. The layer/band/size filters
        // below keep this to menu-bar items; U1's legacy CG capture reads their icons even while hidden.
        guard let windows = CGWindowListCopyWindowInfo(
            [.excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] else {
            return []
        }

        var found: [(x: CGFloat, item: Bevel_Helper_V1_TrayItem)] = []
        for w in windows {
            guard let layer = w[kCGWindowLayer as String] as? Int, layer == statusWindowLayer else { continue }
            guard let pid = w[kCGWindowOwnerPID as String] as? pid_t, pid != ownPID, pid != parentPID else { continue }
            guard let boundsDict = w[kCGWindowBounds as String] as? [String: Any],
                  let rect = CGRect(dictionaryRepresentation: boundsDict as CFDictionary) else { continue }

            // Menu-bar band: pinned near the top with a menu-bar-ish height; status items are narrow.
            guard rect.origin.y <= 40, rect.height >= 8, rect.height <= 40,
                  rect.width >= 8, rect.width <= 400 else { continue }

            let windowNumber = (w[kCGWindowNumber as String] as? Int) ?? 0
            // Self-exclusion (U3): never mirror Bevel's own control item. On macOS 26 it's owned by the
            // Control Centre process, so the own-PID filter above can't catch it — exclude by window ID.
            if let ctrl = controlItem?.cachedWindowID, windowNumber == Int(ctrl) { continue }
            let ownerName = (w[kCGWindowOwnerName as String] as? String) ?? ""
            let windowName = (w[kCGWindowName as String] as? String) ?? ""
            // Exclude Bevel's own app-side control item so we never mirror ourselves (its ◂◂ marker title).
            if windowName.contains("◂") { continue }

            // Denylist (§5, bevel-m3.4): drop items we should not mirror — iStat Menus (live graphs
            // that belong in the native bar; the spec's canonical example), Control Center's own
            // chrome, and modules Bevel already renders (its clock). Filtered BEFORE capture so we
            // never spend a ScreenCaptureKit grab on a denied item.
            if isDenied(windowName: windowName, ownerName: ownerName) { continue }

            // On macOS 26, kCGWindowOwnerName is "Control Center" for every status item
            // (FB18327911); the real identity lives in kCGWindowName (a bundle id, an app path,
            // or a system-item name). Resolve display name + limited-mode icon from that.
            let identity = resolveItem(windowName: windowName, ownerPID: pid, ownerName: ownerName)
            // A generic AppKit window name ("Item-0" → "Item", empty → "Menu item") carries no identity,
            // and every item's owner PID is Control Center on macOS 26 — so the app's real label only
            // lives in accessibility. Query the AX element at the item's centre so the tooltip reads
            // "EXO"/"Weather"/… instead of a useless "Item".
            var displayName = identity.name
            if displayName == "Item" || displayName == "Menu item",
               let axName = axTitle(at: CGPoint(x: rect.midX, y: rect.midY)) {
                displayName = axName
            }

            var item = Bevel_Helper_V1_TrayItem()
            item.itemID = "\(pid):\(windowNumber)"
            item.ownerPid = pid
            item.ownerBundleID = identity.bundleID
            item.ownerName = displayName
            item.tooltip = displayName
            var pr = Bevel_Helper_V1_PixelRect()
            pr.x = Int32(rect.origin.x); pr.y = Int32(rect.origin.y)
            pr.width = Int32(rect.width); pr.height = Int32(rect.height)
            item.bounds = pr
            item.iconPng = identity.icon
            item.isLive = false   // limited mode (§5.5) until SCK capture (M3-B)
            found.append((rect.origin.x, item))
        }

        let sorted = found.sorted { $0.x < $1.x }.map { $0.item }
        dbg("enumerated \(sorted.count) status items: " + sorted.map { $0.ownerName }.joined(separator: ", "))
        return sorted
    }

    /// TEMP geometry probe (bevel-7hf4): when /tmp/bevel-geo exists, dump every menu-bar status window
    /// (ON + OFF screen) with its x/width and on-screen flag, so we can see where hidden items sit and
    /// whether there's a slot next to the control item that stays visible after re-hide. Removed once the
    /// single-item reveal geometry is settled.
    private func dumpGeometry() {
        guard FileManager.default.fileExists(atPath: "/tmp/bevel-geo") else { return }
        guard let wins = CGWindowListCopyWindowInfo([.excludeDesktopElements], kCGNullWindowID) as? [[String: Any]]
        else { return }
        var rows: [(Double, String)] = []
        for w in wins {
            guard (w[kCGWindowLayer as String] as? Int) == statusWindowLayer,
                  let bd = w[kCGWindowBounds as String] as? [String: Any],
                  let r = CGRect(dictionaryRepresentation: bd as CFDictionary), r.origin.y <= 40 else { continue }
            let owner = (w[kCGWindowOwnerName as String] as? String) ?? "?"
            let name = (w[kCGWindowName as String] as? String) ?? ""
            let on = (w[kCGWindowIsOnscreen as String] as? Bool) ?? false
            rows.append((r.origin.x, String(format: "on=%@ x=%5.0f w=%3.0f  %@ '%@'", on ? "Y" : "n",
                                             r.origin.x, r.width, owner, name)))
        }
        let text = rows.sorted { $0.0 < $1.0 }.map { $0.1 }.joined(separator: "\n")
        try? text.write(toFile: "/tmp/bevel-geo.log", atomically: true, encoding: .utf8)
    }

    /// GO/NO-GO variation pass (bevel-6fin): when /tmp/bevel-dragtest exists, try SEVERAL synthetic Cmd-drag
    /// techniques on one on-screen item and log which (if any) actually moves it — separating "synthetic drag
    /// can't reposition macOS-26 Control-Center items" (all fail → approach dead) from "my first sequence was
    /// wrong" (one works → build it). Restores the item after any move. One-shot. → /tmp/bevel-dragtest.log.
    private func cmdDragValidation() {
        let marker = "/tmp/bevel-dragtest"
        guard FileManager.default.fileExists(atPath: marker) else { return }
        try? FileManager.default.removeItem(atPath: marker)   // one-shot

        guard let (rect, num) = pickDragCandidate() else {
            try? "no draggable candidate found".write(toFile: marker + ".log", atomically: true, encoding: .utf8)
            return
        }
        var log = "candidate win=\(num) x=\(Int(rect.origin.x)) w=\(Int(rect.width)) midY=\(Int(rect.midY))\n"

        // Each attempt re-reads the item's live x (so it self-adjusts if a prior attempt shifted it),
        // drags +60px with the given technique, checks movement, and reverses with the SAME technique.
        func attempt(_ name: String, _ drag: (CGPoint, CGPoint) -> Void) {
            guard let x0 = currentX(ofWindow: num) else { log += "\(name): item gone\n"; return }
            let from = CGPoint(x: x0 + rect.width / 2, y: rect.midY)
            let to = CGPoint(x: from.x + 60, y: from.y)
            drag(from, to)
            usleep(450_000)
            let x1 = currentX(ofWindow: num)
            let moved = x1.map { abs($0 - x0) > 8 } ?? false
            log += "\(name): x \(Int(x0)) -> \(x1.map { String(Int($0)) } ?? "gone")  \(moved ? "MOVED ✓" : "no")\n"
            if moved, let x1 = x1 {   // reverse with the same technique
                drag(CGPoint(x: x1 + rect.width / 2, y: rect.midY), from)
                usleep(350_000)
            }
        }

        attempt("A hid + flags-only")      { f, t in synthCmdDrag(from: f, to: t, tap: .cghidEventTap,     realCmdKey: false, threshold: false, slow: false) }
        attempt("B hid + real-Cmd-key")    { f, t in synthCmdDrag(from: f, to: t, tap: .cghidEventTap,     realCmdKey: true,  threshold: true,  slow: false) }
        attempt("C session + real-Cmd")    { f, t in synthCmdDrag(from: f, to: t, tap: .cgSessionEventTap, realCmdKey: true,  threshold: true,  slow: false) }
        attempt("D hid + real-Cmd + slow") { f, t in synthCmdDrag(from: f, to: t, tap: .cghidEventTap,     realCmdKey: true,  threshold: true,  slow: true) }

        let anyMoved = log.contains("MOVED ✓")
        log += anyMoved ? "\nVERDICT: at least one technique moved it → synthetic Cmd-drag is VIABLE ✓\n"
                        : "\nVERDICT: NOTHING moved it → macOS blocks synthetic rearrange of CC items ✗\n"
        try? log.write(toFile: marker + ".log", atomically: true, encoding: .utf8)
    }

    /// Picks one on-screen mirrored status item to experiment on: a sane-width item in the menu-bar band,
    /// preferring a mid-bar one (skip the leftmost few) so it isn't a fixed system anchor and has drag room.
    private func pickDragCandidate() -> (CGRect, Int)? {
        guard let wins = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID)
                as? [[String: Any]] else { return nil }
        let candidates: [(CGRect, Int)] = wins.compactMap { w in
            guard (w[kCGWindowLayer as String] as? Int) == statusWindowLayer,
                  let num = w[kCGWindowNumber as String] as? Int,
                  let bd = w[kCGWindowBounds as String] as? [String: Any],
                  let r = CGRect(dictionaryRepresentation: bd as CFDictionary),
                  r.origin.y <= 40, r.origin.x > 200, r.width >= 12, r.width <= 60 else { return nil }
            return (r, num)
        }.sorted { $0.0.origin.x < $1.0.origin.x }
        return candidates.dropFirst(3).first ?? candidates.last
    }

    private func currentX(ofWindow num: Int) -> CGFloat? {
        guard let wins = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID)
                as? [[String: Any]] else { return nil }
        for w in wins where (w[kCGWindowNumber as String] as? Int) == num {
            if let bd = w[kCGWindowBounds as String] as? [String: Any],
               let r = CGRect(dictionaryRepresentation: bd as CFDictionary) { return r.origin.x }
        }
        return nil
    }

    /// Synthesize a Command-drag with tunable technique. `realCmdKey` posts an actual Cmd keyDown/keyUp
    /// (not just the event flag) so the WindowServer sees the modifier truly held; `threshold` sends a tiny
    /// initial drag to trip the drag-start recognizer; `slow` lengthens the holds/steps. `tap` picks the
    /// injection point (HID vs session).
    private func synthCmdDrag(from: CGPoint, to: CGPoint, tap: CGEventTapLocation,
                              realCmdKey: Bool, threshold: Bool, slow: Bool) {
        let src = CGEventSource(stateID: .combinedSessionState)
        let flags: CGEventFlags = .maskCommand
        if realCmdKey, let kd = CGEvent(keyboardEventSource: src, virtualKey: 0x37, keyDown: true) {
            kd.flags = .maskCommand; kd.post(tap: tap); usleep(40_000)
        }
        CGWarpMouseCursorPosition(from)
        usleep(20_000)
        if let e = CGEvent(mouseEventSource: src, mouseType: .mouseMoved, mouseCursorPosition: from, mouseButton: .left) {
            e.flags = flags; e.post(tap: tap)
        }
        usleep(20_000)
        if let e = CGEvent(mouseEventSource: src, mouseType: .leftMouseDown, mouseCursorPosition: from, mouseButton: .left) {
            e.flags = flags; e.post(tap: tap)
        }
        usleep(slow ? 220_000 : 90_000)
        if threshold {   // a small initial move to register the gesture as a drag
            let p = CGPoint(x: from.x + 5, y: from.y)
            if let e = CGEvent(mouseEventSource: src, mouseType: .leftMouseDragged, mouseCursorPosition: p, mouseButton: .left) {
                e.flags = flags; e.post(tap: tap)
            }
            usleep(50_000)
        }
        let steps = slow ? 24 : 12
        for i in 1...steps {
            let t = CGFloat(i) / CGFloat(steps)
            let p = CGPoint(x: from.x + (to.x - from.x) * t, y: from.y + (to.y - from.y) * t)
            if let e = CGEvent(mouseEventSource: src, mouseType: .leftMouseDragged, mouseCursorPosition: p, mouseButton: .left) {
                e.flags = flags; e.post(tap: tap)
            }
            usleep(slow ? 32_000 : 16_000)
        }
        usleep(80_000)
        if let e = CGEvent(mouseEventSource: src, mouseType: .leftMouseUp, mouseCursorPosition: to, mouseButton: .left) {
            e.flags = flags; e.post(tap: tap)
        }
        if realCmdKey, let ku = CGEvent(keyboardEventSource: src, virtualKey: 0x37, keyDown: false) {
            ku.flags = []; ku.post(tap: tap); usleep(30_000)
        }
    }

    // MARK: - Live capture (ScreenCaptureKit, §5.3)

    /// Enumerates the tray items and, when Screen Recording is granted, overlays a live per-window
    /// ScreenCaptureKit capture on each (Req 5.3), marking it `isLive`. Without the grant — or if any
    /// capture fails — the item keeps its limited-mode app icon (§5.5). One `SCShareableContent`
    /// fetch per call, then a per-window screenshot; the caller throttles the cadence (the 2s poll).
    func enumerateWithCapture() async -> [Bevel_Helper_V1_TrayItem] {
        var items = enumerateTrayItems()
        dumpGeometry()        // TEMP (bevel-7hf4): geometry probe; self-gated by /tmp/bevel-geo
        cmdDragValidation()   // TEMP (bevel-6fin): synthetic Cmd-drag go/no-go; self-gated by /tmp/bevel-dragtest
        await ensureSelfTested()
        // Limited mode (§5.5) when Screen Recording isn't granted OR the self-test disabled live
        // mirroring on this OS build (§5.10) — never show black/wrong frames.
        guard isLiveMirroringEnabled, CGPreflightScreenCaptureAccess() else { return items }

        // Capture each item's window by ID via the legacy CG path (U1/KTD1). Unlike ScreenCaptureKit
        // (which returns -3811 once a window leaves every display), this reads the backing store
        // directly, so items hidden off-screen by Strategy A still capture their real glyph instead of
        // falling back to the limited-mode app icon. Keyed on the windowNumber half of item_id — no
        // on-screen SCShareableContent gate.
        for i in items.indices {
            let parts = items[i].itemID.split(separator: ":")
            guard parts.count == 2, let num = UInt32(parts[1]),
                  let cgImage = LegacyWindowCapture.image(windowID: CGWindowID(num)) else { continue }
            let png = pngFromCGImage(cgImage)
            if !png.isEmpty {   // empty PNG must not overwrite the limited-mode icon (review: correctness)
                items[i].iconPng = png
                items[i].isLive = true
            }
        }
        return items
    }

    /// One-shot capture of a single status-item window, scoped to just that window (not a cropped
    /// full-screen grab — Req 5.3), returned as a 16×16 PNG. Nil on failure (keeps the limited-mode icon).
    private func captureWindow(_ scWindow: SCWindow) async -> Data? {
        let filter = SCContentFilter(desktopIndependentWindow: scWindow)
        let config = SCStreamConfiguration()
        // Capture at 2× the window's point size for a crisp downscale to 16px.
        config.width = max(16, Int(scWindow.frame.width * 2))
        config.height = max(16, Int(scWindow.frame.height * 2))
        config.showsCursor = false
        config.ignoreShadowsSingleWindow = true
        do {
            let cgImage = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
            return pngFromCGImage(cgImage)
        } catch {
            dbg("capture failed for windowID \(scWindow.windowID): \(error)")
            return nil
        }
    }

    /// Encodes a captured status-item window at its NATIVE resolution — NO rescale, NO fit-to-box. The
    /// tray renders each mirrored item at its true macOS size (from the item's on-screen `bounds`, in
    /// points), so a mac icon stays exactly mac-sized — a FAITHFUL slice of the menu bar (bevel-7hf4).
    /// Any scaling here reintroduces the per-item size drift that made the tray look shrunk/flattened; the
    /// only sizing lives in the taskbar (display at bounds size) and the box grid (footprint, never size).
    /// Blank-frame guard only: a fully-transparent/unreadable capture emits nothing so the caller keeps the
    /// limited-mode icon (review: adversarial). The retina pixels downscale crisply to the point-size box.
    private func pngFromCGImage(_ cgImage: CGImage) -> Data {
        guard cgImage.width > 0, cgImage.height > 0, trimTransparent(cgImage) != nil else { return Data() }
        return NSBitmapImageRep(cgImage: cgImage).representation(using: .png, properties: [:]) ?? Data()
    }

    /// Output layout for a trimmed tray glyph — the sizing math, PURE so it can be regression-tested
    /// without a real capture. This sizing oscillated repeatedly: a 1.35× boost made "one big, others
    /// tiny", forcing every glyph to one height blew short TEXT up, and scaling by raw trimmed height made
    /// full-bleed app icons much bigger than padded system glyphs. The shipped rule normalizes each glyph's
    /// cell-padding against an assumed `standardFill`, so a typical system glyph reaches the full box and
    /// app icons cap there too (icons UNIFORM, like the real menu bar), while short glyphs stay
    /// proportionally small. Width is proportional (true width) and clamped so an extreme-aspect strip
    /// shrinks vertically rather than squashing to full height. Glyph is centred vertically in the box.
    struct TrayGlyphLayout: Equatable {
        let canvasW: Int
        let canvasH: Int
        let drawnW: CGFloat
        let drawnH: CGFloat
        let dstY: CGFloat
    }

    /// Scales a captured status-item CELL (the whole menu-bar window — pass its width as `gw`, height as
    /// `gh`) uniformly to the tray box. The caller does NOT trim: the window is a uniform menu-bar cell
    /// with its glyph/text padded inside exactly as macOS draws it, so scaling the whole cell preserves
    /// each item's true proportion (icons large, text small with padding) — a faithful shrink of the bar.
    /// The cell fills the box height; width is proportional (true width) and clamped so an extreme-aspect
    /// strip shrinks vertically rather than squashing. `fullH` is unused (kept for API/test stability).
    static func trayGlyphLayout(gw: CGFloat, gh: CGFloat, fullH: CGFloat, boxH: CGFloat = 32) -> TrayGlyphLayout {
        _ = fullH
        let glyphH = boxH
        let glyphW = glyphH * (gw / gh)
        let canvasW = max(1, min(Int(glyphW.rounded()), 512))
        let canvasH = Int(boxH)
        let drawnW = CGFloat(canvasW)
        let drawnH = min(glyphH, drawnW * (gh / gw))
        let dstY = (CGFloat(canvasH) - drawnH) / 2
        return TrayGlyphLayout(canvasW: canvasW, canvasH: canvasH, drawnW: drawnW, drawnH: drawnH, dstY: dstY)
    }

    /// Crops a captured image to the bounding box of its non-transparent pixels, discarding the
    /// menu-bar padding around the glyph. Returns nil (caller keeps the original) if it's fully
    /// transparent or the pixels can't be read.
    private func trimTransparent(_ cg: CGImage) -> CGImage? {
        let w = cg.width, h = cg.height
        guard w > 0, h > 0, let space = CGColorSpace(name: CGColorSpace.sRGB) else { return nil }
        let bytesPerRow = w * 4
        var data = [UInt8](repeating: 0, count: bytesPerRow * h)
        guard let ctx = CGContext(
            data: &data, width: w, height: h, bitsPerComponent: 8, bytesPerRow: bytesPerRow,
            space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.draw(cg, in: CGRect(x: 0, y: 0, width: w, height: h))

        var minX = w, minY = h, maxX = -1, maxY = -1
        for y in 0..<h {
            let row = y * bytesPerRow
            for x in 0..<w where data[row + x * 4 + 3] > 12 {   // ~5% alpha = real content
                if x < minX { minX = x }; if x > maxX { maxX = x }
                if y < minY { minY = y }; if y > maxY { maxY = y }
            }
        }
        guard maxX >= minX, maxY >= minY else { return nil }
        return cg.cropping(to: CGRect(x: minX, y: minY, width: maxX - minX + 1, height: maxY - minY + 1))
    }

    /// Diff signature that EXCLUDES the icon bytes, so a re-capture (whose pixels can differ frame to
    /// frame, e.g. an iStat graph) doesn't churn the change stream — only identity/geometry changes
    /// count as an UPDATE.
    private func signature(_ item: Bevel_Helper_V1_TrayItem) -> String {
        // Deliberately EXCLUDES bounds: the menu bar reflows by a pixel constantly, and keying the
        // diff on x/width fired a spurious UPDATE for every item each poll — re-decoding icons and
        // dismissing tooltips on the client. Forwarding re-reads live bounds anyway, so the client
        // never needs the jittering coordinates. Only real identity changes now count as an update.
        "\(item.ownerBundleID)|\(item.ownerName)|\(item.tooltip)|\(item.isLive)"
    }

    // MARK: - Self-test gate (§5.10 / §10.1)

    /// Runs the launch-time self-test exactly once (the first capture attempt triggers it), caching
    /// whether live mirroring is safe on this OS build. Concurrent callers wait on the same result.
    private func ensureSelfTested() async {
        if selfTestLock.withLock({ selfTestDone }) { return }
        // A rare concurrent first-call may run the (idempotent) self-test twice — harmless.
        let ok = await runSelfTest()
        selfTestLock.withLock { liveMirroringEnabled = ok; selfTestDone = true }
    }

    private var isLiveMirroringEnabled: Bool { selfTestLock.withLock { liveMirroringEnabled } }

    /// Verifies the systray pipeline on the current OS build (Req 5.10): discover ≥1 item, confirm AX
    /// availability (no-op locate proxy), and — when Screen Recording is granted — capture one frame
    /// and confirm it's non-blank. A granted-but-broken capture disables live mirroring (→ limited
    /// mode); a missing grant is NOT a failure (limited mode is the expected no-grant path). Result +
    /// OS build are written to the offline diagnostic log.
    private func runSelfTest() async -> Bool {
        let build = osBuild()
        let discovered = !enumerateTrayItems().isEmpty
        let axOk = AXIsProcessTrusted()

        var captureOk = true
        let granted = CGPreflightScreenCaptureAccess()
        if granted && discovered {
            captureOk = await selfTestCapture()
        }

        // Live mirroring is safe iff a granted capture actually worked; without a grant we stay in
        // limited mode regardless. Discovery/AX are logged for diagnosis but don't gate capture.
        let live = granted ? captureOk : true
        log("self-test build=\(build) discovered=\(discovered) ax=\(axOk) grant=\(granted) " +
            "capture=\(captureOk) → liveMirroring=\(live)")
        return live
    }

    /// Captures one status-item window and reports whether it has any non-transparent pixels — the
    /// "verify non-blank" step. False means SCK returned black/blocked frames (pipeline broken).
    private func selfTestCapture() async -> Bool {
        guard let content = try? await SCShareableContent.excludingDesktopWindows(
            false, onScreenWindowsOnly: true) else { return false }
        guard let win = content.windows.first(where: {
            $0.windowLayer == statusWindowLayer && $0.frame.origin.y <= 40
            && $0.frame.width >= 8 && $0.frame.width <= 400
        }) else { return false }   // granted but SCK sees no status window → broken

        guard let png = await captureWindow(win), !png.isEmpty,
              let rep = NSBitmapImageRep(data: png) else { return false }
        for x in stride(from: 0, to: rep.pixelsWide, by: 4) {
            for y in stride(from: 0, to: rep.pixelsHigh, by: 4) {
                if (rep.colorAt(x: x, y: y)?.alphaComponent ?? 0) > 0.05 { return true }
            }
        }
        return false   // fully transparent → blank
    }

    /// The current OS build string (e.g. "25G74"), for the self-test diagnostic and OS-change gating.
    private func osBuild() -> String {
        var size = 0
        sysctlbyname("kern.osversion", nil, &size, nil, 0)
        guard size > 0 else { return "?" }
        var buf = [CChar](repeating: 0, count: size)
        sysctlbyname("kern.osversion", &buf, &size, nil, 0)
        return buf.withUnsafeBufferPointer { $0.baseAddress.map { String(cString: $0) } ?? "?" }
    }

    // MARK: - Identity resolution (macOS 26-aware, §5.2)

    /// Resolves a status item's display name + limited-mode icon from its `kCGWindowName` (the real
    /// identity on macOS 26, where the owner is always Control Center). Handles an app path, a
    /// resolvable bundle id, a sub-identifier whose base bundle resolves (e.g.
    /// `com.bjango.istatmenus.cpu` → `com.bjango.istatmenus`), and falls back to the owning process's
    /// icon + a friendly name for Apple/system modules (`WiFi`, `Sound`, `com.apple.menuextra.*`, …).
    private func resolveItem(windowName: String, ownerPID: pid_t, ownerName: String)
        -> (bundleID: String, name: String, icon: Data) {
        let ws = NSWorkspace.shared

        // 1. An app path, e.g. "/Applications/Parallels Toolbox.app".
        if windowName.hasPrefix("/"), windowName.hasSuffix(".app") {
            let name = ((windowName as NSString).lastPathComponent as NSString).deletingPathExtension
            return (windowName, name, pngFromIcon(ws.icon(forFile: windowName)))
        }

        // 2. A bundle id that resolves directly to an installed app.
        if windowName.contains("."), let url = ws.urlForApplication(withBundleIdentifier: windowName) {
            let name = (url.deletingPathExtension().lastPathComponent)
            return (windowName, name, cachedAppIconPNG(bundleID: windowName))
        }

        // 3. A sub-identifier whose base (first 3 dot-segments) bundle resolves — iStat Menus et al.
        if windowName.contains(".") {
            let segs = windowName.split(separator: ".")
            if segs.count >= 3 {
                let base = segs.prefix(3).joined(separator: ".")
                if let url = ws.urlForApplication(withBundleIdentifier: base) {
                    let app = url.deletingPathExtension().lastPathComponent
                    let leaf = segs.last.map(String.init) ?? windowName
                    return (base, "\(app) — \(leaf)", cachedAppIconPNG(bundleID: base))
                }
            }
        }

        // 4. System / generic module (WiFi, Sound, Clock, Item-0, com.apple.menuextra.*): keep the
        //    owning process's icon (Control Center on macOS 26) with a friendly name.
        let app = NSRunningApplication(processIdentifier: ownerPID)
        let icon = app?.bundleIdentifier.map { cachedAppIconPNG(bundleID: $0) } ?? Data()
        return (app?.bundleIdentifier ?? "", friendlyName(windowName.isEmpty ? ownerName : windowName), icon)
    }

    /// Humanizes a raw window/system name: strips the `com.apple.menuextra.` prefix, drops a trailing
    /// `-0` suffix, and leaves already-friendly names (WiFi, Sound, Clock) as-is.
    private func friendlyName(_ raw: String) -> String {
        var s = raw
        if let r = s.range(of: "com.apple.menuextra.") { s.removeSubrange(r) }
        if s.hasSuffix("-0") { s = String(s.dropLast(2)) }
        if s.isEmpty { return "Menu item" }
        return s.prefix(1).uppercased() + s.dropFirst()
    }

    /// Test seam for `friendlyName`.
    func friendlyNameForTest(_ raw: String) -> String { friendlyName(raw) }

    /// Test seams for the self-test (§5.10).
    func runSelfTestForTest() async -> Bool { await runSelfTest() }
    func osBuildForTest() -> String { osBuild() }

    // MARK: - Icons (limited mode)

    private func cachedAppIconPNG(bundleID: String) -> Data {
        if bundleID.isEmpty { return Data() }
        iconCacheLock.lock()
        if let cached = iconCache[bundleID] { iconCacheLock.unlock(); return cached }
        iconCacheLock.unlock()

        let png = appIconPNG(bundleID: bundleID)
        iconCacheLock.lock(); iconCache[bundleID] = png; iconCacheLock.unlock()
        return png
    }

    /// The app with this bundle id's icon as a 16×16 PNG, or empty if it can't be resolved.
    private func appIconPNG(bundleID: String) -> Data {
        guard let appPath = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID)?.path else {
            return Data()
        }
        return pngFromIcon(NSWorkspace.shared.icon(forFile: appPath))
    }

    /// Renders an NSImage into a fixed 16×16 PNG (mirrors WindowServiceImpl's sizing so the reply
    /// stays small and well under gRPC's message limit — tray icons never need more).
    private func pngFromIcon(_ icon: NSImage) -> Data {
        let target = NSSize(width: 16, height: 16)
        guard let rep = NSBitmapImageRep(
            bitmapDataPlanes: nil, pixelsWide: 16, pixelsHigh: 16,
            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
            colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else {
            return Data()
        }
        rep.size = target
        guard let ctx = NSGraphicsContext(bitmapImageRep: rep) else { return Data() }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = ctx
        icon.draw(in: NSRect(origin: .zero, size: target), from: .zero, operation: .copy, fraction: 1.0)
        NSGraphicsContext.restoreGraphicsState()
        return rep.representation(using: .png, properties: [:]) ?? Data()
    }
}
