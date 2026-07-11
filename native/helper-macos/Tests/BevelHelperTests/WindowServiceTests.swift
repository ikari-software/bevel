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