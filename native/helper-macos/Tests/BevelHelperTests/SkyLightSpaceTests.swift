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
        // Non-mirrored displays carry DISTINCT Space ids; each display's own current is marked.
        let spaces = SkyLight.parseManagedSpaces([
            display(uuid: "A", current: 1, spaces: [["id64": 1, "type": 0], ["id64": 2, "type": 0]]),
            display(uuid: "B", current: 4, spaces: [["id64": 3, "type": 0], ["id64": 4, "type": 0]]),
        ])
        let onA = spaces.filter { $0.displayUUID == "A" }
        let onB = spaces.filter { $0.displayUUID == "B" }
        XCTAssertEqual(onA.map(\.id), [1, 2])
        XCTAssertEqual(onA.map(\.isCurrent), [true, false])
        XCTAssertEqual(onB.map(\.id), [3, 4])
        XCTAssertEqual(onB.map(\.isCurrent), [false, true])
    }

    func testMirroredDisplaysDedupeBySpaceID() {
        // bevel-44vg review: mirrored displays list the SAME Space id once per display — the parse
        // dedupes by id so the menu doesn't show each Space twice.
        let spaces = SkyLight.parseManagedSpaces([
            display(uuid: "A", current: 1, spaces: [["id64": 1, "type": 0], ["id64": 2, "type": 0]]),
            display(uuid: "B", current: 1, spaces: [["id64": 1, "type": 0], ["id64": 2, "type": 0]]),
        ])
        XCTAssertEqual(spaces.map(\.id), [1, 2])
        XCTAssertEqual(spaces.map(\.displayUUID), ["A", "A"])   // first sighting wins
        XCTAssertEqual(spaces.map(\.isCurrent), [true, false])
    }

    func testFallbackDesktopRankStartsPastRealRanks() {
        // bevel-44vg review: a user Space whose id64 is missing (ManagedSpaceID fallback) used to
        // get rank 0 — "Desktop 1", colliding with the real first Desktop. Fallback ranks now start
        // past the user-id list.
        let spaces = SkyLight.parseManagedSpaces([
            display(uuid: "D1", current: nil, spaces: [
                ["id64": 100, "type": 0],
                ["id64": 250, "type": 0],
                ["ManagedSpaceID": 900, "type": 0],
            ]),
        ])
        let byID = Dictionary(uniqueKeysWithValues: spaces.map { ($0.id, $0.name) })
        XCTAssertEqual(byID[100], "Desktop 1")
        XCTAssertEqual(byID[250], "Desktop 2")
        XCTAssertEqual(byID[900], "Desktop 3")
    }

    func testCurrentSpaceHonorsManagedSpaceIDFallback() {
        // bevel-44vg review: a "Current Space" entry without id64 still marks isCurrent via
        // ManagedSpaceID.
        let spaces = SkyLight.parseManagedSpaces([
            [
                "Display Identifier": "D1",
                "Current Space": ["ManagedSpaceID": 8],
                "Spaces": [["ManagedSpaceID": 8, "type": 0], ["id64": 9, "type": 0]],
            ],
        ])
        let byID = Dictionary(uniqueKeysWithValues: spaces.map { ($0.id, $0.isCurrent) })
        XCTAssertEqual(byID[8], true)
        XCTAssertEqual(byID[9], false)
    }

    func testDuplicateLabelsAreDisambiguatedWithDisplayUUID() {
        // bevel-44vg review: two displays can both synthesize "Desktop 1" — the duplicate labels
        // get the display UUID appended so the menu rows stay distinguishable.
        let spaces = SkyLight.parseManagedSpaces([
            display(uuid: "A", current: 1, spaces: [["id64": 1, "type": 0]]),
            display(uuid: "B", current: 3, spaces: [["id64": 3, "type": 0]]),
        ])
        XCTAssertEqual(spaces.map(\.name).sorted(), ["Desktop 1 (A)", "Desktop 1 (B)"])
        XCTAssertEqual(spaces.map(\.id), [1, 3])
    }
}
