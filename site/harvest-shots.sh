#!/usr/bin/env bash
# Regenerates site/shots/ from the shell's own Render* tests.
#
# The landing page only ships real renders, so the shots are build OUTPUT that happens to be
# committed — and committed output rots. Two of them did: theme-win2000.png had no test behind it at
# all (a one-off hand-run) and infopane-win9x.png was harvested while text still rendered aliased,
# then sat stale for months while its siblings were refreshed.
#
# This drives the EXISTING tests through the env vars they already expose rather than restating
# their setup, so each shot keeps the assertions its test carries.
#
#   ./site/harvest-shots.sh            regenerate in place
#   ./site/harvest-shots.sh --check    regenerate into a temp dir and diff (CI drift guard)
#
# Run it on macOS: glyph rasterisation is platform-specific, so a shot harvested on Linux will
# differ byte-for-byte from the committed one even when nothing is wrong.
set -euo pipefail

cd "$(dirname "$0")/.."
# ABSOLUTE: `dotnet test` runs the test with the test assembly's own directory as its working
# directory, so a relative _OUT path lands under bin/Debug/... instead of here.
SHOTS="$PWD/site/shots"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

CHECK=0
[ "${1:-}" = "--check" ] && CHECK=1
OUT="$SHOTS"
[ "$CHECK" = 1 ] && OUT="$TMP/out" && mkdir -p "$OUT"

TB=tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj
FM=tests/Bevel.FileManager.Tests/Bevel.FileManager.Tests.csproj

PRODUCED=""
produced() { PRODUCED="$PRODUCED $1"; }

run() { # run <csproj> <filter>  — env comes from the caller
  dotnet test "$1" --filter "$2" -v q --nologo >"$TMP/log" 2>&1 || { cat "$TMP/log"; exit 1; }
}

echo "==> info panes (4)"
BEVEL_INFO_DIR="$TMP/info" && mkdir -p "$BEVEL_INFO_DIR"
BEVEL_INFO_DIR="$BEVEL_INFO_DIR" run "$FM" "FullyQualifiedName~InfoPaneStyleTest.Each_style_renders"
for t in win2000 winxp modern win9x; do cp "$TMP/info/infopane-$t.png" "$OUT/"; produced "infopane-$t.png"; done

echo "==> settings dialog, Appearance tab"
BEVEL_ONB_APPEARANCE_OUT="$OUT/theme-win2000.png" \
  run "$TB" "FullyQualifiedName~RenderOnboardingTest.Render_appearance_tab_to_png"
produced "theme-win2000.png"

echo "==> Start menu"
BEVEL_RENDER_OUT="$OUT/startmenu-win2000.png" \
  run "$TB" "FullyQualifiedName~RenderStartMenuTest.Render_start_menu_to_png"
produced "startmenu-win2000.png"

echo "==> Start menu, type-to-search"
BEVEL_RENDER_OUT="$OUT/startmenu-search.png" \
  run "$TB" "FullyQualifiedName~RenderStartMenuSearchTest"
produced "startmenu-search.png"

echo "==> Downloads stack grid"
BEVEL_STACKGRID_REAL_OUT="$OUT/stack-grid.png" \
  run "$TB" "FullyQualifiedName~RenderStackGridRealPalTest"
produced "stack-grid.png"

# Shots this script cannot regenerate. Each is a hand-composited scene with no single producing
# test; they are listed here so the audit below can tell "deliberately manual" from "silently
# dropped out of the pipeline" — which is exactly the distinction that let theme-win2000.png rot.
MANUAL="hero-luna.png luna-4up.png"

echo "==> audit: every shot the page references is accounted for"
missing=0
for f in $(grep -oE 'shots/[a-zA-Z0-9._-]+' site/index.html | sed 's|shots/||' | sort -u); do
  case " $PRODUCED " in *" $f "*) continue ;; esac
  case " $MANUAL " in
    *" $f "*) [ "$CHECK" = 1 ] && cp "$SHOTS/$f" "$OUT/$f"; echo "    manual (not regenerated): $f" ;;
    *) echo "    UNPRODUCED: $f — no test harvests it and it is not on the manual list"; missing=1 ;;
  esac
done
[ "$missing" = 0 ] || exit 1

echo "==> audit: declared width/height match the rendered pixels"
python3 site/check-shot-sizes.py "$OUT" || exit 1
echo "    declared sizes match"

if [ "$CHECK" = 1 ]; then
  echo "==> drift check"
  drift=0
  for f in "$OUT"/*.png; do
    b="$(basename "$f")"
    case " $MANUAL " in *" $b "*) continue ;; esac
    cmp -s "$f" "$SHOTS/$b" || { echo "    STALE: site/shots/$b differs from a fresh render"; drift=1; }
  done
  [ "$drift" = 0 ] && echo "    all harvested shots are current"
  exit $drift
fi

echo "==> done; review with: git diff --stat site/shots/"
