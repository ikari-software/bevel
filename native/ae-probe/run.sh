#!/usr/bin/env bash
# Build the AE probe into a faceless .app, register it with LaunchServices, and launch it.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="$ROOT/out"
APP="$OUT/BevelAEProbe.app"

dotnet publish "$ROOT/aeprobe.csproj" -c Release -r osx-arm64 --self-contained true -o "$OUT/publish" -v quiet

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp -R "$OUT/publish/." "$APP/Contents/MacOS/"
mv "$APP/Contents/MacOS/aeprobe" "$APP/Contents/MacOS/BevelAEProbe"

cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleIdentifier</key><string>pl.ikari.bevel.aeprobe</string>
  <key>CFBundleName</key><string>BevelAEProbe</string>
  <key>CFBundleExecutable</key><string>BevelAEProbe</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>NSAppleScriptEnabled</key><true/>
  <key>LSUIElement</key><true/>
</dict></plist>
PLIST

LSREG=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister
"$LSREG" -f "$APP"
open "$APP"
echo "launched $APP (log: ~/ae-probe.log)"
