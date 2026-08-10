#!/usr/bin/env swift
//
// C3 retry — bevel-7hf4. The first C3 spike lacked a POSITIVE CONTROL: it never proved CGSMoveWindow
// moves anything at all from our process, so "moves nothing" could have been a binding/coords bug, not
// a real restriction. This retry moves three targets and logs err codes + before/after frames:
//   1. our OWN plain NSWindow   (positive control — must move if the API works & our usage is right)
//   2. our OWN status item      (a status window we created)
//   3. a THIRD-PARTY status item
// Also tries SLSMoveWindow (SkyLight) as well as CGSMoveWindow. On macOS 26 all status items report
// owner "Control Centre" — the point is to see whether that cross-connection ownership is what blocks.
//
// Run as the bundle: /tmp/C3Retry.app/Contents/MacOS/C3Retry 2>/tmp/c3retry.log
//
import AppKit

func log(_ s: String) { FileHandle.standardError.write(Data(("[c3] " + s + "\n").utf8)); fflush(stderr) }

typealias ConnFn = @convention(c) () -> Int32
typealias MoveFn = @convention(c) (Int32, UInt32, UnsafePointer<CGPoint>) -> Int32
let h = dlopen(nil, RTLD_NOW)
func sym<T>(_ name: String, _ t: T.Type) -> T? {
    guard let p = dlsym(h, name) else { log("symbol \(name) NOT FOUND"); return nil }
    return unsafeBitCast(p, to: T.self)
}
let CGSMainConnectionID = sym("CGSMainConnectionID", ConnFn.self)
let CGSMoveWindow = sym("CGSMoveWindow", MoveFn.self)
let SLSMoveWindow = sym("SLSMoveWindow", MoveFn.self)

func cgFrame(_ wid: CGWindowID) -> CGRect? {
    guard let infos = CGWindowListCopyWindowInfo([], kCGNullWindowID) as? [[String: Any]] else { return nil }
    for w in infos where UInt32(exactly: (w[kCGWindowNumber as String] as? Int) ?? -1) == wid {
        if let b = w[kCGWindowBounds as String] as? [String: CGFloat],
           let x = b["X"], let y = b["Y"], let ww = b["Width"], let hh = b["Height"] {
            return CGRect(x: x, y: y, width: ww, height: hh)
        }
    }
    return nil
}

func toWID(_ n: Int?) -> CGWindowID? {
    guard let n, let u = UInt32(exactly: n) else { return nil }
    return CGWindowID(u)
}

func statusWins() -> [(wid: CGWindowID, owner: String, x: CGFloat, w: CGFloat)] {
    guard let infos = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] else { return [] }
    var out: [(CGWindowID, String, CGFloat, CGFloat)] = []
    for w in infos {
        guard let layer = w[kCGWindowLayer as String] as? Int, layer == 25,
              let wid = toWID(w[kCGWindowNumber as String] as? Int),
              let b = w[kCGWindowBounds as String] as? [String: CGFloat], let x = b["X"], let ww = b["Width"] else { continue }
        out.append((wid, (w[kCGWindowOwnerName as String] as? String) ?? "?", x, ww))
    }
    return out
}

func tryMove(_ label: String, _ wid: CGWindowID, to target: CGPoint) {
    let cid = CGSMainConnectionID?() ?? 0
    let before = cgFrame(wid)
    var p = target
    let e1 = CGSMoveWindow?(cid, wid, &p) ?? -999
    usleep(250_000)
    let afterCGS = cgFrame(wid)
    var p2 = target
    let e2 = SLSMoveWindow?(cid, wid, &p2) ?? -999
    usleep(250_000)
    let afterSLS = cgFrame(wid)
    log("\(label) wid=\(wid) before=\(before.map { "\($0)" } ?? "?") target=\(target)")
    log("   CGSMoveWindow err=\(e1) -> \(afterCGS.map { "\($0)" } ?? "?")")
    log("   SLSMoveWindow err=\(e2) -> \(afterSLS.map { "\($0)" } ?? "?")")
    log("   MOVED: \((before != afterCGS || before != afterSLS) ? "YES" : "no")")
}

final class Del: NSObject, NSApplicationDelegate {
    var statusItem: NSStatusItem!
    var plain: NSWindow!
    func applicationDidFinishLaunching(_ n: Notification) {
        let cid = CGSMainConnectionID?() ?? 0
        log("cid=\(cid) CGSMoveWindow=\(CGSMoveWindow != nil) SLSMoveWindow=\(SLSMoveWindow != nil)")

        plain = NSWindow(contentRect: NSRect(x: 300, y: 500, width: 240, height: 130), styleMask: [.titled], backing: .buffered, defer: false)
        plain.title = "C3 control window"
        plain.makeKeyAndOrderFront(nil)

        statusItem = NSStatusBar.system.statusItem(withLength: 40)
        statusItem.button?.title = "C3"

        Task { @MainActor in
            try? await Task.sleep(nanoseconds: 800_000_000)

            log("=== 1. POSITIVE CONTROL: our own plain NSWindow ===")
            if let w = toWID(plain.windowNumber) { tryMove("plainWindow", w, to: CGPoint(x: 700, y: 250)) }
            else { log("plain windowNumber not a valid CGWindowID: \(plain.windowNumber)") }

            let ourX = statusItem.button?.window?.frame.origin.x ?? 99999
            let wins = statusWins()
            log("ourX=\(ourX); \(wins.count) status windows")

            log("=== 2. our OWN status item (found via CGWindowList, x≈ourX) ===")
            if let ours = wins.min(by: { abs($0.x - ourX) < abs($1.x - ourX) }), abs(ours.x - ourX) < 4 {
                tryMove("ourStatusItem", ours.wid, to: CGPoint(x: ours.x, y: 400))
            } else { log("couldn't locate our own status window near ourX=\(ourX)") }

            log("=== 3. a THIRD-PARTY status item ===")
            if let other = wins.first(where: { abs($0.x - ourX) > 4 && $0.w > 8 }) {
                log("target owner=\(other.owner) x=\(other.x)")
                tryMove("thirdParty", other.wid, to: CGPoint(x: other.x, y: 400))
            } else { log("no third-party status window found") }

            log("DONE. Did the 'C3 control window' jump? Did our C3 icon or any status icon move?")
        }
    }
}

let app = NSApplication.shared
let d = Del(); app.delegate = d
app.setActivationPolicy(.regular)
app.run()
