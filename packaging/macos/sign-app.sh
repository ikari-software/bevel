#!/usr/bin/env bash
#
# Signs Bevel.app with the frozen Developer ID + hardened runtime, ready for notarization (M5,
# bevel-nsk). Signs inner Mach-O first (Apple's required order for nested code), then the bundle
# with the entitlements. Idempotent (--force).
#
#   Env: IDENTITY (default = the frozen Developer ID) · arg 1 = app path (default dist/Bevel.app)
#
set -euo pipefail

IDENTITY="${IDENTITY:-Developer ID Application: Cezar Pokorski (4TP7TPH2K6)}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
APP="${1:-$ROOT/dist/Bevel.app}"
ENT="$ROOT/packaging/macos/Bevel.entitlements"

[ -d "$APP" ] || { echo "no bundle at $APP — run build-app.sh first" >&2; exit 1; }

# --timestamp hits Apple's TSA per file (~200 round-trips) — needed for notarization, skipped for a
# fast local signed build. Opt in with TIMESTAMP=1.
TS=(); [ "${TIMESTAMP:-0}" = "1" ] && TS=(--timestamp)
MAIN="$APP/Contents/MacOS/Bevel"

echo "==> Signing nested Mach-O only (dylibs + secondary executables), inner-first"
# Only nested Mach-O needs an individual signature. Managed .NET assemblies (.dll) and data files
# (.json) are sealed as resources by the bundle signature below — signing them individually breaks
# that sealing. The main executable is signed by the bundle step, so it's excluded here (signing it
# directly triggers premature bundle-sealing over still-unsigned nested code).
count=0
while IFS= read -r -d '' f; do
	[ "$f" = "$MAIN" ] && continue
	file -b "$f" | grep -q "Mach-O" || continue
	if [ "$(basename "$f")" = "BevelHelper" ]; then
		# Pin the helper's identifier so its designated requirement (identifier + Team) is STABLE across
		# releases — TCC keys the Accessibility / Screen Recording grants on the DR, so a stable identifier
		# means the user grants BevelHelper once and every later release inherits it (matches dev-sign.sh).
		codesign --force --options runtime "${TS[@]}" --identifier pl.ikari.bevel.helper --sign "$IDENTITY" "$f"
	elif file -b "$f" | grep -q "executable"; then
		# Secondary executables (bevelctl) need the same entitlements as the main app: the
		# Homebrew-SDK apphost links /opt/homebrew brotli, which library validation rejects
		# under the hardened runtime without disable-library-validation.
		codesign --force --options runtime "${TS[@]}" --entitlements "$ENT" --sign "$IDENTITY" "$f"
	else
		codesign --force --options runtime "${TS[@]}" --sign "$IDENTITY" "$f"
	fi
	count=$((count + 1))
done < <(find "$APP/Contents/MacOS" -type f -print0)
echo "    signed $count nested Mach-O files"

echo "==> Signing the bundle (entitlements applied to the main executable)"
codesign --force --options runtime "${TS[@]}" \
	--entitlements "$ENT" --sign "$IDENTITY" "$APP"

echo "==> Verifying"
codesign --verify --deep --strict --verbose=2 "$APP"
codesign -dvvv "$APP" 2>&1 | grep -E "Identifier=|Authority=|TeamIdentifier=|flags=|Runtime"

echo "==> Signed. Next: notarize (xcrun notarytool submit --keychain-profile bevel) + staple."
