# Bevel

**Bevel** is a cross-platform desktop **shell replacement** — a themable desktop,
taskbar (window list, start menu, system tray, clock) and a Windows-2000-Explorer-style
file manager — all fully owner-drawn in C# / [Avalonia](https://avaloniaui.net/) and
rendered pixel-identically on macOS, Windows and Linux. The default look is the
Windows 2000 "Classic" theme. Identifiers are frozen at M0: reverse-DNS `pl.ikari.bevel`,
URL scheme `bevel://`.

The full design lives in [`docs/spec/`](docs/spec/) — start with
[`00-master-plan.md`](docs/spec/00-master-plan.md) and
[`01-architecture.md`](docs/spec/01-architecture.md).

## Status: M0 — Bootstrap scaffold

This is the **M0 bootstrap**: a buildable, trimmed solution that boots an Avalonia
window on macOS and establishes the project graph. It is the foundation, **not** the
product — feature modules are placeholders and the platform layers are stubs.

Per master-plan §3 R9 the M0 solution is trimmed to the **macOS-v1 + Fake** slice.
`Bevel.Pal.Windows`, `Bevel.Pal.Linux`, `Bevel.Themes.Luna`, `Bevel.Themes.Win11`
and the Linux/Windows helpers are added when their platform tracks start.

## Layout

```
Bevel.sln
global.json / Directory.Build.props / Directory.Packages.props   # SDK pin + CPM
src/
  Bevel.Pal.Abstractions   # capability-oriented PAL interfaces + DTOs (BCL only)
  Bevel.Core               # domain / services / settings placeholder
  Bevel.Pal.Fake           # deterministic in-memory PAL (--pal=fake, default)
  Bevel.Pal.MacOS          # macOS PAL impl stubs (AppKit / helper client land here)
  Bevel.Ipc                # gRPC/proto helper contract placeholder -> proto/
  Bevel.UI                 # shared chrome primitives (Avalonia)
  Bevel.Themes.Win2000     # default theme (references Classic.Avalonia.Theme)
  Bevel.Desktop            # desktop surface module (placeholder)
  Bevel.Taskbar            # taskbar module (placeholder)
  Bevel.FileManager        # file manager module (placeholder)
  Bevel.App                # Avalonia executable, composition root (Hosting + DI)
tests/
  Bevel.Pal.ContractTests  # PAL contract assertions vs Fake
  Bevel.Core.Tests         # unit test placeholder
  Bevel.UI.Tests           # Avalonia.Headless UI smoke test
native/helper-macos/       # Swift Package skeleton (BevelHelper) — gRPC/UDS is a later task
proto/                     # bevel.helper.v1.proto placeholder
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
