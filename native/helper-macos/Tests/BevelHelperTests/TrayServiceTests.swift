import XCTest
import AppKit
import CoreGraphics
import GRPCCore
@testable import BevelHelper

/// M3-A: TrayService menu-bar status-item discovery + macOS-26 identity resolution.
/// Enumeration reads the live menu bar (CGWindowList metadata needs no Screen Recording), so the
/// exact set is machine-dependent; these tests assert invariants of the shape, not specific items.
final class TrayServiceTests: XCTestCase {

    func testEnumerateReturnsWellFormedItems() {
        let svc = TrayServiceImpl(expectedKey: "test-key")
        let items = svc.enumerateTrayItems()
        XCTAssertNotNil(items)
        for item in items {
            XCTAssertFalse(item.itemID.isEmpty, "every item needs a stable id")
            // id is "ownerPID:windowNumber".
            let parts = item.itemID.split(separator: ":")
            XCTAssertEqual(parts.count, 2, "item id must be 'pid:windowNumber' — got \(item.itemID)")
            XCTAssertNotNil(Int(parts[0]))
            XCTAssertNotNil(Int(parts[1]))
            XCTAssertFalse(item.isLive, "M3-A is limited mode — no live capture yet")
            XCTAssertGreaterThan(item.bounds.width, 0)
        }
    }

    func testItemIDsAreUnique() {
        let svc = TrayServiceImpl(expectedKey: "test-key")
        let ids = svc.enumerateTrayItems().map { $0.itemID }
        XCTAssertEqual(ids.count, Set(ids).count, "item ids must be unique so the tray doesn't collapse rows")
    }

    func testExcludesOwnProcess() {
        let svc = TrayServiceImpl(expectedKey: "test-key")
        let own = getpid()
        XCTAssertTrue(svc.enumerateTrayItems().allSatisfy { $0.ownerPid != own },
                      "the helper's own process must never appear as a tray item")
    }

    /// M3-B: the capture path. With Screen Recording granted, at least one item should carry live
    /// pixels (isLive); without it, everything stays limited-mode (isLive == false) — both are valid.
    /// Establishes the WindowServer connection first (SCK aborts otherwise, CGS_REQUIRE_INIT).
    @MainActor
    func testCapturePathIsLiveWhenGrantedElseLimited() async {
        _ = NSApplication.shared
        NSApp.setActivationPolicy(.prohibited)
        let svc = TrayServiceImpl(expectedKey: "test-key")
        let items = await svc.enumerateWithCapture()

        if CGPreflightScreenCaptureAccess() {
            XCTAssertTrue(items.contains { $0.isLive },
                          "with Screen Recording granted, some items should be live-captured")
        } else {
            XCTAssertTrue(items.allSatisfy { !$0.isLive }, "without the grant, all items are limited-mode")
        }
    }

    /// forwardClick returns false (and posts no event) for a malformed or vanished item id — the
    /// safe guard path. We deliberately do NOT test a real click, which would pop a live menu.
    func testForwardClickRejectsUnknownItems() {
        let svc = TrayServiceImpl(expectedKey: "test-key")
        XCTAssertFalse(svc.forwardClick(itemID: "not-an-id", button: .left, modifiers: 0))
        XCTAssertFalse(svc.forwardClick(itemID: "1:2147483000", button: .left, modifiers: 0),
                       "a windowNumber that isn't on screen must not resolve to a click")
    }

    func testDenylistHidesIstatAndNoiseButKeepsRealItems() {
        let svc = TrayServiceImpl(expectedKey: "test-key")
        // Denied: iStat Menus (all sub-items), Control Center chrome, Ice control items, our dups.
        XCTAssertTrue(svc.isDenied(windowName: "com.bjango.istatmenus.cpu", ownerName: "Control Center"))
        XCTAssertTrue(svc.isDenied(windowName: "com.bjango.istatmenus.battery", ownerName: "Control Center"))
        XCTAssertTrue(svc.isDenied(windowName: "BentoBox-0", ownerName: "Control Center"))
        XCTAssertTrue(svc.isDenied(windowName: "Ice.ControlItem.Visible", ownerName: "Ice"))
        XCTAssertTrue(svc.isDenied(windowName: "Clock", ownerName: "Control Center"))
        // Kept: real third-party + useful system items.
        XCTAssertFalse(svc.isDenied(windowName: "/Applications/Parallels Toolbox.app", ownerName: "Control Center"))
        XCTAssertFalse(svc.isDenied(windowName: "com.apple.menuextra.vpn", ownerName: "Control Center"))
        XCTAssertFalse(svc.isDenied(windowName: "WiFi", ownerName: "Control Center"))
        XCTAssertFalse(svc.isDenied(windowName: "Item-0", ownerName: "Control Center"))
    }

