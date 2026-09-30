#!/usr/bin/env bash
# Build packaging/macos/Bevel.icns from BevelMark.svg (bevel-g35v).
# Requires: rsvg-convert, magick (ImageMagick), iconutil (macOS).
set -euo pipefail
cd "$(dirname "$0")"
SRC=BevelMark.svg
[ -f "$SRC" ] || { echo "missing $SRC"; exit 1; }

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
ICONSET="$TMP/Bevel.iconset"
mkdir -p "$ICONSET"

MASTER="$TMP/master.png"
rsvg-convert -w 720 -h 720 "$SRC" -o "$TMP/mark.png"

# Blue squircle plate (same stops as the old site #logo tile) + centred mark.
magick -size 1024x1024 gradient:'#5A9BF0-#14305E' -rotate 90 \
  \( +clone -alpha transparent -fill white -draw 'roundrectangle 0,0 1023,1023 230,230' \) \
  -alpha off -compose CopyOpacity -composite \
  "$TMP/mark.png" -gravity center -compose over -composite \
  "$MASTER"

for s in 16 32 128 256 512; do
  magick "$MASTER" -resize "${s}x${s}" "$ICONSET/icon_${s}x${s}.png"
  magick "$MASTER" -resize "$((s*2))x$((s*2))" "$ICONSET/icon_${s}x${s}@2x.png"
done

iconutil -c icns "$ICONSET" -o Bevel.icns
magick "$MASTER" -resize 256x256 BevelMark-256.png
ls -lh Bevel.icns BevelMark-256.png
echo "wrote $(pwd)/Bevel.icns"
