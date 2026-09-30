import ApplicationServices
import XCTest
@testable import BevelHelper

/// Decision-table coverage for the window service's policy units. All pure — no window server,
/// no Accessibility grant — so unlike the spawner tests these always run.

// MARK: - OffscreenWindowTracker

final class OffscreenWindowTrackerTests: XCTestCase {
    private let n = OffscreenWindowTracker.confirmationTicks

    func testUnconfirmedUntilItSurvivesEnoughConsecutivePasses() {
        let t = OffscreenWindowTracker()
        for _ in 0..<(n - 1) { t.advance(candidates: [7], listed: [7]) }
        XCTAssertFalse(t.isConfirmedLive(7), "a tombstone-looking window must be dropped until it has persisted")
        t.advance(candidates: [7], listed: [7])
        XCTAssertTrue(t.isConfirmedLive(7))
    }

    func testStreakRestartsWhenTheWindowStopsBeingACandidate() {
        let t = OffscreenWindowTracker()
        for _ in 0..<(n - 1) { t.advance(candidates: [7], listed: [7]) }
        t.advance(candidates: [], listed: [7])           // came on-screen for one pass
        t.advance(candidates: [7], listed: [7])          // candidate again: count starts over
        XCTAssertFalse(t.isConfirmedLive(7))
    }

    func testConfirmedWindowStaysTrustedWhileListedEvenIfNotACandidate() {
        let t = OffscreenWindowTracker()
        for _ in 0..<n { t.advance(candidates: [7], listed: [7]) }
        t.advance(candidates: [], listed: [7])
        XCTAssertTrue(t.isConfirmedLive(7), "trust must not flicker off when the window is briefly on-screen")
    }

    func testConfirmedWindowIsForgottenOnceCGNoLongerListsIt() {
        let t = OffscreenWindowTracker()
        for _ in 0..<n { t.advance(candidates: [7], listed: [7]) }
        t.advance(candidates: [], listed: [])
        XCTAssertFalse(t.isConfirmedLive(7))
        // A recycled id starts from scratch.
        t.advance(candidates: [7], listed: [7])
        XCTAssertFalse(t.isConfirmedLive(7))
    }

    func testWindowsAreCountedIndependently() {
        let t = OffscreenWindowTracker()
        for _ in 0..<n { t.advance(candidates: [1], listed: [1, 2]) }
        XCTAssertTrue(t.isConfirmedLive(1))
        XCTAssertFalse(t.isConfirmedLive(2))
    }
}

// MARK: - SpaceContext

final class SpaceContextTests: XCTestCase {
    private func ctx(current: Set<Int>, _ table: [CGWindowID: [Int]]) -> SpaceContext {
        SpaceContext(current: current, spacesOf: { table[$0] ?? [] })
    }

    func testWindowOnASpaceNoDisplayShowsIsOnAnotherSpace() {
        // Mirrors the real Jump Desktop capture: displays show {1, 532}; its windows sit on 459.
        let c = ctx(current: [1, 532], [25946: [459], 30426: [532]])
        XCTAssertTrue(c.isOnOtherSpace(25946))
        XCTAssertFalse(c.isOnOtherSpace(30426))
    }

    func testStickyWindowSpanningACurrentSpaceIsNotOnAnotherSpace() {
        XCTAssertFalse(ctx(current: [1], [5: [1, 2, 3]]).isOnOtherSpace(5))
    }

    func testUnknownNeverClaimsAnotherSpace() {
        XCTAssertFalse(ctx(current: [1], [:]).isOnOtherSpace(5), "no spaces reported → unknown, not 'other'")
        XCTAssertFalse(ctx(current: [], [5: [459]]).isOnOtherSpace(5), "no current spaces → SkyLight unavailable")
    }
}

// MARK: - ActivationPlan

final class ActivationPlanTests: XCTestCase {
    func testAXWindowOnCurrentSpaceKeepsTheOriginalLadder() {
        XCTAssertEqual(ActivationPlan.steps(hasAX: true, isOnOtherSpace: false),
                       [.activateApp, .focusViaWindowServer, .raiseViaAX])
    }

    func testAXLessWindowIsMadeKeyThroughTheWindowServerAfterAppActivation() {
        XCTAssertEqual(ActivationPlan.steps(hasAX: false, isOnOtherSpace: false),
                       [.activateApp, .focusViaWindowServer])
    }

    func testOtherSpaceWindowSwitchesSpaceThenFocusesThroughTheWindowServer() {
        XCTAssertEqual(ActivationPlan.steps(hasAX: false, isOnOtherSpace: true),
                       [.switchSpace, .focusViaWindowServer])
        XCTAssertEqual(ActivationPlan.steps(hasAX: true, isOnOtherSpace: true),
                       [.switchSpace, .focusViaWindowServer, .raiseViaAX])
    }

