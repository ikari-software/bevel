# Code Map Inventory

Maps that exist in this repo. Read the nearest one to your change first; follow a map's **Coupling**
section into another map only when evidence points there. Format rules: `FORMAT.md`.

| Map | Covers |
|---|---|
| [`/CODEMAP.md`](../../CODEMAP.md) | Repo overview — process roles, project boundaries, and where theming / settings / IPC / the native helper live. |

No per-package `CODEMAP.md` files yet. Add one at a package root (e.g. `src/Bevel.Taskbar/CODEMAP.md`)
when that package has grep-fails facts worth recording — sibling disambiguation, not-this trails, ownership
boundaries, or key symbols — and list it here in the same change. Good first candidates: `Bevel.UI`
(theming engines), `Bevel.Taskbar` (ShellModel + tray), `Bevel.FileManager` (Filer + VFS UI), and the
Swift helper under `native/helper-macos`.
