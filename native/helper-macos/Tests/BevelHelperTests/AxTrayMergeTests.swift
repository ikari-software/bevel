import XCTest
import CoreGraphics
@testable import BevelHelper

/// bevel-lezo: macOS 27 stopped listing status items as windows, so the tray adds the items that only
/// Accessibility (each app's AXExtrasMenuBar) still reports. The fixtures are REAL readings taken on
/// 2026-11-21: a macOS 26.6.2 VM (Stats + Maccy installed) and the owner's macOS 27.3 host.
final class AxTrayMergeTests: XCTestCase {

    typealias W = AxTrayMerge.WindowItem
    typealias A = AxTrayMerge.AxItem

    // macOS 26.6.2: the window list sees these 10 (every owner is Control Center, pid 394)...
    let win26: [W] = [
        W(windowNumber: 101, pid: 394, frame: CGRect(x: 1196, y: 0, width: 48, height: 30)),
        W(windowNumber: 102, pid: 394, frame: CGRect(x: 1244, y: 0, width: 47, height: 30)),
        W(windowNumber: 103, pid: 394, frame: CGRect(x: 1291, y: 0, width: 27, height: 30)),
        W(windowNumber: 104, pid: 394, frame: CGRect(x: 1318, y: 0, width: 27, height: 30)),
        W(windowNumber: 105, pid: 394, frame: CGRect(x: 1345, y: 0, width: 22, height: 30)),
        W(windowNumber: 106, pid: 394, frame: CGRect(x: 1367, y: 0, width: 71, height: 30)),
        W(windowNumber: 107, pid: 394, frame: CGRect(x: 1438, y: 0, width: 32, height: 30)),
        W(windowNumber: 108, pid: 394, frame: CGRect(x: 1470, y: 0, width: 32, height: 30)),
        W(windowNumber: 109, pid: 394, frame: CGRect(x: 1502, y: 0, width: 42, height: 30)),
        W(windowNumber: 110, pid: 394, frame: CGRect(x: 1544, y: 0, width: 150, height: 30)),
    ]
    // ...and AX reports the same 10 with their real apps, ~7 px inset, plus 8 zero-size CC entries.
    lazy var ax26: [A] = Array(repeating: A(pid: 394, appName: "Control Center", index: 0, label: "", frame: CGRect(x: 0, y: 0, width: 0, height: 0)), count: 8)
        .enumerated().map { i, a in A(pid: a.pid, appName: a.appName, index: i, label: "", frame: a.frame) } + [
        A(pid: 927, appName: "Stats", index: 0, label: "", frame: CGRect(x: 1203, y: 3, width: 34, height: 24)),
        A(pid: 927, appName: "Stats", index: 1, label: "", frame: CGRect(x: 1251, y: 3, width: 33, height: 24)),
        A(pid: 927, appName: "Stats", index: 2, label: "", frame: CGRect(x: 1298, y: 3, width: 12, height: 24)),
        A(pid: 927, appName: "Stats", index: 3, label: "", frame: CGRect(x: 1325, y: 3, width: 12, height: 24)),
        A(pid: 927, appName: "Stats", index: 4, label: "", frame: CGRect(x: 1352, y: 3, width: 8, height: 24)),
        A(pid: 927, appName: "Stats", index: 5, label: "", frame: CGRect(x: 1374, y: 3, width: 57, height: 24)),
        A(pid: 929, appName: "Maccy", index: 0, label: "Maccy — clipboard history", frame: CGRect(x: 1437, y: 3, width: 34, height: 24)),
        A(pid: 533, appName: "Spotlight", index: 0, label: "Spotlight / Search", frame: CGRect(x: 1469, y: 3, width: 34, height: 24)),
        A(pid: 394, appName: "Control Center", index: 8, label: "Control Center", frame: CGRect(x: 1510, y: 3, width: 26, height: 24)),
        A(pid: 394, appName: "Control Center", index: 9, label: "Clock", frame: CGRect(x: 1552, y: 3, width: 134, height: 24)),
    ]

    func testOnMacOS26EveryAxItemHasAWindowTwinSoNothingIsAdded() {
        let extra = AxTrayMerge.unmatched(ax: ax26, windows: win26)
        XCTAssertEqual(extra, [], "on 26 the window list already covers every real item; AX must add nothing")
    }

