#!/usr/bin/env swift
//
// Menu-bar hide PoC — bevel-7hf4 (Ice-style hide/reveal).
//
// The make-or-break question: does a wide NSStatusItem WE own push the neighbouring menu-bar extras
// off the visible bar (the Ice/Bartender mechanism)? macOS lays status items out right-to-left, so an
// item that grows very wide should shove everything to its left off-screen.
//
// Run:   swift native/helper-macos/menubar-hide-poc.swift
// Watch the menu bar. Every 3s our item toggles:
//   • "◀BEVEL"  — narrow (60pt): the other menu-bar icons are visible.
//   • "HIDING◀" — huge width (clamped to the bar): if this PUSHES the other icons off the left /
//                 off-screen, the mechanism works and the whole feature is viable.
// Ctrl-C to stop. Standalone on purpose: the helper blocks its main thread on the gRPC server, so
// AppKit UI can't run there without restructuring — this isolates just the mechanism.
//
import AppKit

let app = NSApplication.shared
app.setActivationPolicy(.accessory)   // agent: no Dock tile, but may own status items

let item = NSStatusBar.system.statusItem(withLength: 60)
item.button?.title = "◀BEVEL"

func log(_ s: String) { FileHandle.standardError.write(Data("[poc] \(s)\n".utf8)) }

var hiding = false
Timer.scheduledTimer(withTimeInterval: 3.0, repeats: true) { _ in
    hiding.toggle()
    // A huge length is clamped by macOS to the available bar width; the item then occupies the space
    // and (right-to-left layout) pushes the items to its LEFT off the visible bar.
    item.length = hiding ? 10_000 : 60
    item.button?.title = hiding ? "HIDING◀" : "◀BEVEL"
    log(hiding ? "WIDE  → neighbours should be pushed off-screen" : "narrow → neighbours visible")
}

log("menu-bar hide PoC running — watch the menu bar; Ctrl-C to stop")
app.run()