    func testSwitchSpaceIsFirstOnOtherSpacePlans() {
        for ax in [true, false] {
            let steps = ActivationPlan.steps(hasAX: ax, isOnOtherSpace: true)
            XCTAssertEqual(steps.first, .switchSpace)
            XCTAssertFalse(ActivationPlan.steps(hasAX: ax, isOnOtherSpace: false).contains(.switchSpace))
        }
    }

    func testSameSpacePlansAlwaysWindowAddressSoMultiSpaceAppsDoNotFrontASibling() {
        for ax in [true, false] {
            let steps = ActivationPlan.steps(hasAX: ax, isOnOtherSpace: false)
            XCTAssertTrue(steps.contains(.focusViaWindowServer))
            XCTAssertEqual(steps.first, .activateApp)
        }
    }

    func testRaiseIsAlwaysLastSoTheClickedWindowEndsTopmost() {
        for ax in [true, false] {
            for other in [true, false] {
                let steps = ActivationPlan.steps(hasAX: ax, isOnOtherSpace: other)
                if let i = steps.firstIndex(of: .raiseViaAX) { XCTAssertEqual(i, steps.count - 1) }
            }
        }
    }
}

// MARK: - FocusResolver

final class FocusResolverTests: XCTestCase {
    private let elA = AXUIElementCreateApplication(101)
    private let elB = AXUIElementCreateApplication(102)
    private let elGhost = AXUIElementCreateApplication(103)

    /// `z` = true depth rank (0 = frontmost); defaults to the id so lists read front-to-back.
    private func win(_ id: CGWindowID, onScreen: Bool = true, minimized: Bool = false, ax: AXUIElement? = nil, z: Int? = nil)
        -> FocusResolver.Candidate {
        .init(cgID: id, isOnScreen: onScreen, isMinimized: minimized, axElement: ax, zRank: z ?? Int(id))
    }
    private let noSPI: (AXUIElement) -> CGWindowID? = { _ in nil }

    func testIdentityWinsOverZOrder() {
        let windows = [win(1, ax: elA), win(2, ax: elB)]   // 1 is topmost, but AX says 2 is focused
        XCTAssertEqual(FocusResolver.focusedWindow(evidence: .element(elB), windows: windows, windowIDForElement: noSPI), 2)
    }

    func testSPIIdentifiesAWindowWithNoCorrelatedElement() {
        let windows = [win(1), win(2)]
        XCTAssertEqual(FocusResolver.focusedWindow(evidence: .element(elGhost), windows: windows, windowIDForElement: { _ in 2 }), 2)
    }

    func testNothingFocusedPressesNothing() {
        // Desktop/menu-bar focus: a fallback here would light up an unrelated window.
        XCTAssertNil(FocusResolver.focusedWindow(evidence: .noFocusedWindow, windows: [win(1)], windowIDForElement: noSPI))
    }

    func testGhostFocusFallsBackToTopmostVisibleWindow() {
        // Nessie class: AX names a title-less surface we don't list.
        let windows = [win(1, onScreen: false), win(2, minimized: true), win(3), win(4)]
        XCTAssertEqual(FocusResolver.focusedWindow(evidence: .element(elGhost), windows: windows, windowIDForElement: noSPI), 3)
    }

    func testSPIResultForAnUnlistedWindowStillFallsBack() {
        XCTAssertEqual(FocusResolver.focusedWindow(evidence: .element(elGhost), windows: [win(5)], windowIDForElement: { _ in 999 }), 5)
    }

    func testUnavailableAXFallsBackToTopmostVisibleWindow() {
        // Kiro class: no AX tree at all.
        XCTAssertEqual(FocusResolver.focusedWindow(evidence: .unavailable, windows: [win(8), win(9)], windowIDForElement: noSPI), 8)
    }

    func testTopmostIsDecidedByDepthRankNotByListOrder() {
        // The regression: list order said 37724 first (it always led the .optionAll list), while the
        // window server had raised 29668. Rank must win.
        let windows = [win(37724, z: 5), win(29668, z: 2)]
        XCTAssertEqual(FocusResolver.focusedWindow(evidence: .unavailable, windows: windows, windowIDForElement: noSPI), 29668)
    }

    func testNoVisibleWindowMeansNothingToPress() {
        let windows = [win(1, onScreen: false), win(2, minimized: true)]
        XCTAssertNil(FocusResolver.focusedWindow(evidence: .unavailable, windows: windows, windowIDForElement: noSPI))
    }
}
