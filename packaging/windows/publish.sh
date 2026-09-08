#!/usr/bin/env bash
# Cross-publish a self-contained win-x64 Bevel build (bevel-ncfp.11 / U11).
# Runs from macOS/Linux (cross-RID publish is supported) or Windows. Produces a runnable folder
# with Bevel.App.exe and the permonitorv2 DPI manifest embedded. No code-signing (KTD-6; LAN dev target).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUT="${1:-$ROOT/dist/win-x64}"

echo "Publishing self-contained win-x64 → $OUT"
dotnet publish "$ROOT/src/Bevel.App/Bevel.App.csproj" \
  --configuration Release \
  --runtime win-x64 \
  --self-contained true \
  -p:BevelPackaging=true \
  -p:PublishReadyToRun=true \
  --output "$OUT"

# Marker so Spotlight/indexers on a macOS staging copy don't churn (mirrors the mac packaging).
touch "$OUT/.metadata_never_index" 2>/dev/null || true

echo "Done. Launch on the box with:  Bevel.App.exe            (default → --role=launcher)"
echo "                         or:   Bevel.App.exe --pal=fake (safe UI-only demo)"
