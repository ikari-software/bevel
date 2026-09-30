import AppKit
import Darwin

// MARK: - SkyLight (private WindowServer client API)

/// The few private SkyLight entry points the window service needs, resolved at RUNTIME with `dlsym`.
///
/// Why dlsym and not `@_silgen_name` + a linker flag: these symbols are private and Apple may rename
/// or remove one in any OS update. A dlsym miss degrades exactly one feature (Space awareness /
/// per-window system focus — the callers have public-API fallbacks) instead of taking the whole
/// helper down at load time, and the package needs no private-framework linker settings.
///
/// Everything here is best-effort and side-effect free unless named `focusWindow` / `switchToSpace`.
enum SkyLight {
    private typealias MainConnection = @convention(c) () -> Int32
    private typealias CopySpacesForWindows = @convention(c) (Int32, Int32, CFArray) -> Unmanaged<CFArray>?
    private typealias CopyManagedDisplaySpaces = @convention(c) (Int32) -> Unmanaged<CFArray>?
    private typealias ShowSpaces = @convention(c) (Int32, CFArray) -> Void
    private typealias HideSpaces = @convention(c) (Int32, CFArray) -> Void
    private typealias SetCurrentSpace = @convention(c) (Int32, CFString, UInt64) -> Void
    private typealias SpaceSetFrontPSN = @convention(c) (Int32, UInt64, ProcessSerialNumber) -> CGError
    private typealias SetFrontProcess = @convention(c) (UnsafeMutablePointer<ProcessSerialNumber>, CGWindowID, UInt32) -> CGError
    private typealias PostEventRecord = @convention(c) (UnsafeMutablePointer<ProcessSerialNumber>, UnsafeMutablePointer<UInt8>) -> CGError
    private typealias ProcessForPID = @convention(c) (pid_t, UnsafeMutablePointer<ProcessSerialNumber>) -> OSStatus

    private struct Symbols: @unchecked Sendable {
        let connection: Int32
        let copySpacesForWindows: CopySpacesForWindows?
        let copyManagedDisplaySpaces: CopyManagedDisplaySpaces?
        let showSpaces: ShowSpaces?
        let hideSpaces: HideSpaces?
        let setCurrentSpace: SetCurrentSpace?
        let spaceSetFrontPSN: SpaceSetFrontPSN?
        let setFrontProcess: SetFrontProcess?
        let postEventRecord: PostEventRecord?
        let processForPID: ProcessForPID?

        static func load() -> Symbols {
            let sky = dlopen("/System/Library/PrivateFrameworks/SkyLight.framework/SkyLight", RTLD_NOW)
            func sym<T>(_ handle: UnsafeMutableRawPointer?, _ name: String, as: T.Type) -> T? {
                guard let handle, let p = dlsym(handle, name) else { return nil }
                return unsafeBitCast(p, to: T.self)
            }
            let main = sym(sky, "CGSMainConnectionID", as: MainConnection.self)
            return Symbols(
                connection: main?() ?? 0,
                copySpacesForWindows: sym(sky, "CGSCopySpacesForWindows", as: CopySpacesForWindows.self),
                copyManagedDisplaySpaces: sym(sky, "CGSCopyManagedDisplaySpaces", as: CopyManagedDisplaySpaces.self),
                showSpaces: sym(sky, "CGSShowSpaces", as: ShowSpaces.self),
                hideSpaces: sym(sky, "CGSHideSpaces", as: HideSpaces.self),
                setCurrentSpace: sym(sky, "CGSManagedDisplaySetCurrentSpace", as: SetCurrentSpace.self),
                spaceSetFrontPSN: sym(sky, "SLSSpaceSetFrontPSN", as: SpaceSetFrontPSN.self),
                setFrontProcess: sym(sky, "_SLPSSetFrontProcessWithOptions", as: SetFrontProcess.self),
                postEventRecord: sym(sky, "SLPSPostEventRecordTo", as: PostEventRecord.self),
                // GetProcessForPID lives in ApplicationServices, not SkyLight. Load it explicitly:
                // dlopen(nil) only sees images the process happens to have loaded already.
                processForPID: sym(
                    dlopen("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices", RTLD_NOW),
                    "GetProcessForPID", as: ProcessForPID.self)
            )
        }
    }

