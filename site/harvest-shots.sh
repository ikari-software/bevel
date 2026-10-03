#!/usr/bin/env bash
# Regenerates site/shots/ from the shell's own Render* tests.
#
# The landing page only ships real renders, so the shots are build OUTPUT that happens to be
# committed — and committed output rots. Two of them did: theme-industrial1999.png had no test behind it at
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
BEVEL_ONB_APPEARANCE_OUT="$OUT/theme-industrial1999.png" \
  run "$TB" "FullyQualifiedName~RenderOnboardingTest.Render_appearance_tab_to_png"
produced "theme-industrial1999.png"

echo "==> Start menu"
BEVEL_RENDER_OUT="$OUT/startmenu-industrial1999.png" \
  run "$TB" "FullyQualifiedName~RenderStartMenuTest.Render_start_menu_to_png"
produced "startmenu-industrial1999.png"

echo "==> Start menu, type-to-search"
# Pin win2000: the theory also covers luna, and both wrote the same path (last writer won).
BEVEL_RENDER_OUT="$OUT/startmenu-search.png" \
  run "$TB" "FullyQualifiedName~Render_searching_start_menu_to_png&DisplayName~win2000"
produced "startmenu-search.png"

echo "==> Luna colourways, four up"
BEVEL_LUNA_4UP_OUT="$OUT/blue2001-4up.png" \
  run "$TB" "FullyQualifiedName~Render_site_four_up"
produced "blue2001-4up.png"

echo "==> Downloads stack grid"
BEVEL_STACKGRID_REAL_OUT="$OUT/stack-grid.png" \
  run "$TB" "FullyQualifiedName~RenderStackGridRealPalTest"
produced "stack-grid.png"

# Nothing is hand-composited any more. The list stays so the audit can still tell "deliberately
# manual" from "silently dropped out of the pipeline" — the distinction that let theme-industrial1999.png rot.
MANUAL=""

# Two passes: each component test dumps its own piece (inheriting its settling and assertions), then
# the composite arranges them. Separate dotnet runs so the parts exist before the hero reads them.
echo "==> hero parts"
HERO_PARTS="$TMP/hero" && mkdir -p "$HERO_PARTS"
BEVEL_HERO_PARTS="$HERO_PARTS" run "$TB" \
  "FullyQualifiedName~Render_luna_start_menu_to_png|FullyQualifiedName~Render_luna_framed_window_to_png|FullyQualifiedName~Render_luna_taskbar_to_png"

echo "==> hero"
BEVEL_HERO_PARTS="$HERO_PARTS" BEVEL_HERO_OUT="$OUT/hero-blue2001.png" \
  run "$TB" "FullyQualifiedName~Render_site_hero"
produced "hero-blue2001.png"

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
