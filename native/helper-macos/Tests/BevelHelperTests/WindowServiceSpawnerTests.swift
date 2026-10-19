import XCTest
import AppKit
import ApplicationServices
@testable import BevelHelper

/// U13/R20: deterministic WindowService coverage against the WindowSpawner rig.
///
/// The pre-existing `WindowServiceTests` enumerate whatever windows happen to be open
/// on the machine — non-deterministic, and quietly vacuous when the runner lacks the
/// permission needed to read window titles (every `for win in windows` loop just skips).
/// These tests instead script a KNOWN window via the spawner and assert the correlation
/// path handles it: enumeration finds it by its exact CGWindowID, a rename is reflected,
/// and a close removes it.
///
/// Gate: reading another app's window title requires Accessibility (AX titles) or Screen
/// Recording (CGWindowList titles); without one, `describe()` drops the untitled window.
/// So these skip cleanly unless the test runner is AX-trusted — window management simply
/// cannot work without the grant, and a skip states that honestly rather than passing
/// vacuously. Grant your terminal/Xcode Accessibility to run them.
final class WindowServiceSpawnerTests: XCTestCase {

    // MARK: - Gate

    private func requireWindowAccessOrSkip() throws {
        guard AXIsProcessTrusted() else {
            throw XCTSkip("""
                Needs Accessibility permission for the test runner. Grant your terminal \
                (or Xcode) Accessibility in System Settings ▸ Privacy & Security ▸ \
                Accessibility, then re-run. Without it the helper cannot read the \
                spawned window's title and correctly drops it, so there is nothing \
                deterministic to assert.
                """)
        }
        // AXIsProcessTrusted() IS NOT ENOUGH. It answers "does this process claim the grant",
        // which comes back true for a binary launched from an Accessibility-granted terminal —
        // while every real cross-app AX query still returns nothing, because the grant is keyed to
        // a signing identity/binary that a freshly built `swift test` bundle does not have. The
        // symptom is brutal to diagnose from here: AXWindows answers .success with an empty (or
        // unusable) list for EVERY app, so the helper never AX-correlates the spawned window, never
        // reads its AX title, and correctly drops it at the no-title gate — and the test fails with
        // "window not found" as though the helper were broken.
        //
        // So prove AX actually WORKS: some other regular app on this machine must expose at least
        // one AX window that maps to a real CGWindowID. If not a single one does, AX is non-functional
        // for this runner and these tests cannot mean anything — skip, as this gate always intended.
        guard axCanSeeSomeoneElsesWindow() else {
            throw XCTSkip("""
                Accessibility reports trusted, but no cross-app AX window is readable from this \
                test runner — AXWindows is empty or unusable for every running app. That is a \
                grant that does not really apply to this binary (TCC keys Accessibility to a \
                signing identity / binary path, and a freshly built test bundle has none), not a \
                bug in the helper: without AX titles the helper correctly drops untitled windows. \
                Run these from an AX-granted host, e.g. the signed dev app's test target, or grant \
                Accessibility to the exact binary running the tests.
                """)
        }
    }

    /// True when at least one OTHER regular app exposes an AX window that resolves to a real
    /// CGWindowID. The predicate is deliberately the SAME one `WindowServiceImpl`'s correlation
    /// depends on — `_AXUIElementGetWindow` returning success with a non-zero id — because anything
    /// weaker passes when AX is useless:
    ///
    /// Measured on an unauthorized runner, every regular app returns a ONE-element AXWindows array
    /// holding the *application* element rather than a window: its AXTitle is the app's name, its
    /// AXPosition is kAXErrorAttributeUnsupported (-25205), and `_AXUIElementGetWindow` answers
    /// kAXErrorIllegalArgument (-25201). A "can I read AXPosition?" check is therefore not enough —
    /// some app elements (Finder's) DO answer AXPosition, which made this gate pass while not one
    /// window on the machine was actually correlatable.
    ///
    /// One success anywhere is enough; apps that answer nothing are skipped rather than counted as
    /// evidence either way.
    private func axCanSeeSomeoneElsesWindow() -> Bool {
        let own = getpid()
        for app in NSWorkspace.shared.runningApplications
        where app.activationPolicy == .regular && app.processIdentifier != own {
            let axApp = AXUIElementCreateApplication(app.processIdentifier)
            var windows: CFTypeRef?
            guard AXUIElementCopyAttributeValue(axApp, kAXWindowsAttribute as CFString, &windows) == .success,
                  let list = windows as? [AXUIElement], !list.isEmpty else { continue }
            for axWin in list {
                var cgID: CGWindowID = 0
                if _AXUIElementGetWindow(axWin, &cgID) == .success, cgID != 0 { return true }
            }
        }
        return false
    }

    // MARK: - Tests

