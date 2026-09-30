import ApplicationServices
import CoreGraphics
import Foundation

// MARK: - Evidence

/// What the frontmost app's accessibility tree says about its focused window. Read ONCE per
/// snapshot pass (not once per window): the answer is per app, and each read is an IPC round-trip
/// into a foreign process.
enum FocusEvidence {
    /// AX names a focused window. It may still be a surface we don't list (an Electron app's
    /// title-less scaffolding window, a palette) — see `FocusResolver`.
    case element(AXUIElement)
    /// AX answered and said the app has NO focused window (desktop / menu bar has focus).
    case noFocusedWindow
    /// AX could not answer: API disabled for that app, timeout, hung process.
    case unavailable

    var debugLabel: String {
        switch self {
        case .element: return "element"
        case .noFocusedWindow: return "no-focused-window"
        case .unavailable: return "ax-unavailable"
        }
    }

    static func read(pid: pid_t) -> FocusEvidence {
        let axApp = AXUIElementCreateApplication(pid)
        // R18: bound the round-trip — a beachballing frontmost app must not stall the poll for the
        // default ~6s AX timeout.
        _ = _AXUIElementSetMessagingTimeout(axApp, 1.0)
        var focused: CFTypeRef?
        switch AXUIElementCopyAttributeValue(axApp, kAXFocusedWindowAttribute as CFString, &focused) {
        case .success:
            // A misbehaving AX server can hand back an unexpected CFType; verify before casting.
            guard let focused, CFGetTypeID(focused) == AXUIElementGetTypeID() else { return .unavailable }
            return .element(focused as! AXUIElement)
        case .noValue, .attributeUnsupported:
            return .noFocusedWindow
        default:
            return .unavailable
        }
    }
}

// MARK: - Resolution

/// Decides which of the foreground app's LISTED windows is the one the user is in.
///
/// Pure (no AX/CG calls of its own — evidence and the SPI lookup are injected) so every branch is
/// unit-testable. The ladder, in order:
///
/// 1. **Identity** — AX's focused element IS one of our windows (element equality; the AX map has
///    already worked around `_AXUIElementGetWindow`'s unreliability), or the SPI maps it to one.
/// 2. **Nothing focused** — AX said so explicitly: the user is on the desktop or a menu, so press
///    NOTHING (a fallback here would light up an unrelated window).
/// 3. **Unidentifiable focus** — AX named a window we don't list (Electron "ghost" surfaces) or
///    could not answer at all (apps with no AX tree). The user is unambiguously in the app, so
///    press its topmost VISIBLE window in z-order.
enum FocusResolver {
    struct Candidate {
        let cgID: CGWindowID
        let isOnScreen: Bool
        let isMinimized: Bool
        let axElement: AXUIElement?
        /// Position in the WINDOW SERVER's true front-to-back order (0 = frontmost). Only the
        /// on-screen-only CG list is a real z-order; the `.optionAll` list is not sorted by depth, so
        /// deriving "topmost" from its order pinned the same window regardless of raises.
        let zRank: Int
    }

    /// `windows` are the foreground app's listed windows, in any order (rank decides depth).
    static func focusedWindow(
        evidence: FocusEvidence,
        windows: [Candidate],
        windowIDForElement: (AXUIElement) -> CGWindowID? = spiWindowID
    ) -> CGWindowID? {
        switch evidence {
        case .noFocusedWindow:
            return nil
        case .element(let focused):
            if let hit = windows.first(where: { $0.axElement.map { CFEqual($0, focused) } ?? false }) {
                return hit.cgID
            }
            if let id = windowIDForElement(focused), windows.contains(where: { $0.cgID == id }) {
                return id
            }
            return topmostVisible(windows)
        case .unavailable:
            return topmostVisible(windows)
        }
    }

    private static func topmostVisible(_ windows: [Candidate]) -> CGWindowID? {
        windows.filter { $0.isOnScreen && !$0.isMinimized }.min { $0.zRank < $1.zRank }?.cgID
    }

    static func spiWindowID(_ element: AXUIElement) -> CGWindowID? {
        var id: CGWindowID = 0
        return _AXUIElementGetWindow(element, &id) == .success && id != 0 ? id : nil
    }
}
