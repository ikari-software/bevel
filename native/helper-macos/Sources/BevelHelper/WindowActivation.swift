import AppKit
import ApplicationServices
import CoreGraphics
import Foundation

/// A window the taskbar asked us to bring forward, with everything the activation ladder needs.
struct WindowTarget {
    let pid: pid_t
    let cgID: CGWindowID
    /// The window-level AX element; nil for apps with no AX tree and for windows on other Spaces
    /// (AX only lists the current Space's windows).
    let axWin: AXUIElement?
    /// On a Space no display is showing right now — activating it must switch Spaces.
    /// Multi-monitor: compared against the SET of per-display current Spaces.
    let isOnOtherSpace: Bool
}

enum ActivationStep: Equatable {
    /// Switch the owning display onto this window's Space (Show/Hide/SetCurrent).
    /// Required because `focusViaWindowServer` does not switch Spaces.
    case switchSpace
    /// App-level activation (`kAXFrontmost` + `NSRunningApplication.activate`). Needed when
    /// Bevel's non-activating taskbar holds the click (bevel-nxic) — but alone it fronts the
    /// *app*, so a multi-Space app can flash the wrong sibling; always follow with
    /// `focusViaWindowServer` when we have a window id.
    case activateApp
    /// Address the window by its WindowServer id (SkyLight): works without AX; does not switch Spaces.
    case focusViaWindowServer
    /// `AXRaise` — reorders within the app; must come last so the clicked window ends topmost.
    case raiseViaAX
}

/// Which steps activate which kind of window, and in what order. Pure, so the decision table is
/// unit-testable without a window server.
enum ActivationPlan {
    static func steps(hasAX: Bool, isOnOtherSpace: Bool) -> [ActivationStep] {
        if isOnOtherSpace {
            // App-level activation would land on whichever Space macOS picks for the app; only an
            // explicit Space switch + window-addressed focus reaches THIS window's Space.
            return hasAX
                ? [.switchSpace, .focusViaWindowServer, .raiseViaAX]
                : [.switchSpace, .focusViaWindowServer]
        }
        // Always window-address after app activate: bare activateApp lets a multi-window app
        // (several fullscreen Spaces on one display) front the wrong sibling.
        return hasAX
            ? [.activateApp, .focusViaWindowServer, .raiseViaAX]
            : [.activateApp, .focusViaWindowServer]
    }
}

enum WindowActivator {
    private static let debugWindows = ProcessInfo.processInfo.environment["BEVEL_DEBUG_WINDOWS"] == "1"

    static func perform(_ target: WindowTarget) {
        let steps = ActivationPlan.steps(hasAX: target.axWin != nil, isOnOtherSpace: target.isOnOtherSpace)
        dbg("activate cg=\(target.cgID) pid=\(target.pid) otherSpace=\(target.isOnOtherSpace) hasAX=\(target.axWin != nil) steps=\(steps)")
        for step in steps {
            switch step {
            case .switchSpace:
                let r = SkyLight.switchToSpace(ofWindow: target.cgID)
                dbg("  switchSpace → ok=\(r.ok) already=\(r.alreadyCurrent) focused=\(r.displayFocused) clicked=\(r.clicked) display=\(r.displayUUID ?? "?") \(r.fromSpace.map(String.init) ?? "?")->\(r.toSpace.map(String.init) ?? "?") currents=\(SkyLight.currentSpaceIDs().sorted())")
            case .activateApp:
                activateApp(pid: target.pid)
                dbg("  activateApp")
            case .focusViaWindowServer:
                // Address THIS window by WindowServer id. Do not follow with setFrontOnSpace —
                // that re-activates the app PSN and can fire didActivateApplication while AX still
                // reports a sibling as focused (sibling_steal via broadcastFocusForApp).
                let ok = SkyLight.focusWindow(pid: target.pid, cgID: target.cgID)
                dbg("  focusViaWindowServer → \(ok)")
                if !ok {
                    activateApp(pid: target.pid)
                    dbg("  focusViaWindowServer fallback activateApp")
                }
            case .raiseViaAX:
                if let axWin = target.axWin {
                    let rc = AXUIElementPerformAction(axWin, kAXRaiseAction as CFString)
                    dbg("  raiseViaAX → \(rc.rawValue)")
                }
            }
        }
    }

    /// `kAXFrontmostAttribute` is the accessibility-native activation; `NSRunningApplication.activate`
    /// is the belt-and-suspenders for apps whose app-level AX is also restricted (Apple Music).
    private static func activateApp(pid: pid_t) {
        let appElement = AXUIElementCreateApplication(pid)
        _ = _AXUIElementSetMessagingTimeout(appElement, 1.0)   // R18: never block on a hung target
        AXUIElementSetAttributeValue(appElement, kAXFrontmostAttribute as CFString, kCFBooleanTrue)
        NSRunningApplication(processIdentifier: pid)?.activate()
    }

    private static func dbg(_ msg: @autoclosure () -> String) {
        guard debugWindows else { return }
        FileHandle.standardError.write(Data(("[BEVEL-WIN] " + msg() + "\n").utf8))
    }
}
