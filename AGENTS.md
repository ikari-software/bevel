# AGENTS.md

Start here, whatever agent you are — this repo's guidance applies to all of you, not just Claude.

- **Navigation first:** [`CODEMAP.md`](CODEMAP.md) — where the code lives and what owns what (process
  roles, theming, settings, IPC, the native helper). Read it before you grep; it records the things
  search can't see (ownership boundaries, sibling disambiguation, not-this trails, key symbols).
- **Code-map format + index:** [`docs/code-maps/FORMAT.md`](docs/code-maps/FORMAT.md) and
  [`docs/code-maps/INVENTORY.md`](docs/code-maps/INVENTORY.md). Add a package-level `CODEMAP.md` when a
  package earns one, and keep the inventory in sync.
- **Full project conventions** — build/test, architecture rules (e.g. `Bevel.Core` must not reference
  Avalonia; never block the UI thread), and task tracking — live in [`CLAUDE.md`](CLAUDE.md). Read it.

Quick reference: build `dotnet build Bevel.sln -clp:ErrorsOnly` · test `dotnet test Bevel.sln` · safe
dev run `dotnet run --project src/Bevel.App -- --pal=fake`. macOS/BSD shell syntax. Task tracking is
`bd` (beads) — not TodoWrite or markdown TODO lists (`bd prime` for context).
