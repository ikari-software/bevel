// swift-tools-version: 6.0
import PackageDescription

// WindowSpawner: a tiny AppKit test rig that opens/renames/closes real windows on
// command, so the window-manager path (CGWindowList enumeration + AX correlation in
// BevelHelper's WindowServiceImpl, and MacOSWindowManager over gRPC) can be tested
// against a KNOWN set of windows instead of whatever apps happen to be open on the
// dev/CI machine. Plan unit U13, requirements R20/R21.
//
// Deliberately smaller than the specced WindowZoo.app / M-INFRA rig — it exists only
// to give this milestone's own tests deterministic windows to enumerate.
let package = Package(
    name: "WindowSpawner",
    platforms: [
        .macOS(.v15)
    ],
    targets: [
        .executableTarget(
            name: "WindowSpawner"
        )
    ]
)
