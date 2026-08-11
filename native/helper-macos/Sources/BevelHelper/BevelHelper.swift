import AppKit
import ApplicationServices   // AXIsProcessTrustedWithOptions + kAXTrustedCheckOptionPrompt (TCC prompt)
import Foundation
import GRPCCore
import GRPCNIOTransportHTTP2

/// Socket path for the atexit cleanup hook. A C `atexit` callback cannot capture context,
/// so the path lives at file scope. Removing the socket on every exit path (including the
/// watchdog's `exit()` and SIGTERM/SIGINT) prevents a stale socket file being left behind
/// (review #8/#20).
private nonisolated(unsafe) var gSocketPath: String?

private func removeSocketOnExit() {
    if let path = gSocketPath {
        unlink(path)
    }
}

@main
enum BevelHelper {
    static let version = "0.1.0"

    struct CLIArguments {
        let socketPath: String
        let token: String
        let parentPID: Int32
    }

    enum CLIError: Error, CustomStringConvertible {
        case missing(String)
        case invalid(String)
        var description: String {
            switch self {
            case .missing(let arg): "Missing required argument: --\(arg)"
            case .invalid(let msg): "Invalid argument: \(msg)"
            }
        }
    }

    static func parseArguments() throws -> CLIArguments {
        let args = CommandLine.arguments.dropFirst()
        var socketPath: String?
        var parentPID: Int32?
        var iterator = args.makeIterator()
        while let arg = iterator.next() {
            switch arg {
            case "--socket":
                socketPath = iterator.next()
            case "--parent-pid":
                if let value = iterator.next(), let pid = Int32(value) {
                    parentPID = pid
                } else {
                    throw CLIError.invalid("--parent-pid requires an integer value")
                }
            default:
                break
            }
        }
        // The auth nonce is delivered out-of-band via the environment, not argv, so it is
        // not exposed through `ps`/KERN_PROCARGS2 to same-user processes (review #10).
        let token = ProcessInfo.processInfo.environment["BEVEL_HELPER_TOKEN"]
        guard let socketPath else { throw CLIError.missing("socket") }
        guard let token, !token.isEmpty else {
            throw CLIError.missing("BEVEL_HELPER_TOKEN environment variable")
        }
        guard let parentPID else { throw CLIError.missing("parent-pid") }
        return CLIArguments(socketPath: socketPath, token: token, parentPID: parentPID)
    }

