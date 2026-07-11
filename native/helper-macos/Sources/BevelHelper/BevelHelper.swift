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

    static func main() async throws {
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

        let watchdog = ReverseWatchdog(parentPID: args.parentPID)
        Task { await watchdog.run() }

        let supervision = SupervisionServiceImpl(helperVersion: version, expectedKey: args.token)
        let windowService = WindowServiceImpl(expectedKey: args.token, parentPID: args.parentPID)

        do {
            let server = GRPCServer(
                transport: .http2NIOPosix(
                    address: .unixDomainSocket(path: args.socketPath),
                    transportSecurity: .plaintext
                ),
                services: [supervision, windowService]
            )

            fputs("BevelHelper v\(version) ready on \(args.socketPath)\n", stderr)
            try await server.serve()
        } catch {
            fputs("BevelHelper: error: \(error)\n", stderr)
            throw error
        }
    }
}
