import XCTest
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

    func testFriendlyNameStripsMenuExtraPrefixAndSuffix() {
        let svc = TrayServiceImpl(expectedKey: "test-key")
        XCTAssertEqual(svc.friendlyNameForTest("com.apple.menuextra.eject"), "Eject")
        XCTAssertEqual(svc.friendlyNameForTest("Item-0"), "Item")
        XCTAssertEqual(svc.friendlyNameForTest("WiFi"), "WiFi")
        XCTAssertEqual(svc.friendlyNameForTest(""), "Menu item")
    }
}
