// windowless-apps.swift — SPIKE diagnostic for bevel-ww71
//
// Lists regular-activation-policy (Dock) apps that are RUNNING RIGHT NOW but own
// ZERO on-screen taskbar-eligible windows. These are the apps the window-centric
// taskbar cannot represent today: a running app with no windows has no CGWindow at
// layer 0, so `describe()` never produces a button for it.
//
// Detection mirrors the helper's own eligibility gate:
//   candidates = NSWorkspace.runningApplications where activationPolicy == .regular
//   minus apps that currently own >=1 layer-0, non-zero-frame window (CGWindowList).
//
// Run standalone (no helper rebuild):  swift windowless-apps.swift
// Requires no special permissions (CGWindowList geometry + NSWorkspace only; window
// TITLES would need Screen Recording, but we only need per-PID window PRESENCE here).

import AppKit
import ApplicationServices
import CoreGraphics
import Foundation

let selfPID = getpid()

// AX authoritative window count for a pid: (total standard-ish windows, minimized count).
// Matches what the helper actually correlates in describe(). Requires Accessibility
// permission for the RUNNING process (this script's parent terminal); returns nil if AX
// is unavailable so we can report that the true detector must live in the granted helper.
let axAvailable = AXIsProcessTrusted()
func axWindowCounts(_ pid: pid_t) -> (total: Int, minimized: Int)? {
    guard axAvailable else { return nil }
    let app = AXUIElementCreateApplication(pid)
    var windowsRef: CFTypeRef?
    guard AXUIElementCopyAttributeValue(app, kAXWindowsAttribute as CFString, &windowsRef) == .success,
          let windows = windowsRef as? [AXUIElement] else { return (0, 0) }
    var minimized = 0
    for w in windows {
        var minRef: CFTypeRef?
        if AXUIElementCopyAttributeValue(w, kAXMinimizedAttribute as CFString, &minRef) == .success,
           (minRef as? Bool) == true { minimized += 1 }
    }
    return (windows.count, minimized)
}

// 1. Regular-policy running apps (the ones macOS shows in the Dock).
let regularApps = NSWorkspace.shared.runningApplications.filter {
    $0.activationPolicy == .regular
}

// 2. Per-PID window presence. The helper enumerates with `.optionAll` and KEEPS
//    minimized (off-screen) windows as taskbar buttons (bevel-m2.3), so "does the
//    taskbar show this app" is really "does the PID own any layer-0 window in the
//    .optionAll list", NOT just on-screen ones. We track both so we can tell a
//    truly-windowless app apart from one whose only windows are minimized/off-screen
//    (the latter ALREADY has a button and must NOT be double-counted).
func layer0CountsByPID(_ options: CGWindowListOption) -> [pid_t: Int] {
    var counts: [pid_t: Int] = [:]
    guard let cgWindows = CGWindowListCopyWindowInfo(
        [options, .excludeDesktopElements], kCGNullWindowID
    ) as? [[String: Any]] else { return counts }
    for w in cgWindows {
        guard (w[kCGWindowLayer as String] as? Int32 ?? 99) == 0,
              let pid = w[kCGWindowOwnerPID as String] as? pid_t else { continue }
        let b = w[kCGWindowBounds as String] as? NSDictionary
        let width = (b?["Width"] as? CGFloat) ?? 0
        let height = (b?["Height"] as? CGFloat) ?? 0
        if width > 1, height > 1 { counts[pid, default: 0] += 1 }
    }
    return counts
}

let onScreenCounts = layer0CountsByPID(.optionOnScreenOnly)
let allCounts = layer0CountsByPID(.optionAll)   // includes minimized / other-Space / off-screen
let pidsOnScreen = Set(onScreenCounts.keys)
let pidsAll = Set(allCounts.keys)

// 3. Classify each regular app by the AUTHORITATIVE signal.
//    When AX is available, an app is windowless iff AX reports 0 standard windows —
//    CG '.optionAll' overcounts with off-screen scaffolding and must NOT be trusted for
//    this decision. Buckets:
//    - trulyWindowless: AX 0 windows (or, no-AX, CG 0 total) → INVISIBLE today (bevel-ww71).
//    - hasHiddenWindows: AX>0 but none on-screen (minimized or on another Space) → already
//      a taskbar button; do NOT double-count.
//    - onScreen:        at least one on-screen window → normal button(s).
struct Row { let name: String; let bundle: String; let pid: pid_t; let active: Bool; let hidden: Bool }
var trulyWindowless: [Row] = []
var hasHiddenWindows: [Row] = []
var onScreen: [Row] = []
for app in regularApps {
    let pid = app.processIdentifier
    if pid == selfPID { continue }
    let row = Row(
        name: app.localizedName ?? "?",
        bundle: app.bundleIdentifier ?? "(no bundle id)",
        pid: pid,
        active: app.isActive,
        hidden: app.isHidden)
    let ax = axWindowCounts(pid)
    let hasRealWindows = ax.map { $0.total > 0 } ?? pidsAll.contains(pid)
    if pidsOnScreen.contains(pid) { onScreen.append(row) }
    else if hasRealWindows { hasHiddenWindows.append(row) }
    else { trulyWindowless.append(row) }
}

func printRows(_ title: String, _ rows: [Row]) {
    print("\n\(title) (\(rows.count)):")
    for r in rows.sorted(by: { $0.name.lowercased() < $1.name.lowercased() }) {
        var flags: [String] = []
        if r.active { flags.append("ACTIVE") }
        if r.hidden { flags.append("hidden") }
        let all = allCounts[r.pid] ?? 0
        let onscr = onScreenCounts[r.pid] ?? 0
        flags.append("CG: \(onscr) on-screen / \(all) total")
        if let ax = axWindowCounts(r.pid) {
            flags.append("AX: \(ax.total) windows (\(ax.minimized) min)")
        }
        let flagStr = "  [\(flags.joined(separator: ", "))]"
        print("  \(r.name)  —  \(r.bundle)  pid=\(r.pid)\(flagStr)")
    }
}

print("=== bevel-ww71 windowless-running-app diagnostic ===")
print("Regular (Dock) apps running: \(regularApps.count - 1) (excluding this diagnostic process)")
print("Accessibility (AX) available to this process: \(axAvailable ? "YES — AX counts are authoritative" : "NO — CG-only; run inside the granted helper for the true count")")
print("NOTE: CG '.optionAll total' includes off-screen/other-Space scaffolding and OVERCOUNTS real")
print("windows; the AX column is the signal the helper's describe() actually gates on.")
printRows("TRULY WINDOWLESS (AX 0 windows — running dock-dot, invisible to today's taskbar; the bevel-ww71 population)", trulyWindowless)
printRows("HIDDEN WINDOWS ONLY (AX>0 but off-screen: minimized or on another Space — already a taskbar button)", hasHiddenWindows)
printRows("ON-SCREEN windows (normal taskbar buttons)", onScreen)
