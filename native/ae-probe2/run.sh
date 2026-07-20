#!/usr/bin/env bash
# Build the Avalonia AE harness into a .app, register with LaunchServices, and launch it.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="$ROOT/out"
APP="$OUT/BevelAETest.app"

dotnet publish "$ROOT/aetest.csproj" -c Release -r osx-arm64 --self-contained true -o "$OUT/publish" -v quiet

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp -R "$OUT/publish/." "$APP/Contents/MacOS/"
mv "$APP/Contents/MacOS/aetest" "$APP/Contents/MacOS/BevelAETest"

cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleIdentifier</key><string>pl.ikari.bevel.aetest</string>
  <key>CFBundleName</key><string>BevelAETest</string>
  <key>CFBundleExecutable</key><string>BevelAETest</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>NSAppleScriptEnabled</key><true/>
  <key>LSUIElement</key><true/>
</dict></plist>
PLIST

LSREG=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister
"$LSREG" -f "$APP"
open "$APP"
echo "launched $APP (log: ~/ae-avalonia.log)"