    private static let symbols = Symbols.load()

    /// The Spaces each display is CURRENTLY showing (one per display). Empty when unavailable.
    static func currentSpaceIDs() -> Set<Int> {
        guard let copy = symbols.copyManagedDisplaySpaces,
              let displays = copy(symbols.connection)?.takeRetainedValue() as? [[String: Any]]
        else { return [] }
        return Set(displays.compactMap { ($0["Current Space"] as? [String: Any])?["id64"] as? Int })
    }

    /// Every Space `window` belongs to (`[]` = unknown). 0x7 = all space kinds (user, fullscreen, system).
    static func spaceIDs(ofWindow window: CGWindowID) -> [Int] {
        guard let copy = symbols.copySpacesForWindows else { return [] }
        return (copy(symbols.connection, 0x7, [window] as CFArray)?.takeRetainedValue() as? [Int]) ?? []
    }

    /// Make `cgID` THE key window of `pid`.
    ///
    /// Addresses a window by WindowServer id (works without AX). This does **not** switch Mission
    /// Control Spaces — measured 2026-09-30: `_SLPSSetFrontProcessWithOptions` returns success while
    /// `CGSCopyManagedDisplaySpaces` current IDs stay put (Jump Desktop on Space 459/1096). Call
    /// `switchToSpace(ofWindow:)` first when the window is off the current Space(s).
    ///
    /// Returns false when a symbol is missing or the process serial number can't be resolved —
    /// the caller then falls back to plain app activation.
    @discardableResult
    static func focusWindow(pid: pid_t, cgID: CGWindowID) -> Bool {
        guard let processForPID = symbols.processForPID,
              let setFront = symbols.setFrontProcess,
              let post = symbols.postEventRecord
        else { return false }

        var psn = ProcessSerialNumber()
        guard processForPID(pid, &psn) == noErr else { return false }

        let kCPSUserGenerated: UInt32 = 0x200
        guard setFront(&psn, cgID, kCPSUserGenerated) == .success else { return false }

        var record = [UInt8](repeating: 0, count: 0xf8)
        record[0x04] = 0xf8
        record[0x3a] = 0x10
        var id = cgID
        memcpy(&record[0x3c], &id, MemoryLayout<UInt32>.size)
        memset(&record[0x20], 0xff, 0x10)
        record[0x08] = 0x01   // key-window down
        _ = post(&psn, &record)
        record[0x08] = 0x02   // key-window up
        _ = post(&psn, &record)
        return true
    }

    /// Result of a Space switch attempt (multi-monitor aware: one current Space per display).
    struct SpaceSwitchResult: Equatable {
        var ok: Bool
        /// Display whose current Space we changed (or would have), when known.
        var displayUUID: String?
        var fromSpace: Int?
        var toSpace: Int?
        var alreadyCurrent: Bool
        /// Whether we resolved an NSScreen and parked the pointer on it.
        var displayFocused: Bool
        /// Whether a synthetic left-click was posted on that display (Dock recomposite nudge).
        var clicked: Bool

        static let failed = SpaceSwitchResult(
            ok: false, displayUUID: nil, fromSpace: nil, toSpace: nil,
            alreadyCurrent: false, displayFocused: false, clicked: false)
    }

