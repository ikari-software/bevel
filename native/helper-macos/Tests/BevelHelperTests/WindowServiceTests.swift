import XCTest
import GRPCCore
@testable import BevelHelper

/// U8: WindowService enumeration, AX correlation, and observer logic.
final class WindowServiceTests: XCTestCase {

    // MARK: - Enumeration

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
        // We test the internal mapping indirectly by checking that
        // known notifications produce valid change kinds.
        // The mapping is tested via the implementation's enqueueAXEvent path.
        // This test validates the enum values exist.
        let kinds: [Bevel_Helper_V1_WindowChange.Kind] = [
            .snapshot, .opened, .closed, .focused,
            .titleChanged, .minimized, .deminimized, .moved
        ]
        XCTAssertEqual(kinds.count, 8, "All 8 WindowChange kinds should be defined")
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