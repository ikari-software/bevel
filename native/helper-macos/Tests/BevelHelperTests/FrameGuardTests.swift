import XCTest
import CoreGraphics
@testable import BevelHelper

/// bevel-tm1n: the pre-warmed Filer is built hidden at (0,453,1920,987); a mitigation pass that still
/// held that frame snapped the shown, centred 800x600 window to x=0 w=1920. Same cases as the C# twin.
final class FrameGuardTests: XCTestCase {
    func testToleratesRoundingButNotADifferentFrame() {
        let f = CGRect(x: 100, y: 200, width: 800, height: 600)
        XCTAssertTrue(FrameGuard.matches(live: f, expected: CGRect(x: 101, y: 200, width: 800, height: 602)))
        XCTAssertFalse(FrameGuard.matches(live: f, expected: CGRect(x: 100, y: 200, width: 1920, height: 600)))
        XCTAssertFalse(FrameGuard.matches(live: f, expected: CGRect(x: 100, y: 453, width: 800, height: 600)))
    }

    func testTheMeasuredRaceIsRefused() {
        let liveCentred = CGRect(x: 880, y: 435, width: 800, height: 600)
        let staleHidden = CGRect(x: 0, y: 453, width: 1920, height: 987)
        XCTAssertFalse(FrameGuard.matches(live: liveCentred, expected: staleHidden))
    }
}
