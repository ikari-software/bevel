import AppKit
import ApplicationServices
import CoreGraphics
import Foundation
import GRPCCore
import GRPCProtobuf
import ScreenCaptureKit

/// Accessibility discovery and pressing for menu-bar items that only AX still reports (bevel-lezo:
/// macOS 27 dropped status items from the window lists). Split out of TrayServiceImpl.swift to keep
/// that file navigable; members are internal because Swift's `private` doesn't cross files.
extension TrayServiceImpl {
    // MARK: - Accessibility discovery (bevel-lezo)

    /// The app's AXExtrasMenuBar children, in order (index = position, part of the item id).
    func axExtras(pid: pid_t) -> [AXUIElement] {
        let app = AXUIElementCreateApplication(pid)
        _ = _AXUIElementSetMessagingTimeout(app, 0.3)   // an unresponsive app must not stall enumeration
        var bar: CFTypeRef?
        guard AXUIElementCopyAttributeValue(app, "AXExtrasMenuBar" as CFString, &bar) == .success,
              let bar, CFGetTypeID(bar) == AXUIElementGetTypeID() else { return [] }
        var kids: CFTypeRef?
        AXUIElementCopyAttributeValue(bar as! AXUIElement, kAXChildrenAttribute as CFString, &kids)
        return (kids as? [AXUIElement]) ?? []
    }

    func axString(_ el: AXUIElement, _ attr: String) -> String {
        var v: CFTypeRef?
        AXUIElementCopyAttributeValue(el, attr as CFString, &v)
        return (v as? String) ?? ""
    }

    /// The item's name as Accessibility gives it: its title, else its description.
    func axLabel(_ el: AXUIElement) -> String {
        let title = axString(el, kAXTitleAttribute as String)
        return title.isEmpty ? axString(el, kAXDescriptionAttribute as String) : title
    }

    func axFrame(_ el: AXUIElement) -> CGRect {
        var p: CFTypeRef?, s: CFTypeRef?
        var pt = CGPoint.zero, sz = CGSize.zero
        if AXUIElementCopyAttributeValue(el, kAXPositionAttribute as CFString, &p) == .success, let p,
           CFGetTypeID(p) == AXValueGetTypeID() { AXValueGetValue(p as! AXValue, .cgPoint, &pt) }
        if AXUIElementCopyAttributeValue(el, kAXSizeAttribute as CFString, &s) == .success, let s,
           CFGetTypeID(s) == AXValueGetTypeID() { AXValueGetValue(s as! AXValue, .cgSize, &sz) }
        return CGRect(origin: pt, size: sz)
    }

    /// Every running app's menu-bar extras as Accessibility reports them. Empty without the
    /// Accessibility grant, which degrades to exactly today's window-list behaviour.
    func axExtrasItems() -> [AxTrayMerge.AxItem] {
        guard AXIsProcessTrusted() else { return [] }
        var out: [AxTrayMerge.AxItem] = []
        for app in NSWorkspace.shared.runningApplications {
            let pid = app.processIdentifier
            for (i, el) in axExtras(pid: pid).enumerated() {
                out.append(AxTrayMerge.AxItem(pid: pid, appName: app.localizedName ?? "", index: i,
                                              label: axLabel(el), frame: axFrame(el)))
            }
        }
        return out
    }

    /// Clicks an AX-only item by pressing its element directly (it has no window to target). Re-resolves
    /// the element from pid + index and checks it is still in the menu-bar band, so a vanished or
    /// reordered item cannot press something else.
    ///
    /// Same order as the window path: AX press first, then a real click at the item. The click matters on
    /// macOS 27, where MenuBarAgent's system items (Wi-Fi, Sound, Control Center, clock) are SwiftUI
    /// hosting views with no AX actions at all; measured: a click at the item opens Control Center.
    func pressAxExtra(pid: pid_t, index: Int, itemID: String,
                              button: Bevel_Helper_V1_ForwardClickRequest.Button, modifiers: UInt32) -> Bool {
        guard AXIsProcessTrusted() else { return false }
        let extras = axExtras(pid: pid)
        let recorded = axIdentityLock.withLock { axIdentity[itemID] }
        guard let target = AxTrayMerge.resolvePressTarget(
                index: index, recorded: recorded,
                candidates: extras.map { AxTrayMerge.Identity(label: axLabel($0), frame: axFrame($0)) })
        else { return false }   // the item the user saw is gone or ambiguous: press nothing
        let el = extras[target]
        let frame = axFrame(el)
        guard AxTrayMerge.isInMenuBarBand(frame) else { return false }
        let actions = AxTrayMerge.pressActions(rightClick: button == .right).map { $0 as CFString }
        for action in actions where AXUIElementPerformAction(el, action) == .success {
            dbg("AX-pressed extra \(itemID) via \(action)")
            return true
        }
        let displays = NSScreen.screens.compactMap { s -> CGRect? in
            guard let n = s.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber else { return nil }
            return CGDisplayBounds(CGDirectDisplayID(n.uint32Value))
        }
        guard let point = AxTrayMerge.fallbackClickPoint(frame: frame, displays: displays) else { return false }
        return clickViaCGEvent(at: point, itemID: itemID, button: button, modifiers: modifiers)
    }
}
