# Hero app fixtures

Original Bevel-drawn icons (not third-party OS marks). SVG sources in `src/`;
64×64 PNGs are the committed raster used by `HeroApps` / the landing-page hero harvest.

Regenerate PNGs after editing an SVG:

```sh
for n in Browser Console Viewer Calculator; do
  rsvg-convert -w 64 -h 64 tests/fixtures/hero-apps/src/$n.svg \
    -o tests/fixtures/hero-apps/$n.png
done
```

Or: `BEVEL_REFRESH_HERO_APPS=1 dotnet test … --filter FullyQualifiedName~RefreshHeroAppFixtureTest`
