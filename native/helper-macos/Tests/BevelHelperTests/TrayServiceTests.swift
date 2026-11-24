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
            // id is "ownerPID:windowNumber" for a window-list item, or "ax:pid:index" for an item only
            // Accessibility reports (bevel-lezo: macOS 27 dropped status items from the window list).
            if let ax = AxTrayMerge.parse(itemID: item.itemID) {
                XCTAssertEqual(ax.pid, item.ownerPid, "an AX item's id carries its owner pid")
            } else {
                let parts = item.itemID.split(separator: ":")
                XCTAssertEqual(parts.count, 2, "item id must be 'pid:windowNumber' or 'ax:pid:index' — got \(item.itemID)")
                XCTAssertNotNil(Int(parts[0]))
                XCTAssertNotNil(Int(parts[1]))
            }
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

    /// M3-B + bevel-yg7h: the capture path. The FIRST enumeration never waits for the self-test —
    /// it returns limited-mode items immediately; once the detached self-test completes, a later
    /// poll upgrades to live pixels (isLive) when Screen Recording is granted. Without the grant,
    /// everything stays limited-mode — both are valid.
    /// Establishes the WindowServer connection first (SCK aborts otherwise, CGS_REQUIRE_INIT).
    @MainActor
    func testCapturePathIsLiveWhenGrantedElseLimited() async {
        _ = NSApplication.shared
        NSApp.setActivationPolicy(.prohibited)
        let svc = TrayServiceImpl(expectedKey: "test-key")

        let first = await svc.enumerateWithCapture()
        XCTAssertTrue(first.allSatisfy { !$0.isLive },
                      "the first enumeration must not await the self-test — limited-mode items immediately (bevel-yg7h)")

        guard CGPreflightScreenCaptureAccess() else {
            let items = await svc.enumerateWithCapture()
            XCTAssertTrue(items.allSatisfy { !$0.isLive }, "without the grant, all items are limited-mode")
            return
        }
        // Granted: poll until the detached self-test lands and the capture upgrades to live.
        for _ in 0..<20 {
            let items = await svc.enumerateWithCapture()
            if items.contains(where: { $0.isLive }) { return }
            try? await Task.sleep(nanoseconds: 500_000_000)
        }
        XCTFail("with Screen Recording granted, a later poll should live-capture some items")
    }

    /// forwardClick returns false (and posts no event) for a malformed or vanished item id — the
    /// safe guard path. We deliberately do NOT test a real click, which would pop a live menu.
    func testForwardClickRejectsUnknownItems() {
        let svc = TrayServiceImpl(expectedKey: "test-key")
        XCTAssertFalse(svc.forwardClick(itemID: "not-an-id", button: .left, modifiers: 0))
        XCTAssertFalse(svc.forwardClick(itemID: "1:2147483000", button: .left, modifiers: 0),
                       "a windowNumber that isn't on screen must not resolve to a click")
        XCTAssertFalse(svc.forwardClick(itemID: "ax:1:999999", button: .left, modifiers: 0),
                       "an AX index that doesn't exist must not resolve to a press")
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
        // nil = inconclusive (nothing capturable): not a verdict, and never "broken".
        XCTAssertNotEqual(ok, false, "self-test should pass (working capture, or limited mode without a grant)")
    }

    // MARK: - Self-test re-arm on the limited→live transition (bevel-qcd9)

    /// Reproduces the TRANSITION this bug is about: the self-test runs once while ungranted (the normal
    /// cold-start case — Screen Recording is rarely pre-granted), then the grant arrives LATER in the
    /// SAME session. Before the fix, `ensureSelfTested`'s one-shot cache meant that later arrival was
    /// invisible: the cached verdict (computed when there was nothing to capture) kept gating live mode
    /// forever, so `captureOk` was NEVER actually exercised for this session. This is the pure decision
    /// `ensureSelfTested` now consults — no real self-test or real grant needed to prove the state
    /// machine, as a pure static seam — same seam style as `trayGlyphLayout`.
    func testEnumerationDoesNotWaitForTheSelfTest() {
        // bevel-yg7h: while the self-test has not completed, live capture is gated OFF regardless
        // of the grant — the first enumeration returns limited-mode (app icon) items immediately
        // instead of awaiting the self-test. Only a completed, enabling, granted verdict captures.
        XCTAssertFalse(TrayServiceImpl.shouldCaptureLive(selfTestDone: false, liveEnabled: true, granted: true))
        XCTAssertFalse(TrayServiceImpl.shouldCaptureLive(selfTestDone: false, liveEnabled: false, granted: false))
        XCTAssertFalse(TrayServiceImpl.shouldCaptureLive(selfTestDone: true, liveEnabled: false, granted: true))
        XCTAssertFalse(TrayServiceImpl.shouldCaptureLive(selfTestDone: true, liveEnabled: true, granted: false))
        XCTAssertTrue(TrayServiceImpl.shouldCaptureLive(selfTestDone: true, liveEnabled: true, granted: true))
    }
    /// machine, same seam style as `trayGlyphLayout`.
    func testPixelHashIsStableAndContentSensitive() {
        // bevel-m3.2.1: identical bytes hash equal, one flipped byte changes the hash, and the same
        // bytes at a different geometry hash differently (width/height are mixed in).
        func makeImage(_ bytes: [UInt8], width: Int, height: Int) -> CGImage {
            let provider = CGDataProvider(data: Data(bytes) as CFData)!
            return CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32,
                           bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(),
                           bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue),
                           provider: provider, decode: nil, shouldInterpolate: false,
                           intent: .defaultIntent)!
        }
        let pixels = [UInt8](repeating: 0xAB, count: 4 * 4 * 4)
        let a = makeImage(pixels, width: 4, height: 4)
        let b = makeImage(pixels, width: 4, height: 4)
        XCTAssertEqual(TrayServiceImpl.pixelHash(a), TrayServiceImpl.pixelHash(b))

        var changed = pixels; changed[0] ^= 0xFF
        XCTAssertNotEqual(TrayServiceImpl.pixelHash(a), TrayServiceImpl.pixelHash(makeImage(changed, width: 4, height: 4)))

        XCTAssertNotEqual(TrayServiceImpl.pixelHash(a), TrayServiceImpl.pixelHash(makeImage(pixels, width: 2, height: 8)))

        // ce-code-review: an image with no readable pixel bytes (nil dataProvider data) must hash nil,
        // never a width/height-only value — otherwise every same-size frame is a cache "hit" and a
        // stale PNG is served forever.
        XCTAssertNil(TrayServiceImpl.pixelHash(width: 4, height: 4, data: nil))
        XCTAssertNil(TrayServiceImpl.pixelHash(width: 2, height: 8, data: nil))
    }

    func testSelfTestReArmsOnceOnTheUngrantedToGrantedTransition() {
        // 1. Never run yet → always run (first capture attempt triggers it, granted or not).
        XCTAssertTrue(TrayServiceImpl.selfTestShouldRun(granted: false, selfTestDone: false, grantPreviouslySeen: false))
        XCTAssertTrue(TrayServiceImpl.selfTestShouldRun(granted: true, selfTestDone: false, grantPreviouslySeen: false))

        // 2. Already run, STILL ungranted on a later poll → do not re-run every 2s (this is the common
        //    steady state before the user ever grants Screen Recording).
        XCTAssertFalse(TrayServiceImpl.selfTestShouldRun(granted: false, selfTestDone: true, grantPreviouslySeen: false))

        // 3. THE BUG: already run (while ungranted), and the grant has just appeared. The cached verdict
        //    never called selfTestCapture() — it must get exactly one more chance to, now that there is
        //    something real to capture. Before bevel-qcd9's fix this returned false (stuck forever).
        XCTAssertTrue(TrayServiceImpl.selfTestShouldRun(granted: true, selfTestDone: true, grantPreviouslySeen: false),
                      "a grant obtained mid-session must re-arm the self-test exactly once")

        // 4. Already run AND that re-arm has happened (grantPreviouslySeen caught up) → steady state
        //    again, no re-running on every subsequent granted poll.
        XCTAssertFalse(TrayServiceImpl.selfTestShouldRun(granted: true, selfTestDone: true, grantPreviouslySeen: true))

        // 5. A revoke-then-re-grant (System Settings toggle) is a NEW rising edge and re-arms again.
        XCTAssertFalse(TrayServiceImpl.selfTestShouldRun(granted: false, selfTestDone: true, grantPreviouslySeen: true))
        XCTAssertTrue(TrayServiceImpl.selfTestShouldRun(granted: true, selfTestDone: true, grantPreviouslySeen: false))
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


    // Review (2026-11-21): the Changes stream's cadence. With Accessibility-only items (macOS 27) a
    // 250 ms glyph-only tick runs between full enumerations; without any (macOS 26) the loop must keep
    // its old one-tick-per-enumeration cadence, so window items aren't re-captured four times a second.
    func testPollCadenceTicksFastOnlyWhenThereAreAxItems() {
        let twoSeconds: UInt64 = 2_000_000_000
        XCTAssertEqual(TrayServiceImpl.tickDelay(hasAxItems: true, enumerationInterval: twoSeconds),
                       TrayServiceImpl.glyphRefreshInterval)
        XCTAssertEqual(TrayServiceImpl.tickDelay(hasAxItems: false, enumerationInterval: twoSeconds), twoSeconds)
        XCTAssertEqual(TrayServiceImpl.tickDelay(hasAxItems: true, enumerationInterval: 100_000_000), 100_000_000)
    }

    func testFullEnumerationIsDueOnceTheIntervalHasElapsed() {
        XCTAssertFalse(TrayServiceImpl.isFullEnumerationDue(elapsed: 899_999_999, interval: 900_000_000))
        XCTAssertTrue(TrayServiceImpl.isFullEnumerationDue(elapsed: 900_000_000, interval: 900_000_000))
    }

    // A self-test that had nothing to capture (no window item with a backing store, no AX item on a
    // display, e.g. everything parked by consolidation at launch) has learnt nothing. Latching live
    // mirroring OFF on that is the bevel-p6g4 failure shape again; it must stay undecided and retry.
    func testSelfTestCaptureVerdict() {
        XCTAssertEqual(TrayServiceImpl.captureVerdict(attempted: true, nonBlank: true), true)
        XCTAssertEqual(TrayServiceImpl.captureVerdict(attempted: true, nonBlank: false), false)
        XCTAssertNil(TrayServiceImpl.captureVerdict(attempted: false, nonBlank: false))
    }
}
