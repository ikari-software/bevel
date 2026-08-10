#!/usr/bin/env swift
//
// Menu-bar hide PoC — bevel-7hf4 (Ice-style hide/reveal).
//
// The make-or-break question: does a wide NSStatusItem WE own push the neighbouring menu-bar extras
// off the visible bar (the Ice/Bartender mechanism)? macOS lays status items out right-to-left, so an
// item that grows very wide should shove everything to its left off-screen.
//
// Must run as a BUNDLE (packaging/menubar-poc wraps this). A bare executable has no LaunchServices
// identity, so NSStatusBar has nothing to attach to. And the status item MUST be created in
// applicationDidFinishLaunching — the status bar isn't ready before the app finishes launching.
//
// Watch the menu bar. Every 3s our item toggles:
//   • "◀BEVEL"  — narrow (80pt): the other menu-bar icons are visible.
//   • "HIDING◀" — huge width: if this PUSHES the other icons off the left, the mechanism works.
//
import AppKit

func log(_ s: String) { FileHandle.standardError.write(Data("[poc] \(s)\n".utf8)); fflush(stderr) }

final class PocDelegate: NSObject, NSApplicationDelegate {
    var item: NSStatusItem!
    var hiding = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        log("applicationDidFinishLaunching — creating status item")

        // THE CORRECTION (read from Ice, not guessed): anchor the item with autosaveName +
        // preferredPosition so macOS restores its slot after every length change. Without this our
        // item flew to x=-5056. Ice's StatusItemDefaults key format: "NSStatusItem Preferred Position
        // <autosaveName>", a CGFloat in UserDefaults.standard, seeded BEFORE the item is created.
        let autosaveName = "BevelPocItem"
        let posKey = "NSStatusItem Preferred Position \(autosaveName)"
        if UserDefaults.standard.object(forKey: posKey) == nil {
            UserDefaults.standard.set(CGFloat(0), forKey: posKey)   // 0 = Ice's anchor for its main icon
        }
        item = NSStatusBar.system.statusItem(withLength: 80)
        item.autosaveName = autosaveName
        if item.button == nil { log("WARNING: item.button was nil") }
        item.button?.title = "◀BEVEL"
        item.button?.image = NSImage(systemSymbolName: "chevron.left.2", accessibilityDescription: "Bevel")
        log("status item created; length=\(item.length) visible=\(item.isVisible) autosave=\(autosaveName)")

        // DECISIVE DIAGNOSTIC: where did macOS actually place our item's window?
        func reportPlacement(_ tag: String) {
            let f = item.button?.window?.frame
            let screen = NSScreen.main?.frame
            let vis = NSScreen.main?.visibleFrame
            log("\(tag): itemWindowFrame=\(f.map { "\($0)" } ?? "nil") isVisible=\(item.isVisible) screen=\(screen.map { "\($0)" } ?? "nil") visibleFrame=\(vis.map { "\($0)" } ?? "nil")")
        }
        reportPlacement("placement")

        Timer.scheduledTimer(withTimeInterval: 3.0, repeats: true) { [weak self] _ in
            guard let self else { return }
            self.hiding.toggle()
            // A huge length is clamped by macOS to the bar width; the item occupies the space and
            // (right-to-left layout) pushes the items to its LEFT off the visible bar.
            self.item.length = self.hiding ? 10_000 : 80
            self.item.button?.title = self.hiding ? "HIDING◀" : "◀BEVEL"
            reportPlacement(self.hiding ? "WIDE " : "narrow")
        }
        NSApp.activate(ignoringOtherApps: true)
    }
}

let app = NSApplication.shared
let delegate = PocDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)   // agent: no Dock tile, but may own status items
log("starting run loop")
app.run()
