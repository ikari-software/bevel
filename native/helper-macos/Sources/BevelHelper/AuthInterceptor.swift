import Crypto
import Foundation
import GRPCCore

/// Capability-scoped HMAC authentication interceptor (U2 / bevel-l3o).
///
/// Validates the `x-bevel-token` header against every incoming RPC, uniformly
/// across unary and server-streaming calls. The header format is:
///
///     x-bevel-token: <capability>:<HMAC-SHA256(key, capability)>
///
/// - The HMAC key is the per-session nonce passed via `BEVEL_HELPER_TOKEN`.
/// - Each capability (e.g. "supervision", "windows") produces a different HMAC.
/// - A token valid for "supervision" is not valid for "windows" and vice versa.
final class AuthInterceptor: ServerInterceptor, Sendable {
    private let expectedKey: String

    static let tokenHeader = "x-bevel-token"

    init(expectedKey: String) {
        self.expectedKey = expectedKey
    }

    func intercept<Input: Sendable, Output: Sendable>(
        request: StreamingServerRequest<Input>,
        context: ServerContext,
        next: @Sendable (StreamingServerRequest<Input>, ServerContext) async throws -> StreamingServerResponse<Output>
    ) async throws -> StreamingServerResponse<Output> {
        let methodPath = context.descriptor.fullyQualifiedMethod

        // Extract the expected capability from the method path.
        // e.g. "bevel.helper.v1.SupervisionService/Ping" → "supervision"
        //      "bevel.helper.v1.WindowService/ListWindows" → "windows"
        guard let expectedCapability = Self.capabilityFromMethod(methodPath) else {
            throw RPCError(
                code: .unauthenticated,
                message: "Unknown service: \(methodPath)"
            )
        }

        try Self.authenticate(request.metadata, expectedKey: expectedKey, expectedCapability: expectedCapability)

        return try await next(request, context)
    }

    /// Validates the token header for the given capability.
    static func authenticate(
        _ metadata: Metadata,
        expectedKey: String,
        expectedCapability: String
    ) throws {
        let provided = Array(metadata[stringValues: tokenHeader]).first
        guard let provided else {
            throw RPCError(
                code: .unauthenticated,
                message: "Missing \(tokenHeader) header"
            )
        }

        // Parse "capability:hmac"
        let parts = provided.split(separator: ":", maxSplits: 1, omittingEmptySubsequences: false)
        guard parts.count == 2 else {
            throw RPCError(
                code: .unauthenticated,
                message: "Malformed \(tokenHeader): expected 'capability:hmac'"
            )
        }

        let capability = String(parts[0])
        let providedHmac = String(parts[1])

        guard capability == expectedCapability else {
            throw RPCError(
                code: .unauthenticated,
                message: "Token is for '\(capability)', not '\(expectedCapability)'"
            )
        }

        let expectedHmac = computeHmac(key: expectedKey, capability: expectedCapability)
        guard constantTimeEquals(providedHmac, expectedHmac) else {
            throw RPCError(
                code: .unauthenticated,
                message: "Invalid HMAC for capability '\(expectedCapability)'"
            )
        }
    }

    /// Derives the expected capability name from a gRPC method path.
    /// e.g. "bevel.helper.v1.SupervisionService/Ping" → "supervision"
    ///      "bevel.helper.v1.WindowService/ListWindows" → "windows"
    static func capabilityFromMethod(_ fullyQualifiedMethod: String) -> String? {
        // Split on the last "/" to get the service name
        guard let slashIndex = fullyQualifiedMethod.lastIndex(of: "/") else { return nil }
        let serviceName = String(fullyQualifiedMethod[..<slashIndex])

        // Service names are like "bevel.helper.v1.SupervisionService"
        // Extract "SupervisionService" → "supervision"
        guard let lastDot = serviceName.lastIndex(of: ".") else { return nil }
        let shortName = String(serviceName[serviceName.index(after: lastDot)...])

        // Strip "Service" suffix and lowercase
        if shortName.hasSuffix("Service") {
            return String(shortName.dropLast(7)).lowercased()
        }
        return shortName.lowercased()
    }

    /// HMAC-SHA256(key, message) → lowercase hex string.
    static func computeHmac(key: String, capability: String) -> String {
        let keyData = SymmetricKey(data: Data(key.utf8))
        let capabilityData = Data(capability.utf8)
        let signature = HMAC<SHA256>.authenticationCode(for: capabilityData, using: keyData)
        return Data(signature).map { String(format: "%02x", $0) }.joined()
    }

    /// Length-checked, constant-time comparison to avoid leaking the HMAC via timing.
    static func constantTimeEquals(_ a: String, _ b: String) -> Bool {
        let ab = Array(a.utf8)
        let bb = Array(b.utf8)
        guard ab.count == bb.count else { return false }
        var diff: UInt8 = 0
        for i in 0..<ab.count {
            diff |= ab[i] ^ bb[i]
        }
        return diff == 0
    }
}