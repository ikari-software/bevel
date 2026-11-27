import Foundation

/// The attention signal for a taskbar window (bevel-qb19).
///
/// `NSApplication.requestUserAttention` (the Dock bounce) is NOT observable cross-process:
/// the Dock's AX tree exposes no attention attribute (spike 2026-11 — the AXApplicationDockItem's
/// full attribute list is AXChildren/AXFrame/AXIsApplicationRunning/AXParent/AXPosition/
/// AXProgressValue/AXRole/AXRoleDescription/AXSelected/AXShownMenuUIElement/AXSize/AXStatusLabel/
/// AXSubrole/AXTitle/AXTopLevelUIElement/AXURL, and the SDK headers define no kAX*Attention*),
/// and `NSDockTile.badgeLabel` belongs to the owning process only. So the shipped signal is the
/// closest public-API proxy: the window NEWLY APPEARED or its TITLE CHANGED while its owning app
/// was NOT the foreground app — the same moments a bounce would be raised. NEVER inferred from
/// title CONTENT, only from the fact of the change/appearance.
///
/// Pure and side-effect free so `WindowPolicyTests`/`AttentionPolicyTests` can drive it without
/// a live WindowServer.
enum AttentionPolicy {
    /// Unix-ms timestamp to stamp on `win`, given what the last accepted pass knew.
    ///
    /// - Parameters:
    ///   - existed: whether this window id was already in the store last pass (same pid).
    ///   - previousTitle: the title stored last pass ("" when the window was not stored).
    ///   - previousAttentionAt: the stored `attention_at` (0 = none).
    ///   - currentTitle: the title this pass computed (after the stable-diff retention).
    ///   - pid: owning process id.
    ///   - foregroundPID: the effective foreign frontmost pid for this pass, if any.
    ///   - nowMs: current unix time in milliseconds (injected for tests).
    /// Minimum gap between two ACCEPTED stamps of the same window (bevel-qb19 review): a
    /// title-chatty background app (chat clients, build tools, "(3) Building…") changes its title
    /// several times a second, and each accepted change would otherwise re-raise attention forever.
    /// A trigger landing within this window of the last accepted stamp is suppressed (the old
    /// stamp stays, so the VM's strictly-newer compare sees no event).
    static let minReTriggerIntervalMs: UInt64 = 3_000

    /// - Returns: `nowMs` when the window newly appeared or its title changed while its app is not
    ///   foreground; `0` when the app IS foreground (attention is consumed, not raised); otherwise
    ///   the previous value unchanged (sticky — frame-only changes and re-passes never retrigger).
    ///   A trigger within `minReTriggerIntervalMs` of the last accepted stamp is suppressed and
    ///   keeps the previous stamp.
    static func attentionAt(
        existed: Bool,
        previousTitle: String,
        previousAttentionAt: UInt64,
        currentTitle: String,
        pid: pid_t,
        foregroundPID: pid_t?,
        nowMs: UInt64
    ) -> UInt64 {
        // Foreground app: no attention is ever raised, and a previously raised one is consumed.
        if pid != 0, pid == foregroundPID { return 0 }
        let triggered = !existed || currentTitle != previousTitle
        guard triggered else { return previousAttentionAt }
        // Per-window minimum re-trigger interval: too soon after the last accepted stamp → keep it.
        // (The `nowMs > previousAttentionAt` guard keeps the subtraction from underflowing on clock skew.)
        if previousAttentionAt != 0, nowMs > previousAttentionAt,
           nowMs - previousAttentionAt < minReTriggerIntervalMs {
            return previousAttentionAt
        }
        return nowMs
    }

    /// Unix time in milliseconds (separated so tests can pass a fixed clock).
    static func nowUnixMs() -> UInt64 {
        UInt64(Date().timeIntervalSince1970 * 1000)
    }
}
