import GRPCCore
import GRPCProtobuf

/// Supervision service. Validates capability-scoped tokens (U2): the header format is
/// `x-bevel-token: supervision:<HMAC-SHA256(expectedKey, "supervision")>`.
/// `AuthInterceptor.swift` provides the shared helpers; this service applies them
/// per-handler until a shared interceptor is adopted across all services.
final class SupervisionServiceImpl: RegistrableRPCService, Sendable {
    let helperVersion: String
    let expectedKey: String

    init(helperVersion: String, expectedKey: String) {
        self.helperVersion = helperVersion
        self.expectedKey = expectedKey
    }

    func registerMethods<Transport: ServerTransport>(with router: inout RPCRouter<Transport>) {
        router.registerHandler(
            forMethod: MethodDescriptor(
                fullyQualifiedService: "bevel.helper.v1.SupervisionService",
                method: "Ping"
            ),
            deserializer: ProtobufDeserializer<Bevel_Helper_V1_PingRequest>(),
            serializer: ProtobufSerializer<Bevel_Helper_V1_PingReply>(),
            handler: { [helperVersion, expectedKey] request, context in
                try AuthInterceptor.authenticate(
                    request.metadata,
                    expectedKey: expectedKey,
                    expectedCapability: "supervision"
                )
                _ = try await ServerRequest(stream: request)
                var reply = Bevel_Helper_V1_PingReply()
                reply.helperVersion = helperVersion
                return StreamingServerResponse(single: ServerResponse(message: reply))
            }
        )
    }
}