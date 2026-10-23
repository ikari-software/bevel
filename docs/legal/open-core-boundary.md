# The open-core boundary

**Status:** decided 2026-10-04. The licence is in force; the repository split is not yet done.

Bevel Desktop is developed **open-core**. This repository is Apache-2.0. A second, private
repository holds proprietary components, and an official build combines the two.

This document says which component sits on which side, and — more importantly — **what the seam
between them has to be**, because that is the part that can quietly become impossible.

## Why Apache-2.0 and not something else

The alternatives considered were GPL-3.0, MIT, FSL and PolyForm Shield. The reasoning that
selected Apache-2.0:

- **Copyleft does not protect revenue.** Under GPL, someone may legally take the source, strip a
  licence check, and redistribute "Bevel Pro, unlocked" — they need only keep it GPL. Copyleft
  protects *openness*, not a paywall. Realising this is what moved the decision: if the paywall
  needs protecting, that protection has to come from the code not being published at all
  (open-core) or from the paid value being a *service*, never from the licence text.
- **GPL actively obstructs the paid build.** Bevel ships proprietary paid builds and may ship
  through stores. With a GPL core, every such build leans on the "the licence does not bind the
  copyright holder" exception. That is legally sound but operationally brittle, and it collapses
  the instant one outside contribution lands without a CLA.
- **Apache-2.0 adds an explicit patent grant** (§3) and explicitly withholds trademark rights
  (§6), both of which matter once money is involved. MIT gives neither.
- **It is OSI-approved**, so distro packaging, Flathub, and the word "open source" all stay
  available. FSL and PolyForm Shield would each have cost that.
- **FSL was the closest runner-up** and was rejected for one specific reason: its grant converts
  each version to Apache-2.0 after two years. That is safe for SaaS, whose code rots, and leaky
  for a desktop shell, which does not — a two-year-old Bevel is still a perfectly good Bevel, so
  a competitor could simply wait out the clock.

## The line

**Open (this repository, Apache-2.0):**

| Project | Why it is open |
|---|---|
| `Bevel.Core` | Domain, settings, VFS. The substrate everything needs. |
| `Bevel.UI` | Shared chrome, `Glyphs`, the theming services. |
| `Bevel.Taskbar` | The taskbar, Start menu, tray, `ShellModel`. |
| `Bevel.Themes.Industrial1999`, `Bevel.Themes.Blue2001` | The skins are the project's public identity. |
| `Bevel.Desktop` | The desktop surface. |
| `Bevel.Ipc`, `Bevel.ShellCore.Ipc` | Transports. Must be open for the private side to speak them. |
| `Bevel.Pal.*` (Abstractions, MacOS, Windows, Fake) | Platform abstraction; `Fake` is what makes the open tree testable. |
| `Bevel.Interop` | ObjC interop, automation control socket. |
| `Bevel.App` | Composition root, role dispatch, supervision. |
| `bevelctl`, `BevelShot`, `ThemeGen` | Tooling. |
| `third_party/classic-avalonia` | Vendored MIT, stays MIT (see NOTICE). |

**Private (separate repository, proprietary):**

| Component | Notes |
|---|---|
| `Bevel.FileManager` — Filer | The file manager window, `Components/`, VFS UI. |
| Selected widgets | Which ones is not yet settled. |

## The seam, and why this split is tractable

The thing that usually kills an open-core split is that the open composition root has to
*reference* the private assembly. Here it does today — `Bevel.App` has seven files that
`using Bevel.FileManager`:

```
FileManagerWindowFactory.cs   FileManagerShellSurface.cs   FileManagerWindowRegistry.cs
ParkedFilerWindowHost.cs      MainWindow.axaml.cs          App.axaml.cs
CompositionRoot.cs
```

**But Bevel is already multi-process, and the boundary can follow a process boundary that
exists.** Each Filer window already spawns as its own `--role=filer` process with `--open-path`.
A process is launched by path and spoken to over IPC; it does not need to be linked. So the open
launcher can spawn a private filer binary without ever referencing its assembly.

Two useful seams already exist and should be the template: `IShellSurface` (implemented by
`FileManagerShellSurface`) and `IParkedFilerWindowHost`. The work is to move the *interfaces*
into the open tree and the *implementations* behind the process boundary.

Non-negotiables for the seam:

1. **The open tree must build, run and pass its tests with the private half absent.** If
   `dotnet build Bevel.sln` requires the private repo, the open repo is theatre. The open build
   must degrade honestly — no Filer surface, everything else working.
2. **No private type may appear in an open signature.** The moment `Bevel.App` needs a
   `FileManagerWindow` in a method signature, the boundary is gone.
3. **`Bevel.Core` must stay free of it entirely.** It already is: the only mention is an
   `InternalsVisibleTo` for `Bevel.FileManager.Tests` and one comment noting that the `ViewMode`
   enum lives in `Bevel.FileManager` *because* Core cannot reference it. ARCH-02 already
   enforces the direction this split needs.
4. **The IPC contracts stay open.** A private filer speaking a private protocol to an open core
   would make the open half unusable on its own.

## Pricing (sketch — not decided, not implemented)

Recorded here so the licensing choices above are read in the right context. Numbers and
mechanics are explicitly provisional.

- **Pay once** — perpetual licence to the *current* version. No updates beyond it.
- **Monthly** — updates, plus capabilities that genuinely cost ongoing backend effort rather
  than being an artificial gate: AI features, settings sync, file sync. The test for anything in
  this tier is that it is *expensive to run*, not merely withheld.
- **Perpetual-everything** — a deliberately expensive one-off, around €499.

The licensing consequence: anything that is a **service** (sync, AI, a licence server) is
protected by being a service and does not depend on the open-core split at all. Anything that is
**local code** has to live in the private repository to be protected. That distinction should
drive which features land on which side, rather than the split being decided first.

## What is not solved by any of this

**The Mac App Store is blocked on architecture, not licence.** `packaging/macos/Bevel.entitlements`
requests `com.apple.security.cs.allow-unsigned-executable-memory` and
`com.apple.security.cs.disable-library-validation`, both disqualifying for the Mac App Store, and
does **not** request `com.apple.security.app-sandbox`, which is mandatory there. The shell also
calls `SCShareableContent`, `CGRequestScreenCaptureAccess`, and writes `com.apple.dock`
preferences — modifying another application's preferences is categorically forbidden under the
sandbox, and it is how the Dock is hidden and restored. A separately TCC-granted helper over a
Unix socket compounds it.

Realistic distribution for this category: **Setapp** on macOS (accepts non-sandboxed power-user
utilities), **Microsoft Store** on Windows (no sandbox mandate for Win32/MSIX), Homebrew **cask**
(casks host proprietary software routinely), AUR, Flathub. Direct download remains primary.

## Open obligations

- **A CLA is required before the first outside contribution is merged.** Open-core and any future
  dual-licensing both depend on single-copyright ownership. Every commit in history is currently
  ikari's, so this costs one file today and is near-impossible to retrofit later. See `CLA.md`.
- The repository split itself is not done. Filer is still in this tree, Apache-2.0, and remains so
  until it is moved.
