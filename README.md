# Bevel Desktop

**Bevel Desktop** is a cross-platform desktop **shell replacement** — a themable desktop,
taskbar (window list, start menu, system tray, clock) and an integrated classic-style
file manager — all fully owner-drawn in C# / [Avalonia](https://avaloniaui.net/). It targets
**macOS today** (Windows and Linux PALs are planned). The default skin is **Bevel 1999
Industrial**, with switchable **Bevel 2001 Blue** and a **Bevel Flat (preview)** theme in
design. Identifiers are frozen: reverse-DNS `pl.ikari.bevel`, URL scheme `bevel://`.

The full design lives in [`docs/spec/`](docs/spec/) — start with
[`00-master-plan.md`](docs/spec/00-master-plan.md) and
[`01-architecture.md`](docs/spec/01-architecture.md).

## Status

Working macOS shell, well past the bootstrap. The taskbar (window list with grouping,
Start menu, adaptive system tray, clock), the Bevel 1999 Industrial + Bevel 2001 Blue theme
engines, a multi-process split (`--role` launcher/core/taskbar + per-window Filer processes),
a real macOS platform layer with a Swift helper (window/tray enumeration over gRPC/UDS), and
a classic-style file manager (navigation, tabs, streaming enumeration, drag-drop, rename,
Folder Options, selectable info panel) are all implemented. Code-signing + notarization + a
DMG release lane exist. `Bevel.Pal.Windows` / `Bevel.Pal.Linux` and Flat are still to come.

See open work with `bd ready`; the design spec is in [`docs/spec/`](docs/spec/).

**Site:** [bevel.ikari.software](https://bevel.ikari.software/) (canonical). Alias:
[ikari.software/bevel-desktop](https://ikari.software/bevel-desktop) → 301 to the former.
Static Velopack feed directory (empty until first release):
`https://bevel.ikari.software/updates` — set `updateFeedUrl` to that when you want checks on.

## Trademarks

Bevel Desktop is an independent, open-source software project developed by ikari.software.
Bevel Desktop is not affiliated with, endorsed by, sponsored by, or associated with Microsoft
Corporation, Apple Inc., or the Linux Foundation. Microsoft, Windows, Windows 2000, Windows XP,
Windows Vista, and the Windows logo are registered trademarks of Microsoft Corporation. Apple,
macOS, and the Apple logo are registered trademarks of Apple Inc. Linux is the registered
trademark of Linus Torvalds. Tux the penguin was created by Larry Ewing (lewing@isc.tamu.edu)
using The GIMP. All third-party marks are used strictly in a descriptive and referential
capacity.

Parked clearance / theme-licensing questions: [`docs/legal/open-questions-parked.md`](docs/legal/open-questions-parked.md).
Third-party themes: **no hosted gallery**; install from **git repositories** only. Package licensing / install disclaimers remain **TBD**.

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
  Bevel.Themes.Luna        # Bevel 2001 Blue glossy vector ControlThemes
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
