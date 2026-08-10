#!/usr/bin/env swift
//
// Capture-while-hidden spike — bevel-7hf4, the make-or-break for Strategy C.
//
// Question: can we capture a status-item window BY ID while it's hidden off-screen? If yes, C works
// (hide the crowd from the real bar, still show real live icons in Bevel's tray). If the hidden
// capture comes back empty/transparent, C collapses to a glanceable-only mirror.
//
// Method: create our anchored control item (proven), capture a real third-party status item WHILE
// VISIBLE (-> /tmp/cap-before.png), then expand our control item to push it off-screen and capture
// the SAME window ID again (-> /tmp/cap-after.png). Compare the two PNGs + the non-transparent pixel
// counts. Must run as a signed .app bundle (status items need bundle identity + Screen Recording).
//
import AppKit
import ScreenCaptureKit

func log(_ s: String) { FileHandle.standardError.write(Data(("[cap] " + s + "\n").utf8)); fflush(stderr) }

struct Win { let wid: CGWindowID; let owner: String; let pid: pid_t; let x, y, w, h: CGFloat }

func statusWindows(onScreenOnly: Bool) -> [Win] {
    let opts: CGWindowListOption = onScreenOnly ? [.optionOnScreenOnly, .excludeDesktopElements] : [.excludeDesktopElements]
    guard let infos = CGWindowListCopyWindowInfo(opts, kCGNullWindowID) as? [[String: Any]] else { return [] }
    var out: [Win] = []
    for w in infos {
        guard
            let layer = w[kCGWindowLayer as String] as? Int, layer == 25,
            let wid = w[kCGWindowNumber as String] as? Int,
            let b = w[kCGWindowBounds as String] as? [String: CGFloat],
            let x = b["X"], let y = b["Y"], let ww = b["Width"], let hh = b["Height"]
        else { continue }
        let owner = (w[kCGWindowOwnerName as String] as? String) ?? "?"
        let pid = pid_t((w[kCGWindowOwnerPID as String] as? Int) ?? 0)
        out.append(Win(wid: CGWindowID(wid), owner: owner, pid: pid, x: x, y: y, w: ww, h: hh))
    }
    return out
}

func frame(of wid: CGWindowID) -> String {
    if let w = statusWindows(onScreenOnly: false).first(where: { $0.wid == wid }) {
        return "(\(w.x),\(w.y),\(w.w),\(w.h))"
    }
    return "not-listed"
}

func captureByID(_ windowID: CGWindowID, to path: String) async -> Bool {
    do {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        guard let win = content.windows.first(where: { $0.windowID == windowID }) else {
            log("  window \(windowID) NOT in SCShareableContent — hidden windows may be excluded"); return false
        }
        let f = win.frame
        let cfg = SCStreamConfiguration()
        cfg.width = max(2, Int(f.width * 2)); cfg.height = max(2, Int(f.height * 2))
        cfg.showsCursor = false; cfg.ignoreShadowsSingleWindow = true
        let img = try await SCScreenshotManager.captureImage(contentFilter: SCContentFilter(desktopIndependentWindow: win), configuration: cfg)
        guard let png = NSBitmapImageRep(cgImage: img).representation(using: .png, properties: [:]) else { log("  png encode failed"); return false }
        try png.write(to: URL(fileURLWithPath: path))
        var opaque = 0
        if let data = img.dataProvider?.data, let ptr = CFDataGetBytePtr(data) {
            let n = CFDataGetLength(data); var i = 3
            while i < n { if ptr[i] > 12 { opaque += 1 }; i += 4 }
        }
        log("  OK wid=\(windowID) scWinFrame=\(f) -> \(path) (\(img.width)x\(img.height), ~\(opaque) non-transparent px)")
        return true
    } catch { log("  capture error: \(error)"); return false }
}

final class Del: NSObject, NSApplicationDelegate {
    var item: NSStatusItem!
    func applicationDidFinishLaunching(_ n: Notification) {
        if !CGPreflightScreenCaptureAccess() { _ = CGRequestScreenCaptureAccess() }

        let name = "BevelCapPoc"
        let key = "NSStatusItem Preferred Position \(name)"
        if UserDefaults.standard.object(forKey: key) == nil { UserDefaults.standard.set(CGFloat(0), forKey: key) }
        item = NSStatusBar.system.statusItem(withLength: 40)
        item.autosaveName = name
        item.button?.title = "CAP"

        Task { @MainActor in
            try? await Task.sleep(nanoseconds: 600_000_000)
            // On macOS 26 EVERY item is owned by the "Control Centre" pid, so we can't filter by pid,
            // and NSWindow.windowNumber can be negative (traps CGWindowID()). Identify OUR item by its
            // frame x instead, and target one to our LEFT so our expansion pushes it off-screen.
            let ourX = item.button?.window?.frame.origin.x ?? 99999
            log("our control item x=\(ourX)")
            let candidates = statusWindows(onScreenOnly: true).filter { abs($0.x - ourX) > 3 && $0.w > 8 && $0.x < ourX - 3 }
            log("candidate items to our left: \(candidates.count)")
            for w in candidates.sorted(by: { $0.x < $1.x }).prefix(8) { log("  wid=\(w.wid) x=\(w.x) w=\(w.w)") }
            guard let t = candidates.min(by: { $0.x < $1.x }) else { log("no item to our left — cannot test hide"); return }
            log("TARGET wid=\(t.wid) owner=\(t.owner) visibleFrame=(\(t.x),\(t.y),\(t.w),\(t.h))")

            log("--- capture WHILE VISIBLE ---")
            _ = await captureByID(t.wid, to: "/tmp/cap-before.png")

            log("--- expand control item to 10000 (hide) ---")
            item.length = 10_000
            try? await Task.sleep(nanoseconds: 1_500_000_000)
            log("TARGET after-hide frame = \(frame(of: t.wid))  (off-screen x<0 or x>screen => it's hidden)")

            log("--- capture WHILE HIDDEN (same wid) ---")
            _ = await captureByID(t.wid, to: "/tmp/cap-after.png")

            item.length = 40
            log("DONE. Compare /tmp/cap-before.png vs /tmp/cap-after.png (and the px counts above).")
        }
    }
}

let app = NSApplication.shared
let d = Del(); app.delegate = d
app.setActivationPolicy(.accessory)
app.run()