    /// Enumeration finds the spawner's window by its exact CGWindowID, with the title
    /// the spawner set — proving the CGWindowList→AX correlation and the AX-title
    /// fallback in `describe()`.
    func testEnumerateFindsSpawnedWindow() throws {
        try requireWindowAccessOrSkip()
        let spawner = try SpawnerHarness.launch()
        defer { spawner.quit() }

        let title = "BevelSpawnerAlpha-\(UUID().uuidString.prefix(8))"
        let cgID = try spawner.openWindow(key: "a", x: 400, y: 400, w: 520, h: 360, title: title)

        let svc = WindowServiceImpl(expectedKey: "test-key")
        defer { svc.shutdown() } // stop this instance's reconciliation poll promptly
        let win = try waitForWindow(svc, cgID: cgID, timeout: 5.0)

        XCTAssertEqual(win.windowID, String(cgID), "enumerate must find the exact window we spawned")
        XCTAssertEqual(win.title, title, "the AX-title fallback should surface the spawner's window title")
        XCTAssertEqual(win.pid, Int32(spawner.pid), "the enumerated window must belong to the spawner process")
        XCTAssertGreaterThan(win.frame.width, 0)
        XCTAssertGreaterThan(win.frame.height, 0)
        XCTAssertFalse(win.isMinimized)
    }

    /// A rename is reflected in a subsequent enumeration — the title comes from live AX,
    /// not a stale cache.
    func testRenameIsReflectedInEnumeration() throws {
        try requireWindowAccessOrSkip()
        let spawner = try SpawnerHarness.launch()
        defer { spawner.quit() }

        let original = "BevelSpawnerBefore-\(UUID().uuidString.prefix(8))"
        let cgID = try spawner.openWindow(key: "a", x: 420, y: 420, w: 480, h: 340, title: original)
        let svc = WindowServiceImpl(expectedKey: "test-key")
        defer { svc.shutdown() }
        _ = try waitForWindow(svc, cgID: cgID, timeout: 5.0)

        let renamed = "BevelSpawnerAfter-\(UUID().uuidString.prefix(8))"
        try spawner.rename(key: "a", title: renamed)

        let updated = try waitForWindow(svc, cgID: cgID, matching: { $0.title == renamed }, timeout: 5.0)
        XCTAssertEqual(updated.title, renamed)
    }

    /// Closing the window removes it from enumeration — the "window gone" path the
    /// reconciliation poll depends on (bevel-m2.3).
    func testCloseRemovesWindowFromEnumeration() throws {
        try requireWindowAccessOrSkip()
        let spawner = try SpawnerHarness.launch()
        defer { spawner.quit() }

        let title = "BevelSpawnerGamma-\(UUID().uuidString.prefix(8))"
        let cgID = try spawner.openWindow(key: "a", x: 440, y: 440, w: 460, h: 320, title: title)
        let svc = WindowServiceImpl(expectedKey: "test-key")
        defer { svc.shutdown() }
        _ = try waitForWindow(svc, cgID: cgID, timeout: 5.0)

        try spawner.close(key: "a")

        // A closed window leaves CGWindowList only once the NSWindow deallocs; under
        // full-suite load (many background polls) that teardown can lag, so allow generous
        // time. The poll returns as soon as it's gone, so the happy path stays fast.
        let removed = waitUntil(timeout: 12.0) {
            !svc.enumerateWindows().contains { $0.windowID == String(cgID) }
        }
        XCTAssertTrue(removed, "the closed window must disappear from enumeration")
    }

    // MARK: - Enumeration polling helpers

    /// Poll `enumerateWindows()` until a window with `cgID` appears (optionally matching
    /// an extra predicate), or fail after `timeout`.
    @discardableResult
    private func waitForWindow(
        _ svc: WindowServiceImpl,
        cgID: CGWindowID,
        matching predicate: ((Bevel_Helper_V1_TaskbarWindow) -> Bool)? = nil,
        timeout: TimeInterval,
        file: StaticString = #filePath,
        line: UInt = #line
    ) throws -> Bevel_Helper_V1_TaskbarWindow {
        let deadline = Date().addingTimeInterval(timeout)
        var last: Bevel_Helper_V1_TaskbarWindow?
        repeat {
            if let match = svc.enumerateWindows().first(where: {
                $0.windowID == String(cgID) && (predicate?($0) ?? true)
            }) {
                return match
            }
            last = svc.enumerateWindows().first { $0.windowID == String(cgID) }
            Thread.sleep(forTimeInterval: 0.15)
        } while Date() < deadline

        XCTFail(
            "window \(cgID) not found within \(timeout)s (last seen title=\(last?.title ?? "<absent>"))",
            file: file, line: line)
        throw SpawnerHarness.HarnessError.timeout("enumerate window \(cgID)")
    }

    private func waitUntil(timeout: TimeInterval, _ condition: () -> Bool) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        repeat {
            if condition() { return true }
            Thread.sleep(forTimeInterval: 0.15)
        } while Date() < deadline
        return condition()
    }
}
