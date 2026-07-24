#!/usr/bin/env bash
# Builds BevelHelper and RE-SIGNS it with a STABLE identity so macOS TCC grants (Accessibility, Screen
# Recording) PERSIST across rebuilds.
#
# Why: a raw `swift build` ad-hoc-signs the binary, and TCC keys the grant on the ad-hoc cdhash — which
# changes on every build. So each rebuild looks like a brand-new app and you must re-authorize
# Accessibility every time (and the tray tooltip AX lookup / click-forwarding silently fail until you
# do). Signing with a fixed certificate + identifier keeps the *designated requirement* constant, so
# you grant Accessibility (and Screen Recording) ONCE and every later build inherits it.
#
#   Env: HELPER_SIGN_IDENTITY (default = the local Apple Development cert)
#        CONFIG (default = debug)
set -euo pipefail
cd "$(dirname "$0")"

CONFIG="${CONFIG:-debug}"
IDENTITY="${HELPER_SIGN_IDENTITY:-Apple Development: Cezar Pokorski (KQ832UQ6C7)}"
IDENTIFIER="pl.ikari.bevel.helper"

swift build -c "$CONFIG"
BIN=".build/$CONFIG/BevelHelper"

# Stable cert + stable identifier => stable designated requirement => TCC keeps the grant.
codesign --force --sign "$IDENTITY" --identifier "$IDENTIFIER" "$BIN"
echo "==> signed $BIN"
codesign -dv "$BIN" 2>&1 | grep -E "Identifier=|Authority=|Signature=" | head -3
