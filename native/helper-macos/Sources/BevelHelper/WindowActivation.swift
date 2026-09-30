import AppKit
import ApplicationServices
import CoreGraphics

/// A window the taskbar asked us to bring forward, with everything the activation ladder needs.
struct WindowTarget {
    let pid: pid_t
    let cgID: CGWindowID
    /// The window-level AX element; nil for apps with no AX tree and for windows on other Spaces
    /// (AX only lists the current Space's windows).
    let axWin: AXUIElement?
    /// On a Space no display is showing right now — activating it must switch Spaces.
    let isOnOtherSpace: Bool
}

enum ActivationStep: Equatable {
    /// App-level activation (`kAXFrontmost` + `NSRunningApplication.activate`). The only step that
    /// works when Bevel's high-level taskbar holds key focus (bevel-nxic).
    case activateApp
    /// Address the window by its WindowServer id (SkyLight): works without AX, and crosses Spaces.
    case focusViaWindowServer
    /// `AXRaise` — reorders within the app; must come last so the clicked window ends topmost.
    case raiseViaAX
}

/// Which steps activate which kind of window, and in what order. Pure, so the decision table is
/// unit-testable without a window server.
enum ActivationPlan {
    static func steps(hasAX: Bool, isOnOtherSpace: Bool) -> [ActivationStep] {
        if isOnOtherSpace {
            // App-level activation would land on whichever Space macOS picks for the app; only the
            // window-addressed call can go to THIS window's Space.
            return hasAX ? [.focusViaWindowServer, .raiseViaAX] : [.focusViaWindowServer]
        }
        // No AX element means AXRaise is impossible (Kiro-class apps): activate the app, then make
        // the specific window key through the WindowServer instead.
        return hasAX ? [.activateApp, .raiseViaAX] : [.activateApp, .focusViaWindowServer]
    }
}

enum WindowActivator {
    static func perform(_ target: WindowTarget) {
        for step in ActivationPlan.steps(hasAX: target.axWin != nil, isOnOtherSpace: target.isOnOtherSpace) {
            switch step {
            case .activateApp:
                activateApp(pid: target.pid)
            case .focusViaWindowServer:
                // SkyLight unavailable → plain app activation still gets the user into the app.
                if !SkyLight.focusWindow(pid: target.pid, cgID: target.cgID) {
                    activateApp(pid: target.pid)
                }
            case .raiseViaAX:
                if let axWin = target.axWin {
                    _ = AXUIElementPerformAction(axWin, kAXRaiseAction as CFString)
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
}
