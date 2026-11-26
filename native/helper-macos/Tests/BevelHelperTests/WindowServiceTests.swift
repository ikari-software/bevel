import XCTest
import GRPCCore
import AppKit
@testable import BevelHelper

/// U8: WindowService enumeration, AX correlation, and observer logic.
final class WindowServiceTests: XCTestCase {

    // MARK: - Enumeration

    func testLegacyThumbnailEncoderFitsWithinBounds() {
        // bevel-tzx: the shared encoder (LegacyWindowCapture.png(from:maxW:maxH:)) must fit a
        // 400x200 image inside 240x160 preserving the 2:1 aspect — 240x120, never upscaled.
        let provider = CGDataProvider(data: Data(repeating: 0x7F, count: 400 * 200 * 4) as CFData)!
        let image = CGImage(width: 400, height: 200, bitsPerComponent: 8, bitsPerPixel: 32,
                            bytesPerRow: 400 * 4, space: CGColorSpaceCreateDeviceRGB(),
                            bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue),
                            provider: provider, decode: nil, shouldInterpolate: false,
                            intent: .defaultIntent)!

        let png = LegacyWindowCapture.png(from: image, maxW: 240, maxH: 160)
        XCTAssertNotNil(png)

        let decoded = NSBitmapImageRep(data: png!)?.cgImage
        XCTAssertNotNil(decoded)
        XCTAssertLessThanOrEqual(decoded!.width, 240)
        XCTAssertLessThanOrEqual(decoded!.height, 160)
        XCTAssertEqual(decoded!.width, 240)   // 400*0.6 — the binding constraint is width
        XCTAssertEqual(decoded!.height, 120)  // aspect preserved at 2:1
    }

    func testEnumerateWindowsReturnsArray() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        let windows = svc.enumerateWindows()
        // On a real macOS desktop this should return at least some windows.
        // We test that the call doesn't crash and returns a valid array.
        XCTAssertNotNil(windows)
        // All windows should have non-empty window_id.
        for win in windows {
            XCTAssertFalse(win.windowID.isEmpty, "Every window must have a non-empty window_id")
            XCTAssertFalse(win.appName.isEmpty, "Every window must have a non-empty app_name")
        }
    }

    func testEnumerateWindowsExcludesOwnProcess() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        let ownPID = String(getpid())
        let windows = svc.enumerateWindows()
        let ownWindows = windows.filter { String($0.pid) == ownPID }
        XCTAssertTrue(ownWindows.isEmpty, "Own process windows must be excluded from enumeration")
    }

    func testParentProcessExcludesOnlyReservedShellChrome() {
        let parent: pid_t = 4242
        let svc = WindowServiceImpl(expectedKey: "test-key", parentPID: parent)

        XCTAssertTrue(svc.isShellChrome(pid: parent, title: "Bevel Desktop"))
        XCTAssertTrue(svc.isShellChrome(pid: parent, title: "Bevel Taskbar"))
        XCTAssertFalse(svc.isShellChrome(pid: parent, title: "Exploring - /Users"))
        XCTAssertFalse(svc.isShellChrome(pid: parent, title: "Properties"))
        XCTAssertFalse(svc.isShellChrome(pid: 9999, title: "Bevel Desktop"))
    }

    // MARK: - offscreenGhostReason (bevel-0mur / bevel-j48j)

    /// bevel-j48j: Beeper's ordered-out Electron window now lands on the CURRENT Space
    /// (CGSCopySpacesForWindows → [1]), so the no-Space gate no longer catches it. The remaining
    /// tell is kCGWindowAlpha == 0: a never-shown window is fully transparent, while a Cmd-H
    /// hidden window keeps alpha=1 (measured 2026-11-24: HideTest cg=9848 hidden → alpha=1,
    /// spaces=[1], still in AX; Beeper phantom cg=4719 → alpha=0, spaces=[1], no AX).
    func testOffscreenGhostDropsAlphaZeroWindowOnCurrentSpace() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        // No ?? [] fallback: production's spacesOf returns nil for an unknown window, and the
        // nil/[] distinction is load-bearing for isOnNoSpace.
        let spaces = SpaceContext(current: [1], spacesOf: { [4719: [1]][$0] })
        let entry: [String: Any] = [
            kCGWindowIsOnscreen as String: false,
            kCGWindowAlpha as String: CGFloat(0),
        ]
        let reason = svc.offscreenGhostReason(cgID: 4719, hasAX: false, entry: entry, spaces: spaces)
        XCTAssertEqual(reason, "offscreen-no-ax alpha=0 (ordered out)",
                       "off-screen AX-less alpha=0 window on the current Space is ordered out — drop")
    }

    /// bevel-0mur, extended: a window known to be on NO Space is ordered out even WITH an AX
    /// element — minimized/hidden windows keep their Space, so they never hit this.
    func testOffscreenGhostDropsNoSpaceWindowEvenWithAX() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        let spaces = SpaceContext(current: [1], spacesOf: { [236: []][$0] })
        let entry: [String: Any] = [
            kCGWindowIsOnscreen as String: false,
            kCGWindowAlpha as String: CGFloat(1),
        ]
        let reason = svc.offscreenGhostReason(cgID: 236, hasAX: true, entry: entry, spaces: spaces)
        XCTAssertNotNil(reason, "on-no-space window is ordered out regardless of AX")
        XCTAssertTrue(reason?.contains("no-space") == true, "reason names the no-space signal, got: \(reason ?? "nil")")
    }

    /// The tracker population must be untouched by the new alpha gate: a hidden AX-less window
    /// keeps alpha=1, so it falls through to the tombstone/tracker decision, not the alpha drop.
    func testOffscreenGhostDoesNotAlphaDropHiddenWindowWithAlphaOne() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        let spaces = SpaceContext(current: [1], spacesOf: { [1895: [1]][$0] })
        let entry: [String: Any] = [
            kCGWindowIsOnscreen as String: false,
            kCGWindowAlpha as String: CGFloat(1),
        ]
        let reason = svc.offscreenGhostReason(cgID: 1895, hasAX: false, entry: entry, spaces: spaces)
        XCTAssertEqual(reason, "offscreen-no-ax (unconfirmed tombstone)",
                       "alpha=1 off-screen window must reach the tracker, not the alpha drop (got: \(reason ?? "nil"))")
    }

    /// A missing alpha key (older macOS) must fail OPEN — fall through to the tracker, never drop.
    func testOffscreenGhostTreatsMissingAlphaAsNotOrderedOut() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        let spaces = SpaceContext(current: [1], spacesOf: { [1895: [1]][$0] })
        let entry: [String: Any] = [kCGWindowIsOnscreen as String: false]
        let reason = svc.offscreenGhostReason(cgID: 1895, hasAX: false, entry: entry, spaces: spaces)
        XCTAssertEqual(reason, "offscreen-no-ax (unconfirmed tombstone)")
    }

    func testOffscreenGhostKeepsOnScreenWindow() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        let spaces = SpaceContext(current: [1], spacesOf: { _ in [1] })
        let entry: [String: Any] = [
            kCGWindowIsOnscreen as String: true,
            kCGWindowAlpha as String: CGFloat(0),   // even a transparent ON-screen window is visible territory
        ]
        XCTAssertNil(svc.offscreenGhostReason(cgID: 4717, hasAX: false, entry: entry, spaces: spaces))
    }

    func testOffscreenGhostKeepsOtherSpaceWindow() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        let spaces = SpaceContext(current: [1], spacesOf: { [5: [459]][$0] })
        let entry: [String: Any] = [
            kCGWindowIsOnscreen as String: false,
            kCGWindowAlpha as String: CGFloat(1),
        ]
        XCTAssertNil(svc.offscreenGhostReason(cgID: 5, hasAX: false, entry: entry, spaces: spaces),
                     "a live window on another Space stays listed so activation can switch to it")
    }

    func testOffscreenGhostKeepsAXCorrelatedWindowOnCurrentSpace() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        let spaces = SpaceContext(current: [1], spacesOf: { _ in [1] })
        let entry: [String: Any] = [
            kCGWindowIsOnscreen as String: false,
            kCGWindowAlpha as String: CGFloat(1),
        ]
        XCTAssertNil(svc.offscreenGhostReason(cgID: 4717, hasAX: true, entry: entry, spaces: spaces),
                     "AX-correlated (e.g. minimized/hidden) windows carry a live signal")
    }

    /// bevel-0mur, original case: no-Space WITHOUT an AX element is still dropped.
    func testOffscreenGhostDropsNoSpaceWindowWithoutAX() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        let spaces = SpaceContext(current: [1], spacesOf: { [236: []][$0] })
        let entry: [String: Any] = [kCGWindowIsOnscreen as String: false]
        XCTAssertEqual(svc.offscreenGhostReason(cgID: 236, hasAX: false, entry: entry, spaces: spaces),
                       "on-no-space (ordered out)")
    }

    /// Hardening: the on-screen gate runs FIRST, so an on-screen window with a (transiently)
    /// known-empty Spaces answer is never dropped — fullscreen/Space-warp transitions.
    func testOffscreenGhostKeepsOnScreenWindowEvenOnNoSpace() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        let spaces = SpaceContext(current: [1], spacesOf: { [77: []][$0] })
        let entry: [String: Any] = [
            kCGWindowIsOnscreen as String: true,
            kCGWindowAlpha as String: CGFloat(1),
        ]
        XCTAssertNil(svc.offscreenGhostReason(cgID: 77, hasAX: false, entry: entry, spaces: spaces),
                     "on-screen is checked before the no-Space gate")
    }

    /// Gate order pin: hasAX=true + alpha=0 + current Space must be KEPT (AX guard precedes the
    /// alpha gate) — moving the alpha gate above the AX guard would drop it and fail this test.
    func testOffscreenGhostKeepsAXWindowWithAlphaZero() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        let spaces = SpaceContext(current: [1], spacesOf: { [4717: [1]][$0] })
        let entry: [String: Any] = [
            kCGWindowIsOnscreen as String: false,
            kCGWindowAlpha as String: CGFloat(0),
        ]
        XCTAssertNil(svc.offscreenGhostReason(cgID: 4717, hasAX: true, entry: entry, spaces: spaces))
    }

    /// Gate order pin: other-Space keep precedes the alpha gate — an alpha=0 window on an unshown
    /// Space is a live window and must stay listed.
    func testOffscreenGhostKeepsOtherSpaceWindowWithAlphaZero() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        let spaces = SpaceContext(current: [1], spacesOf: { [5: [459]][$0] })
        let entry: [String: Any] = [
            kCGWindowIsOnscreen as String: false,
            kCGWindowAlpha as String: CGFloat(0),
        ]
        XCTAssertNil(svc.offscreenGhostReason(cgID: 5, hasAX: false, entry: entry, spaces: spaces),
                     "isOnOtherSpace keep must precede the alpha=0 gate")
    }

    /// bevel-j48j hardening: a window that was EVER observed opaque (alpha>0) and later reports
    /// alpha=0 off-screen is an opacity hide (setAlphaValue: 0), not an ordered-out window — the
    /// alpha gate must not fire; it falls through to the tracker like any hidden window.
    func testOffscreenGhostDoesNotAlphaDropWindowOnceSeenOpaque() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        let spaces = SpaceContext(current: [1], spacesOf: { [900: [1]][$0] })
        let visible: [String: Any] = [
            kCGWindowIsOnscreen as String: false,
            kCGWindowAlpha as String: CGFloat(1),
        ]
        let hidden: [String: Any] = [
            kCGWindowIsOnscreen as String: false,
            kCGWindowAlpha as String: CGFloat(0),
        ]
        // Pass 1: alpha=1 observed (records maxAlpha=1), falls to the tracker.
        _ = svc.offscreenGhostReason(cgID: 900, hasAX: false, entry: visible, spaces: spaces)
        // Pass 2: app hid itself via opacity — must NOT take the alpha=0 drop.
        let reason = svc.offscreenGhostReason(cgID: 900, hasAX: false, entry: hidden, spaces: spaces)
        XCTAssertEqual(reason, "offscreen-no-ax (unconfirmed tombstone)",
                       "once-opaque window goes to the tracker, not the alpha drop (got: \(reason ?? "nil"))")
    }

    /// The tracker-confirmed branch: after confirmationTicks reconcile passes the window is kept.
    func testOffscreenGhostKeepsTrackerConfirmedWindow() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        let spaces = SpaceContext(current: [1], spacesOf: { [4719: [1]][$0] })
        let entry: [String: Any] = [
            kCGWindowIsOnscreen as String: false,
            kCGWindowAlpha as String: CGFloat(1),
        ]
        for _ in 0..<OffscreenWindowTracker.confirmationTicks {
            svc.offscreenTracker.advance(candidates: [4719], listed: [4719])
        }
        XCTAssertNil(svc.offscreenGhostReason(cgID: 4719, hasAX: false, entry: entry, spaces: spaces),
                     "tracker-confirmed live window is kept")
    }

    func testBevelTransientDropsShellPopupsWithoutAX() {
        let parent: pid_t = 4242
        let svc = WindowServiceImpl(expectedKey: "test-key", parentPID: parent)
        let axMap: [CGWindowID: AXUIElement] = [:]

        XCTAssertTrue(svc.isBevelTransient(pid: parent, cgID: 99, axMap: axMap, isMinimized: false))
        XCTAssertTrue(svc.isBevelTransient(
            pid: parent, cgID: 100, axMap: axMap, isMinimized: false, frameWidth: 120, frameHeight: 40))
        XCTAssertFalse(svc.isBevelTransient(
            pid: parent, cgID: 101, axMap: axMap, isMinimized: false, frameWidth: 800, frameHeight: 600))
        XCTAssertFalse(svc.isBevelTransient(pid: 9999, cgID: 99, axMap: axMap, isMinimized: false))
        XCTAssertFalse(svc.isBevelTransient(pid: parent, cgID: 99, axMap: axMap, isMinimized: true))
    }

    func testStableDiffRetainsTitleOnlyForKnownAXWindow() {
        let svc = WindowServiceImpl(expectedKey: "test-key")

        XCTAssertEqual(
            svc.titleForStableDiff(
                current: "", previous: "Before rename", hasAXWindow: true),
            "Before rename")
        XCTAssertEqual(
            svc.titleForStableDiff(
                current: "After rename", previous: "Before rename", hasAXWindow: true),
            "After rename")
        XCTAssertEqual(
            svc.titleForStableDiff(
                current: "", previous: "Phantom", hasAXWindow: false),
            "")
        XCTAssertEqual(
            svc.titleForStableDiff(
                current: "", previous: nil, hasAXWindow: true),
            "")
    }

    func testEnumerateWindowsHasExpectedFields() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        let windows = svc.enumerateWindows()
        for win in windows {
            // All fields should be present (even if empty/zero).
            XCTAssertNotNil(win.windowID)
            // pid should be non-zero for any real window.
            XCTAssertNotEqual(win.pid, 0, "Window \(win.windowID) should have a non-zero pid")
            // Non-minimized windows must have a real on-screen frame — describe()
            // drops zero-area phantoms. Minimized windows are exempt: enumeration
            // uses .optionAll to keep them listed (bevel-m2.3) and they may report
            // no on-screen frame.
            if !win.isMinimized {
                XCTAssertGreaterThan(win.frame.width, 0, "Window \(win.windowID) frame width should be > 0")
                XCTAssertGreaterThan(win.frame.height, 0, "Window \(win.windowID) frame height should be > 0")
            }
        }
    }

    // MARK: - AX correlation

    /// bevel-3rs: `_AXUIElementGetWindow` fails to resolve a CGWindowID for some AX
    /// windows (Finder folder/browser windows are the known case). The frame-comparison
    /// fallback must then correlate the AX window to its same-PID CG window by frame, so
    /// the window lands in axMap, the AX-title fallback fills in the folder name, and the
    /// window is KEPT instead of dropped at the no-title gate.
    func testFrameFallbackCorrelatesFinderLikeWindowBySameFrame() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        // A real Finder folder window: non-zero frame reported by both AX and CGWindowList.
        let axFrame = CGRect(x: 200, y: 120, width: 900, height: 640)
        let candidates: [(cgID: CGWindowID, frame: CGRect)] = [
            (cgID: 1618, frame: CGRect(x: 200, y: 120, width: 900, height: 640)),
        ]
        XCTAssertEqual(
            svc.frameMatchedCGID(axFrame: axFrame, candidates: candidates, claimed: []),
            1618,
            "An AX window whose CGWindowID could not be resolved by the SPI must correlate to its same-frame CG window")
    }

    /// Sub-pixel rounding between AX and CGWindowList coordinates must still match.
    func testFrameFallbackToleratesSubPixelDrift() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        let axFrame = CGRect(x: 200.4, y: 119.6, width: 900.8, height: 640.2)
        let candidates: [(cgID: CGWindowID, frame: CGRect)] = [
            (cgID: 1618, frame: CGRect(x: 200, y: 120, width: 900, height: 640)),
        ]
        XCTAssertEqual(
            svc.frameMatchedCGID(axFrame: axFrame, candidates: candidates, claimed: []),
            1618)
    }

    /// Guardrail: a zero-area AX frame must NOT match anything — otherwise the fallback
    /// would latch onto the title-less phantom layer-0 strips the no-title gate rejects,
    /// re-opening the floodgates the gate exists to close.
    func testFrameFallbackRejectsZeroAreaAXFrame() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        let candidates: [(cgID: CGWindowID, frame: CGRect)] = [
            (cgID: 42, frame: CGRect(x: 0, y: 0, width: 0, height: 0)),
            (cgID: 43, frame: CGRect(x: 10, y: 10, width: 500, height: 30)),
        ]
        XCTAssertNil(
            svc.frameMatchedCGID(
                axFrame: CGRect(x: 0, y: 0, width: 0, height: 0),
                candidates: candidates, claimed: []),
            "A zero-area AX frame must never correlate — junk stays out of axMap")
    }

    /// A titled Finder-like window and a title-less junk window differ ONLY in whether a
    /// real same-frame AX window exists. When no candidate frame matches (the junk case),
    /// no correlation is made, so describe() keeps hasAX=false and the no-title gate drops it.
    func testFrameFallbackDoesNotCorrelateWhenNoFrameMatches() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        let candidates: [(cgID: CGWindowID, frame: CGRect)] = [
            (cgID: 1618, frame: CGRect(x: 200, y: 120, width: 900, height: 640)),
        ]
        XCTAssertNil(
            svc.frameMatchedCGID(
                axFrame: CGRect(x: 999, y: 999, width: 64, height: 64),
                candidates: candidates, claimed: []),
            "An AX window with no same-frame CG candidate must not be correlated")
    }

    /// Already-claimed CGWindowIDs (resolved directly by the SPI) must not be re-used by
    /// the fallback, so two AX windows can't collapse onto one CG window.
    func testFrameFallbackSkipsClaimedCGIDs() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        let frame = CGRect(x: 200, y: 120, width: 900, height: 640)
        let candidates: [(cgID: CGWindowID, frame: CGRect)] = [
            (cgID: 1618, frame: frame),
            (cgID: 1619, frame: frame),
        ]
        XCTAssertEqual(
            svc.frameMatchedCGID(axFrame: frame, candidates: candidates, claimed: [1618]),
            1619,
            "The fallback must skip CGWindowIDs already correlated by the SPI")
    }

    func testCorrelateAXElementsReturnsMap() {
        // This exercises the private _AXUIElementGetWindow path.
        // We can't guarantee results without real windows, but it shouldn't crash.
        let svc = WindowServiceImpl(expectedKey: "test-key")
        let windows = svc.enumerateWindows()
        XCTAssertNotNil(windows)
        // If we got windows, they should have successfully correlated to AX elements.
        // The isFocused flag exercises the correlation path.
        for win in windows {
            // isFocused should be a boolean (not crash-producing).
            let _ = win.isFocused
        }
    }

    // MARK: - Window control (error paths)

    func testActivateInvalidWindowID() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        XCTAssertThrowsError(try svc.activateWindow(windowID: "999999999")) { error in
            guard let rpcError = error as? RPCError else {
                XCTFail("Expected RPCError, got \(error)")
                return
            }
            XCTAssertEqual(rpcError.code, .notFound)
        }
    }

    func testActivateEmptyWindowID() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        XCTAssertThrowsError(try svc.activateWindow(windowID: "")) { error in
            guard let rpcError = error as? RPCError else {
                XCTFail("Expected RPCError, got \(error)")
                return
            }
            XCTAssertEqual(rpcError.code, .invalidArgument)
        }
    }

    func testMinimizeInvalidWindowID() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        XCTAssertThrowsError(try svc.minimizeWindow(windowID: "999999999")) { error in
            guard let rpcError = error as? RPCError else {
                XCTFail("Expected RPCError, got \(error)")
                return
            }
            XCTAssertEqual(rpcError.code, .notFound)
        }
    }

    func testRestoreInvalidWindowID() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        XCTAssertThrowsError(try svc.restoreWindow(windowID: "999999999")) { error in
            guard let rpcError = error as? RPCError else {
                XCTFail("Expected RPCError, got \(error)")
                return
            }
            XCTAssertEqual(rpcError.code, .notFound)
        }
    }

    func testRestoreAndActivateInvalidWindowID() {
        // bevel-nxic: the atomic op resolves the window like activate/restore, so an unknown id is notFound.
        let svc = WindowServiceImpl(expectedKey: "test-key")
        XCTAssertThrowsError(try svc.restoreAndActivate(windowID: "999999999")) { error in
            guard let rpcError = error as? RPCError else {
                XCTFail("Expected RPCError, got \(error)")
                return
            }
            XCTAssertEqual(rpcError.code, .notFound)
        }
    }

    func testRestoreAndActivateEmptyWindowID() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        XCTAssertThrowsError(try svc.restoreAndActivate(windowID: "")) { error in
            guard let rpcError = error as? RPCError else {
                XCTFail("Expected RPCError, got \(error)")
                return
            }
            XCTAssertEqual(rpcError.code, .invalidArgument)
        }
    }

    func testCloseInvalidWindowID() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        XCTAssertThrowsError(try svc.closeWindow(windowID: "999999999")) { error in
            guard let rpcError = error as? RPCError else {
                XCTFail("Expected RPCError, got \(error)")
                return
            }
            XCTAssertEqual(rpcError.code, .notFound)
        }
    }

    func testZoomInvalidWindowID() {
        // bevel-kpkx: the zoom verb resolves the window like close/minimize, so an unknown id is notFound.
        let svc = WindowServiceImpl(expectedKey: "test-key")
        XCTAssertThrowsError(try svc.zoomWindow(windowID: "999999999")) { error in
            guard let rpcError = error as? RPCError else {
                XCTFail("Expected RPCError, got \(error)")
                return
            }
            XCTAssertEqual(rpcError.code, .notFound)
        }
    }

    func testHideAppUnknownBundleIDReturnsFalse() {
        // bevel-d2j9: no running app carries this bundle id, so hide() is never called and the
        // reply is ok=false. (Only ever probe with ids that match NOTHING — a matching id would
        // really hide that app, including this test runner.)
        let svc = WindowServiceImpl(expectedKey: "test-key")
        XCTAssertFalse(svc.hideApp(bundleID: "com.bevel.nonexistent-app-for-tests"))
    }

    func testHideAppEmptyBundleIDReturnsFalse() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        XCTAssertFalse(svc.hideApp(bundleID: ""))
    }

    // MARK: - AXError → RPCError mapping

    func testAXErrorInvalidUIElementMapsToNotFound() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        // We can't directly call the private method, but we can test through
        // the window control methods which use it internally.
        XCTAssertThrowsError(try svc.activateWindow(windowID: "1")) { error in
            guard let rpcError = error as? RPCError else {
                XCTFail("Expected RPCError, got \(error)")
                return
            }
            // Should be .notFound (invalidUIElement) or .notFound (window not in list)
            // Both are acceptable — the key is that we get a proper RPCError.
            XCTAssertTrue(
                rpcError.code == .notFound || rpcError.code == .invalidArgument,
                "Expected .notFound or .invalidArgument, got \(rpcError.code)"
            )
        }
    }

    // MARK: - AXNotification mapping

    func testAXNotificationToChangeKindMapsCorrectly() {
        // Direct assertions on the real mapping — the previous version of this test only counted
        // enum cases and could never fail (review: testing, direct-verified).
        XCTAssertEqual(axNotificationToChangeKind(kAXFocusedWindowChangedNotification), .focused)
        XCTAssertEqual(axNotificationToChangeKind(kAXTitleChangedNotification), .titleChanged)
        XCTAssertEqual(axNotificationToChangeKind(kAXWindowMiniaturizedNotification), .minimized)
        XCTAssertEqual(axNotificationToChangeKind(kAXWindowDeminiaturizedNotification), .deminimized)
        XCTAssertEqual(axNotificationToChangeKind(kAXMovedNotification), .moved)
        XCTAssertEqual(axNotificationToChangeKind(kAXResizedNotification), .moved)
        XCTAssertEqual(axNotificationToChangeKind(kAXWindowCreatedNotification), .opened)
        XCTAssertEqual(axNotificationToChangeKind(kAXUIElementDestroyedNotification), .closed)
        XCTAssertNil(axNotificationToChangeKind("AXSomethingWeNeverRegistered"))
    }

    func testAXErrorToRPCMapsErrorCodes() {
        // Pins the not-found vs transient vs internal split the RPC surface promises.
        let svc = WindowServiceImpl(expectedKey: "test-key")
        XCTAssertEqual(svc.axErrorToRPC(.invalidUIElement, windowID: "7").code, .notFound)
        XCTAssertEqual(svc.axErrorToRPC(.cannotComplete, windowID: "7").code, .unavailable)
        XCTAssertEqual(svc.axErrorToRPC(.apiDisabled, windowID: "7").code, .internalError)
    }

    // MARK: - Reconciliation poll

    func testReconciliationDoesNotCrash() {
        // The reconciliation poll runs on a background Task.
        // We just verify that the service can be created and torn down
        // without crashing (the poll runs in the background).
        _ = WindowServiceImpl(expectedKey: "test-key")
        // Give the poll a moment to run at least once.
        let expectation = self.expectation(description: "poll ran")
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) {
            expectation.fulfill()
        }
        wait(for: [expectation], timeout: 3.0)
        // Deinit should clean up observers and poll task.
        // No crash = pass.
    }

    // MARK: - Subscriber lifecycle

    func testSubscriberLifecycle() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        // Subscriber management is internal, but we can verify that
        // enumerateWindows works after multiple calls (which exercise
        // the state lock and store).
        for _ in 0..<5 {
            let windows = svc.enumerateWindows()
            XCTAssertNotNil(windows)
        }
        svc.shutdown()
    }

    // MARK: - Shutdown

    func testShutdownIsIdempotent() {
        let svc = WindowServiceImpl(expectedKey: "test-key")
        svc.shutdown()
        svc.shutdown() // second call should not crash
        // enumerateWindows after shutdown should still work (it's stateless)
        let windows = svc.enumerateWindows()
        XCTAssertNotNil(windows)
    }
}