    func testZeroSizeAndOffBandAxEntriesAreDropped() {
        XCTAssertFalse(AxTrayMerge.isInMenuBarBand(CGRect(x: 0, y: 0, width: 0, height: 0)))
        // The control items Bevel and Ice park off-screen on 27: 5002 px wide at y=1443.
        XCTAssertFalse(AxTrayMerge.isInMenuBarBand(CGRect(x: 0, y: 1443, width: 5002, height: 24)))
        XCTAssertTrue(AxTrayMerge.isInMenuBarBand(CGRect(x: 1039, y: 3, width: 24, height: 24)))
    }

    func testOnMacOS27WithNoWindowsEveryRealAxItemIsAdded() {
        // A slice of the 27.3 host reading: one off-screen control item, then real items.
        let ax27: [A] = [
            A(pid: 7017, appName: "Ice", index: 0, label: "", frame: CGRect(x: 0, y: 1443, width: 5002, height: 24)),
            A(pid: 1500, appName: "Flameshot", index: 0, label: "", frame: CGRect(x: 1039, y: 3, width: 24, height: 24)),
            A(pid: 1501, appName: "JetBrains Toolbox", index: 0, label: "", frame: CGRect(x: 1063, y: 3, width: 24, height: 24)),
            A(pid: 1502, appName: "Bitwarden", index: 0, label: "", frame: CGRect(x: 1478, y: 3, width: 20, height: 24)),
        ]
        let extra = AxTrayMerge.unmatched(ax: ax27, windows: [])
        XCTAssertEqual(extra.map(\.appName), ["Flameshot", "JetBrains Toolbox", "Bitwarden"])
    }

    func testCentreMatchingSurvivesTheAxInsetButNotANeighbour() {
        let w = W(windowNumber: 1, pid: 394, frame: CGRect(x: 1196, y: 0, width: 48, height: 30))
        let inset = A(pid: 927, appName: "Stats", index: 0, label: "", frame: CGRect(x: 1203, y: 3, width: 34, height: 24))
        let neighbour = A(pid: 927, appName: "Stats", index: 1, label: "", frame: CGRect(x: 1251, y: 3, width: 33, height: 24))
        XCTAssertTrue(AxTrayMerge.sameItem(inset, w))
        XCTAssertFalse(AxTrayMerge.sameItem(neighbour, w))
    }

    func testAxItemIdsRoundTripAndAreDistinctFromWindowIds() {
        let a = A(pid: 1502, appName: "Bitwarden", index: 3, label: "", frame: .zero)
        let id = AxTrayMerge.itemID(a)
        XCTAssertEqual(id, "ax:1502:3")
        let parsed = AxTrayMerge.parse(itemID: id)
        XCTAssertEqual(parsed?.pid, 1502)
        XCTAssertEqual(parsed?.index, 3)
        XCTAssertNil(AxTrayMerge.parse(itemID: "1502:3"), "a window id is not an AX id")
        XCTAssertNil(AxTrayMerge.parse(itemID: "ax:x:3"))
        XCTAssertNil(AxTrayMerge.parse(itemID: "ax:1502"))
    }

    func testTheIceBarPanelIsDeniedButIceItemsAreNot() {
        let svc = TrayServiceImpl(expectedKey: "test-key")
        XCTAssertTrue(svc.isDenied(windowName: "Ice Bar", ownerName: "Ice"))
        XCTAssertFalse(svc.isDenied(windowName: "", ownerName: "Bitwarden"))
    }

    // Clicks (measured live on 27.3, 2026-11-21): with menu-bar consolidation ON every tray click came in
    // with park=true, and the park route only understands window ids, so every AX item failed silently.
    // AX items must route to the AX press path whatever `park` says: they have no window to relocate.
    func testAxItemsRouteToAxPressEvenWhenParking() {
        XCTAssertEqual(AxTrayMerge.clickRoute(itemID: "ax:1155:3", park: true), .axItem(pid: 1155, index: 3))
        XCTAssertEqual(AxTrayMerge.clickRoute(itemID: "ax:1155:3", park: false), .axItem(pid: 1155, index: 3))
    }

