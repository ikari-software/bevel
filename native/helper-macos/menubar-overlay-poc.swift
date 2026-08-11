// menubar-overlay-poc.swift — OVERLAY-HIDE proof-of-concept (bevel Strategy C, live-updates fix)
//
// WHAT THIS TESTS
// ---------------
// Strategy C consolidates the macOS menu-bar extras into Bevel's bottom tray by HIDING the real
// items and SHOWING captured copies. The current hide pushes items FULLY off-screen; once off every
// display macOS freezes their backing store, so `CGWindowListCreateImageFromArray` returns identical
// bytes forever and the tray copies never update (clock, battery %, iStat graphs go stale).
//
// OVERLAY-HIDE keeps the real items ON-SCREEN (so they keep redrawing) but VISUALLY COVERS them with
// an opaque Bevel window. This PoC answers the two make-or-break questions live:
//
//   Q1  Does a borderless window at level (statusBar + 1) actually COVER the status-item strip on
//       macOS 26 (Tahoe), or does the system menu bar always draw on top?   → eyeball the strip.
//   Q2  Do the covered items KEEP REDRAWING (so captures stay live), or does full occlusion freeze
//       them like the off-screen push does?   → watch the per-item byte-hash column: a live item
//       (clock ticks each minute, iStat/battery each second) must keep printing CHANGED while it is
//       COVERED. If it prints SAME forever under the overlay, occlusion froze it (overlay-hide fails
//       for that item); if it keeps changing, overlay-hide works.
//
// HOW TO RUN
// ----------
//   swiftc -O menubar-overlay-poc.swift -o /tmp/overlay-poc && /tmp/overlay-poc
//
// A bare executable is fine here (unlike the NSStatusItem PoC, which needed a .app bundle) — an
// overlay is an ordinary NSWindow, not a status item. It runs as an .accessory app (no Dock icon).
// Byte-hashing needs the Screen Recording grant (same as the real tray capture); without it the Q2
// column stays blank but Q1 (coverage) still works. Grant Terminal/the built binary Screen Recording
// if the hashes read 0.  Ctrl-C to quit; the overlay is removed on exit.
//
// WHAT TO LOOK FOR
// ----------------
//   * The right ~520pt of the menu bar should be covered by a translucent RED strip (kept see-through
//     ONLY so you can watch the items redraw underneath during the test — production paints it opaque).
//   * Any OPEN menu (click a still-visible item, or Spotlight) must appear ABOVE the red strip — that
//     proves level 26 sits below pop-up menus (101), i.e. reveal-by-click will still show real menus.
//   * The console prints one line/second per status item: its window id, whether it is under the
//     overlay (COVERED/clear), and CHANGED/SAME vs the previous capture.

import AppKit
import CoreGraphics

// ── Legacy backing-store capture (same dlsym path Bevel's helper uses) ────────────────────────────
// CGWindowListCreateImageFromArray reads a window's backing store by id regardless of occlusion; the
// symbol is `unavailable` in the macOS 26 SDK, so reach it via dlsym + a @convention(c) typealias.
enum LegacyWindowCapture {
    private typealias CreateFromArray = @convention(c) (CGRect, CFArray, UInt32) -> Unmanaged<CGImage>?
    private static let create: CreateFromArray? = {
        guard let handle = dlopen(nil, RTLD_NOW),
              let sym = dlsym(handle, "CGWindowListCreateImageFromArray") else { return nil }
        return unsafeBitCast(sym, to: CreateFromArray.self)
    }()
    static var isAvailable: Bool { create != nil }
    static func image(windowID: CGWindowID) -> CGImage? {
        guard let create else { return nil }
        let ptr = UnsafeMutablePointer<UnsafeRawPointer?>.allocate(capacity: 1)
        defer { ptr.deallocate() }
        ptr[0] = UnsafeRawPointer(bitPattern: UInt(windowID))
        guard let windowArray = CFArrayCreate(kCFAllocatorDefault, ptr, 1, nil) else { return nil }
        let option = CGWindowImageOption([.boundsIgnoreFraming, .bestResolution]).rawValue
        return create(.null, windowArray, option)?.takeRetainedValue()
    }
}

/// Cheap content hash of a captured window's pixels (FNV-1a over the backing bytes).
func pixelHash(_ cg: CGImage) -> UInt64 {
    guard let data = cg.dataProvider?.data as Data? else { return 0 }
    var h: UInt64 = 0xcbf29ce484222325
    // Sample every 97th byte so a big Retina capture still hashes fast but still reflects real change.
    var i = 0
    data.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
        while i < raw.count {
            h = (h ^ UInt64(raw[i])) &* 0x100000001b3
            i += 97
        }
    }
    return h
}

