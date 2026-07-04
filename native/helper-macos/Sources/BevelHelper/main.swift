// BevelHelper entry point (skeleton).
//
// In the real helper this process is spawned by Bevel.App with
// `--socket <path> --token <nonce> --parent-pid <pid>` (SUP-01), stands up a
// grpc-swift server on a Unix domain socket, verifies the peer credential + nonce
// (IPC-04), and answers Ping/GetHelperInfo plus the Window/Tray/Input/Session
// streaming RPCs. None of that exists yet — this just proves the SwiftPM target
// builds and runs.

import Foundation

print("BevelHelper skeleton — no IPC yet. See docs/spec/01-architecture.md §3–4 and proto/bevel.helper.v1.proto")
