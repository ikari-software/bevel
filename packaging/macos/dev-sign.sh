#!/usr/bin/env bash
# DEV signing (NOT release — see sign-app.sh for the notarizable Developer ID build).
#
# Stable-signs the locally-built app host + helper so macOS TCC permission grants (Accessibility,
# Screen Recording) PERSIST across rebuilds. `dotnet build` and `swift build` ad-hoc-sign their
# output, and TCC keys the grant on the ad-hoc cdhash — which changes on every build. So each rebuild
# looks like a brand-new binary and you must re-grant Accessibility every time (and the tray tooltip
# AX lookup + click-forwarding silently fail until you do). Signing with a fixed certificate + fixed
# identifier keeps the *designated requirement* constant, so you grant ONCE and later builds inherit it.
#
# Run after building. Then, the FIRST time, fully quit + relaunch Bevel and grant Accessibility +
# Screen Recording to it once — subsequent rebuilds keep the grant.
#
#   Env: HELPER_SIGN_IDENTITY (default = the local Apple Development cert)
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

IDENTITY="${HELPER_SIGN_IDENTITY:-Apple Development: Cezar Pokorski (KQ832UQ6C7)}"
APP="src/Bevel.App/bin/Debug/net10.0/Bevel.App"
HELPER="native/helper-macos/.build/debug/BevelHelper"

if [ -f "$APP" ]; then
	codesign --force --sign "$IDENTITY" --identifier "pl.ikari.bevel" "$APP"
	echo "==> signed app    -> pl.ikari.bevel"
fi
if [ -f "$HELPER" ]; then
	codesign --force --sign "$IDENTITY" --identifier "pl.ikari.bevel.helper" "$HELPER"
	echo "==> signed helper -> pl.ikari.bevel.helper"
fi

# The PACKAGED app (dist/Bevel.app, from build-app.sh) has its OWN binaries — build-app.sh's dotnet
# publish + the bundled release helper are AD-HOC signed, so their designated requirement is a cdhash
# that changes every build. That's the app you actually run, so WITHOUT this it re-prompts for
# Accessibility/Screen-Recording on every launch and the helper's tray capture never sticks (bevel:
# packaged app was signing the wrong files entirely). Stable-sign the bundle: (1) deep-sign everything
# with the real cert + the app identifier, (2) give the NESTED helper its own distinct TCC identity,
# (3) re-seal the bundle so it records the helper's new signature. Identifier + cert => constant
# designated requirement => grant ONCE.
APP_BUNDLE="dist/Bevel.app"
if [ -d "$APP_BUNDLE" ]; then
	codesign --force --deep --sign "$IDENTITY" --identifier "pl.ikari.bevel" "$APP_BUNDLE"
	codesign --force --sign "$IDENTITY" --identifier "pl.ikari.bevel.helper" "$APP_BUNDLE/Contents/MacOS/BevelHelper"
	codesign --force --sign "$IDENTITY" --identifier "pl.ikari.bevel" "$APP_BUNDLE"
	echo "==> signed dist/Bevel.app -> pl.ikari.bevel (nested helper -> pl.ikari.bevel.helper)"
fi
echo "Done. If this is the first stable sign: quit + relaunch Bevel, then grant Accessibility once."
