---
name: nuclear-pr-review
description: "Use for unusually strict PR, branch, or diff reviews focused on maintainability, abstractions, file sprawl, type safety, tests, and blast radius. Adapted for Bevel (C#/.NET + Avalonia + Swift helper)."
license: Proprietary
---

# Nuclear PR Review

Use this skill for strict PR, branch, or diff review. The bar is maintainability, not just behavior: simple, clear, correct, safe, typed, well-owned, and reviewable code.

Before reviewing, read `references/review-doctrine.md`. Use it as the pushback guide. Keep this file as the execution checklist.

**Review target acquisition.** There is no `universal-pr-review` workflow in this repo. Do this instead:

- PR number or URL given → `gh pr view <n> --json headRefOid,baseRefName,body,statusCheckRollup,reviews,comments`, then `gh pr diff <n>`. Confirm the local checkout matches `headRefOid` before reading files; if it doesn't, fetch it. Never review stale code.
- Branch given → `git fetch`, then diff against the merge base (`git merge-base HEAD origin/main`), not against `main`'s tip.
- Nothing given → review the working diff plus any unpushed commits (`git log --oneline origin/main..HEAD`).
- Read `CLAUDE.md`, `AGENTS.md`, `CODEMAP.md`, and the relevant `docs/code-maps/` entry before commenting. Ground every finding in a current `path:line`.

The built-in `/code-review` command is a *different* tool with a different bar (correctness bugs plus cleanups). Do not substitute it for this skill; this one blocks on structure.

Core focus: inspect the diff and surrounding code, then review for structural simplification, abstraction quality, file sprawl, ad-hoc branching, ownership, type safety, blast radius, and test evidence.

## Review Stance

Perform a deep code quality audit, not a local-nit pass. Reconsider whether the implementation can be structured more cleanly without changing behavior. Look for structural moves that make code smaller, more obvious, or more naturally owned by the existing architecture. Prefer changes that remove concepts, branches, files, or layers over changes that merely tidy them.

## Quick Loop

1. Verify head, base, merge base, diff, checks, PR body, and review threads.
2. State the core change in one sentence.
3. Split the diff into core logic, generated/mechanical churn, tests, packaging/deploy, docs.
4. Name blast radius and what could break before listing wins.
5. Learn the canonical local pattern for the project/layer touched.
6. Hunt for unnecessary abstraction, wrong ownership, loose contracts, brittle orchestration, weak evidence, and review-hostile scope.
7. Apply the actionability gate from `references/review-doctrine.md`: prove each finding is caused or materially worsened by this diff and reachable in a current code path before assigning P1/P2.
8. Leave only findings that name the risk and point to a better shape.

## Required Dossier

Capture before commenting:

- Review target: head SHA, base, merge base, branch, and whether the checkout is current.
- Diff shape: changed files, diff stat, large files, generated files (`*.g.cs`, protobuf output), `packages.lock.json`, `global.json`, `.axaml`, packaging scripts, tests, docs.
- Core behavior: what changes, what must stay unchanged, and current behavior when inferable.
- Blast radius: which roles (`launcher`/`core`/`taskbar`/`explorer`/`desktop`), which projects, which PAL implementations, the shell-core IPC surface, the Swift helper contract, `settings.db` shape, TCC grants, packaging/signing.
- Evidence: tests, on-device QA, render captures, measurements, or gaps.
- PR hygiene: whether the body explains why, impact, risk, and validation.

## Required Checks

Apply these checks in order:

1. **Context and ownership**
   - Read `CLAUDE.md`, `CODEMAP.md`, and the package's code map before judging placement.
   - Follow callers, callees, PAL capability interfaces, generated contracts, shared helpers, and project boundaries far enough to support each claim.
   - Search for a canonical helper before accepting a bespoke one-off.
   - Check whether logic lives in the project that owns the concept: domain/services/VFS in `Bevel.Core`, composition in `Bevel.App`, shared chrome in `Bevel.UI`, platform behavior behind `Bevel.Pal.Abstractions`.

2. **Core-vs-mechanical**
   - Isolate hand-written behavior from generated output, lockfiles, formatting, and packaging churn.
   - Verify generated files came from the right tool and hide no unrelated edits.

3. **Simplification**
   - Ask "do we need this?" for new files, wrappers, flags, modes, helpers, tests, and settings.
   - Flag thin wrappers, pass-through helpers, generic mechanisms, repeated conditionals, narrow flags, and nullable modes that move complexity without deleting it.
   - Prefer direct code when an abstraction does not clarify ownership, invariants, or reuse.
   - Prefer delete, flatten, move, or reuse over adding another abstraction.

