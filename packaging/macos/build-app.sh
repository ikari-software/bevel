#!/usr/bin/env bash
#
# Assembles Bevel.app (M5 packaging). Produces an unsigned bundle; signing + notarization + DMG are
# the separate cert-gated step (bevel-nsk / 09-engineering-plan §M5). Idempotent.
#
#   Env: RID (default osx-arm64) · CONFIG (Release) · VERSION (0.1.0) · BUILD (1) · OUT (<repo>/dist)
#        CLEAN (1) — start from clean intermediates (see below); CLEAN=0 for a fast incremental dev iteration.
#
set -euo pipefail

RID="${RID:-osx-arm64}"
CONFIG="${CONFIG:-Release}"
VERSION="${VERSION:-0.1.0}"
BUILD="${BUILD:-1}"
CLEAN="${CLEAN:-1}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUT="${OUT:-$ROOT/dist}"
APP="$OUT/Bevel.app"
PKG="$ROOT/packaging/macos"

# Single-file publish so Contents/MacOS holds ONLY Mach-O (the apphost + a few native libs) — no
# loose managed .dll / .json, which codesign otherwise flags as unsigned "code" in an .app's MacOS
# dir. DebugType=none drops PDBs (not shippable, and also flagged). Native libs self-extract at run
# time; the hardened-runtime disable-library-validation entitlement permits that.
# BevelPackaging=true is REQUIRED here: Directory.Build.targets sets UseAppHost=false for every Exe on a
# normal build/test (the Launch/TCC-hygiene fix — no apphosts pollute Spotlight). The packaging path is
# the sole exception; without this flag PublishSingleFile would conflict with UseAppHost=false and the
# publish would fail (and dist/Bevel.app would have no native Mach-O to rename to CFBundleExecutable).
# PublishReadyToRun (bevel-k93j): crossgen the IL to native ahead-of-time for $RID so each role process
# (launcher/core/taskbar/explorer) doesn't JIT the framework + Avalonia graph cold on startup — the biggest
# multi-process startup cost, paid N×. Slower to build, faster to start. Works with single-file/self-contained.
PUBLISH_ARGS=(-c "$CONFIG" -r "$RID" --self-contained true -p:BevelPackaging=true
	-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
	-p:PublishReadyToRun=true
	-p:DebugType=none -p:DebugSymbols=false -v quiet)

# Clean-by-default (bevel-9pgv). A dist build once crash-looped the taskbar on
# `XamlLoadException: No precompiled XAML found for Bevel.Taskbar.TaskbarView` and shipped a 0-byte
# Luna DLL — a corrupted incremental state, not a tree bug (clean publishes of the same tree boot).
# The hazard is structural: Avalonia's CompileAvaloniaXamlTask rewrites obj/<cfg>/net10.0/<proj>.dll IN
# PLACE after CoreCompile with no inputs/outputs of its own, so an interrupted or concurrent Release build
# can leave plain IL (no compiled XAML) in obj/, and PublishReadyToRun + PublishSingleFile crossgen and
# bundle whatever they find. The packaged artifact must not inherit whatever obj/ happens to hold, so the
# release path removes every $CONFIG obj/ + bin/ dir under src/ and third_party/ (the whole App + CLI
# graph, crossgen cache included) plus the previous publish dirs, then publishes from scratch. Deliberately
# NOT `dotnet clean`: Clean needs a restore whose assets file already carries the RID target, and a plain
# `dotnet build`/`dotnet test` leaves one that doesn't (NETSDK1047) — the rm is restore-independent. Debug
# dirs are untouched, so the dev loop keeps its incremental state. Costs a full rebuild (~1 min on top of
# the composite crossgen, which dominates anyway). CLEAN=0 keeps the incremental fast path for dev
# iteration — the verify-bundle gate below still guards it.
if [ "$CLEAN" != "0" ]; then
	echo "==> Cleaning $CONFIG intermediates under src/ and third_party/ — CLEAN=0 to skip"
	find "$ROOT/src" "$ROOT/third_party" -type d \( -path "*/obj/$CONFIG" -o -path "*/bin/$CONFIG" \) -prune -exec rm -rf {} +
	rm -rf "$OUT/publish-app" "$OUT/publish-cli"
