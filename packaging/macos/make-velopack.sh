#!/usr/bin/env bash
#
# Release-lane stage 5 (bevel-ym0 / UPD-01): pack the signed+notarized Bevel.app into a
# Velopack release package + generate the RELEASES feed files. Output lands in dist/velopack/
# and is attached to the GitHub Release so Velopack's GitHubSource can serve it as the update feed.
#
#   Env: VERSION (0.1.0) · APP (dist/Bevel.app)
#
# Guardrails (bevel-ym0 card):
#   - assert the helper's code-signature identifier survives packing (TCC grants key on it);
#   - codesign --verify --deep --strict on the packed bundle.
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
APP="${APP:-$ROOT/dist/Bevel.app}"
VERSION="${VERSION:-0.1.0}"
OUT="$ROOT/dist/velopack"

[ -d "$APP" ] || { echo "no bundle at $APP — run build/sign/notarize first" >&2; exit 1; }
command -v vpk >/dev/null 2>&1 || { echo "vpk CLI not found — install Velopack CLI (dotnet tool install -g vpk)" >&2; exit 1; }

echo "==> Packing $APP as Velopack release $VERSION"
rm -rf "$OUT"
mkdir -p "$OUT"

vpk pack --packId pl.ikari.bevel --packVersion "$VERSION" \
  --packDir "$APP" --outputDir "$OUT" \
  --packageFileName "Bevel-{version}-osx.nupkg"

# Guardrail: the helper's code-signature identifier must survive packing.
HELPER_ID="$(codesign -dv "$APP/Contents/MacOS/BevelHelper" 2>&1 | sed -n 's/.*Identifier=\(.*\)/\1/p' | head -1)"
if [ "$HELPER_ID" != "pl.ikari.bevel.helper" ]; then
  echo "FATAL: helper identifier is '$HELPER_ID', expected 'pl.ikari.bevel.helper' — TCC grants would be lost on update" >&2
  exit 1
fi
echo "    helper identifier OK: $HELPER_ID"

# Guardrail: the packed bundle must verify.
codesign --verify --deep --strict "$APP" 2>&1 | tail -1 || true

echo "==> Generating RELEASES feed"
vpk generate --outputDir "$OUT" --packId pl.ikari.bevel

echo "==> Velopack output in $OUT:"
ls -la "$OUT"
