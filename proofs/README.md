# Proofs

Machine-checked proofs of a few Bevel invariants, written in [Bend](https://github.com/HigherOrderCO/Bend)
(`bend <file> --check-only`). They exist for properties where a proof buys something tests cannot: a test
samples cases, a proof covers the domain.

Two are here today:

| File | Invariant | Why it earns a proof |
|---|---|---|
| `handback.bend` | The key-focus handback never re-activates the captured app when a third app holds the foreground, and still restores focus when nothing else took front. | Safety property behind bevel-hx63. Five constructors cover the *entire* input space, because the C# only ever compares its three pids for equality. |
| `taskbar_geometry.bend` | For every size tier the glyph fits inside its button; and each tray per-row budget is safe — `rows × budget + 6 ≤ HeightForRows(rows)`. | bevel-c54t's fits-invariant was sampled tier-by-tier, and bevel-xpfl was a real overflow of exactly this inequality. |

## The catch, and how it is handled

**Bend cannot see C#.** These files prove things about a *model*. A model that drifts from the code is
worse than no model, because it still reports success (bend prints `ALL PROOFS CHECK`).

So the proofs are pinned: `tests/Bevel.Taskbar.Tests/ProofModelTests.cs` **parses these files** and asserts
that every constant and decision they assume is what the shipped C# actually produces. It reads the proof
rather than restating its numbers, so drift on *either* side fails the build. Demonstrated: changing
`TaskIconSize(Big)` from 32 to 28 in the proof keeps Bend happy (28 < 40 is still true) and fails the C#
test with `Expected: 28, Actual: 32`.

The same test project also runs `bend --check-only` on each file, so the proofs are verified by
`dotnet test` and cannot rot unnoticed. Bend is optional tooling: if it is not on `PATH` those two cases
no-op and the pinning tests still run.

## Division of labour

- **Proof**: quantifies over the domain — every tier, every handback world. Where the division lives in the
  C# (the tray budget), the proof verifies that the *value* is safe rather than reproducing the arithmetic.
- **Test**: checks the modelled domain is the real one, and that the C# computes the values proved safe.

## Adding one

Worth a proof when the property is (a) pure, (b) over a small or closed domain, and (c) something a
sampled test would only partially cover. Not worth it for anything needing real I/O, AX, or timing —
those want on-device QA instead.

```bash
bend proofs/<file>.bend --check-only        # check one
dotnet test tests/Bevel.Taskbar.Tests --filter ProofModelTests
```

Notes on Bend 2.x that cost time: `law` states a theorem and a same-named `def <name>()` proves it (`{==}`
is refl); `import Base` (capitalised) brings in `Bool`/`U32`/`Cmp`; comparison is `U32.cmp -> LT|EQ|GT`
(there is no `lte`); and the language is linear — a parameter used twice must be marked `+x: T`.
