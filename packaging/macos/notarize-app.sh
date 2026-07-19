#!/usr/bin/env bash
#
# Notarizes + staples a signed Bevel.app (M5 release lane, stage 3 of build → sign → notarize).
# Written to run identically locally and in CI (bevel-nsk) — only the credential source differs:
#
#   CI (preferred): App Store Connect API key — non-interactive, revocable, no personal 2FA.
#       NOTARY_KEY=/path/AuthKey_XXXX.p8  NOTARY_KEY_ID=XXXX  NOTARY_ISSUER=<uuid>
#   Local:          a stored notarytool keychain profile.
#       NOTARY_PROFILE=bevel   (default)
#
#   Arg 1 = app path (default dist/Bevel.app).
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
APP="${1:-$ROOT/dist/Bevel.app}"
ZIP="${ZIP:-$ROOT/dist/Bevel.zip}"

[ -d "$APP" ] || { echo "no bundle at $APP — run build-app.sh + sign-app.sh (TIMESTAMP=1) first" >&2; exit 1; }

# API key wins (CI); else the local keychain profile.
if [ -n "${NOTARY_KEY:-}" ]; then
	CREDS=(--key "$NOTARY_KEY" --key-id "$NOTARY_KEY_ID" --issuer "$NOTARY_ISSUER")
	echo "==> Credentials: App Store Connect API key ($NOTARY_KEY_ID)"
else
	CREDS=(--keychain-profile "${NOTARY_PROFILE:-bevel}")
	echo "==> Credentials: keychain profile '${NOTARY_PROFILE:-bevel}'"
fi

echo "==> Zipping (ditto preserves the bundle structure)"
rm -f "$ZIP"
ditto -c -k --keepParent "$APP" "$ZIP"

echo "==> Submitting to the Apple notary service (waits for the verdict)"
xcrun notarytool submit "$ZIP" "${CREDS[@]}" --wait --timeout 30m

echo "==> Stapling the ticket onto the bundle"
xcrun stapler staple "$APP"

echo "==> Verifying"
xcrun stapler validate "$APP"
spctl --assess --type execute -vv "$APP"

echo "==> Notarized + stapled: $APP"