fi

echo "==> Publishing Bevel.App ($CONFIG / $RID, single-file)"
dotnet publish "$ROOT/src/Bevel.App/Bevel.App.csproj" "${PUBLISH_ARGS[@]}" -o "$OUT/publish-app"

# Gate (bevel-9pgv): prove the bundle carries every UI assembly's compiled XAML + `!AvaloniaResources`
# before it becomes dist/Bevel.app. Fails the build loudly instead of shipping an artifact whose taskbar
# throws XamlLoadException at first paint. python3 comes with the Xcode CLT that `swift build` below
# already requires.
echo "==> Verifying Avalonia payloads in the single-file bundle"
python3 "$PKG/verify-bundle.py" "$OUT/publish-app/Bevel.App"

echo "==> Publishing bevelctl"
dotnet publish "$ROOT/src/bevelctl/bevelctl.csproj" "${PUBLISH_ARGS[@]}" -o "$OUT/publish-cli"

echo "==> Assembling $APP"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

# App payload → Contents/MacOS; the apphost is renamed to CFBundleExecutable (Bevel).
cp -R "$OUT/publish-app/." "$APP/Contents/MacOS/"
mv "$APP/Contents/MacOS/Bevel.App" "$APP/Contents/MacOS/Bevel"
# bevelctl ships alongside, on-PATH once the user symlinks it (see the cask/postinstall).
cp "$OUT/publish-cli/bevelctl" "$APP/Contents/MacOS/bevelctl"

# The Swift helper (window + tray enumeration via AX / CGWindowList / ScreenCaptureKit) → Contents/MacOS.
# WITHOUT this the shipped app can't enumerate windows or the tray at all — ResolveHelperBinary finds no
# BevelHelper, the helper never launches, and the taskbar stays empty regardless of TCC grants. It's a
# distinct binary that needs its OWN Accessibility + Screen Recording grants, keyed to its signature.
echo "==> Building + bundling BevelHelper ($CONFIG)"
swift build --package-path "$ROOT/native/helper-macos" --configuration release
cp "$ROOT/native/helper-macos/.build/release/BevelHelper" "$APP/Contents/MacOS/BevelHelper"

# Info.plist (version-substituted) + the M4 scripting terminology + icon.
sed -e "s/__VERSION__/$VERSION/g" -e "s/__BUILD__/$BUILD/g" \
	"$PKG/Info.plist" > "$APP/Contents/Info.plist"
cp "$ROOT/src/Bevel.App/Bevel.sdef" "$APP/Contents/Resources/Bevel.sdef"
[ -f "$PKG/Bevel.icns" ] && cp "$PKG/Bevel.icns" "$APP/Contents/Resources/Bevel.icns" || true

echo "==> Validating bundle"
plutil -lint "$APP/Contents/Info.plist"
test -x "$APP/Contents/MacOS/Bevel"
test -x "$APP/Contents/MacOS/BevelHelper"
test -f "$APP/Contents/Resources/Bevel.sdef"

# Keep the repo's build output OUT of Spotlight/Launchpad so it doesn't shadow the canonical
# /Applications/Bevel.app in the "Bevel" app list (bevel-5yx5). A .metadata_never_index marker in
# OUT (dist/) — NOT inside the .app bundle, which would break the signature — makes Spotlight skip
# dist/Bevel.app AND the dist/publish-app intermediate. rm -rf above only clears $APP, so this marker
# in $OUT survives rebuilds.
: > "$OUT/.metadata_never_index"

echo "==> Built $APP (unsigned). Next: sign-app.sh (Developer ID + notarytool profile 'bevel')."
