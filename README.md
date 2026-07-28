# Bevel

**Bevel** is a cross-platform desktop **shell replacement** — a themable desktop,
taskbar (window list, start menu, system tray, clock) and a Windows-2000-Explorer-style
file manager — all fully owner-drawn in C# / [Avalonia](https://avaloniaui.net/). It targets
**macOS today** (Windows and Linux PALs are planned). The default look is the Windows 2000
"Classic" theme, with a switchable **Luna** (XP) theme and a Flat/Whistler theme in design.
Identifiers are frozen: reverse-DNS `pl.ikari.bevel`, URL scheme `bevel://`.

The full design lives in [`docs/spec/`](docs/spec/) — start with
[`00-master-plan.md`](docs/spec/00-master-plan.md) and
[`01-architecture.md`](docs/spec/01-architecture.md).

## Status

Working macOS shell, well past the bootstrap. The taskbar (window list with grouping,
Start menu, adaptive system tray, clock), the Win2000 + Luna theme engines, a multi-process
split (`--role` launcher/core/taskbar + per-window Explorer processes), a real macOS platform
layer with a Swift helper (window/tray enumeration over gRPC/UDS), and a Windows-2000-style
file manager (navigation, tabs, streaming enumeration, drag-drop, rename, Folder Options,
selectable info panel) are all implemented. Code-signing + notarization + a DMG release lane
exist. `Bevel.Pal.Windows` / `Bevel.Pal.Linux` and a Flat theme are still to come.

See open work with `bd ready`; the design spec is in [`docs/spec/`](docs/spec/).

## Layout

```
Bevel.sln
global.json / Directory.Build.props / Directory.Packages.props   # SDK pin + CPM
src/
  Bevel.Pal.Abstractions   # capability-oriented PAL interfaces + DTOs (BCL only)
  Bevel.Core               # domain / services / settings / VFS (no Avalonia — ARCH-02)
  Bevel.Pal.Fake           # deterministic in-memory PAL (--pal=fake)
  Bevel.Pal.MacOS          # macOS PAL: AppKit interop, helper client, window/tray/icons
  Bevel.Ipc                # gRPC-over-UDS helper transport (nonce auth)
  Bevel.ShellCore.Ipc      # taskbar <-> core UDS transport
  Bevel.Interop            # ObjC interop + automation control socket (bevel:// / CLI)
  Bevel.UI                 # shared chrome (BevelWindow, Glyphs, theming services)
  Bevel.Themes.Win2000     # default theme (aliases Classic.Avalonia.Theme)
  Bevel.Themes.Luna        # Luna (XP) glossy vector ControlThemes
  Bevel.Desktop            # desktop surface window
  Bevel.Taskbar            # taskbar, Start menu, tray, background ShellModel
  Bevel.FileManager        # Explorer window + components + VFS UI
  Bevel.App                # executable: composition root, --role/--pal, window factories
  bevelctl                 # small CLI
tests/                     # xUnit + Avalonia.Headless (per-project test suites)
benchmarks/Bevel.Benchmarks# BenchmarkDotNet (not in the solution)
native/helper-macos/       # Swift helper (BevelHelper): AX/CGWindowList/SCK over gRPC
packaging/macos/           # build-app.sh, dev-sign.sh, notarization
proto/                     # bevel.helper.v1.proto
```

## Build & test

```sh
dotnet build Bevel.sln
dotnet test  Bevel.sln
```

Requires the .NET 10 SDK (pinned in `global.json`). Run the app with the Fake PAL:

```sh
dotnet run --project src/Bevel.App -- --pal=fake
```
