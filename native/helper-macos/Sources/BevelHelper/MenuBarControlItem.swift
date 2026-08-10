import AppKit

/// Bevel's control status item — the anchored `NSStatusItem` whose expansion hides the real menu-bar
/// items (Strategy A). macOS lays status items out right-to-left; a very wide item pushes its neighbours
/// off the visible bar. Anchored via `autosaveName` + the private "NSStatusItem Preferred Position"
/// UserDefault (seeded before creation) so it keeps a stable, on-screen slot across length changes —
/// without the anchor macOS parks it off-screen (proven: `native/helper-macos/menubar-hide-poc.swift`).
///
/// Created but NOT installed until consolidation is enabled (U5 wires the hide/reveal RPC to `setHidden`),
/// so the menu bar is untouched by default. All AppKit; main-actor isolated.
@MainActor
final class MenuBarControlItem {
    private static let autosaveName = "BevelTrayControl"
    private static let expandedLength: CGFloat = 10_000

    private var item: NSStatusItem?
    private(set) var isHidingItems = false

    /// Installs the control item into the menu bar (idempotent). Starts collapsed (revealing).
    func install() {
        guard item == nil else { return }
        // Seed the preferred position BEFORE creating the item so macOS restores a stable slot.
        let key = "NSStatusItem Preferred Position \(Self.autosaveName)"
        if UserDefaults.standard.object(forKey: key) == nil {
            UserDefaults.standard.set(CGFloat(0), forKey: key)
        }
        let statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.autosaveName = Self.autosaveName
        statusItem.button?.image = NSImage(systemSymbolName: "chevron.left.2", accessibilityDescription: "Bevel tray")
        item = statusItem
        applyLength()
    }

    /// Removes the control item, revealing everything. Idempotent.
    func uninstall() {
        if let item { NSStatusBar.system.removeStatusItem(item) }
        item = nil
        isHidingItems = false
    }

    /// Hide (expand the control item) or reveal (collapse it) the items to its left. Installs on first
    /// hide so the bar stays pristine until consolidation is enabled.
    func setHidden(_ hidden: Bool) {
        isHidingItems = hidden
        if item == nil { install() }
        applyLength()
    }

    private func applyLength() {
        item?.length = isHidingItems ? Self.expandedLength : NSStatusItem.variableLength
    }
}

/// Minimal app delegate — its only job today is to own the control item so it survives (`NSApp.delegate`
/// is `weak`) and to mark that AppKit finished launching (status items must be created after launch).
@MainActor
final class HelperAppDelegate: NSObject, NSApplicationDelegate {
    let controlItem: MenuBarControlItem
    init(controlItem: MenuBarControlItem) { self.controlItem = controlItem }
    // Control item is installed on demand when consolidation is enabled (U5); nothing to show at launch.
    func applicationDidFinishLaunching(_ notification: Notification) {}
}
