# Review Doctrine

Pushback guide for `nuclear-pr-review`. The SKILL.md is the checklist; this is the argument you make when something is wrong, plus the stack specifics for **.NET 10 / C# 13+ / Avalonia / Swift helper**.

---

## 1. The Actionability Gate

Run this before assigning any severity. It exists because a strict reviewer that cannot distinguish "this diff broke it" from "I noticed something" becomes noise, and noise gets ignored.

A finding earns **P1/P2** only if all four hold:

1. **Caused or materially worsened by this diff.** A preexisting wart the diff merely touches is non-blocking. Say so explicitly rather than smuggling it in at P2.
2. **Reachable in a current code path.** Trace an actual caller. Dead-on-arrival code is a deletion suggestion, not a defect.
3. **Checked against the canonical path.** Read the real callers, the real tests, and the real runtime shape. Do not reason from the interface alone.
4. **Has a named better shape.** If you cannot describe the cleaner structure, you have a feeling, not a finding.

Downgrade to non-blocking, or omit, anything that is theoretical, future-facing, or purely a coverage improvement.

**Two failure modes, both fatal to a review's credibility:**

- *Rubber stamp* — approving because the suite is green. Green proves the covered paths still pass; it says nothing about the paths nobody covered.
- *Flood* — twelve P2s where two matter. Severity inflation destroys the signal that makes a strict review worth reading.

**Verify before you assert.** If a bead, comment, or PR body states a cause, confirm it in code. Stated premises are wrong often enough that checking is the job — a review that repeats a wrong premise with more confidence has made things worse.

---

## 2. Bevel Architecture Invariants

These are presumptive blockers. Each is load-bearing and each has been broken before.

### UI thread (non-negotiable)
`.Result`, `.Wait()`, `.GetAwaiter().GetResult()`, `Task.Run(...).Result`, inline icon render, directory enumeration, or file I/O on a path reachable from the UI thread is **at least P1**. Heavy work goes off-thread; only the cheap result marshals back via `Dispatcher.UIThread.Post/InvokeAsync`.

Sync-over-async is the usual disguise. So is a property getter that touches the disk.

### Project boundaries
- `Bevel.Core` **must not** reference Avalonia (ARCH-02). A `using Avalonia` there is P1 regardless of how convenient.
- Composition belongs in `Bevel.App`; shared chrome in `Bevel.UI`; platform behavior behind `Bevel.Pal.Abstractions`.
- Logic in the wrong project is P1 even when it works — wrong ownership is the defect.

### Single source of truth
Bar height, work-area band, row height, and icon metrics each have exactly one authority (`TaskbarTheme` → `HeightForRows` → `TaskbarGeometry.WorkAreaBand`). A diff that forks, duplicates, or hardcodes a value that already has an owner is P1. Two authorities silently disagree the moment one changes.

### Assets and rendering
Vector only — SVG or code-drawn geometry bound to theme tokens. Never a raster asset for chrome. **Never disable antialiasing**, including "for authenticity". Never upscale; re-render at the target size.

### Theming
`LunaVariantService` and `ColorSchemeService` **override** static theme tokens and must be `Clear()`'d symmetrically when switching away. A new brush must exist in *every* skin — a token defined in one theme renders invisible in the others. Do not add `RequestedThemeVariant` / `ThemeVariantScope` overrides; the app pins Light deliberately.

### No hiding
Never hide a dead control or route around a missing feature. Wire it to real functionality or build it. Hiding relabels a gap as intentional — and that includes leaving a PAL capability unregistered so "unsupported here" becomes an accident of DI resolution rather than a stated fact. Register an explicit null object with a comment saying why.

---

## 3. Modern C# / .NET 10 Add-On

Apply where it makes the code smaller or the invariant explicit — not as a style sweep. A rewrite to a newer construct that changes nothing a reader must hold is P3 at best.

### Nullability and contracts
- Nullable reference types are on. Treat every `!` (null-forgiving) as a **claim that needs proof at that line**; prefer `is not null` patterns or a real guard. A `!` that only silences the compiler is P2.
- `required` init-only members beat a constructor-less mutable bag with optional fields.
- `record` / `record struct` for value semantics. Watch for records exposing mutable collection properties — that quietly breaks the equality the record implies.
- Expose `IReadOnlyList<T>` / `IReadOnlyDictionary<,>`; returning `List<T>` hands callers a mutation channel you did not intend.
- Prefer a domain type over primitive obsession: an `int` that is really a pid, a DIP, or a row count invites the wrong arithmetic. The repo already does this (`TrayItemId`, `WindowId`).
- `object` / `dynamic` / unchecked casts / stringly-typed shapes get the same pushback the original doctrine gave `any`: name the invariant they blur.