// ── The overlay window ────────────────────────────────────────────────────────────────────────────
final class OverlayController {
    var window: NSWindow?
    let overlayWidth: CGFloat = 520   // cover the right strip where status items live

    /// Overlay x-range in GLOBAL top-left coordinates (matches CGWindowList bounds), for the COVERED test.
    var coveredXRange: ClosedRange<CGFloat> = 0...0

    func install() {
        guard let screen = NSScreen.main else { fputs("no main screen\n", stderr); return }

        // Menu-bar height: the notch inset when notched, else the status-bar thickness.
        let menuBarHeight = screen.safeAreaInsets.top > 0 ? screen.safeAreaInsets.top : NSStatusBar.system.thickness

        // AppKit frame: bottom-left origin. Top strip, right-aligned.
        let frame = NSRect(
            x: screen.frame.maxX - overlayWidth,
            y: screen.frame.maxY - menuBarHeight,
            width: overlayWidth,
            height: menuBarHeight
        )
        // Convert to global TOP-LEFT for the covered-x test (y flips; we only need x here).
        coveredXRange = (screen.frame.maxX - overlayWidth)...screen.frame.maxX

        let w = NSPanel(
            contentRect: frame,
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        // Level 26 = kCGStatusWindowLevel (25, where status items live) + 1: above the items, below
        // pop-up menus (101) so a revealed menu still shows on top.
        w.level = NSWindow.Level(rawValue: NSWindow.Level.statusBar.rawValue + 1)
        w.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle, .fullScreenAuxiliary]
        w.isOpaque = false
        w.hasShadow = false
        w.ignoresMouseEvents = true   // click-through: the tray drives reveal; direct bar clicks pass through
        // Translucent red ONLY for the PoC so you can watch items redraw under it. Production => opaque.
        w.backgroundColor = NSColor.systemRed.withAlphaComponent(0.35)
        w.orderFrontRegardless()
        self.window = w

        fputs("overlay installed: frame=\(frame) level=\(w.level.rawValue) menuBarH=\(menuBarHeight)\n", stderr)
        fputs("capture available=\(LegacyWindowCapture.isAvailable) screenRecordingGranted=\(CGPreflightScreenCaptureAccess())\n\n", stderr)
    }
}

// ── Status-item liveness monitor ────────────────────────────────────────────────────────────────
struct Monitored { var id: CGWindowID; var x: CGFloat; var lastHash: UInt64 }

final class Monitor {
    var items: [CGWindowID: Monitored] = [:]
    let coveredXRange: ClosedRange<CGFloat>
    init(coveredXRange: ClosedRange<CGFloat>) { self.coveredXRange = coveredXRange }

    func tick() {
        guard let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID)
                as? [[String: Any]] else { return }
        var line = ""
        for w in list {
            guard let layer = w[kCGWindowLayer as String] as? Int, layer == 25,          // status-item layer
                  let bounds = w[kCGWindowBounds as String] as? [String: Any],
                  let rect = CGRect(dictionaryRepresentation: bounds as CFDictionary),
                  rect.origin.y <= 40, rect.height >= 8, rect.height <= 40,
                  rect.width >= 8, rect.width <= 400,
                  let num = w[kCGWindowNumber as String] as? Int else { continue }
            let id = CGWindowID(num)
            let covered = coveredXRange.contains(rect.midX)
            guard let cg = LegacyWindowCapture.image(windowID: id) else { continue }
            let h = pixelHash(cg)
            let prev = items[id]?.lastHash
            let changed = prev != nil && prev != h
            items[id] = Monitored(id: id, x: rect.midX, lastHash: h)
            let tag = covered ? "COVERED" : "clear  "
            let delta = prev == nil ? "first " : (changed ? "CHANGED" : "SAME   ")
            line += String(format: "  id=%-6d x=%4.0f %@ %@\n", num, rect.midX, tag, delta)
        }
        FileHandle.standardError.write(Data(("--- \(Date()) ---\n" + line).utf8))
    }
}

// ── Main ────────────────────────────────────────────────────────────────────────────────────────
let app = NSApplication.shared
app.setActivationPolicy(.accessory)

let overlay = OverlayController()
let monitor = Box<Monitor?>(nil)

final class Box<T> { var value: T; init(_ v: T) { value = v } }

// Install on launch, then poll once a second.
DispatchQueue.main.async {
    overlay.install()
    monitor.value = Monitor(coveredXRange: overlay.coveredXRange)
}
Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { _ in
    monitor.value?.tick()
}

fputs("menubar-overlay-poc running — Ctrl-C to quit. Watch the red strip and the CHANGED/SAME column.\n", stderr)
app.run()
