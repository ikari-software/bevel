import XCTest
import CoreGraphics
@testable import BevelHelper

/// bevel-lezo follow-up: on macOS 27 the status-item glyphs exist only in WindowServer's final composite,
/// so the helper pulls them out by comparing the menu-bar strip WITH the menu-bar windows (composite)
/// against the same strip WITHOUT them (background). These pin the pure matte maths.
final class MenuBarMatteTests: XCTestCase {

    /// One premultiplied RGBA pixel repeated `n` times.
    private func px(_ r: UInt8, _ g: UInt8, _ b: UInt8, _ a: UInt8 = 255, n: Int = 1) -> [UInt8] {
        Array([[r, g, b, a]].joined()).repeated(n)
    }

    func testWhiteInkOverDarkBackgroundRecoversFullAlpha() {
        let out = MenuBarMatte.extract(composite: px(255, 255, 255), background: px(30, 40, 50),
                                       colourLayer: nil, ink: .white)
        XCTAssertEqual(out, [255, 255, 255, 255])
    }

    func testUnchangedPixelIsTransparent() {
        let out = MenuBarMatte.extract(composite: px(30, 40, 50), background: px(30, 40, 50),
                                       colourLayer: nil, ink: .white)
        XCTAssertEqual(out, [0, 0, 0, 0])
    }

    func testHalfBlendGivesHalfAlpha() {
        // 50 % white over (0,0,0) composites to ~128.
        let out = MenuBarMatte.extract(composite: px(128, 128, 128), background: px(0, 0, 0),
                                       colourLayer: nil, ink: .white)
        XCTAssertEqual(Int(out[3]), 128, accuracy: 2)
        XCTAssertEqual(out[0], out[3], "premultiplied white: RGB equals alpha")
    }

    func testNoiseBelowFloorIsDropped() {
        // Glass shimmer / compression noise between the two captures must not leave a haze.
        let out = MenuBarMatte.extract(composite: px(36, 46, 56), background: px(30, 40, 50),
                                       colourLayer: nil, ink: .white)
        XCTAssertEqual(out[3], 0)
    }

    func testBlackInkOverLightBackground() {
        let full = MenuBarMatte.extract(composite: px(0, 0, 0), background: px(230, 230, 230),
                                        colourLayer: nil, ink: .black)
        XCTAssertEqual(full, [0, 0, 0, 255])
        let none = MenuBarMatte.extract(composite: px(230, 230, 230), background: px(230, 230, 230),
                                        colourLayer: nil, ink: .black)
        XCTAssertEqual(none[3], 0)
    }

    func testColourLayerSitsOnTop() {
        // An opaque red pixel from the coloured layer wins over the white matte under it.
        let out = MenuBarMatte.extract(composite: px(255, 0, 0), background: px(30, 30, 30),
                                       colourLayer: px(255, 0, 0, 255), ink: .white)
        XCTAssertEqual(out, [255, 0, 0, 255])
        // A transparent coloured-layer pixel leaves the matte alone.
        let through = MenuBarMatte.extract(composite: px(255, 255, 255), background: px(30, 30, 30),
                                           colourLayer: px(0, 0, 0, 0), ink: .white)
        XCTAssertEqual(through, [255, 255, 255, 255])
    }

    func testInkFollowsBackgroundLuminance() {
        XCTAssertEqual(MenuBarMatte.ink(forBackground: px(20, 30, 60, n: 8)), .white)   // dark bar → white glyphs
        XCTAssertEqual(MenuBarMatte.ink(forBackground: px(235, 235, 240, n: 8)), .black) // light bar → black glyphs
    }

    func testPixelRectMapsItemIntoStrip() {
        // Strip captured from x=1000 (points) at 2x; an item at x=1500, 24 pt wide, 3..27 pt tall.
        let r = MenuBarMatte.pixelRect(item: CGRect(x: 1500, y: 3, width: 24, height: 24),
                                       strip: CGRect(x: 1000, y: 0, width: 1560, height: 30),
                                       imageWidth: 3120, imageHeight: 60)
        XCTAssertEqual(r, MenuBarMatte.PixelRect(x: 1000, y: 6, width: 48, height: 48))
    }

    func testPixelRectClipsAndRejectsOutside() {
        let strip = CGRect(x: 1000, y: 0, width: 100, height: 30)
        XCTAssertNil(MenuBarMatte.pixelRect(item: CGRect(x: 2000, y: 0, width: 20, height: 20),
                                            strip: strip, imageWidth: 200, imageHeight: 60))
        let clipped = MenuBarMatte.pixelRect(item: CGRect(x: 1090, y: 0, width: 20, height: 30),
                                             strip: strip, imageWidth: 200, imageHeight: 60)
        XCTAssertEqual(clipped, MenuBarMatte.PixelRect(x: 180, y: 0, width: 20, height: 60))
    }

    func testCropCopiesRows() {
        // 3x2 image, pixel value = index; crop the 2x1 at (1,1).
        let img: [UInt8] = (0..<6).flatMap { i in [UInt8(i), 0, 0, 255] }
        let c = MenuBarMatte.crop(img, imageWidth: 3, MenuBarMatte.PixelRect(x: 1, y: 1, width: 2, height: 1))
        XCTAssertEqual(c, [4, 0, 0, 255, 5, 0, 0, 255])
    }
}

private extension Array {
    func repeated(_ n: Int) -> [Element] { (0..<n).flatMap { _ in self } }
}
