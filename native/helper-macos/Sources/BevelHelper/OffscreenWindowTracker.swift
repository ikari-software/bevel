import CoreGraphics
import Foundation

/// Policy for windows that are OFF-SCREEN and have NO accessibility element — the one population
/// where "live window" and "closed-window tombstone" look identical in a single observation.
///
/// A window that was just closed leaves a CGWindowList entry (name and bounds intact) for a while;
/// it has no AX element and is off-screen, exactly like (a) a live window on another Space —
/// which `SpaceContext` classifies without guessing — and (b) a live window of an app that never
/// exposes AX (hidden with Cmd-H, or an Electron build with its AX API off). For (b) the only
/// signal left is time: a tombstone leaves the list, a live window does not.
///
/// So: a window is DROPPED until it has been observed off-screen-and-AX-less for
/// `confirmationTicks` consecutive reconcile passes, then TRUSTED for as long as CG keeps listing
/// it. A tombstone that outlives the window is self-correcting the same way — once CG drops the
/// entry the button closes through the normal closed-diff.
///
/// Counting happens once per reconcile pass (`advance`), never per `describe()` call: describe runs
/// from several paths per tick and must stay a pure read (`isConfirmedLive`).
final class OffscreenWindowTracker: @unchecked Sendable {
    /// 8 reconcile ticks × 500 ms = ~4 s.
    static let confirmationTicks = 8

    private let lock = NSLock()
    private var streak: [CGWindowID: Int] = [:]
    private var confirmed = Set<CGWindowID>()

    /// Read-only: has this window earned trust?
    func isConfirmedLive(_ window: CGWindowID) -> Bool {
        lock.withLock { confirmed.contains(window) }
    }

    /// One reconcile pass. `candidates` = windows off-screen with no AX element this pass;
    /// `listed` = every window id CGWindowList currently carries.
    func advance(candidates: Set<CGWindowID>, listed: Set<CGWindowID>) {
        lock.withLock {
            for id in candidates {
                let n = streak[id, default: 0] + 1
                if n >= Self.confirmationTicks {
                    confirmed.insert(id)
                    streak[id] = nil
                } else {
                    streak[id] = n
                }
            }
            // A window that stopped being a candidate (came on-screen, gained AX) restarts its count.
            streak = streak.filter { candidates.contains($0.key) }
            // Gone from the CG list entirely → forget it (the closed-diff owns the button).
            confirmed.formIntersection(listed)
        }
    }
}