    func testDenylistExcludesDeniedItemsFromEnumeration() {
        let svc = TrayServiceImpl(expectedKey: "test-key")
        let items = svc.enumerateTrayItems()
        // No enumerated item's identity should be an iStat sub-item (denied before it's built).
        XCTAssertFalse(items.contains { $0.ownerBundleID.lowercased().contains("istatmenus") },
                       "iStat Menus items must be filtered by the denylist")
    }

    /// The self-test (§5.10) should pass on a normal desktop: capture works when granted, and a
    /// missing grant is a valid limited-mode outcome (not a failure). Only a granted-but-broken
    /// pipeline returns false — which would be a real regression to surface.
    @MainActor
    func testSelfTestPassesOnANormalDesktop() async {
        _ = NSApplication.shared
        NSApp.setActivationPolicy(.prohibited)
        let svc = TrayServiceImpl(expectedKey: "test-key")
        let ok = await svc.runSelfTestForTest()
        XCTAssertTrue(ok, "self-test should pass (working capture, or limited mode without a grant)")
    }

    func testOsBuildIsReadable() {
        let svc = TrayServiceImpl(expectedKey: "test-key")
        let build = svc.osBuildForTest()
        XCTAssertFalse(build.isEmpty)
        XCTAssertNotEqual(build, "?", "kern.osversion should resolve to a real build string")
    }

    func testFriendlyNameStripsMenuExtraPrefixAndSuffix() {
        let svc = TrayServiceImpl(expectedKey: "test-key")
        XCTAssertEqual(svc.friendlyNameForTest("com.apple.menuextra.eject"), "Eject")
        XCTAssertEqual(svc.friendlyNameForTest("Item-0"), "Item")
        XCTAssertEqual(svc.friendlyNameForTest("WiFi"), "WiFi")
        XCTAssertEqual(svc.friendlyNameForTest(""), "Menu item")
    }

    // MARK: - Tray glyph sizing (regression guard)

    /// The tray-icon sizing oscillated repeatedly (a 1.35x boost made "one big, others tiny"; forcing one
    /// uniform height blew short TEXT like "exo" up; scaling by raw trimmed height made full-bleed app
    /// icons much bigger than padded system glyphs). These lock the shipped behaviour: cell-padding
    /// normalized against standardFill so ICONS render uniform while short glyphs stay proportionally small.
    func testTrayGlyphSizing() {
        let box: CGFloat = 32

        // A typical padded system glyph fills ~2/3 (standardFill) of its cell -> reaches the full box.
        let sys = TrayServiceImpl.trayGlyphLayout(gw: 20, gh: 22, fullH: 33) // 22/33 == 0.667
        XCTAssertEqual(sys.drawnH, box, accuracy: 0.6, "a ~0.66-fill system glyph should fill the box")

        // A full-bleed app icon (fills its whole cell) is CAPPED at the box, never bigger.
        let app = TrayServiceImpl.trayGlyphLayout(gw: 30, gh: 30, fullH: 31)
        XCTAssertLessThanOrEqual(app.drawnH, box + 0.01, "full-bleed icon must not exceed the box")

        // KEY: app icon and padded system glyph render at ~the SAME height (guards "one big, others tiny").
        XCTAssertEqual(app.drawnH, sys.drawnH, accuracy: 1.0, "app icons must not dwarf system glyphs")

        // A short TEXT glyph (small fraction of the cell, like "exo") stays proportionally SMALLER — the
        // regression where it got blown up to the full box height must not come back.
        let text = TrayServiceImpl.trayGlyphLayout(gw: 40, gh: 12, fullH: 33) // 12/33 == 0.36
        XCTAssertLessThan(text.drawnH, box * 0.72, "short text glyph must stay smaller than an icon")
        XCTAssertGreaterThan(text.drawnH, 8, "but still visible")

        // Monotonic: more cell-fill => taller (up to the cap) — never inverted.
        XCTAssertGreaterThan(sys.drawnH, text.drawnH)

        // A wide status strip keeps its TRUE width; height stays bounded by the box.
        let wide = TrayServiceImpl.trayGlyphLayout(gw: 120, gh: 20, fullH: 30)
        XCTAssertLessThanOrEqual(wide.drawnH, box + 0.01)
        XCTAssertGreaterThan(wide.drawnW, wide.drawnH, "wide strip keeps its true width, not squashed to a square")

        // An extreme-aspect glyph is clamped at 512px wide and shrinks VERTICALLY (doesn't squash to full height).
        let extreme = TrayServiceImpl.trayGlyphLayout(gw: 2000, gh: 20, fullH: 30)
        XCTAssertEqual(extreme.canvasW, 512)
        XCTAssertLessThan(extreme.drawnH, box, "extreme aspect shrinks vertically under the 512 width clamp")

        // Glyph is vertically centred in the box.
        XCTAssertEqual(text.dstY, (CGFloat(text.canvasH) - text.drawnH) / 2, accuracy: 0.01)
        XCTAssertEqual(text.canvasH, 32)
    }
}
