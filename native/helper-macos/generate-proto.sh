#!/usr/bin/env bash
set -euo pipefail

# Regenerate Swift protobuf messages (only .pb.swift, not .grpc.swift) from
# proto/bevel.helper.v1.proto. Service implementations are written manually
# against grpc-swift 1.x's CallHandlerProvider protocol.
# Run from the repo root, or adjust PROTO_DIR and OUT_DIR below.

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PROTO_DIR="$REPO_ROOT/proto"
OUT_DIR="$REPO_ROOT/native/helper-macos/Sources/BevelHelper/Generated"

mkdir -p "$OUT_DIR"

protoc \
  --swift_out="$OUT_DIR" \
  --swift_opt=Visibility=Public \
  -I "$PROTO_DIR" \
  "$PROTO_DIR/bevel.helper.v1.proto"

echo "proto: generated SwiftProtobuf messages in $OUT_DIR"
