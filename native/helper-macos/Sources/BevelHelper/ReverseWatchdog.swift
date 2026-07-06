import Foundation

/// Watches for parent process death by polling `getppid()`.
///
/// When a parent process dies on macOS the orphaned child is re-parented to
/// PID 1 (launchd), so checking `getppid()` is reliable and requires no
/// special permissions (unlike `kill(pid, 0)` which SIP may block for PID 1).
struct ReverseWatchdog {
    let parentPID: Int32
    let intervalNanos: UInt64 = 2_000_000_000

    func run() async {
        while true {
            if getppid() != parentPID {
                fputs("BevelHelper: parent PID \(parentPID) died, exiting\n", stderr)
                Foundation.exit(2)
            }
            try? await Task.sleep(nanoseconds: intervalNanos)
        }
    }
}
