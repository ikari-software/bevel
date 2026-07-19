#!/usr/bin/env bash
#
# Release-lane stage 4: package the signed+notarized Bevel.app into a distributable .dmg with a
# drag-to-Applications layout, then sign and (by default) notarize+staple the DMG itself so the
# download experience is clean. Same local/CI credential model as notarize-app.sh.
#
#   Env: VERSION (0.1.0) · IDENTITY (frozen Developer ID) · NOTARIZE (1; set 0 to skip) ·
#        NOTARY_KEY/KEY_ID/ISSUER (CI) or NOTARY_PROFILE (local, default 'bevel')
#   Arg 1 = app path (default dist/Bevel.app).
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
APP="${1:-$ROOT/dist/Bevel.app}"
VERSION="${VERSION:-0.1.0}"
IDENTITY="${IDENTITY:-Developer ID Application: Cezar Pokorski (4TP7TPH2K6)}"
DMG="$ROOT/dist/Bevel-$VERSION.dmg"
STAGE="$ROOT/dist/dmg-stage"

[ -d "$APP" ] || { echo "no bundle at $APP — run build/sign/notarize first" >&2; exit 1; }

echo "==> Staging (app + drag-to-Applications symlink)"
rm -rf "$STAGE" "$DMG"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/Bevel.app"        # -R preserves the stapled notarization ticket
ln -s /Applications "$STAGE/Applications"

echo "==> Building compressed DMG"
hdiutil create -volname "Bevel" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
rm -rf "$STAGE"

echo "==> Signing DMG"
codesign --force --timestamp --sign "$IDENTITY" "$DMG"

if [ "${NOTARIZE:-1}" = "1" ]; then
	if [ -n "${NOTARY_KEY:-}" ]; then
		CREDS=(--key "$NOTARY_KEY" --key-id "$NOTARY_KEY_ID" --issuer "$NOTARY_ISSUER")
	else
		CREDS=(--keychain-profile "${NOTARY_PROFILE:-bevel}")
	fi
	echo "==> Notarizing DMG (waits)"
	xcrun notarytool submit "$DMG" "${CREDS[@]}" --wait --timeout 30m
	xcrun stapler staple "$DMG"
fi

echo "==> Verifying"
codesign --verify --verbose "$DMG"
spctl --assess --type open --context context:primary-signature -vv "$DMG" 2>&1 || true
echo "==> Built $DMG"
ls -lh "$DMG" | awk '{print "    size:", $5}'