### Async correctness (this replaces the JS request-path section)
- `async void` **only** for genuine event handlers. Anywhere else it is an unobservable crash — P1.
- Independent awaits run concurrently: `await Task.WhenAll(a, b)`, not `await a; await b;`. Flag serial awaits where the work is independent *and* the concurrent shape also removes sequencing logic.
- Thread `CancellationToken` through; a token parameter that is accepted and ignored is worse than none. Bound anything that talks to the helper with a timeout — a wedged peer must not leak an in-flight task per call.
- `ValueTask` for hot paths only, and never awaited twice or stored.
- Fire-and-forget `_ = SomethingAsync()` needs a stated owner for its failures, or it is a swallowed exception with extra steps.
- `IDisposable`/`IAsyncDisposable` symmetry; `using var` at the narrowest scope. A replaced bitmap must be disposed — one leak per hover becomes one leak per frame.
- **`TimeProvider` over `DateTime.UtcNow`** for anything with a deadline, cooldown, or rate limit. A real-clock test is slow, flaky, and usually ends up platform-skipped — which is how a rate limiter ships with no deterministic coverage. If a diff adds time-based logic without a time seam, that is P2 with a clear better shape.

### Class design — composition over inheritance
- **Default to composition.** A new base class earns its place only by holding an invariant every derived type must honour. Shared *code* is not a reason to inherit; that is what a helper, an injected collaborator, or an extension method is for. A two-member abstract base with one implementation is P2 with an obvious better shape.
- **`sealed` unless subclassing is a designed feature.** An unsealed class is a public extension point you now have to keep working.
- **Prefer an interface plus an implementation over an abstract class** when the contract is behavioural. The PAL is the house pattern: capability interface, one real implementation per platform, one fake. Copy that shape rather than inventing a parallel hierarchy.
- **Watch for inheritance used to reach protected state.** That is coupling wearing a type hat; pass the state in.
- **In Avalonia, do not subclass a control to restyle it.** Use a `ControlTheme`, a style selector, or an attached behaviour. Subclassing to change appearance fragments theming and breaks the token-driven recolor engines. `BevelWindow : ClassicWindow` exists because it inherits *client-drawn chrome behaviour*, not to change colours — that is the bar for a new control subclass.
- Deep hierarchies in view models are almost always a missing collaborator. Flag a third level.

### Properties
- **Public fields are not reviewable API.** Use properties; `init` or `required` for construct-once values.
- **A getter must be cheap and side-effect-free.** This is stricter here than in ordinary C#: XAML bindings re-evaluate getters on every invalidation, so an expensive computed property becomes a per-frame cost, and a getter that touches disk or takes a lock can block the UI thread from inside a binding. Compute once and cache, or make it an explicit method so the cost is visible at the call site.
- Expression-bodied getters for genuine projections; a getter that spans a screen wants to be a method.
- Raise change notification for anything bound — a silently-updated property renders stale. Follow the repo's `ObservableObject` pattern; do not hand-roll a second one.
- Do not expose a settable property whose setter must be called in a particular order. That is a constructor or a factory.
- An optimistic UI property update (set locally, then confirm from the round-trip) is an established pattern here, but it must be paired with the reconcile that corrects it — flag the optimistic half arriving alone.

### LINQ
- **Prefer LINQ for clarity in cold paths**: startup wiring, projections, one-shot filtering, test setup. `.Where().Select().FirstOrDefault()` beats a hand-rolled loop with an index and a flag, and it beats a nested conditional chain.
- **Avoid it in per-item, per-frame, and per-window hot paths.** Every operator allocates an enumerator and a closure; in an enumeration that runs on every reconcile tick or item realization that cost is real and repeated. A plain `for` over a list is the right call there, and worth saying so in a comment.
- **Never enumerate twice.** `.Any()` then `.First()`, or `.Count()` then indexing, walks the sequence more than once — and if the source is lazy, does the work more than once. Materialize with `.ToList()` once, or restructure.
- `.Count()` on something already `IReadOnlyCollection<T>` should be `.Count`. `.Any()`, not `.Count() > 0`.
- Watch for deferred execution crossing a thread or a lock boundary: a query built on the background thread and enumerated on the UI thread runs its predicate on the UI thread. That is a UI-thread block hiding inside a `var`.
- An `O(n²)` shape (`.Any()` or `.Contains()` inside a loop over the same collection) is P2 once the collection is window- or file-count sized. A `HashSet`/`Dictionary` lookup or a single grouped pass is the fix.
- LINQ that needs a comment to explain what it returns has lost the argument — split it or name the intermediate.

