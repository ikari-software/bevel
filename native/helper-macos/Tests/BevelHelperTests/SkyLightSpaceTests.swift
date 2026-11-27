import XCTest
@testable import BevelHelper

/// bevel-44vg: pure coverage for `SkyLight.parseManagedSpaces` — the labeling/ranking rules behind
/// the "Move to Space" menu. No WindowServer, no dlsym: the parse takes the raw
/// `CGSCopyManagedDisplaySpaces` answer, so these always run.

final class SkyLightSpaceTests: XCTestCase {

    private func display(uuid: String, current: Int?, spaces: [[String: Any]]) -> [String: Any] {
        var d: [String: Any] = [
            "Display Identifier": uuid,
            "Spaces": spaces,
        ]
        if let current {
            d["Current Space"] = ["id64": current]
        }
        return d
    }

    func testUserSpacesAreLabeledDesktopNByIdRankAndCurrentIsMarked() {
        let spaces = SkyLight.parseManagedSpaces([
            display(uuid: "D1", current: 100, spaces: [
                ["id64": 100, "type": 0],
                ["id64": 250, "type": 0],
                ["id64": 300, "type": 4],   // fullscreen
            ]),
        ])
        XCTAssertEqual(spaces.map(\.name), ["Desktop 1", "Desktop 2", "Fullscreen"])
        XCTAssertEqual(spaces.map(\.id), [100, 250, 300])
        XCTAssertEqual(spaces.map(\.isCurrent), [true, false, false])
        XCTAssertEqual(spaces.map(\.type), [0, 0, 4])
        XCTAssertEqual(spaces.map(\.displayUUID), ["D1", "D1", "D1"])
    }

    func testDesktopRankFollowsIdOrderNotListOrder() {
        let spaces = SkyLight.parseManagedSpaces([
            display(uuid: "D1", current: nil, spaces: [
                ["id64": 900, "type": 0],
                ["id64": 100, "type": 0],
            ]),
        ])
        // id 100 ranks first regardless of position in the Spaces array.
        let byID = Dictionary(uniqueKeysWithValues: spaces.map { ($0.id, $0.name) })
        XCTAssertEqual(byID[100], "Desktop 1")
        XCTAssertEqual(byID[900], "Desktop 2")
        XCTAssertTrue(spaces.allSatisfy { !$0.isCurrent })
    }

    func testWindowServerNameWinsOverSynthesizedLabel() {
        let spaces = SkyLight.parseManagedSpaces([
            display(uuid: "D1", current: nil, spaces: [
                ["id64": 42, "type": 0, "name": "Work"],
            ]),
        ])
        XCTAssertEqual(spaces.first?.name, "Work")
    }

    func testSystemAndUnknownTypesGetKindLabelsAndManagedSpaceIDFallback() {
        let spaces = SkyLight.parseManagedSpaces([
            display(uuid: "D2", current: nil, spaces: [
                ["id64": 7, "type": 2],                        // dashboard
                ["ManagedSpaceID": 8, "type": 99],             // no id64 → ManagedSpaceID, unknown type
            ]),
        ])
        let byID = Dictionary(uniqueKeysWithValues: spaces.map { ($0.id, $0.name) })
        XCTAssertEqual(byID[7], "System")
        XCTAssertEqual(byID[8], "Space 8")
    }

    func testEmptyOrMalformedAnswerYieldsNoSpaces() {
        XCTAssertTrue(SkyLight.parseManagedSpaces([]).isEmpty)
        XCTAssertTrue(SkyLight.parseManagedSpaces([["Spaces": "not-an-array"]]).isEmpty)
        XCTAssertTrue(SkyLight.parseManagedSpaces([["Spaces": [["type": 0]]]]).isEmpty)  // no id at all
    }

    func testMultipleDisplaysEachCarryTheirOwnCurrentSpace() {
        let spaces = SkyLight.parseManagedSpaces([
            display(uuid: "A", current: 1, spaces: [["id64": 1, "type": 0], ["id64": 2, "type": 0]]),
            display(uuid: "B", current: 2, spaces: [["id64": 1, "type": 0], ["id64": 2, "type": 0]]),
        ])
        // Same Space ids exist on both displays; only the per-display current is marked.
        let onA = spaces.filter { $0.displayUUID == "A" }
        let onB = spaces.filter { $0.displayUUID == "B" }
        XCTAssertEqual(onA.map(\.isCurrent), [true, false])
        XCTAssertEqual(onB.map(\.isCurrent), [false, true])
    }
}
