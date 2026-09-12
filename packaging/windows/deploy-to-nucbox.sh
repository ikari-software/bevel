#!/usr/bin/env bash
# Deploy the win-x64 build to the LAN Windows box NUCBOX_EVO-X2 over SMB (bevel-ncfp.11 / U11).
#
# PREFER packaging/windows/deploy-ssh.sh. Port 22 turned out to be open with key auth already working,
# so the SSH script does the whole round trip in one command — publish, copy, stop the old instance,
# extract, optionally launch — with no Finder mount and no manual launch on the box. This SMB script
# remains for the case where only 445 is reachable.
# The box (192.168.1.48) has 445/SMB open, RDP off (memory: windows-deploy-box). RDP being off means
# delivery is file-copy to a share, not a remote session — so this mounts an SMB share and copies.
#
# Usage:  ./deploy-to-nucbox.sh //NUCBOX_EVO-X2/bevel   [source-dir]
#   arg1: the UNC share to copy into (must be writable; create it on the box first, or use an admin share)
#   arg2: source publish dir (default: dist/win-x64 from publish.sh)
#
# This does NOT hardcode credentials — macOS `open smb://…` or Finder-mount the share first, or pass a
# pre-mounted /Volumes path as the share. Never embeds a password (global rule: no secrets in scripts).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SHARE="${1:?usage: deploy-to-nucbox.sh //HOST/share [source-dir]}"
SRC="${2:-$ROOT/dist/win-x64}"

[ -d "$SRC" ] || { echo "No build at $SRC — run packaging/windows/publish.sh first." >&2; exit 1; }

# Resolve a mounted path. If the caller passed a //host/share UNC, expect it already mounted under
# /Volumes (macOS) — we do not auto-mount to avoid prompting for/handling credentials here.
MOUNT="$SHARE"
if [[ "$SHARE" == //* ]]; then
  name="$(basename "$SHARE")"
  if [ -d "/Volumes/$name" ]; then MOUNT="/Volumes/$name"; else
    echo "Share $SHARE is not mounted. In Finder: Go ▸ Connect to Server ▸ smb:$SHARE, then re-run." >&2
    echo "  (Or:  open 'smb:$SHARE'  and authenticate, then re-run.)" >&2
    exit 1
  fi
fi

echo "Copying $SRC → $MOUNT/Bevel"
rsync -a --delete "$SRC/" "$MOUNT/Bevel/"
echo "Deployed. On the box, run:  C:\\path\\to\\share\\Bevel\\Bevel.App.exe"
