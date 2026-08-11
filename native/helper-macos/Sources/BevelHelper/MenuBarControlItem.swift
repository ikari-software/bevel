import AppKit

/// TEMP diagnostic (bevel-7hf4): append a line to a fixed log so we can see the consolidation chain
/// land in the packaged app, where helper stderr isn't easily captured. Remove once validated.
func consolidationLog(_ message: String) {
    let path = "/tmp/bevel-consolidation.log"
    if !FileManager.default.fileExists(atPath: path) {
        FileManager.default.createFile(atPath: path, contents: nil)
    }
    if let fh = FileHandle(forWritingAtPath: path) {
        fh.seekToEndOfFile()
        fh.write(("[helper] " + message + "\n").data(using: .utf8) ?? Data())
        try? fh.close()
    }
}

/// Bevel's control status item — the anchored `NSStatusItem` whose expansion hides the real menu-bar
/// items (Strategy A). macOS lays status items out right-to-left; a very wide item pushes its neighbours
/// off the visible bar. Anchored via `autosaveName` + the private "NSStatusItem Preferred Position"
/// UserDefault (seeded before creation) so it keeps a stable slot across length changes.
///
/// CONCURRENCY (bevel-7hf4, hard-won): the item is **created once at launch** on the main thread. Creating
/// an `NSStatusItem` LATER from a dispatched main-queue block DEADLOCKS — on macOS 26 the status-bar
/// creation does synchronous IPC that needs the run loop, but a dispatched block occupies the run loop
/// (proven live: setHidden entered on the main thread and never returned). So: create at launch, then only
/// TOGGLE LENGTH, applied from a repeating run-loop **Timer** (a natural run-loop callback, not a dispatched
/// block) driven by a thread-safe `pending` flag the gRPC handler sets and returns immediately. NOT
/// `@MainActor` (the helper's main-actor executor isn't serviced by NSApp.run()); `@unchecked Sendable`
/// reflects the "touch AppKit only from the main run loop" contract.
final class MenuBarControlItem: @unchecked Sendable {
    private static let autosaveName = "BevelTrayControl"
    private static let expandedLength: CGFloat = 10_000

    private var item: NSStatusItem?
    private var applied = false
    private let lock = NSLock()
    private var pending: Bool?
    private var timer: Timer?

    /// True while hiding the neighbours (the last applied state). Plain read; fine to sample racily.
    private(set) var isHidingItems = false
    /// Our own window ID, cached on the main thread, for the tray enumerator's self-exclusion (U3).
    private(set) var cachedWindowID: CGWindowID?

    /// Create the status item and start the apply poller. MUST run on the main thread at launch.
    func startOnMain() {
        install()
        // Apply the pending state from a repeating run-loop timer — a natural run-loop callback, so the
        // (light) length toggle behaves like the launch context, unlike a dispatched block.
        let t = Timer(timeInterval: 0.2, repeats: true) { [weak self] _ in self?.applyPending() }
        RunLoop.main.add(t, forMode: .common)
        timer = t
        consolidationLog("startOnMain: control item created, poller running (windowID=\(cachedWindowID.map(String.init) ?? "nil"))")
    }

    private func install() {
        guard item == nil else { return }
        let key = "NSStatusItem Preferred Position \(Self.autosaveName)"
        if UserDefaults.standard.object(forKey: key) == nil { UserDefaults.standard.set(CGFloat(0), forKey: key) }
        let statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.autosaveName = Self.autosaveName
        statusItem.button?.image = NSImage(systemSymbolName: "chevron.left.2", accessibilityDescription: "Bevel tray")
        item = statusItem
        cachedWindowID = windowID(of: statusItem)
    }

    /// Request a hidden state from ANY thread (the gRPC handler). Cheap, non-blocking — the timer applies it.
    func requestHidden(_ hidden: Bool) {
        lock.lock(); pending = hidden; lock.unlock()
    }

    /// Main-run-loop applier: toggles the item's length only (no creation), so it can't deadlock.
    private func applyPending() {
        lock.lock(); let want = pending; pending = nil; lock.unlock()
        guard let want, !(applied && want == isHidingItems) else { return }
        applied = true
        isHidingItems = want
        item?.length = want ? Self.expandedLength : NSStatusItem.variableLength
        cachedWindowID = item.map(windowID(of:)) ?? cachedWindowID
        consolidationLog("applyPending: hidden=\(want) length=\(item?.length ?? -1) windowID=\(cachedWindowID.map(String.init) ?? "nil")")
    }

    private func windowID(of item: NSStatusItem) -> CGWindowID? {
        guard let n = item.button?.window?.windowNumber, n > 0 else { return nil }
        return CGWindowID(n)
    }
}

/// Minimal app delegate — creates the control item at launch (the only context where NSStatusItem
/// creation is safe) and owns it so it survives (`NSApp.delegate` is `weak`).
@MainActor
final class HelperAppDelegate: NSObject, NSApplicationDelegate {
    let controlItem: MenuBarControlItem
    init(controlItem: MenuBarControlItem) { self.controlItem = controlItem }
    func applicationDidFinishLaunching(_ notification: Notification) {
        // DISABLED: creating/owning the control NSStatusItem in the helper process wedges it — every
        // status-bar operation hangs (the helper's NSApp.run() isn't a real app lifecycle, so the
        // status-bar IPC deadlocks). The control item must move to the main Bevel app (Avalonia, real
        // AppKit lifecycle) via ObjC interop. Until then this stays off so the helper boots + mirrors
        // the tray normally. See bevel-7hf4 follow-up. Do NOT call controlItem.startOnMain() here.
    }
}
