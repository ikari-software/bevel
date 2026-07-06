import GRPCCore
import GRPCProtobuf

/// Supervision service. Every RPC must carry the per-session nonce (IPC-04): the Unix
/// socket lives in a world-visible temp dir, so possession of the token — handed only to
/// the legitimate client via `--token` — is what authenticates a caller.
final class SupervisionServiceImpl: RegistrableRPCService, Sendable {
    let helperVersion: String
    let expectedToken: String

    init(helperVersion: String, expectedToken: String) {
        self.helperVersion = helperVersion
        self.expectedToken = expectedToken
    }

    static let tokenHeader = "x-bevel-token"

    func registerMethods<Transport: ServerTransport>(with router: inout RPCRouter<Transport>) {
        router.registerHandler(
            forMethod: MethodDescriptor(
                fullyQualifiedService: "bevel.helper.v1.SupervisionService",
                method: "Ping"
            ),
            deserializer: ProtobufDeserializer<Bevel_Helper_V1_PingRequest>(),
            serializer: ProtobufSerializer<Bevel_Helper_V1_PingReply>(),
            handler: { [helperVersion, expectedToken] request, context in
                try Self.authenticate(request.metadata, expectedToken: expectedToken)
                _ = try await ServerRequest(stream: request)
                var reply = Bevel_Helper_V1_PingReply()
                reply.helperVersion = helperVersion
                return StreamingServerResponse(single: ServerResponse(message: reply))
            }
        )
    }

    /// Rejects the RPC unless it presents the expected nonce.
    static func authenticate(_ metadata: Metadata, expectedToken: String) throws {
        let provided = Array(metadata[stringValues: tokenHeader]).first
        guard let provided, constantTimeEquals(provided, expectedToken) else {
            throw RPCError(code: .unauthenticated, message: "Invalid or missing helper token")
        }
    }

    /// Length-checked, constant-time comparison to avoid leaking the token via timing.
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
