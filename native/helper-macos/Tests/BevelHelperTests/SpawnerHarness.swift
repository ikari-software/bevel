import Foundation
import XCTest

/// Test-side driver for the WindowSpawner rig (tests/rigs/macos-lite/WindowSpawner).
///
/// Launches the spawner as a child process and speaks its line protocol over
/// stdin/stdout, blocking on each command's ack so callers synchronize on the window
/// actually existing rather than sleeping. `@unchecked Sendable`: the stdout buffer is
/// guarded by an `NSCondition`, so the background reader thread and the test thread
/// share it safely.
final class SpawnerHarness: @unchecked Sendable {

    enum HarnessError: Error, CustomStringConvertible {
        case binaryMissing(String)
        case timeout(String)
        case badAck(String)
        var description: String {
            switch self {
            case .binaryMissing(let m): "WindowSpawner binary not available: \(m)"
            case .timeout(let m):       "timed out waiting for: \(m)"
            case .badAck(let m):        "unexpected spawner ack: \(m)"
            }
        }
    }

    private let process: Process
    private let stdin: Pipe
    private let cond = NSCondition()
    private var lines: [String] = []      // complete ack lines, guarded by `cond`
    private var pending = Data()          // partial trailing bytes, guarded by `cond`
    private var eof = false               // set when the child closes stdout

    var pid: pid_t { process.processIdentifier }

    private init(process: Process, stdin: Pipe) {
        self.process = process
        self.stdin = stdin
    }

    // MARK: - Launch

    /// Launch the spawner and wait for its `ready` line.
    static func launch(timeout: TimeInterval = 10.0) throws -> SpawnerHarness {
        let binary = try resolveBinary()

        let process = Process()
        process.executableURL = URL(fileURLWithPath: binary)
        let stdin = Pipe()
        let stdout = Pipe()
        process.standardInput = stdin
        process.standardOutput = stdout
        // Leave stderr attached to the test's so any spawner crash is visible in logs.

        let harness = SpawnerHarness(process: process, stdin: stdin)
        stdout.fileHandleForReading.readabilityHandler = { [weak harness] handle in
            let data = handle.availableData
            harness?.ingest(data)
        }

        try process.run()
        _ = try harness.waitForLine(prefix: "ready", timeout: timeout)
        return harness
    }

    /// Resolve the spawner binary: explicit override, then the prebuilt debug binary,
    /// building it once if absent. Throws `binaryMissing` (→ callers XCTSkip/fail) if it
    /// still can't be produced.
    private static func resolveBinary() throws -> String {
        let fm = FileManager.default
        if let override = ProcessInfo.processInfo.environment["BEVEL_WINDOW_SPAWNER"],
           !override.isEmpty, fm.isExecutableFile(atPath: override) {
            return override
        }

        let rig = repoRoot()
            .appendingPathComponent("tests/rigs/macos-lite/WindowSpawner", isDirectory: true)
        let debugBinary = rig.appendingPathComponent(".build/debug/WindowSpawner")
        if fm.isExecutableFile(atPath: debugBinary.path) {
            return debugBinary.path
        }

        // Not built yet — build it once (the rig has no external deps, so this is quick).
        let build = Process()
        build.executableURL = URL(fileURLWithPath: "/usr/bin/env")
        build.arguments = ["swift", "build", "--package-path", rig.path]
        build.standardOutput = FileHandle.nullDevice
        build.standardError = FileHandle.nullDevice
        try? build.run()
        build.waitUntilExit()

        if fm.isExecutableFile(atPath: debugBinary.path) {
            return debugBinary.path
        }
        throw HarnessError.binaryMissing(
            "expected \(debugBinary.path); build it with `swift build --package-path tests/rigs/macos-lite/WindowSpawner`")
    }

    /// Repo root, resolved from this source file's location:
    /// <repo>/native/helper-macos/Tests/BevelHelperTests/SpawnerHarness.swift → up 5.
    private static func repoRoot() -> URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()   // BevelHelperTests
            .deletingLastPathComponent()   // Tests
            .deletingLastPathComponent()   // helper-macos
            .deletingLastPathComponent()   // native
            .deletingLastPathComponent()   // repo root
    }

    // MARK: - Commands

    /// open <key> <x> <y> <w> <h> <title>; returns the window's CGWindowNumber.
    @discardableResult
    func openWindow(key: String, x: Int, y: Int, w: Int, h: Int, title: String,
                    timeout: TimeInterval = 5.0) throws -> CGWindowID {
        send("open \(key) \(x) \(y) \(w) \(h) \(title)")
        let ack = try waitForLine(prefix: "ok open \(key) id=", timeout: timeout)
        guard let idPart = ack.split(separator: "=").last, let cgID = CGWindowID(idPart) else {
            throw HarnessError.badAck(ack)
        }
        return cgID
    }

    func rename(key: String, title: String, timeout: TimeInterval = 5.0) throws {
        send("rename \(key) \(title)")
        _ = try waitForLine(prefix: "ok rename \(key)", timeout: timeout)
    }

    func close(key: String, timeout: TimeInterval = 5.0) throws {
        send("close \(key)")
        _ = try waitForLine(prefix: "ok close \(key)", timeout: timeout)
    }

    /// Best-effort shutdown: ask the spawner to quit, then ensure the process is gone.
    func quit() {
        if process.isRunning {
            send("quit")
            _ = try? waitForLine(prefix: "ok quit", timeout: 2.0)
        }
        if process.isRunning {
            process.terminate()
        }
    }

    // MARK: - I/O

    private func send(_ command: String) {
        guard let data = (command + "\n").data(using: .utf8) else { return }
        stdin.fileHandleForWriting.write(data)
    }

    /// Accumulate stdout bytes into complete lines under the condition lock.
    private func ingest(_ data: Data) {
        cond.lock()
        defer { cond.unlock() }
        if data.isEmpty {
            eof = true
            cond.broadcast()
            return
        }
        pending.append(data)
        while let nl = pending.firstIndex(of: 0x0A) {
            let lineData = pending[pending.startIndex..<nl]
            pending.removeSubrange(pending.startIndex...nl)
            if let line = String(data: lineData, encoding: .utf8) {
                lines.append(line.trimmingCharacters(in: .whitespaces))
            }
        }
        cond.broadcast()
    }

    /// Block until a stdout line begins with `prefix`, consuming lines up to and
    /// including the match. Throws on timeout or premature EOF.
    @discardableResult
    private func waitForLine(prefix: String, timeout: TimeInterval) throws -> String {
        let deadline = Date().addingTimeInterval(timeout)
        cond.lock()
        defer { cond.unlock() }
        var scan = 0
        while true {
            while scan < lines.count {
                let line = lines[scan]
                scan += 1
                if line.hasPrefix(prefix) {
                    lines.removeFirst(scan)
                    return line
                }
            }
            if eof { throw HarnessError.timeout("\(prefix) (spawner stdout closed)") }
            if Date() >= deadline { throw HarnessError.timeout(prefix) }
            _ = cond.wait(until: deadline)
        }
    }
}