### Structure
- `internal` unless the surface is genuinely public.
- `static` local functions where no capture is needed; `file`-scoped types for single-file helpers.
- Switch expressions and pattern matching over if-else ladders — the repo's `size switch` tier mapping is the house style.
- Collection expressions (`[...]`) where they shorten without obscuring.
- Primary constructors are fine for plain carriers; be wary when the type needs validation or the captured parameter gets mutated — the capture semantics surprise readers.

### AOT and trimming (the shell is heading to AOT)
- Reflection-based `System.Text.Json` is AOT-hostile. Use the source-generated context — the repo already has `SettingsJsonContext`; a new payload that bypasses it is P2.
- No `dynamic`, no runtime code generation, no reflection over types the trimmer cannot see.
- New P/Invoke should be source-generated `[LibraryImport]`, not `[DllImport]`. Existing `objc_msgSend` interop is `DllImport` by history; new interop should not add to it.
- `[UnmanagedCallersOnly]` is the established pattern for native callbacks (see the Apple Events recipe).

### Performance, where it is structural
- `ArrayPool<byte>` / `Span<T>` for pixel and parse buffers rather than per-call allocation — relevant to the BGRA pool path.
- `FrozenDictionary` for read-mostly lookups built once at startup.
- Do not chase micro-optimizations. Flag allocation only when the fix also simplifies the shape.

### Warning hygiene
The solution builds with warnings. A diff that adds new warnings, or suppresses one without a reason comment, is P2. `#pragma warning disable` with no justification is a hidden decision.

---

## 4. Boundary-Object Traps

The bug class: an object crossing a boundary is **not** the type the call site assumes, and the test hides it by stubbing that boundary with something richer than production supplies. Every stack has a version of this; here are Bevel's three.

Three boundaries in this repo hand you something that is **not** the rich object you might assume:

1. **PAL capability results.** A capability advertises `Capabilities { Available, Notes }`. A capability that exists in the container is not a capability that *works* — `Available: false` and a null-object implementation both resolve fine. Verify the call site handles the unavailable case as a normal path, not an exception path. And verify the **Fake PAL actually implements it**: a fake that no-ops the method under test gives you a green suite over an untested path. That has already happened (`FakeWindowManager.RepositionAsync` was a no-op, so an entire mitigation engine had zero coverage).

2. **gRPC-generated messages.** A generated message is a wire DTO, not a domain object. Absent fields arrive as type defaults, not null — `0`, `""`, `false` are indistinguishable from "not sent". Check whether the code can tell "reported zero" from "not reported". Also respect the transport caps: a payload that grows per-window (icons, thumbnails) is bounded by the 4 MB `ListWindows` ceiling, and the existing 64×64 icon size is a deliberate trade against it.

3. **The settings blob.** Settings are a single-row JSON blob in SQLite, owned by the **core** process, polled at 750 ms. Peers never open the DB directly. Consequences a diff must respect: a new setting has to round-trip the `SerializeBlob`/`ProjectBlob` peer-snapshot seam (that, not the in-process object, is how peers actually read it); it needs a default and a garbage-value fallback; live-apply must ride the poll rather than an in-process event; and a setting that exists in the DB but appears in no UI is a half-wired feature.

For all three: **distrust a test that stubs the boundary with a richer object than production supplies.** Check that every member the call site touches actually exists on what really crosses the wire — not on the convenient double.

---

## 5. Test-Evidence Doctrine

Headless-green is weak evidence for a shell. Say what it does and does not prove.

