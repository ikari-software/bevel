#!/usr/bin/env swift
//
// C3 spike — bevel-7hf4: can we move ANOTHER app's status-item window off the menu bar via the
// private CGS API? If yes, "true bottom-native" menus are reachable (move the item to the tray, its
// menu opens there). If no (error code / no visible move / instant snap-back), C3 is a dead end and
// we ship C2 (reveal-at-top) + C1 (native-bottom proxy for the readable subset).
//
// Grounding: Ice links private CGS but only READS windows (CGSGetScreenRectForWindow, spaces, counts)
// and moves items via synthetic drags (horizontal, on the bar) — it never calls CGSMoveWindow. Strong
// hint this won't work cross-process. This proves it either way. Symbols via dlsym (no link-time dep).
//
// Run: swift native/helper-macos/menubar-c3-move-poc.swift   (from your terminal)
// Watch the menu bar: do any icons DROP into the screen for ~4s? Then it restores them.
//
import AppKit

func log(_ s: String) { FileHandle.standardError.write(Data((s + "\n").utf8)); fflush(stderr) }

// Private CGS symbols, resolved at runtime from the already-loaded CoreGraphics.
typealias ConnFn = @convention(c) () -> Int32
typealias MoveFn = @convention(c) (Int32, UInt32, UnsafePointer<CGPoint>) -> Int32
let handle = dlopen(nil, RTLD_NOW)
guard
    let connSym = dlsym(handle, "CGSMainConnectionID"),
    let moveSym = dlsym(handle, "CGSMoveWindow")
else {
    log("FAIL: could not resolve CGSMainConnectionID / CGSMoveWindow — symbols absent")
    exit(1)
}
let CGSMainConnectionID = unsafeBitCast(connSym, to: ConnFn.self)
let CGSMoveWindow = unsafeBitCast(moveSym, to: MoveFn.self)

// Establish a WindowServer connection (a bare CLI can otherwise report connection 0).
_ = NSApplication.shared
let cid = CGSMainConnectionID()
log("CGSMainConnectionID = \(cid)")

// Enumerate status-item windows (menu-bar band, status window layer 25).
guard let infos = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] else {
    log("FAIL: CGWindowListCopyWindowInfo returned nil"); exit(1)
}
struct Item { let wid: UInt32; let owner: String; let x: CGFloat; let y: CGFloat; let w: CGFloat; let h: CGFloat }
var items: [Item] = []
for w in infos {
    guard
        let layer = w[kCGWindowLayer as String] as? Int, layer == 25,
        let wid = w[kCGWindowNumber as String] as? Int,
        let b = w[kCGWindowBounds as String] as? [String: CGFloat],
        let x = b["X"], let y = b["Y"], let width = b["Width"], let height = b["Height"]
    else { continue }
    let owner = (w[kCGWindowOwnerName as String] as? String) ?? "?"
    items.append(Item(wid: UInt32(wid), owner: owner, x: x, y: y, w: width, h: height))
}
log("found \(items.count) status-item windows (layer 25):")
for it in items { log("  wid=\(it.wid) owner=\(it.owner) frame=(\(it.x),\(it.y),\(it.w),\(it.h))") }
if items.isEmpty { log("No layer-25 windows — tell me and I'll widen the filter."); exit(0) }

// Try to move each DOWN 300px. Record the CGError per window; snapshot originals to restore.
log("--- CGSMoveWindow: attempting to drop each 300px ---")
var originals: [(UInt32, CGPoint)] = []
for it in items {
    originals.append((it.wid, CGPoint(x: it.x, y: it.y)))
    var target = CGPoint(x: it.x, y: it.y + 300)
    let err = CGSMoveWindow(cid, it.wid, &target)
    log("  wid=\(it.wid) owner=\(it.owner) -> err=\(err) (0 = success)")
}
log(">>> LOOK at the menu bar NOW: did any icons drop into the screen? (restoring in 4s) <<<")
Thread.sleep(forTimeInterval: 4)

for (wid, p) in originals { var pp = p; _ = CGSMoveWindow(cid, wid, &pp) }
log("restored originals. done.")