4. **Contracts, types, and class design**
   - Push back on `object`, `dynamic`, unchecked casts, loose JSON, optional bags, duplicated schemas, stringly shapes, and silent fallbacks. Treat every `!` null-forgiving operator as a claim needing proof at that line.
   - Prefer generated/source-of-truth contracts, typed PAL capabilities, and canonical wire shapes.
   - **Composition over inheritance.** A new base class must hold an invariant every derived type honours; shared code alone is a helper or an injected collaborator. `sealed` unless subclassing is a designed feature. In Avalonia, restyle with a `ControlTheme` or behaviour — never by subclassing a control.
   - **Properties**: no public fields; getters cheap and side-effect-free (XAML re-evaluates them on every invalidation, so an expensive getter is a per-frame cost and a blocking one is a UI-thread stall inside a binding); change notification for anything bound.
   - **LINQ**: preferred for clarity in cold paths; avoided in per-item/per-frame hot paths where the allocation repeats; never enumerating a sequence twice; `.Count`/`.Any()` over `.Count() > 0`. See the doctrine for the deferred-execution and `O(n²)` traps.
   - Apply the **boundary-object checks** in `references/review-doctrine.md` — PAL capability results, gRPC-generated messages, and the settings blob each have a specific way of lying to you, and specs that stub the boundary with the wrong object type hide it.

