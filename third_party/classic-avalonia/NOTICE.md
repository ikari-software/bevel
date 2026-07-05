# Vendored: Classic.Avalonia

This directory contains an in-tree (vendored) copy of two projects from the
**Classic.Avalonia** theme, which Bevel previously consumed as the
`Classic.Avalonia.Theme` NuGet package.

- **Upstream:** https://github.com/BAndysc/Classic.Avalonia
- **Tag:** `v11.3.0.3`
- **Commit:** `43142b75592728bd75e4ad74053f5711d9fbffa0`
- **License:** MIT (see `LICENSE`), author **bandysc** (Copyright (c) 2024 BAndysc)
- **Copied:** 2026-07-05

## What was vendored

- `Classic.Avalonia.Theme/` — the Win2000 classic theme (`ClassicWindow`, `ClassicTheme`, …)
- `Classic.CommonControls.Avalonia/` — controls the theme depends on (also needs `Avalonia.Skia`)

The upstream `Dock`, `ColorPicker`, and `DataGrid` projects were **not** vendored
(nothing in Bevel references them).

## Why

We want to own the Win2000 theme source outright (extend/patch it freely) and
drop the external NuGet dependency.

## Local modifications

The upstream namespaces **and** assembly names (`Classic.Avalonia.Theme`,
`Classic.CommonControls.Avalonia`) are preserved intact so every
`avares://Classic.Avalonia.Theme/...` URI, `using Classic.Avalonia.Theme;`, and
`xmlns:classic="using:Classic.Avalonia.Theme"` keeps resolving unchanged.

Only the two `.csproj` files were edited for this repo: single `net10.0` target,
Central Package Management (no `Version=` on `PackageReference`), and removal of
NuGet-packaging metadata. Source (`.cs`, `.axaml`, assets) is unmodified.

## Pulling upstream fixes

To pull upstream fixes, diff this directory against upstream tag `v11.3.0.3`
(commit `43142b75592728bd75e4ad74053f5711d9fbffa0`) and apply the delta.
