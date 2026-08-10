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

// CGWindowListCreateImage is `unavailable` on macOS 26 and even Ice's protocol trick is now a hard
// error — but the C function CGWindowListCreateImageFromArray still exists in CoreGraphics. Reach it via
// dlsym (bypasses the compile-time availability check). It reads the backing store by ID — works for
// FULLY off-screen windows (where SCK returns -3811).
typealias WLCIFA = @convention(c) (CGRect, CFArray, UInt32) -> Unmanaged<CGImage>?
let _cgh = dlopen(nil, RTLD_NOW)
let CGWindowListCreateImageFromArray = dlsym(_cgh, "CGWindowListCreateImageFromArray").map { unsafeBitCast($0, to: WLCIFA.self) }

func captureLegacy(_ windowID: CGWindowID, to path: String) -> Bool {
    guard let fn = CGWindowListCreateImageFromArray else { log("  symbol CGWindowListCreateImageFromArray NOT FOUND"); return false }
    let ptr = UnsafeMutablePointer<UnsafeRawPointer?>.allocate(capacity: 1)
    defer { ptr.deallocate() }
    ptr[0] = UnsafeRawPointer(bitPattern: UInt(windowID))
    guard let arr = CFArrayCreate(kCFAllocatorDefault, ptr, 1, nil) else { log("  CFArray fail"); return false }
    let option = CGWindowImageOption([.boundsIgnoreFraming, .bestResolution]).rawValue
    guard let img = fn(.null, arr, option)?.takeRetainedValue() else {
        log("  LEGACY nil for wid=\(windowID)"); return false
    }
    guard let png = NSBitmapImageRep(cgImage: img).representation(using: .png, properties: [:]) else { return false }
    try? png.write(to: URL(fileURLWithPath: path))
    var opaque = 0
    if let data = img.dataProvider?.data, let ptr2 = CFDataGetBytePtr(data) {
        let n = CFDataGetLength(data); var i = 3
        while i < n { if ptr2[i] > 12 { opaque += 1 }; i += 4 }
    }
    log("  LEGACY OK wid=\(windowID) -> \(path) (\(img.width)x\(img.height), ~\(opaque) non-transparent px)")
    return opaque > 4
}

func captureByID(_ windowID: CGWindowID, to path: String) async -> Bool {
    for attempt in 1...4 {
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
            log("  OK wid=\(windowID) attempt=\(attempt) scWinFrame=\(f) -> \(path) (\(img.width)x\(img.height), ~\(opaque) non-transparent px)")
            return true
        } catch {
            log("  wid=\(windowID) attempt \(attempt) error: \((error as NSError).code) \(error.localizedDescription)")
            try? await Task.sleep(nanoseconds: 450_000_000)
        }
    }
    return false
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

            // Close the combo: find a THIRD-PARTY item the hide pushed FULLY off-screen (x<0 or x>=screenW),
            // not merely occluded, and capture it there. Our expanded control item is width ~5002 (>1000),
            // so the w<1000 filter excludes it.
            let screenW = NSScreen.main?.frame.width ?? 1728
            let offscreen = statusWindows(onScreenOnly: false).filter { $0.w > 8 && $0.w < 1000 && ($0.x < -4 || $0.x >= screenW) }
            log("fully off-screen third-party items after hide: \(offscreen.count)")
            for w in offscreen.prefix(4) { log("  wid=\(w.wid) x=\(w.x) w=\(w.w)") }
            if !offscreen.isEmpty {
                var sck = 0, legacy = 0
                for (i, off) in offscreen.prefix(4).enumerated() {
                    log("--- FULLY OFF-SCREEN third-party (wid=\(off.wid) x=\(off.x)) ---")
                    if await captureByID(off.wid, to: "/tmp/cap-off-sck-\(i).png") { sck += 1 }        // SCK (expect fail)
                    if captureLegacy(off.wid, to: "/tmp/cap-offscreen-\(i).png") { legacy += 1 }        // Ice's CG API (expect OK)
                }
                let n = min(4, offscreen.count)
                log("off-screen: SCK \(sck)/\(n), LEGACY(CGWindowListCreateImage) \(legacy)/\(n)")
            } else {
                log("none fully off-screen — Tahoe reflows third-party items to OCCLUDED on-screen instead (that combo already proven)")
            }

            item.length = 40
            log("DONE. Compare cap-before / cap-after / cap-offscreen (+ the px counts above).")
        }
    }
}

let app = NSApplication.shared
let d = Del(); app.delegate = d
app.setActivationPolicy(.accessory)
app.run()