5. **Safety and evidence**
   - Map each risky behavior to a test or check that proves it.
   - Treat green CI as insufficient when it does not cover the changed path. A headless-green suite says nothing about AX, TCC, window activation, or Dock behavior.
   - Distinguish real failures from known flake classes with evidence (see the doctrine's render-test section).

6. **Orchestration and state**
   - Flag sequential or partial-update flows when independent work or atomic grouping would reduce reasoning burden.
   - Keep business logic separate from orchestration, IPC transport, persistence, and generated-contract glue.
   - Do not over-index on micro-optimizations; focus on structure that reduces reasoning burden.
   - **UI-thread audit is mandatory, not optional.** Any `.Result`, `.Wait()`, `.GetAwaiter().GetResult()`, inline icon render, enumeration, or file I/O reachable from the UI thread is at least P1. See the doctrine.
   - **Async correctness** (the C# analogue of the original's JS request-path check): `async void` outside a real event handler is an unobservable crash; independent awaits belong in `Task.WhenAll` rather than run serially; a `CancellationToken` accepted and ignored is worse than none; bound every helper call with a timeout; a fire-and-forget discard needs a stated owner for its failures; `TimeProvider` over `DateTime.UtcNow` wherever a deadline, cooldown, or rate limit appears, so the behaviour is deterministically testable instead of platform-skipped.

7. **Reviewability**
   - Treat a file crossing roughly 1000 lines, or substantial additions to an already-busy file, as a decomposition question. `TaskbarView.axaml(.cs)` is the standing offender — new markup belongs in its own file referenced by one line.
   - Push to split or defer unrelated upgrades, generated churn, or independent risky changes when they obscure risk.
   - Prefer small, reversible PRs with clear rollback.

## Questions To Ask While Reviewing

- Can this be reframed so fewer concepts, helpers, branches, files, or modes exist?
- Did the diff make a cohesive module more coupled, stateful, or difficult to scan?
- Is this behavior implemented in the project, role, or layer that owns the concept?
- Are repeated conditionals pointing to a missing model, policy, dispatcher, helper, or boundary?
- Is the code direct and readable, or relying on incidental control flow?
- Does this abstraction clarify ownership, invariants, or reuse, or just hide the current complexity?
- Did a cast, fallback, optional field, or loose object shape blur a real invariant?
- Would splitting the file, PR, or pure logic materially improve reviewability?
- Can the Fake PAL actually exercise this? If the fake no-ops the new path, the coverage is theater.

## Domain Add-Ons

Use `references/review-doctrine.md` for stack specifics. Always apply matching add-ons for:

- UI thread, Avalonia layout/measure, and theming/token changes.
- PAL capabilities, the Fake PAL, and cross-platform parity.
- Multi-process roles, shell-core IPC, and `settings.db`.
- The Swift helper, ObjC interop, AX/TCC, and gRPC contracts.
- Icon/bitmap caching and the shared memory-mapped pool.
- Tests: headless Avalonia, render captures, and xUnit collections.
- Packaging, signing, notarization, and CI.

## What To Flag Aggressively

Flag aggressively when the PR has:

- Complicated implementations with a concrete path to delete meaningful complexity.
- Refactors that rearrange complexity without reducing the concepts a reader must hold.
- New feature logic threaded through shared or unrelated paths.
- One-off booleans, nullable modes, repeated conditionals, or special cases likely to become permanent.
- Wrong owner, layer, role, or source of truth — including duplicating a metric that already has one authority.
- Loose contracts, casts, fallbacks, optional bags, or duplicated schemas.
- Business logic mixed with orchestration, IPC transport, persistence, or generated glue.
- Copy-pasted logic or bespoke helpers duplicating canonical utilities.
- Generated/lockfile/mechanical churn hiding the real diff.
- Large-file growth or file sprawl, especially around the 1000-line boundary.
- Tests that miss the risky behavior, or a Fake PAL that no-ops the path under test.
- Unclear blast radius, rollback, or PR context.
- Temporary-looking choices without an owner, exit criteria, or follow-up bead.
- A hidden dead control, or a capability left unregistered so absence becomes a DI accident rather than a stated fact.

Do not leave comments for preference-only cleanup.

## Preferred Fix Shape

- Delete unnecessary layers, wrappers, files, flags, or tests.
- Move logic to the owner or source of truth.
- Replace scattered branches with one model, policy, dispatcher, or helper.
- Turn a special case into a normal path with fewer exceptions.
- Extract a focused helper only when it clarifies ownership, invariants, or reuse.
- Split a large file into smaller owned modules; move new `.axaml` into its own file.
- Collapse duplicate paths into one clearer flow.
- Make type and data boundaries explicit; register an explicit null object rather than leaving a capability unbound.
- Separate business logic from orchestration, transport, persistence, and generated glue.
- Group related writes or state transitions atomically when partial state is hard to reason about.
- Reuse canonical helpers, theme tokens, or generated contracts.
- Split unrelated or independently risky changes into follow-ups, filed as beads.

## Tone

- Be direct, serious, and specific without being rude.
- Do not dilute structural concerns into style nits.
- If the change makes the codebase harder to reason about, say that plainly.
- If there is a plausible simpler design, describe the shape and why it reduces complexity.
- Use sparse, high-conviction comments. Avoid flooding the review with cosmetic feedback.
- Treat speculative, future-only, or preexisting concerns as non-blocking unless the current diff makes them materially worse.

## Severity

- **P0**: data loss, security, compliance, a crash-on-launch, or a merge-blocking correctness failure.
- **P1**: likely regression, wrong owner/source of truth, unsafe blast radius, broken contract, UI-thread block, or serious reviewability issue that should block merge.
- **P2**: meaningful maintainability, type-safety, test-evidence, or simplification issue with a clear improvement path.
- **P3**: optional cleanup. Use sparingly.

Every finding must include:

- Risk/cost.
- Why this diff creates or preserves it.
- Better shape, owner, contract, test, or scope split.

Do not assign P1/P2 for a parity, performance, or safety concern until you have checked the canonical code path, actual callers, relevant tests, and real runtime shape. If the issue is only theoretical, future-facing, or a coverage improvement, say so as non-blocking or omit it.

## Output

Lead with findings, ordered by severity and grounded in file/line references:

```text
Findings
- [P1] path/to/file.cs:123 - This puts feature-specific branching in a shared flow. Move the policy behind <owner/helper> so the general path stays readable.
- [P2] path/to/file.cs:45 - This wrapper does not preserve an invariant or reduce call-site complexity. Use the existing <helper> directly.

Structural opportunities
- Optional simplifications worth considering but not blockers.

PR summary
- Core logic:
- Generated/mechanical:
- Tests:
- Packaging/rollout:

Verification
- Commands run, source reads, CI checks, on-device QA still required, and blockers.
```

If there are no findings, say so clearly and still note residual risk or test gaps. File non-blocking follow-ups as beads (`BD_IGNORE_SCHEMA_SKEW=1 bd create ...`), never as markdown TODOs.

## Approval Bar

Do not approve only because behavior appears correct. Treat these as presumptive blockers unless clearly justified:

- The PR leaves avoidable incidental complexity when a simpler structure is visible.
- The PR pushes a file past roughly 1000 lines or substantially bloats an already-busy file.
- The PR adds ad-hoc branches, mode flags, or feature checks into unrelated flows.
- The PR places logic in the wrong project, role, layer, or source of truth.
- The PR adds unnecessary wrappers, casts, fallbacks, or generic mechanisms that obscure the design.
- The PR keeps builders, lifecycle hooks, options bags, or async wrappers that carry no invariant and do not reduce call-site complexity.
- The PR duplicates a canonical helper, theme token, or generated contract.
- The PR mixes business logic with orchestration, transport, persistence, or generated glue.
- The PR has unclear blast radius, rollback, or test evidence for risky behavior.
- The PR mixes core behavior with unrelated generated, lockfile, dependency, or formatting churn.
- The PR blocks the UI thread, disables antialiasing, ships a raster asset, or hides a dead control.

If the approval bar is not met, leave actionable feedback that points to the cleaner structure.