    /// Switch the display that owns `window` onto one of that window's Spaces.
    ///
    /// Multi-monitor reality (bevel-szw4, Jorvik / yabai notes): `CGSManagedDisplaySetCurrentSpace`
    /// updates CGS bookkeeping immediately, but Dock only recomposites that display's windows when
    /// the pointer is on it — and a synthetic **click** is what forces the paint (mouseMoved /
    /// warp-hold-restore alone leave currents flipped and the UI stuck until a real click minutes
    /// later). So: warp onto the owning display, Hide/Show/SetCurrent, click, and **leave** the
    /// pointer there (mouse-follows-focus). Restoring the cursor in the same turn cancels the paint.
    @discardableResult
    static func switchToSpace(ofWindow window: CGWindowID) -> SpaceSwitchResult {
        guard let show = symbols.showSpaces,
              let hide = symbols.hideSpaces,
              let setCurrent = symbols.setCurrentSpace,
              let copyManaged = symbols.copyManagedDisplaySpaces
        else { return .failed }

        let winSpaces = Set(spaceIDs(ofWindow: window))
        guard !winSpaces.isEmpty else { return .failed }

        guard let displays = copyManaged(symbols.connection)?.takeRetainedValue() as? [[String: Any]]
        else { return .failed }

        for display in displays {
            guard let uuid = display["Display Identifier"] as? String else { continue }
            let listed = display["Spaces"] as? [[String: Any]] ?? []
            let onDisplay = Set(listed.compactMap { ($0["id64"] as? Int) ?? ($0["ManagedSpaceID"] as? Int) })
            // This window's Space on THIS display (one app can own several fullscreen Spaces on the
            // same display). Pick the Space that belongs to the clicked window.
            guard let target = winSpaces.first(where: { onDisplay.contains($0) }) else { continue }
            let currentOnDisplay = (display["Current Space"] as? [String: Any])?["id64"] as? Int

            if currentOnDisplay == target {
                return SpaceSwitchResult(
                    ok: true, displayUUID: uuid, fromSpace: target, toSpace: target,
                    alreadyCurrent: true, displayFocused: false, clicked: false)
            }

            // Prefer the window's own centre (lands on the session); fall back to display mid.
            // Do NOT synthetic-click: Jump/RDP fullscreen would eat the click on the remote.
            // Leave the pointer here (mouse-follows-focus) — restoring cancels Dock's paint.
            let parkPoint = windowCenterCocoa(window) ?? screen(forDisplayUUID: uuid).map {
                CGPoint(x: $0.frame.midX, y: $0.frame.midY)
            }
            let focused: Bool
            if let parkPoint {
                parkPointer(atCocoa: parkPoint)
                focused = true
            } else {
                focused = focusDisplay(uuid: uuid)
            }

            let from = currentOnDisplay
            if let from {
                hide(symbols.connection, [from] as CFArray)
            }
            show(symbols.connection, [target] as CFArray)
            setCurrent(symbols.connection, uuid as CFString, UInt64(target))

            return SpaceSwitchResult(
                ok: true, displayUUID: uuid, fromSpace: from, toSpace: target,
                alreadyCurrent: false, displayFocused: focused, clicked: false)
        }
        return .failed
    }

    /// Put the pointer on the Mission Control display identified by `uuid` (display midpoint).
    @discardableResult
    static func focusDisplay(uuid: String) -> Bool {
        guard let screen = screen(forDisplayUUID: uuid) else { return false }
        let f = screen.frame
        parkPointer(atCocoa: CGPoint(x: f.midX, y: f.midY))
        return true
    }

    private static func parkPointer(atCocoa cocoa: CGPoint) {
        CGAssociateMouseAndMouseCursorPosition(0)
        warpCursor(toCocoa: cocoa)
        postMouseMoved(atCocoa: cocoa)
        CGAssociateMouseAndMouseCursorPosition(1)
    }

    private static func screen(forDisplayUUID uuid: String) -> NSScreen? {
        NSScreen.screens.first { screen in
            guard let num = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber
            else { return false }
            guard let cf = CGDisplayCreateUUIDFromDisplayID(num.uint32Value)?.takeRetainedValue()
            else { return false }
            let screenUUID = CFUUIDCreateString(nil, cf) as String? ?? ""
            return screenUUID.caseInsensitiveCompare(uuid) == .orderedSame
        }
    }

    /// Cocoa mid-point of a CG window's bounds, if CGWindowList still knows it.
    private static func windowCenterCocoa(_ window: CGWindowID) -> CGPoint? {
        let info = CGWindowListCopyWindowInfo([.optionIncludingWindow], window) as? [[String: Any]]
        guard let bounds = info?.first?[kCGWindowBounds as String] as? [String: Any],
              let x = bounds["X"] as? CGFloat,
              let y = bounds["Y"] as? CGFloat,
              let w = bounds["Width"] as? CGFloat,
              let h = bounds["Height"] as? CGFloat,
              w > 0, h > 0
        else { return nil }
        // CGWindow bounds are Quartz (top-left). Convert centre to Cocoa for warp helpers.
        let quartzMid = CGPoint(x: x + w / 2, y: y + h / 2)
        let primaryMaxY = NSScreen.screens.map(\.frame.maxY).max() ?? quartzMid.y
        return CGPoint(x: quartzMid.x, y: primaryMaxY - quartzMid.y)
    }

