namespace Bevel.Ipc;

/// <summary>
/// Placeholder for the Bevel.App &lt;-&gt; BevelHelper IPC contract.
///
/// The single source of truth for the wire protocol is <c>proto/bevel.helper.v1.proto</c>
/// (gRPC over Unix domain sockets, snapshot+delta event streams, shared-memory frame
/// ring side channel — see docs/spec/01-architecture.md §4). This project will hold the
/// generated C# client/server stubs (Grpc.Tools) plus the DTO&lt;-&gt;protobuf mapping so the
/// PAL abstraction never leaks wire types (PAL-04). Empty at M0.
/// </summary>
public static class HelperContract
{
    /// <summary>protobuf package name, matches <c>proto/bevel.helper.v1.proto</c>.</summary>
    public const string ProtoPackage = "bevel.helper.v1";
}
