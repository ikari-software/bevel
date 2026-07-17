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

    func testFriendlyNameStripsMenuExtraPrefixAndSuffix() {
        let svc = TrayServiceImpl(expectedKey: "test-key")
        XCTAssertEqual(svc.friendlyNameForTest("com.apple.menuextra.eject"), "Eject")
        XCTAssertEqual(svc.friendlyNameForTest("Item-0"), "Item")
        XCTAssertEqual(svc.friendlyNameForTest("WiFi"), "WiFi")
        XCTAssertEqual(svc.friendlyNameForTest(""), "Menu item")
    }
}