    func testWindowItemsKeepTheirRoutes() {
        XCTAssertEqual(AxTrayMerge.clickRoute(itemID: "394:101", park: true), .parkWindow)
        XCTAssertEqual(AxTrayMerge.clickRoute(itemID: "394:101", park: false), .window)
    }

    // MenuBarAgent's system items (Wi-Fi, Sound, Control Center, clock) are SwiftUI hosting views with
    // NO AX actions on 27, so a failed press must fall back to a real click at the item, but only when
    // the item is actually on the visible bar: a parked (off-screen) item must not get a click at a
    // point that is now something else.
    func testSyntheticClickFallbackOnlyForVisibleItems() {
        let displays = [CGRect(x: 0, y: 0, width: 2560, height: 1440)]
        XCTAssertEqual(AxTrayMerge.fallbackClickPoint(frame: CGRect(x: 2367, y: 0, width: 26, height: 30), displays: displays),
                       CGPoint(x: 2380, y: 15))
        XCTAssertNil(AxTrayMerge.fallbackClickPoint(frame: CGRect(x: -400, y: 0, width: 26, height: 30), displays: displays))
        XCTAssertNil(AxTrayMerge.fallbackClickPoint(frame: CGRect(x: 0, y: 0, width: 5002, height: 30), displays: displays),
                     "the 5002 px control items are not clickable glyphs")
    }

    // Review (2026-11-21): an AX item id is positional (pid + child index). If the app adds, removes or
    // reorders an extra between enumeration and click, the same index names a different item, and
    // pressing it would open the wrong menu. The press must resolve to the item the user saw.
    typealias I = AxTrayMerge.Identity

    func testPressTargetKeepsTheIndexWhenItStillNamesTheSameItem() {
        let rec = I(label: "Weather", frame: CGRect(x: 1876, y: 0, width: 52, height: 30))
        XCTAssertEqual(AxTrayMerge.resolvePressTarget(index: 1, recorded: rec, candidates: [
            I(label: "CPU", frame: CGRect(x: 1800, y: 0, width: 40, height: 30)), rec]), 1)
    }

    func testPressTargetFollowsTheItemWhenTheIndexShifted() {
        let rec = I(label: "Weather", frame: CGRect(x: 1876, y: 0, width: 52, height: 30))
        let inserted = I(label: "New", frame: CGRect(x: 1700, y: 0, width: 24, height: 30))
        XCTAssertEqual(AxTrayMerge.resolvePressTarget(index: 0, recorded: rec, candidates: [inserted, rec]), 1)
    }

    func testUnlabelledItemsMatchByPositionAndFailClosedWhenAmbiguous() {
        // MenuBarAgent's system items have no label at all: only position can identify them.
        let rec = I(label: "", frame: CGRect(x: 2367, y: 0, width: 26, height: 30))
        let neighbour = I(label: "", frame: CGRect(x: 2306, y: 0, width: 21, height: 30))
        let shifted = I(label: "", frame: CGRect(x: 2370, y: 0, width: 26, height: 30))   // clock reflow
        XCTAssertEqual(AxTrayMerge.resolvePressTarget(index: 0, recorded: rec, candidates: [neighbour, shifted]), 1)
        XCTAssertNil(AxTrayMerge.resolvePressTarget(index: 0, recorded: rec, candidates: [neighbour]))
    }

    func testPressTargetFailsClosedWithoutARecordOrOnAnOutOfRangeIndex() {
        let rec = I(label: "Weather", frame: CGRect(x: 1876, y: 0, width: 52, height: 30))
        XCTAssertNil(AxTrayMerge.resolvePressTarget(index: 0, recorded: nil, candidates: [rec]))
        XCTAssertNil(AxTrayMerge.resolvePressTarget(index: -1, recorded: rec, candidates: []))
        XCTAssertNil(AxTrayMerge.resolvePressTarget(index: 5, recorded: rec, candidates: [
            I(label: "Other", frame: CGRect(x: 0, y: 0, width: 20, height: 30))]))
    }

    func testPressActionOrderFollowsTheButton() {
        XCTAssertEqual(AxTrayMerge.pressActions(rightClick: false), ["AXPress", "AXShowMenu"])
        XCTAssertEqual(AxTrayMerge.pressActions(rightClick: true), ["AXShowMenu", "AXPress"])
    }
}
