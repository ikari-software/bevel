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

    // MARK: - Minimum re-trigger interval (bevel-qb19 review)

    func testTriggerWithinMinReTriggerIntervalIsSuppressed() {
        // A title-chatty background app re-stamps at now+1000 — inside the 3s window — so the
        // previous stamp is kept (the VM's strictly-newer compare then sees no event).
        let at = AttentionPolicy.attentionAt(
            existed: true, previousTitle: "build", previousAttentionAt: now,
            currentTitle: "build (2)", pid: 42, foregroundPID: 7, nowMs: now + 1_000)
        XCTAssertEqual(at, now, "a trigger within minReTriggerIntervalMs of the last accepted stamp is suppressed")
    }

    func testTriggerAfterMinReTriggerIntervalReStamps() {
        let at = AttentionPolicy.attentionAt(
            existed: true, previousTitle: "build", previousAttentionAt: now,
            currentTitle: "build (9)", pid: 42, foregroundPID: 7,
            nowMs: now + AttentionPolicy.minReTriggerIntervalMs)
        XCTAssertEqual(at, now + AttentionPolicy.minReTriggerIntervalMs,
                       "once the interval has elapsed, a title change re-stamps")
    }

    func testMinReTriggerDoesNotApplyToTheFirstStamp() {
        // previousAttentionAt == 0 (never stamped): the interval must not eat the first event.
        let at = AttentionPolicy.attentionAt(
            existed: true, previousTitle: "a", previousAttentionAt: 0,
            currentTitle: "b", pid: 42, foregroundPID: 7, nowMs: now)
        XCTAssertEqual(at, now)
    }

    func testMinReTriggerSurvivesClockSkewWithoutUnderflow() {
        // nowMs BEFORE the previous stamp (clock skew): must not trap on UInt64 underflow.
        let at = AttentionPolicy.attentionAt(
            existed: true, previousTitle: "a", previousAttentionAt: now,
            currentTitle: "b", pid: 42, foregroundPID: 7, nowMs: now - 500)
        XCTAssertEqual(at, now - 500, "skewed clock: no underflow; the VM's > compare ignores the older value")
    }

    // MARK: - Call-site guards (bevel-qb19 review): WindowServiceImpl.attentionStamp / appPresenceStamp

    func testUnprimedInitialFillNeverStampsAtTheCallSite() {
        // The initial-fill bug: on !primed the old call site passed previousTitle:"" for a window
        // not in the store, so EVERY background window with a title was stamped at helper start.
        let at = WindowServiceImpl.attentionStamp(
            primed: false, previousMatches: false, previous: nil,
            currentTitle: "Doc", pid: 42, foregroundPID: 7, nowMs: now)
        XCTAssertEqual(at, 0, "initial fill must not stamp — no pulse storm at helper start")
    }

    func testPrimedNewWindowStampsAtTheCallSite() {
        let at = WindowServiceImpl.attentionStamp(
            primed: true, previousMatches: false, previous: nil,
            currentTitle: "Doc", pid: 42, foregroundPID: 7, nowMs: now)
        XCTAssertEqual(at, now)
    }

    func testPrimedForegroundAppNeverStampsAtTheCallSite() {
        let at = WindowServiceImpl.attentionStamp(
            primed: true, previousMatches: false, previous: nil,
            currentTitle: "Doc", pid: 42, foregroundPID: 42, nowMs: now)
        XCTAssertEqual(at, 0)
    }

    func testAppPresenceLastWindowCloseDoesNotStamp() {
        // The last window of a backgrounded app closing surfaces the app as a presence entry —
        // that is NOT a launch bounce; the icon-only button must not pulse.
        let at = WindowServiceImpl.appPresenceStamp(
            primed: true, previouslyPresent: false, previousAttentionAt: 0,
            appPID: 42, foregroundPID: 7, ownedWindowLastPass: true, nowMs: now)
        XCTAssertEqual(at, 0, "last-window-close is not an app launch — no stamp")
    }

    func testAppPresenceGenuineLaunchWhileBackgroundStamps() {
        let at = WindowServiceImpl.appPresenceStamp(
            primed: true, previouslyPresent: false, previousAttentionAt: 0,
            appPID: 42, foregroundPID: 7, ownedWindowLastPass: false, nowMs: now)
        XCTAssertEqual(at, now)
    }

    func testAppPresenceForegroundLaunchDoesNotStamp() {
        let at = WindowServiceImpl.appPresenceStamp(
            primed: true, previouslyPresent: false, previousAttentionAt: 0,
            appPID: 42, foregroundPID: 42, ownedWindowLastPass: false, nowMs: now)
        XCTAssertEqual(at, 0)
    }

    func testAppPresenceInitialFillDoesNotStamp() {
        let at = WindowServiceImpl.appPresenceStamp(
            primed: false, previouslyPresent: false, previousAttentionAt: 0,
            appPID: 42, foregroundPID: 7, ownedWindowLastPass: false, nowMs: now)
        XCTAssertEqual(at, 0)
    }

    func testAppPresenceStickyAcrossPasses() {
        let at = WindowServiceImpl.appPresenceStamp(
            primed: true, previouslyPresent: true, previousAttentionAt: 111,
            appPID: 42, foregroundPID: 7, ownedWindowLastPass: false, nowMs: now)
        XCTAssertEqual(at, 111, "a persisting presence entry keeps its sticky stamp")
    }
}
