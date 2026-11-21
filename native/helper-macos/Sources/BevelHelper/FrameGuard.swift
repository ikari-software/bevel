import CoreGraphics

/// Mirror of `Bevel.Pal.Abstractions.FrameGuard` (bevel-tm1n): the one rule for "is this window still
/// where the caller thinks". Reposition is compare-and-set when the request carries `expected`, so a
/// correction computed from an out-of-date snapshot never stamps its stale x/y/width onto a window.
enum FrameGuard {
    /// Points of slack per edge: AX and CoreGraphics round fractional frames differently.
    static let tolerance: CGFloat = 2

    static func matches(live: CGRect, expected: CGRect) -> Bool {
        abs(live.minX - expected.minX) <= tolerance && abs(live.minY - expected.minY) <= tolerance
            && abs(live.width - expected.width) <= tolerance && abs(live.height - expected.height) <= tolerance
    }
}
