#!/usr/bin/env python3
"""Every shot's width=/height= in index.html must equal the PNG's pixel size divided by the asset scale.

The page ships HiDPI screenshots: the PNGs are rendered at SCALE times the size the page lays them
out at, so the browser spends the extra pixels on a Retina display. The attributes therefore carry
the LOGICAL size and the file carries SCALE times that.

The browser lays out to the attributes, so a re-harvest that changes a render's dimensions silently
squashes the image until they follow. startmenu-win2000.png grew a "Show Desktop" row (210x206 ->
211x225) and the page kept declaring the old size.

Usage: check-shot-sizes.py <dir-of-shots>   (run from the repo root)
"""
import pathlib
import re
import struct
import sys

# Must match SiteShot.Scale in tests/Shared/SiteShot.cs.
SCALE = 2

# Hand-composited shots that no test produces yet, so they are still 1x. Named rather than inferred:
# a shot that quietly fails the scale rule should be a visible exception, not an invisible pass.
MANUAL_1X = {
    "hero-luna.png",       # hand-composited, no producing test yet
    "theme-win2000.png",   # Avalonia mis-scales its TabControl content above 1x
}

shots = pathlib.Path(sys.argv[1])
html = pathlib.Path("site/index.html").read_text()
bad = 0

for m in re.finditer(r'shots/([A-Za-z0-9._-]+)" width="(\d+)" height="(\d+)"', html):
    name, want_w, want_h = m.group(1), int(m.group(2)), int(m.group(3))
    png = shots / name
    if not png.exists():
        continue
    if name in MANUAL_1X:
        print(f"    (skipped, still 1x and hand-composited: {name})")
        continue
    # PNG: 8-byte signature, 4-byte length, "IHDR", then width and height as big-endian uint32.
    got_w, got_h = struct.unpack(">II", png.read_bytes()[16:24])
    if (got_w, got_h) != (want_w * SCALE, want_h * SCALE):
        print(f"    SIZE MISMATCH: {name} is {got_w}x{got_h}; index.html declares {want_w}x{want_h}, "
              f"which at {SCALE}x should be {want_w * SCALE}x{want_h * SCALE}")
        bad = 1

sys.exit(bad)
