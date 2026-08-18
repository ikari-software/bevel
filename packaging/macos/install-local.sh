#!/usr/bin/env bash
#
# Release-lane final stage: install an ALREADY signed + notarized dist/Bevel.app into /Applications as
# the single canonical Bevel, so its Developer-ID + notarized designated requirement is what LaunchServices
# and TCC key on (only that identity grants Accessibility / Screen Recording that PERSIST across rebuilds).
#
# This script does NOT sign and does NOT notarize — it installs a prepared bundle. Run the pipeline first:
#     build-app.sh  ->  sign-app.sh (TIMESTAMP=1)  ->  notarize-app.sh  ->  install-local.sh
#
# What it does, idempotently and safely:
#   1. Quits any running Bevel (graceful, then hard) so the copy isn't in use.
#   2. rsync --delete dist/Bevel.app -> /Applications/Bevel.app (mirror, no stale leftovers).
#   3. lsregister -f the installed copy (register the ONE canonical bundle with LaunchServices).
#   4. Prints the single resolved bundle LaunchServices now sees for the bundle id.
#   5. Relaunches the installed bundle ONLY IF Bevel was already running when we started (so a deploy
#      doesn't leave your live shell dead) — never launches a shell that wasn't there. NO_RELAUNCH=1 opts out.
#
#   Env: APP (source bundle, default dist/Bevel.app) · DEST (default /Applications/Bevel.app) ·
#        BUNDLE_ID (default pl.ikari.bevel) · SKIP_NOTARY_CHECK=1 to bypass the staple-gate ·
#        NO_RELAUNCH=1 to skip the was-running relaunch.
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
APP="${APP:-$ROOT/dist/Bevel.app}"
DEST="${DEST:-/Applications/Bevel.app}"
BUNDLE_ID="${BUNDLE_ID:-pl.ikari.bevel}"
LSREGISTER="/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"

# --- Guards: refuse to run on anything but a real, prepared .app bundle --------------------------------
[ -d "$APP" ] || { echo "no bundle at $APP — run build-app.sh + sign-app.sh + notarize-app.sh first" >&2; exit 1; }
case "$DEST" in
	/Applications/*.app) ;;                      # must be a *.app UNDER /Applications — never a bare dir
	*) echo "refusing: DEST '$DEST' is not an /Applications/*.app path" >&2; exit 1 ;;
esac

# Verify the source is signed with Developer ID + notarization-stapled. A raw/ad-hoc bundle installed here
# would NOT get persistent TCC, defeating the point — fail loudly unless explicitly overridden.
if [ "${SKIP_NOTARY_CHECK:-0}" != "1" ]; then
	if ! codesign --verify --deep --strict "$APP" 2>/dev/null; then
		echo "refusing: $APP fails codesign --verify (sign-app.sh with Developer ID first, or SKIP_NOTARY_CHECK=1)" >&2
		exit 1
	fi
	if ! xcrun stapler validate "$APP" >/dev/null 2>&1; then
		echo "refusing: $APP is not notarization-stapled (run notarize-app.sh first, or SKIP_NOTARY_CHECK=1)" >&2
		exit 1
	fi
	echo "==> Source verified: Developer-ID signed + notarization-stapled"
fi

# --- 1. Quit any running Bevel ------------------------------------------------------------------------
# Record whether a Bevel shell is live BEFORE we quit it, so step 5 can bring it back (only if it was
# there). Match the main binary from ANY path (dev dist/ or /Applications) and exclude BevelHelper — the
# trailing "( |$)" means "Bevel" followed by an arg or end-of-cmdline, so ".../MacOS/BevelHelper" is skipped.
WAS_RUNNING=0
if pgrep -f "Bevel\.app/Contents/MacOS/Bevel( |\$)" >/dev/null 2>&1; then WAS_RUNNING=1; fi

# Graceful first (let the launcher tear down core/taskbar + the Dock restore), then hard-kill stragglers.
echo "==> Quitting any running Bevel"
osascript -e 'tell application id "'"$BUNDLE_ID"'" to quit' >/dev/null 2>&1 || true
# Give it a moment, then hard-kill by the installed executable path + role children if any survive.
for _ in 1 2 3 4 5 6; do
	pgrep -f "$DEST/Contents/MacOS/Bevel" >/dev/null 2>&1 || break
	sleep 0.5
done
pkill -f "$DEST/Contents/MacOS/Bevel" 2>/dev/null || true

# --- 2. Mirror install --------------------------------------------------------------------------------
# rsync --delete mirrors the bundle (removing any stale files from a prior version) WITHOUT an rm -rf on a
# broad path. The trailing slashes copy contents-into-dir; -a preserves the stapled ticket + perms.
echo "==> Installing $APP -> $DEST (rsync --delete)"
mkdir -p "$DEST"
rsync -a --delete "$APP"/ "$DEST"/

# --- 3. Register the single canonical copy ------------------------------------------------------------
if [ -x "$LSREGISTER" ]; then
	echo "==> Registering with LaunchServices (lsregister -f)"
	"$LSREGISTER" -f "$DEST"
else
	echo "warn: lsregister not found at expected path — skipping explicit registration" >&2
fi

# --- 4. Report the single resolved bundle -------------------------------------------------------------
echo "==> Installed. LaunchServices now resolves '$BUNDLE_ID' to:"
RESOLVED="$(mdfind "kMDItemCFBundleIdentifier == '$BUNDLE_ID'" 2>/dev/null || true)"
if [ -n "$RESOLVED" ]; then
	echo "$RESOLVED" | sed 's/^/    /'
	COUNT="$(printf '%s\n' "$RESOLVED" | grep -c . || true)"
	[ "$COUNT" -gt 1 ] && echo "    NOTE: $COUNT bundles carry this id — expected exactly 1 after the Launch/TCC-hygiene fix." >&2
else
	echo "    (Spotlight has not re-indexed yet — the installed bundle is: $DEST)"
fi
echo "==> Done: $DEST"

# --- 5. Relaunch ONLY if Bevel was running when we started (don't spawn a shell that wasn't there) -----
if [ "$WAS_RUNNING" = "1" ] && [ "${NO_RELAUNCH:-0}" != "1" ]; then
	echo "==> Bevel was running — relaunching the installed bundle"
	open "$DEST"
elif [ "$WAS_RUNNING" = "1" ]; then
	echo "==> Bevel was running but NO_RELAUNCH=1 — not relaunching"
fi
