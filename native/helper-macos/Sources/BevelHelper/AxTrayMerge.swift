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

    /// What the tray recorded about an AX item at enumeration: enough to recognise it again at click time.
    struct Identity: Equatable {
        let label: String
        let frame: CGRect
    }

    /// Centre slack when an item has no label and only its position identifies it. A seconds clock
    /// reflows its neighbours by a few points; real neighbours sit at least ~17 pt apart.
    static let identityTolerance: CGFloat = 12

    /// Which of an app's current AX extras is the item the user clicked (review 2026-11-21). The id is
    /// positional, so after the app adds, removes or reorders an extra, `index` may name a different
    /// item. Keep the index when it still names the recorded item; otherwise follow the item by label
    /// (or, unlabelled, by position); fail closed rather than press something the user didn't see.
    static func resolvePressTarget(index: Int, recorded: Identity?, candidates: [Identity]) -> Int? {
        guard let rec = recorded else { return nil }
        func distance(_ c: Identity) -> CGFloat {
            hypot(c.frame.midX - rec.frame.midX, c.frame.midY - rec.frame.midY)
        }
        func matches(_ c: Identity) -> Bool {
            c.label == rec.label && (!rec.label.isEmpty || distance(c) <= identityTolerance)
        }
        if candidates.indices.contains(index), matches(candidates[index]) { return index }
        let m = candidates.indices.filter { matches(candidates[$0]) }
        if m.count == 1 { return m[0] }
        let near = m.filter { distance(candidates[$0]) <= identityTolerance }
        return near.count == 1 ? near[0] : nil
    }

    /// AX actions to try, in order: a right click wants the menu first, a left click the press.
    static func pressActions(rightClick: Bool) -> [String] {
        rightClick ? ["AXShowMenu", "AXPress"] : ["AXPress", "AXShowMenu"]
    }

    enum ClickRoute: Equatable {
        /// An Accessibility-only item: press its element. It has no window, so the window-based parking
        /// (Ice's self-addressed move) cannot apply whatever `park` says.
        case axItem(pid: Int32, index: Int)
        /// A window-backed item while consolidated: relocate it to a visible slot, then press.
        case parkWindow
        /// A window-backed item on the visible bar: press it where it is.
        case window
    }

    static func clickRoute(itemID: String, park: Bool) -> ClickRoute {
        if let ax = parse(itemID: itemID) { return .axItem(pid: ax.pid, index: ax.index) }
        return park ? .parkWindow : .window
    }

    /// Where a synthesized click may land for an item whose element offers no AX action (MenuBarAgent's
    /// hosting views on 27): its centre, but only when the item is a real glyph on a visible display. An
    /// item parked off-screen has no visible point, and clicking where it used to be would hit something else.
    static func fallbackClickPoint(frame: CGRect, displays: [CGRect]) -> CGPoint? {
        guard isInMenuBarBand(frame) else { return nil }
        let centre = CGPoint(x: frame.midX, y: frame.midY)
        return displays.contains { $0.contains(centre) } ? centre : nil
    }

    static func parse(itemID: String) -> (pid: Int32, index: Int)? {
        let parts = itemID.split(separator: ":")
        guard parts.count == 3, parts[0] == "ax", let pid = Int32(parts[1]), let index = Int(parts[2]) else { return nil }
        return (pid, index)
    }
}
