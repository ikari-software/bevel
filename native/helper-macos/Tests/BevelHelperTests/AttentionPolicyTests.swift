import XCTest
@testable import BevelHelper

/// bevel-qb19 — decision-table coverage for `AttentionPolicy`: the attention signal is the fact
/// of a window newly appearing or its title changing while its app is NOT foreground (the Dock's
/// AX tree exposes no requestUserAttention state — spike 2026-11). Pure: no window server, no
/// Accessibility grant.

final class AttentionPolicyTests: XCTestCase {
    private let now: UInt64 = 1_700_000_000_000

    func testNewWindowWhileBackgroundTriggersAttention() {
        let at = AttentionPolicy.attentionAt(
            existed: false, previousTitle: "", previousAttentionAt: 0,
            currentTitle: "Doc", pid: 42, foregroundPID: 7, nowMs: now)
        XCTAssertEqual(at, now)
    }

    func testTitleChangeWhileBackgroundTriggersNewTimestamp() {
        let at = AttentionPolicy.attentionAt(
            existed: true, previousTitle: "Doc", previousAttentionAt: 111,
            currentTitle: "Doc (edited)", pid: 42, foregroundPID: 7, nowMs: now)
        XCTAssertEqual(at, now, "a title change while background re-stamps, not keeps the old value")
    }

    func testNoTriggerWhenAppIsForeground() {
        // Title changed, but the app IS the foreground app: consumed, not raised.
        let at = AttentionPolicy.attentionAt(
            existed: true, previousTitle: "Doc", previousAttentionAt: 111,
            currentTitle: "Doc!", pid: 42, foregroundPID: 42, nowMs: now)
        XCTAssertEqual(at, 0)
    }

    func testNewWindowOfForegroundAppDoesNotTrigger() {
        let at = AttentionPolicy.attentionAt(
            existed: false, previousTitle: "", previousAttentionAt: 0,
            currentTitle: "Doc", pid: 42, foregroundPID: 42, nowMs: now)
        XCTAssertEqual(at, 0)
    }

    func testFrameOnlyChangeDoesNotTriggerAndKeepsStickyValue() {
        let at = AttentionPolicy.attentionAt(
            existed: true, previousTitle: "Doc", previousAttentionAt: 111,
            currentTitle: "Doc", pid: 42, foregroundPID: 7, nowMs: now)
        XCTAssertEqual(at, 111, "same title (frame-only move/resize) must not retrigger or clear")
    }

    func testRePollWithUnchangedTitleDoesNotRetrigger() {
        let at = AttentionPolicy.attentionAt(
            existed: true, previousTitle: "Doc", previousAttentionAt: 0,
            currentTitle: "Doc", pid: 42, foregroundPID: 7, nowMs: now)
        XCTAssertEqual(at, 0)
    }

    func testNoForegroundAppAtAllStillTriggers() {
        let at = AttentionPolicy.attentionAt(
            existed: false, previousTitle: "", previousAttentionAt: 0,
            currentTitle: "Doc", pid: 42, foregroundPID: nil, nowMs: now)
        XCTAssertEqual(at, now)
    }

    func testTitleContentIsNeverInspectedOnlyTheFactOfChange() {
        // A title that merely LOOKS urgent ("(7) urgent!") while background must not behave
        // differently from any other change — and the same title twice must not trigger.
        let first = AttentionPolicy.attentionAt(
            existed: true, previousTitle: "chat", previousAttentionAt: 0,
            currentTitle: "(7) urgent!", pid: 42, foregroundPID: 7, nowMs: now)
        XCTAssertEqual(first, now, "fact of change only — content is irrelevant, but change still triggers")
        let second = AttentionPolicy.attentionAt(
            existed: true, previousTitle: "(7) urgent!", previousAttentionAt: now,
            currentTitle: "(7) urgent!", pid: 42, foregroundPID: 7, nowMs: now + 500)
        XCTAssertEqual(second, now, "unchanged title never retriggers, whatever the content")
    }
}
