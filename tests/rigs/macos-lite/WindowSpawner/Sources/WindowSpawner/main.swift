import AppKit

// ── WindowSpawner ───────────────────────────────────────────────────────────
//
// A minimal AppKit app that scripts real on-screen windows from a line protocol on
// stdin, so tests can assert the window-manager path against a KNOWN set of windows.
//
// It MUST be a `.regular` app with titled NSWindows: BevelHelper's WindowServiceImpl
// filters enumeration to `activationPolicy == .regular`, standard-window subrole,
// non-empty title, and a non-zero on-screen frame — a bare command-line tool's windows
// would be filtered out before any test could see them.
//
// Protocol (one command per line on stdin; one ack per command on stdout):
//   open <key> <x> <y> <w> <h> <title...>   → ok open <key> id=<cgWindowNumber>
//   rename <key> <title...>                 → ok rename <key>
//   close <key>                             → ok close <key>
//   list                                    → window <key> id=<n> title=<title> (×N)
//                                             then: ok list <count>
//   quit                                    → ok quit  (then the process exits)
//
// Unknown key on rename/close → `err nokey <key>`. Unparseable line → `err parse <line>`.
// `id=` is NSWindow.windowNumber, which IS the kCGWindowNumber the helper reports, so a
// driver can correlate exactly. Every ack is flushed immediately so a driver can wait
// for the window to actually exist instead of sleeping blindly.
//
// Closing stdin (EOF) is treated as `quit` so a driver that dies never leaks the rig.

/// A parsed command. Value type so it crosses the stdin-thread → main-thread hop safely.
enum Command: Sendable {
    case open(key: String, x: Double, y: Double, w: Double, h: Double, title: String)
    case rename(key: String, title: String)
    case close(key: String)
    case list
    case quit
}

/// Parses one protocol line. Pure/nonisolated — runs on the stdin reader thread.
/// The title is always the tail of the line, so it may contain spaces without quoting.
func parse(_ raw: String) -> Command? {
    let line = raw.trimmingCharacters(in: .whitespaces)
    if line.isEmpty { return nil }
    // Split into at most enough leading tokens; keep the title tail intact.
    let parts = line.split(separator: " ", omittingEmptySubsequences: true).map(String.init)
    guard let verb = parts.first else { return nil }

    switch verb {
    case "open":
        // open <key> <x> <y> <w> <h> <title...>
        guard parts.count >= 7,
              let x = Double(parts[2]), let y = Double(parts[3]),
              let w = Double(parts[4]), let h = Double(parts[5]) else { return nil }
        let title = parts[6...].joined(separator: " ")
        return .open(key: parts[1], x: x, y: y, w: w, h: h, title: title)
    case "rename":
        // rename <key> <title...>
        guard parts.count >= 3 else { return nil }
        return .rename(key: parts[1], title: parts[2...].joined(separator: " "))
    case "close":
        guard parts.count == 2 else { return nil }
        return .close(key: parts[1])
    case "list":
        return .list
    case "quit":
        return .quit
    default:
        return nil
    }
}

/// Prints one ack line and flushes so the driver sees it immediately.
func emit(_ line: String) {
    print(line)
    fflush(stdout)
}

@MainActor
final class Spawner {
    private var windows: [String: NSWindow] = [:]

    func handle(_ command: Command) {
        switch command {
        case let .open(key, x, y, w, h, title):
            open(key: key, x: x, y: y, w: w, h: h, title: title)
        case let .rename(key, title):
            rename(key: key, title: title)
        case let .close(key):
            close(key: key)
        case .list:
            list()
        case .quit:
            quit()
        }
    }

    private func open(key: String, x: Double, y: Double, w: Double, h: Double, title: String) {
        // Reuse the slot if the key is already open (idempotent-ish for retries): close first.
        if let existing = windows[key] {
            existing.close()
            windows[key] = nil
        }
        let window = NSWindow(
            contentRect: NSRect(x: x, y: y, width: w, height: h),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false)
        window.title = title
        window.isReleasedWhenClosed = false
        window.tabbingMode = .disallowed
        // orderFrontRegardless: appear even when the rig isn't the active app, so a test
        // driving it in the background still gets a real on-screen window to enumerate.
        window.orderFrontRegardless()
        windows[key] = window
        emit("ok open \(key) id=\(window.windowNumber)")
    }

    private func rename(key: String, title: String) {
        guard let window = windows[key] else { emit("err nokey \(key)"); return }
        window.title = title
        emit("ok rename \(key)")
    }

    private func close(key: String) {
        guard let window = windows.removeValue(forKey: key) else { emit("err nokey \(key)"); return }
        // orderOut then close, and let the local `window` (now the last strong reference)
        // drop on return so the NSWindow deallocs and its window-server window leaves
        // CGWindowList promptly. Enumeration uses `.optionAll`, which includes OFF-SCREEN
        // windows (so minimized windows stay listed) — a closed-but-not-yet-deallocated
        // window would otherwise keep showing up until AppKit gets around to freeing it.
        window.orderOut(nil)
        window.close()
        emit("ok close \(key)")
    }

    private func list() {
        for (key, window) in windows {
            emit("window \(key) id=\(window.windowNumber) title=\(window.title)")
        }
        emit("ok list \(windows.count)")
    }

    private func quit() {
        for window in windows.values { window.close() }
        windows.removeAll()
        emit("ok quit")
        NSApp.terminate(nil)
    }
}

// ── Entry point ─────────────────────────────────────────────────────────────
// The Spawner and all NSWindow work stay on the main thread; a background thread does
// the blocking stdin reads and hands parsed (Sendable) commands back to main. The
// delegate owns the Spawner; the reader thread never captures it — it looks the delegate
// up via NSApp.delegate inside the main-actor hop, so the Sendable thread closure only
// ever carries Sendable values (the parsed Command / the raw line).

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    let spawner = Spawner()

    func applicationDidFinishLaunching(_ notification: Notification) {
        startStdinReader()
        emit("ready")
    }

    private func startStdinReader() {
        let reader = Thread {
            while let line = readLine(strippingNewline: true) {
                let parsed = parse(line)
                DispatchQueue.main.async {
                    MainActor.assumeIsolated {
                        guard let spawner = (NSApp.delegate as? AppDelegate)?.spawner else { return }
                        if let command = parsed { spawner.handle(command) }
                        else { emit("err parse \(line)") }
                    }
                }
            }
            // stdin closed (driver exited) → quit so we never leak the rig.
            DispatchQueue.main.async {
                MainActor.assumeIsolated { (NSApp.delegate as? AppDelegate)?.spawner.handle(.quit) }
            }
        }
        reader.stackSize = 1 << 20
        reader.start()
    }
}

let app = NSApplication.shared
app.setActivationPolicy(.regular)
let delegate = AppDelegate()
app.delegate = delegate
app.run()
