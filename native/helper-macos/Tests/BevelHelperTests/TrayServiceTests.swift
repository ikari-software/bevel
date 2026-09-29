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

    // MARK: - Self-test re-arm on the limited→live transition (bevel-qcd9)

    /// Reproduces the TRANSITION this bug is about: the self-test runs once while ungranted (the normal
    /// cold-start case — Screen Recording is rarely pre-granted), then the grant arrives LATER in the
    /// SAME session. Before the fix, `ensureSelfTested`'s one-shot cache meant that later arrival was
    /// invisible: the cached verdict (computed when there was nothing to capture) kept gating live mode
    /// forever, so `captureOk` was NEVER actually exercised for this session. This is the pure decision
    /// `ensureSelfTested` now consults — no real self-test or real grant needed to prove the state
    /// machine, same seam style as `trayGlyphLayout`.
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

    /// The tray sizing oscillated repeatedly before landing on "don't trim": the shipped rule scales the
    /// whole captured menu-bar CELL uniformly to the tray box (the caller passes the window dims), so each
    /// item keeps its true menu-bar proportion — icons large, text ("exo") small with its padding. These
    /// lock the pure scaling math: the cell fills the box height independent of the captured window height
    /// (fullH — the source of the half-size regression), width stays proportional, and extreme aspect
    /// shrinks vertically under the 512 clamp.
    func testTrayGlyphSizing() {
        let box: CGFloat = 32

        // Every item fills the box at ONE uniform height (matches the menu bar), independent of the
        // captured window height (fullH varied here to prove it — the half-size regression came from fullH).
        let squareTight = TrayServiceImpl.trayGlyphLayout(gw: 20, gh: 20, fullH: 22)
        let squareTall  = TrayServiceImpl.trayGlyphLayout(gw: 20, gh: 20, fullH: 60)
        XCTAssertEqual(squareTight.drawnH, box, accuracy: 0.6, "a square glyph fills the box")
        XCTAssertEqual(squareTight.drawnH, squareTall.drawnH, accuracy: 0.001,
                       "size must NOT depend on captured window height (the half-size regression)")

        // A full-bleed app icon, a padded system glyph, and a wide text strip ("exo") ALL render at the
        // same height — no "one big, others tiny", no shrunk-then-blown-up oscillation.
        let app  = TrayServiceImpl.trayGlyphLayout(gw: 30, gh: 30, fullH: 31)
        let text = TrayServiceImpl.trayGlyphLayout(gw: 42, gh: 12, fullH: 33)
        XCTAssertEqual(app.drawnH, box, accuracy: 0.6, "app icon fills the box")
        XCTAssertEqual(text.drawnH, box, accuracy: 0.6, "text ('exo') fills the box too, like the menu bar")

        // Wide items keep their TRUE width (not squashed to a square).
        XCTAssertGreaterThan(text.drawnW, text.drawnH)

        // An extreme-aspect glyph is clamped at 512px wide and shrinks VERTICALLY, not squashed to full height.
        let extreme = TrayServiceImpl.trayGlyphLayout(gw: 2000, gh: 20, fullH: 30)
        XCTAssertEqual(extreme.canvasW, 512)
        XCTAssertLessThan(extreme.drawnH, box, "extreme aspect shrinks vertically under the 512 width clamp")

        // Canvas is the fixed box; glyph vertically centred.
        XCTAssertEqual(text.canvasH, 32)
        XCTAssertEqual(app.dstY, (CGFloat(app.canvasH) - app.drawnH) / 2, accuracy: 0.01)
    }
}
