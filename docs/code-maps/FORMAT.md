# Code Map Format

`CODEMAP.md` files are navigation maps for agents, not full architecture docs. They help an agent find
the right code quickly without copying large amounts of detail out of the codebase.

When the target package path is already known, read its local `CODEMAP.md` directly and skip
`INVENTORY.md`. If several paths are named, start with the nearest owning map, then read coupled maps
only when that first map or source evidence shows they're needed.

## Size Budget

- Target 35–70 lines for most maps.
- Use up to 90 lines only for complex, hand-verified systems (e.g. the repo-root `CODEMAP.md`).
- Cap lists at 8 bullets. If there are more, summarize with a directory, glob, or count.
- Don't paste long generated-file, test, dependency, or dashboard inventories. Point to the owning directory.

## Spend Lines Where Grep Fails

Agents find well-named files cheaply with search; maps pay for what search can't see. Priority order:

1. **Ownership / boundary facts** — which layer owns the data or behaviour ("live theme colour comes from
   `Blue2001VariantService` / `ColorSchemeService`, not the static `.axaml`"; "`Bevel.Core` must not reference Avalonia").
2. **Disambiguation between siblings** — when several files could plausibly own a change, say which is which
   ("Win2000 window chrome = `BevelWindow` in `Bevel.UI`; the taskbar's window/tray model = `ShellModel` in
   `Bevel.Taskbar/Model`").
3. **Not-this warnings for known false trails** ("to change a setting, don't edit `settings.json` — it's a
   passive export; go through `SettingsService`").
4. **Exact symbol names, not just files** — naming `ResolveHelperBinary` turns "how is the helper found?" into
   one targeted grep instead of an exploration.
5. **Contract source-of-truth pointers** — "gRPC message shapes live in `proto/`; verify there, don't infer
   from a call site."

A line that only names a file whose name already matches its concept adds little; keep such entries as
anchors for the facts above, not as inventory.

## Local Doc Sections

Use this compact shape unless a package has a strong reason to differ:

```markdown
# <Name> Code Map

For shared format rules, see `docs/code-maps/FORMAT.md`.

## Snapshot
- Purpose, code path, runtime shape, main language/framework signals.

## Start Here
- 5–8 files/directories to read first, with one-line reasons.

## Entrypoints
- Roles, commands, handlers, routes, workers, package exports, or deploy scripts.

## Contracts And Data
- Source contracts, schemas, generated-model boundaries, key type directories, persistence.

## Coupling
- Verified upstream/downstream relationships when known. Name/registry clues only when labeled as clues.

## Local Commands
- Package-local test, lint, build, generate, or dev-run commands.

## Notes
- Sharp edges, generated-code warnings, regulatory flags, stale-doc concerns, verification gaps.
```

## What To Avoid

- Generic change recipes that are the same for every .NET project or Avalonia view.
- Long "open questions" repeated across maps.
- Raw dependency lists from `.csproj` / `package.json`.
- Raw generated-file or migration lists when the directory path is enough.
- Metadata a future agent can get from the solution file or `INVENTORY.md`.

## Update Checklist

Update a local map only when a change alters **entrypoints, contracts, persistence models, generated-code
boundaries, local commands, runtime dependencies, deploy shape, or known sharp edges**. Routine edits that
don't move those don't need a map change.

If a package is renamed, added, removed, or moved, update `INVENTORY.md` in the same change.

> Path hygiene: a stale path makes agents distrust the whole map. Re-check every path you cite. (A
> `scripts/bin/check-codemaps` linter like the one this format came from isn't wired in this repo yet —
> verify by hand until it is.)