    // Synchronous main-actor entry (was `async throws`). An `async` main runs under the Swift
    // concurrency runtime's own main-thread drain, and calling NSApp.run() inside it does NOT drain the
    // libdispatch main queue — so `DispatchQueue.main.async`/`MainActor.run` hops never execute and the
    // control item never moves (proven live). A synchronous @MainActor main lets NSApp.run() own the
    // main thread the standard AppKit way, which drains the main queue (bevel-7hf4).
    @MainActor
    static func main() throws {
        let args: CLIArguments
        do {
            args = try parseArguments()
        } catch {
            fputs("error: \(error)\n", stderr)
            fputs("usage: BevelHelper --socket <path> --token <nonce> --parent-pid <pid>\n", stderr)
            Foundation.exit(1)
        }

        if args.parentPID <= 0 {
            fputs("error: --parent-pid must be a positive integer\n", stderr)
            Foundation.exit(1)
        }

        if FileManager.default.fileExists(atPath: args.socketPath) {
            try FileManager.default.removeItem(atPath: args.socketPath)
        }
        defer {
            try? FileManager.default.removeItem(atPath: args.socketPath)
        }

        // Ensure the socket is removed on every exit path: the watchdog's exit() and
        // SIGTERM/SIGINT bypass the defer above (review #8/#20).
        gSocketPath = args.socketPath
        atexit(removeSocketOnExit)
        signal(SIGTERM) { _ in Foundation.exit(0) }
        signal(SIGINT) { _ in Foundation.exit(0) }

        // ScreenCaptureKit (tray live capture, §5.3) needs a WindowServer/CGS connection, which a bare
        // CLI process lacks — SCScreenshotManager otherwise aborts with CGS_REQUIRE_INIT. Bring up
        // NSApplication as a prohibited agent (headless: no Dock tile, no menu bar) to establish it.
        _ = NSApplication.shared
        // .accessory (was .prohibited): a prohibited agent can't present the TCC prompts below, so the
        // helper never appears in the Accessibility / Screen Recording lists — forcing the user to add it
        // by hand from inside the .app bundle. .accessory is still headless (no Dock tile, no Cmd-Tab, no
        // menu bar) but may request permission. Still establishes the CGS connection ScreenCaptureKit needs.
        NSApp.setActivationPolicy(.accessory)

        // Register with TCC and PROMPT once, so a Finder-launched helper asks for — and shows up in —
        // Accessibility (window control) and Screen Recording (live tray icons) on its own, instead of the
        // user having to drag BevelHelper out of the bundle. Both self-gate: they only prompt when the
        // grant is missing and are no-ops once granted. (From a terminal it "just works" only because TCC
        // attributes the grant to the already-authorised terminal — Finder launches get neither for free.)
        if !AXIsProcessTrusted() {
            // The key is the CFString value of kAXTrustedCheckOptionPrompt; use the literal so Swift 6
            // strict concurrency doesn't flag the global 'var' as shared mutable state.
            _ = AXIsProcessTrustedWithOptions(["AXTrustedCheckOptionPrompt": true] as CFDictionary)
        }
        if !CGPreflightScreenCaptureAccess() {
            _ = CGRequestScreenCaptureAccess()
        }

        let watchdog = ReverseWatchdog(parentPID: args.parentPID)
        Task { await watchdog.run() }

        let supervision = SupervisionServiceImpl(helperVersion: version, expectedKey: args.token)
        let windowService = WindowServiceImpl(expectedKey: args.token, parentPID: args.parentPID)
        let trayService = TrayServiceImpl(expectedKey: args.token, parentPID: args.parentPID)

        let server = GRPCServer(
            transport: .http2NIOPosix(
                address: .unixDomainSocket(path: args.socketPath),
                transportSecurity: .plaintext
            ),
            services: [supervision, windowService, trayService]
        )

        // Serve on a DETACHED task so the MAIN thread is free to run AppKit (U2/KTD2). A control
        // NSStatusItem (Strategy A) and status-item events need a pumped AppKit run loop; the helper
        // previously blocked main on `server.serve()` with no run loop. Detached (not a plain `Task`,
        // which would inherit the main actor and be starved once `NSApp.run()` blocks it); NIO does the
        // actual serving on its own event-loop threads, so serving behaviour is unchanged.
        Task.detached {
            do {
                fputs("BevelHelper v\(version) ready on \(args.socketPath)\n", stderr)
                try await server.serve()
                fputs("BevelHelper: server stopped\n", stderr)
                Foundation.exit(0)
            } catch {
                fputs("BevelHelper: error: \(error)\n", stderr)
                Foundation.exit(1)
            }
        }

        // Run AppKit on the main thread. The control item is created but NOT installed until
        // consolidation is enabled (U5), so the menu bar is untouched by default. `NSApp.run()` never
        // returns; socket cleanup happens via the atexit hook, not the (now-unreached) defer.
        let controlItem = MenuBarControlItem()
        gControlItem = controlItem
        trayService.controlItem = controlItem   // U5: SetConsolidation + U3 self-exclusion reach it here
        let delegate = HelperAppDelegate(controlItem: controlItem)
        gAppDelegate = delegate
        NSApp.delegate = delegate
        NSApp.run()
    }
}

// Retained for the process lifetime: NSApp.delegate is weak, and the control item must outlive main().
@MainActor private var gControlItem: MenuBarControlItem?
@MainActor private var gAppDelegate: HelperAppDelegate?