    /// `NSEvent.mouseLocation` / `NSScreen.frame` are Cocoa (origin bottom-left);
    /// `CGWarpMouseCursorPosition` / `CGEvent` want Quartz (origin top-left).
    private static func quartzPoint(fromCocoa cocoa: CGPoint) -> CGPoint {
        let primaryMaxY = NSScreen.screens.map(\.frame.maxY).max() ?? cocoa.y
        return CGPoint(x: cocoa.x, y: primaryMaxY - cocoa.y)
    }

    private static func warpCursor(toCocoa cocoa: CGPoint) {
        CGWarpMouseCursorPosition(quartzPoint(fromCocoa: cocoa))
    }

    private static func postMouseMoved(atCocoa cocoa: CGPoint) {
        let quartz = quartzPoint(fromCocoa: cocoa)
        if let moved = CGEvent(mouseEventSource: nil, mouseType: .mouseMoved,
                               mouseCursorPosition: quartz, mouseButton: .left) {
            moved.post(tap: .cghidEventTap)
        }
    }

    @discardableResult
    private static func postClick(atCocoa cocoa: CGPoint) -> Bool {
        let quartz = quartzPoint(fromCocoa: cocoa)
        let src = CGEventSource(stateID: .hidSystemState)
        guard let down = CGEvent(mouseEventSource: src, mouseType: .leftMouseDown,
                                 mouseCursorPosition: quartz, mouseButton: .left),
              let up = CGEvent(mouseEventSource: src, mouseType: .leftMouseUp,
                               mouseCursorPosition: quartz, mouseButton: .left)
        else { return false }
        down.post(tap: .cghidEventTap)
        up.post(tap: .cghidEventTap)
        return true
    }

    /// Pin `pid` as the front process on a single Space without clobbering other Spaces' front
    /// memory (alt-tab / yabai `SLSSpaceSetFrontPSN` — needed when an app owns several fullscreen
    /// Spaces on one display so a sibling session does not steal focus after the switch).
    @discardableResult
    static func setFrontOnSpace(_ space: Int, pid: pid_t) -> Bool {
        guard let setFront = symbols.spaceSetFrontPSN,
              let processForPID = symbols.processForPID
        else { return false }
        var psn = ProcessSerialNumber()
        guard processForPID(pid, &psn) == noErr else { return false }
        return setFront(symbols.connection, UInt64(space), psn) == .success
    }
}

// MARK: - Spaces

/// Which Mission Control Spaces are on screen right now, captured once per snapshot pass.
///
/// Multi-monitor: this is a SET — one current Space **per display**. Never treat it as a single
/// global Space.
///
/// The question this answers — "is this window on a Space I'm not looking at?" — is what tells a
/// LIVE window apart from the tombstone a just-closed window leaves in CGWindowList. Both are
/// off-screen and AX-less, but only the former sits on a Space no display is showing (a
/// tombstone stays on the Space it died on, which is on screen or was just left).
struct SpaceContext {
    let current: Set<Int>
    private let spacesOf: (CGWindowID) -> [Int]

    init(current: Set<Int>, spacesOf: @escaping (CGWindowID) -> [Int]) {
        self.current = current
        self.spacesOf = spacesOf
    }

    static func capture() -> SpaceContext {
        SpaceContext(current: SkyLight.currentSpaceIDs(), spacesOf: SkyLight.spaceIDs(ofWindow:))
    }

    /// True only when the window's Spaces are KNOWN and none of them is showing. Unknown (missing
    /// SkyLight symbols, empty answer) never claims "other Space": that would keep every ghost.
    func isOnOtherSpace(_ window: CGWindowID) -> Bool {
        guard !current.isEmpty else { return false }
        let spaces = spacesOf(window)
        return !spaces.isEmpty && current.isDisjoint(with: spaces)
    }
}
