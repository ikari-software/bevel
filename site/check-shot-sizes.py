#!/usr/bin/env python3
"""Every shot's width=/height= in index.html must equal the PNG's real pixel size.

The browser lays out to the attributes, so a re-harvest that changes a render's dimensions silently
squashes the image until they follow. startmenu-win2000.png grew a "Show Desktop" row (210x206 ->
211x225) and the page kept declaring the old size.

Usage: check-shot-sizes.py <dir-of-shots>   (run from the repo root)
"""
import pathlib
import re
import struct
import sys

shots = pathlib.Path(sys.argv[1])
html = pathlib.Path("site/index.html").read_text()
bad = 0

for m in re.finditer(r'shots/([A-Za-z0-9._-]+)" width="(\d+)" height="(\d+)"', html):
    name, want_w, want_h = m.group(1), int(m.group(2)), int(m.group(3))
    png = shots / name
    if not png.exists():
        continue
    # PNG: 8-byte signature, 4-byte length, "IHDR", then width and height as big-endian uint32.
    got_w, got_h = struct.unpack(">II", png.read_bytes()[16:24])
    if (got_w, got_h) != (want_w, want_h):
        print(f"    SIZE MISMATCH: {name} is {got_w}x{got_h} but index.html declares {want_w}x{want_h}")
        bad = 1

sys.exit(bad)
