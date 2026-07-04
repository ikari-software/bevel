// swift-tools-version: 6.0
import PackageDescription

// BevelHelper — the macOS native helper daemon skeleton (01 §3.4).
//
// This is STRUCTURE ONLY. The gRPC-over-UDS handshake, supervision protocol
// (SUP-01..07) and the AX / ScreenCaptureKit / CGEvent / Apple Events integration
// land in later M0/M1+ tasks. It is intentionally NOT part of the .NET solution and
// is not built by `dotnet build`; it is built by SwiftPM (and later NUKE).
let package = Package(
    name: "BevelHelper",
    platforms: [
        .macOS(.v14) // macOS 14 (Sonoma) floor — 02 §10.
    ],
    targets: [
        .executableTarget(
            name: "BevelHelper"
        )
    ]
)
