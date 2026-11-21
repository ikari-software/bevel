import CoreGraphics

/// bevel-lezo. On macOS 27 menu-bar status items no longer appear in `CGWindowListCopyWindowInfo`
/// (measured on 27.3: one status-layer window left, and it is Ice's own panel), but every running app's
/// Accessibility `AXExtrasMenuBar` still lists them with position and size. On macOS 26 BOTH sources
/// list the same items: the window list (every owner is Control Center) and AX (the real apps), with
/// AX frames inset ~7 px from the window frames.
///
/// So the tray keeps the window-list items exactly as before (macOS 26 behaviour unchanged) and adds
/// only the AX items that have no window twin. Twins are matched by CENTRE, not by app (the window
/// owner is always Control Center on 26) and not by left edge (the AX inset breaks a tight x match).
/// Pure: no AppKit, no AX calls, so the 26 and 27 readings can be replayed in tests.
enum AxTrayMerge {

    struct WindowItem: Equatable {
        let windowNumber: Int
        let pid: Int32
        let frame: CGRect
    }

    struct AxItem: Equatable {
        let pid: Int32
        let appName: String
        /// Position among the app's AXExtrasMenuBar children: with the pid, the item's stable id.
        let index: Int
        let label: String
        let frame: CGRect
    }

    /// Centres within this distance are the same menu-bar item. Measured worst case on 26: ~1 px.
    /// Real neighbours are at least ~20 px apart (the narrowest item seen is 8 px wide plus spacing).
    static let centreTolerance: CGFloat = 6

    /// The same filter the window path applies: pinned near the top, menu-bar height, narrow. Drops
    /// the zero-size entries Control Center reports for hidden modules and the 5002 px control items
    /// Bevel and Ice park off-screen.
    static func isInMenuBarBand(_ r: CGRect) -> Bool {
        r.origin.y >= 0 && r.origin.y <= 40 && r.height >= 8 && r.height <= 40 && r.width >= 6 && r.width <= 400
    }

    static func sameItem(_ a: AxItem, _ w: WindowItem) -> Bool {
        abs(a.frame.midX - w.frame.midX) <= centreTolerance && abs(a.frame.midY - w.frame.midY) <= centreTolerance
    }

    /// AX items in the menu-bar band that no window-list item already covers, left to right.
    static func unmatched(ax: [AxItem], windows: [WindowItem]) -> [AxItem] {
        ax.filter { a in isInMenuBarBand(a.frame) && !windows.contains { sameItem(a, $0) } }
          .sorted { $0.frame.minX < $1.frame.minX }
    }

    /// "ax:<pid>:<index>". Distinct from the window path's "<pid>:<windowNumber>", so every consumer
    /// that parses a window number (capture, move, the self-test) skips AX items without a special case.
    static func itemID(_ a: AxItem) -> String { "ax:\(a.pid):\(a.index)" }

    static func parse(itemID: String) -> (pid: Int32, index: Int)? {
        let parts = itemID.split(separator: ":")
        guard parts.count == 3, parts[0] == "ax", let pid = Int32(parts[1]), let index = Int(parts[2]) else { return nil }
        return (pid, index)
    }
}