- **`[Collection("TaskbarTheme")]`** is required on any test class that mutates `Application.Current`'s theme or shared resources. Missing it produces cross-test corruption that looks like a product bug.
- **Render tests must poll the captured frame**, not the window bounds, and not a single capture. Under a full-solution run test assemblies execute in parallel and the first frame can still be pending — `CaptureRenderedFrame()` returns null or a stale composite even though layout has settled. Poll the frame on a time-based deadline. A second known edge: the first capture after a batch of bitmaps lands can be the pre-bitmap composite, so capture twice.
- **A test that passes alone and fails in the suite is a real finding**, not a retry candidate. Either it leaks shared state or it races layout. Diagnose which; never "fix" it by weakening the assertion.
- **A platform-skipped test is a coverage hole with a label.** `SkipOnWindows` on the only test for a behavior means that behavior is untested on Windows. Usually the fix is a seam (see `TimeProvider`), not a skip.
- **Never weaken or re-baseline a render assertion without stating why the new baseline is correct.** If a metric legitimately moved, say which change moved it.
- **Behavioural acceptance criteria need on-device QA**, and the review should say so rather than implying tests covered it. AX, TCC, window activation, Dock, zoom, and tray capture cannot be proven headlessly. Enumerate the manual steps.

---

## 6. Multi-Process and IPC Add-On

Split mode is the only mode. An argument-less launch is `--role=launcher`, supervising `core` + `taskbar` (+ `desktop` when shown, + one `explorer` per window).

- The **core** owns `settings.db` and the Swift helper and serves both to peers over shell-core IPC. A peer opening either directly is P1.
- `RoleProcessSupervisor` owns spawn/restart. SIGTERM handlers must set `ctx.Cancel` or children orphan.
- A change to the IPC surface must keep the managed client and the server in sync, and must be reviewed for **every role** that consumes it — ask which roles are affected, by name.
- Per-session HMAC nonce auth; socket directory at 0700. A new socket without nonce auth is P1 (the automation control socket's file-perm-only auth is a known outstanding gap, not a precedent to copy).
- Icons render off-thread, cached, shared across processes via a memory-mapped BGRA pool. New cache entries need collision-free keys (`path|size` style). Note that per-file *content* (a thumbnail) does not belong in a pool keyed by reusable *type* icons.

---

## 7. Native, Interop, and Platform Add-On

- The Swift helper needs its **own** TCC grant keyed by binary path. Re-sign after packaging or grants churn (ad-hoc cdhash changes force re-granting).
- Public API only. `NSDockTile.badgeLabel` is not readable cross-app; the Dock's own AX tree is (`AXApplicationDockItem` → `AXStatusLabel`). Prefer a documented public path over a private symbol, and document honestly when no path exists rather than inventing a heuristic. **Never infer data the platform does not expose** — a plausible-looking guess is worse than an empty state.
- `NSScreen.visibleFrame` is computed by the system from the menu bar and Dock only. A third-party utility bar cannot shrink it. Any claim to "reserve" space deserves scrutiny; correction-after-the-fact is a mitigation and must be labelled as one, in the code and in the setting's own description.
- Deprecated-but-functional AppKit calls: check the deployment target before "modernizing". Activation strength is not symmetric — `activateIgnoringOtherApps` outranks a bare `activate()`, which turns a race into a guaranteed loss.
- Keep the sync-AE / enumeration paths off the UI thread; a synchronous get over a huge enumeration freezes the shell.

---

## 8. Packaging, Signing, CI

- Re-run `build-app.sh` + `dev-sign.sh` after a build so TCC grants stick.
- Flag anything that links host-specific paths (e.g. `/opt/homebrew`) into a signed build — it breaks portability.
- Benchmarks live outside `Bevel.sln` and must be built explicitly; a perf claim with no benchmark run is unevidenced.
- Secrets, notarization, and release workflow changes are blast-radius findings even when the diff is small.

---

## 9. Phrasing That Lands

State the cost, then the shape. Keep it short.

- *"This adds a second authority for bar height. `TaskbarTheme.HeightForRows` already owns it; read from there so the two cannot disagree."*
- *"This wrapper carries no invariant and every call site still does the same work. Call `<helper>` directly and delete the layer."*
- *"Green CI does not cover this path — the Fake PAL no-ops `X`, so the new branch is untested. Make the fake apply the change and assert through it."*
- *"`!` here asserts non-null but the only writer is a snapshot that can legitimately be empty. Handle the empty case as a normal path."*
- *"This is a mitigation, not a fix, and the setting's description implies otherwise. Say what it actually guarantees."*
- *"Behavior looks right. I am still blocking: this puts feature branching in a shared flow, which is how the last three of these became permanent."*

Do not soften a structural objection into a nit. If the codebase gets harder to reason about, say that.
