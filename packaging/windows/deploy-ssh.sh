#!/usr/bin/env bash
# Publish + deploy the win-x64 build to the LAN Windows box over SSH (bevel-windeploy).
#
# Why SSH and not the SMB path in deploy-to-nucbox.sh: that script was written when the box was believed
# to expose only 445/SMB, so it can copy but not RUN anything — it ends by telling you to go launch the
# exe yourself, and it requires the share to be Finder-mounted first. Port 22 is in fact open with key
# auth already working, which makes the whole round trip one command: publish, copy, stop the old
# instance, extract, and optionally launch. The SMB script stays for the credentials-only-share case.
#
# No credentials live here (global rule: no secrets in scripts) — authentication is whatever your SSH
# key/agent already provides. If key auth isn't set up, this fails fast rather than prompting.
#
# Usage:
#   ./deploy-ssh.sh                     # publish, deploy, don't launch
#   ./deploy-ssh.sh --launch            # ... and start the shell on the box
#   ./deploy-ssh.sh --no-publish        # deploy whatever is already in dist/win-x64
#
# Environment:
#   BEVEL_WIN_HOST   ssh target        (default: 192.168.1.48)
#   BEVEL_WIN_DEST   install directory (default: <remote %USERPROFILE%>\Bevel)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HOST="${BEVEL_WIN_HOST:-192.168.1.48}"
SRC="$ROOT/dist/win-x64"

PUBLISH=1
LAUNCH=""
for arg in "$@"; do
    case "$arg" in
        --no-publish) PUBLISH=0 ;;
        --launch)     LAUNCH="-Launch" ;;
        -h|--help)    sed -n '2,22p' "${BASH_SOURCE[0]}"; exit 0 ;;
        *) echo "unknown argument: $arg" >&2; exit 2 ;;
    esac
done

# --- transport -------------------------------------------------------------------------------------
# This box sets OpenSSH's DefaultShell to pwsh.exe, which breaks the scp/sftp SUBSYSTEM — both die with
# "Connection closed", because sshd resolves sftp-server.exe through that shell. Fixing it properly
# means editing the box's sshd_config, which a deploy script has no business doing to someone's machine.
# So everything moves over the ssh EXEC channel instead, which works regardless of the default shell.
#
# Commands are sent as -EncodedCommand (UTF-16LE base64). That removes every quoting layer at once:
# no bash expansion, no PowerShell re-parsing, no backslash mangling of Windows paths — all three of
# which bit during setup.
ps_encode() { printf '%s' "$1" | iconv -f UTF-8 -t UTF-16LE | base64 | tr -d '\n'; }

remote_ps() {
    ssh -o BatchMode=yes "$HOST" "pwsh -NoProfile -EncodedCommand $(ps_encode "$1")"
}

remote_ps_file() {
    ssh -o BatchMode=yes "$HOST" \
        "pwsh -NoProfile -ExecutionPolicy Bypass -File \"$1\" $2"
}

# Stream a local file into a remote path over stdin. [Console]::OpenStandardInput() is the RAW stream,
# so binary survives intact (verified by sha256 below).
push_file() {
    local src="$1" dest="$2"
    remote_ps "\$ErrorActionPreference='Stop'; \$fs=[IO.File]::Create('$dest'); [Console]::OpenStandardInput().CopyTo(\$fs); \$fs.Close()" < "$src"
}
# ---------------------------------------------------------------------------------------------------

# Fail fast and loudly on auth rather than hanging on a password prompt in a script.
if ! ssh -o BatchMode=yes -o ConnectTimeout=8 "$HOST" 'exit' 2>/dev/null; then
    echo "Cannot reach $HOST over SSH with key auth." >&2
    echo "  Check the box is up, then:  ssh-copy-id $HOST" >&2
    exit 1
fi

if [ "$PUBLISH" = "1" ]; then
    "$ROOT/packaging/windows/publish.sh" "$SRC"
fi
[ -d "$SRC" ] || { echo "No build at $SRC — drop --no-publish, or run publish.sh first." >&2; exit 1; }

# Resolve the install dir ON the box so the path is right for whoever is logged in, instead of baking
# one developer's profile path into the script.
if [ -n "${BEVEL_WIN_DEST:-}" ]; then
    DEST="$BEVEL_WIN_DEST"
else
    DEST="$(remote_ps 'Write-Output (Join-Path $env:USERPROFILE Bevel)' | tr -d '\r')"
fi
[ -n "$DEST" ] || { echo "Could not resolve a remote install directory." >&2; exit 1; }

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
ARCHIVE="$STAGE/bevel-win-x64.tar.gz"

# One archive beats `scp -r` over a self-contained publish: that is ~200 files, and SCP pays a full
# round trip per file, which over Wi-Fi is the difference between seconds and minutes.
echo "==> Packing $(find "$SRC" -type f | wc -l | tr -d ' ') files"
tar -czf "$ARCHIVE" -C "$SRC" .

# Write-Output, not a bare `$env:TEMP`: PowerShell parses a bare string at statement position as a
# COMMAND and tries to execute the path. Same trap the remote .ps1 file exists to avoid.
REMOTE_TMP="$(remote_ps 'Write-Output $env:TEMP' | tr -d '\r')"
[ -n "$REMOTE_TMP" ] || { echo "Could not resolve the remote temp directory." >&2; exit 1; }

echo "==> Copying $(du -h "$ARCHIVE" | cut -f1) to $HOST"
push_file "$ARCHIVE" "$REMOTE_TMP\\bevel-win-x64.tar.gz"
push_file "$ROOT/packaging/windows/deploy-remote.ps1" "$REMOTE_TMP\\bevel-deploy-remote.ps1"

# Verify the payload survived the stream before unpacking over a working install. A truncated archive
# would otherwise half-extract and leave the box with a broken mix of two builds.
LOCAL_SHA="$(shasum -a 256 "$ARCHIVE" | cut -d' ' -f1)"
REMOTE_SHA="$(remote_ps "(Get-FileHash '$REMOTE_TMP\\bevel-win-x64.tar.gz' -Algorithm SHA256).Hash.ToLower()" | tr -d '\r')"
if [ "$LOCAL_SHA" != "$REMOTE_SHA" ]; then
    echo "Transfer corrupted: local $LOCAL_SHA != remote $REMOTE_SHA" >&2
    exit 1
fi
echo "    sha256 verified"

echo "==> Installing to $DEST"
remote_ps_file "$REMOTE_TMP\\bevel-deploy-remote.ps1" "-Archive '$REMOTE_TMP\\bevel-win-x64.tar.gz' -Dest '$DEST' $LAUNCH"

echo "==> Done."
[ -n "$LAUNCH" ] || echo "    Launch it with:  ./deploy-ssh.sh --launch   (or run Bevel.App.exe on the box)"
