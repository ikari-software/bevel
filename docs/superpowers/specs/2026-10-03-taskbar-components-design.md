# Taskbar as configurable, generalized components — design

**Bead:** [bevel-aqr7](../../../.beads/issues.jsonl) · **Date:** 2026-10-03 · **Status:** design approved, awaiting implementation plan

This spec covers **sub-project 1** of five. The later four are named in [Decomposition](#decomposition) but not specified here.

---

## 1. Problem

The taskbar is a fixed layout, and every feature so far has been paid for by editing it. Measured on 2026-10-03:

| Fact | Value |
|---|---|
| `TaskbarView.axaml` | 563 lines |
| `TaskbarView.axaml.cs` | 1256 lines |
| `TaskbarViewModel` | 128 lines exposing **five** named properties |
| Layout | `Grid ColumnDefinitions="Auto,*,Auto"` |
| Taskbar keys on `BevelSettings` | **27 of 46** properties (59% of the object) |
| Hand-written edits per new settings key | **3** (`SettingsService.cs:542`, `:664`, `:932`) |
| …**plus** a hand-coded settings window | `OnboardingWindow.axaml.cs`, 755 lines, **25 of 27 keys** |
| Multi-instance features today | exactly **one** (`TaskbarStacks`) |

The settings tax is therefore paid **twice**: once across three serialization sites, and again in a 755-line hand-written settings window (`OnboardingWindow.axaml.cs` — "M2 onboarding and settings window"). A schema-driven editor removes both at once, which is why sub-project 3 *replaces* most of that window rather than adding to it.

The actual coupling is narrower than the line counts suggest: `TaskbarViewModel` exposes `Model`, `ShowDesktopCommand`, `StartMenu`, `Tray` and `Stacks`, and `TaskbarView.axaml` binds each **by name**. That name-binding is the thing to break — not the file size.

Two forces make this urgent rather than tidy:

- **Shell replacement turns optional extras into obligations.** Under `bevel-yli7` Bevel must itself draw volume, network, input and battery (`bevel-985s.1–.4`), because in the Win10/11 era those stopped being `Shell_NotifyIcon` clients and became Explorer's own shell-drawn UI. All four are structurally identical — glyph, state source, flyout — so building them as four more hardcoded blocks would permanently quadruple the problem this epic exists to remove.
- **`TaskbarStacks` already proves the need and shows the shape of the failure.** It is a `string[]`, so an instance's entire configuration is a bare folder path. There is nowhere to put per-instance settings because the concept does not exist.

### 1.1 What already points the right way

Three existing pieces are the model in embryo, and the design leans on all three rather than inventing alternatives:

- **`TrayItemViewModel`** (`Id`, `Tooltip`, `IconSource`, `IsLive`) arrives from another process and `TrayIconCell` renders it in-bar. That is already a state-only out-of-process component.
- **`MmfBgraPool`** (`src/Bevel.UI/MmfBgraPool.cs:44`) is a working cross-process BGRA transport, used for icons today.
- **`StackViewModel.cs:276`** documents the per-instance runtime-state hazard and its fix: *"One `PreviewLoader` per stack: its cache is trimmed to that folder's live entries, so stacks never evict each other's cell images."*

---

## 2. Scope

**In scope (sub-project 1):** the component contract; a registry of component types; the ordered-list layout host; per-instance persistence and the migration of the 27 keys; bar geometry derived from components (§4.4); failure isolation; the conformance test suite. Bevel's own components go through the public contract from day one.

### 2.1 Precursor: the Downloads stack as the first component, via the third-party route

Before migrating anything load-bearing, **the Downloads stack becomes the first component — and deliberately via the third-party path** (manifest, own process, component bus) rather than the easy local-bound path.

It is the right precursor because it already exercises every hard part of the contract:

| Contract feature | How the stack already proves it |
|---|---|
| Multi-instance | `TaskbarStacks` is a `string[]`; N stacks exist today |
| Per-instance **settings** | currently only a folder path — the gap the model closes |
| Per-instance **runtime state** | `StackViewModel.cs:276` — one `PreviewLoader` per stack so caches do not evict each other |
| `flyout` primitive | `StackFlyoutView` is a real grid flyout with hover and keyboard selection |
| `surface` primitive | content previews are decoded bitmaps — the natural first surface |
| Theming under switch | its tooltip/selection bugs this session are exactly the staleness class §3.3 guards |

Proving the **public** path first, on a component that is genuinely hard, is what stops the contract quietly growing a privileged shortcut that only built-ins can take. If the stack cannot be expressed as a third-party component, the contract is wrong — and we find out before migrating the clock, the strip or the tray.

**Out of scope, deliberately:** imitating vendor surfaces with no Bevel meaning — Copilot, the Widgets board, Meet Now, search highlights. The component *model* should let a third party add such a thing; Bevel shipping imitations of them would be purpose-free.

### Decomposition

| # | Sub-project | Notes |
|---|---|---|
| **1** | **Contract + registry + ordered-list host + persistence** | **This spec.** |
| 2 | External component loading + isolation | Discovery, versioning, the untrusted-process lifecycle |
| 3 | Arrangement UI in Settings | Add / remove / reorder / configure |
| 4 | Zone helpers and wizards | **v2** — deferred by decision (§4.2) |
| 5 | Helper-channel migration | macOS-only; sequenced last (§5.3.2) |

Components themselves are already filed: `bevel-985s.1–.4`, `bevel-aqr7.1–.12`.

---

## 3. The contract

### 3.1 A component is a manifest, not a type

Declaring components by manifest rather than by C# type is what makes the boundary public rather than theoretical, and it is what lets persistence survive refactoring.

| Field | Purpose |
|---|---|
| `id` | Stable reverse-DNS type-id (`run.bevel.clock`). **Never** a .NET type name. |
| `contractVersion` | Integer. The bar refuses a manifest from the future, loudly. |
| `displayName`, `description` | Shown in the arrangement UI. |
| `multiInstance` | Bool. Clock yes; Start button no. |
| `sizing` | `fixed` \| `content` \| `greedy`. |
| `requiresCapability` | PAL capability name; the component is simply **absent** where the PAL reports it unavailable, reusing the existing feature-detect contract. |
| `settingsSchema` | Self-describing typed fields with defaults, labels and ranges. |
| `view` | The primitive tree (§3.3). |

Two fields carry most of the weight:

**`settingsSchema` is what removes the three-edits-per-key tax.** The arrangement UI renders each component's editor *from the schema*, so a new component ships zero hand-written settings UI and zero hand-written serialization.

**`id` is a stable string.** Instances persist against it, so a component can be renamed or refactored freely, and an instance whose id no longer resolves is handled by §6 rather than crashing the bar.

### 3.2 Type vs instance — what "per instance" means

A **type** is the manifest. An **instance** is a *placement* of that type on a bar:

```
{ instanceId, typeId, settings, visible }
```

Three distinct things belong to the instance, not the type:

1. **Identity.** `instanceId` is generated at add-time and stable for the life of the placement. Reordering changes list position and never the id, so moving a component cannot lose its configuration.
2. **Settings *values*.** The *schema* belongs to the type; the *values* belong to the instance. "Show seconds" is a field in the clock type's schema; each clock instance holds its own value — local time with seconds here, UTC without there. Values **nest inside the list entry** rather than living in a side table keyed by id, which makes orphaned settings structurally impossible.
3. **Runtime state.** Two instances of a type must not share mutable runtime state. This is a contract requirement, not an implementation detail; `StackViewModel.cs:276` is the precedent.

Consequences:

- `multiInstance: false` means at most one instance of that `typeId` per bar, **enforced at add-time** rather than assumed.
- **Removing** an instance deletes its settings; **hiding** it (`visible: false`) keeps them. This is why the legacy `Show*` bools map to the visible flag and not to removal (§5.2).
- A bar *is* an ordered list, so per-display (`bevel-tjr2`) means a different list. An instance belongs to exactly one bar.

### 3.3 Primitives, and `surface`

A component's `view` is a tree of Bevel-provided primitives: `glyph`, `label`, `badge`, `separator`, `flyout`, and `surface`.

**`surface` is a primitive, not a parallel rendering mode.** It is a pixel region the component paints itself, backed by `MmfBgraPool`. Making it a primitive rather than a mode means degradation is **local to the region**: a component can be entirely bar-rendered, entirely a surface, or — the useful case — a surface for the one thing that needs arbitrary pixels with ordinary primitives for its label, tooltip and flyout.

> **Why the bar owns rendering at all.** A themable shell cannot let components ship their own pixels wholesale, or *"fidelity = colours + feel + function"* stops holding the moment a user switches theme or re-hues a colourway. A foreign-rendered component is stale on the next switch. This is not hypothetical: `bevel-voqo` (mirror icons go stale), `bevel-yduf` (fails to render icons) and `bevel-sd6n` (items expose no capturable pixels) are the same failure class, already filed against the macOS mirror.

Three rules are therefore **mandatory** on every `surface`, enforced at manifest validation:

| Rule | Why |
|---|---|
| Must declare accessible **name and role** | Otherwise every surface is a hole in the UIA/AX tree that `TaskbarAccessibilityTests` cannot cover. |
| Theme tokens are **pushed** to the component, with a re-paint signal on theme/colourway switch | Without this, surfaces go stale on every switch — `bevel-voqo` exactly. |
| Declares **intrinsic size**, honours the scale it is handed, **never positions itself** | The bar owns layout (§4.3). |

---

## 4. Layout

### 4.1 One ordered list; `spacer` is a component

There are no zones. Components sit in a single ordered list, and a **spacer** is a weighted component like any other. Centring *emerges* from two equal spacers — the flexbox / `NSToolbar` model. Win2000 and Win11 become the same model with different arrangements:

```
Win2000   [ start, window-strip(greedy), tray, clock ]
Win11     [ spacer, start, pinned, window-strip, spacer, tray, clock ]
```

### 4.2 Rejected alternatives

**Three fixed zones (leading / centre / trailing)** — rejected. The centre zone *is* the greedy cell, so its contents anchor to that cell's left edge rather than to the bar's centre. True centring needs slack on **both** sides, which three fixed zones cannot express; `bevel-9ue` would remain a special case permanently.

**Zones for people, spacers for the engine** — deferred to **v2**, not rejected on merit. User-facing helpers and wizards offering leading/centre/trailing buckets are sub-project 4. Keeping v1 to one representation means no zone→list projection to keep in sync or test.

Because v1 has no zones, a newly added component needs a deterministic home. **Rule: append at the end of the list; the user reorders.** No cleverness guessing intent.

### 4.3 Layout rules

- The bar owns layout. A component declares `fixed`, `content` or `greedy` plus an intrinsic size, and never positions itself.
- **At most one `greedy` component per list**, validated at load.
- Dividers stop being chrome. Today `TaskbarView.axaml.cs` tracks the Start button's `Bounds` to position `StartDivider`; as a list entry that code goes away. The tray well becomes a component too.
- `bevel-9ue` (edge position, vertical bars, centred alignment) and `bevel-tjr2` (per-display component sets) collapse into this model instead of needing their own mechanisms.

### 4.4 Bar geometry must be derived from components, not from a global static

This is the sharpest existing-code conflict, found by tracing how settings are *used* rather than how they are named.

`TaskbarTheme` (`TaskbarWindow.cs:495`) is a **`public static class` holding mutable statics** — `ButtonHeight` and `TaskIconSize`, both `{ get; private set; }` — and it is `Configure`d exactly once, from `App.axaml.cs:338`, with `settings.Current.TaskbarButtonSize`. Everything geometric then derives from it:

```
RowHeight      => ButtonHeight + 4
TaskbarHeight  => RowHeight + 2
HeightForRows(rows) => TaskbarHeight + (rows - 1) * RowHeight
```

`TaskbarWindow` calls `HeightForRows` in six places to pin `MinHeight`/`MaxHeight`, and the same value feeds `TaskbarGeometry.WorkAreaBand` — **the work-area strip Bevel reserves from the OS.**

So today: *the bar's height, and the work area it claims, are a function of one component's setting, routed through global mutable state.* Three ways that breaks under per-instance settings:

1. A global static holds exactly one value; per-instance settings are plural by definition.
2. A bar may have **zero** window-strip instances, leaving nothing to `Configure` from.
3. Getting it wrong is user-visible and has history: `bevel-kbx8` (CI softening the Windows work-area height assert against Explorer's AppBar) and the work-area claim landed in `514269e`.

**Required design:** an explicit **measurement pass** replaces the static.

- Each instance reports its height contribution from its own resolved settings.
- The bar composes those into its own height — `max` for a single row, and the row model (`TaskbarRows`) multiplies it.
- That composed height is the single source for `MinHeight`/`MaxHeight` **and** for the work-area claim. One derivation, not two.
- `TaskbarTheme`'s mutable statics become **per-bar computed state**, which multi-monitor (`bevel-tjr2`) requires anyway: two bars with different components cannot share one static.
- Re-measurement runs on any instance settings change, so geometry live-applies. Note `App.axaml.cs:375` already records the opposite failure (`bevel-kclq`: values "staying frozen at whatever `TaskbarRows`/etc. `Initialize` saw") — live-apply is a requirement, not a nicety.

There is a good precedent in-tree for decoupling: `TaskbarWindow.cs:505` notes *"The tray is deliberately NOT driven from here — it keeps its own `TaskbarTrayIconSize` slider."* The tray already escaped the static. This generalizes that decision to every component.

---

## 5. Execution, transport, persistence

### 5.1 Three execution tiers, one contract

| Tier | Where it runs | IPC |
|---|---|---|
| **Built-in** — start, window-strip, tray, stacks, clock, and all four `985s` indicators | taskbar process | none |
| **Third-party with code** | own supervised process | component bus |
| **Third-party declarative-only** — view bound to state the bar already publishes | nowhere; no process | none |

Built-ins go through the **identical** manifest, primitive vocabulary and settings schema, binding a local implementation instead of an IPC one. That dogfooding is what keeps the public door honest: if the contract cannot express Bevel's own clock, it cannot express anyone else's component — and that is discovered immediately rather than after shipping. It also means a clock does not cost a process.

### 5.2 Persistence and migration

New shape, one key where there were roughly twenty:

```
taskbarComponents: [ { instanceId, typeId, settings{}, visible } ]
```

All 27 keys were classified by **tracing their consumers**, not by their names. That changed several answers, so the evidence is recorded alongside each group.

| Destination | n | Keys | Evidence |
|---|---|---|---|
| **Bar-level, but *computed from* components** (§4.4) | 3 | `TaskbarRows`, `TaskbarButtonSize`, `TaskbarTrayIconSize` | `TaskbarButtonSize` → `TaskbarTheme.Configure` → bar height **and the work-area claim**; `TaskbarRows` drives bar height *and* tray rows; `TaskbarTrayIconSize` is also read by `TaskbarWindow.cs` |
| **Bar-level proper** — describe the bar, not anything in it | 5 | `TaskbarLocked`, `TaskbarAlwaysOnTop`, `TaskbarBackgroundColor`, `TaskbarOpacity`, `TaskbarFontSize` | view + settings window only |
| **Data-layer, not view** | 1 | `TaskbarStartMenuFrequentCount` | feeds `shellModel.FrequentCap` at `App.axaml.cs:309/381/382`, re-applied on `settings.Changed` — outside the taskbar's view entirely |
| **Window-strip instance settings** | 8 | `ButtonWidth`, `ButtonWidthMode`, `MinButtonWidth`, `Grouping`, `ButtonLabels`, `MiddleClickCloses`, `ReclickMinimize`, `WindowSort` | behaviour consumers confirm these belong to the strip: `Grouping` → `TaskbarGrouping.cs`, `TaskbarItemsProjector.cs`, `TaskGroupViewModel.cs`, `TaskButtonMenu.cs`; `ReclickMinimize` → `TaskButtonClickPolicy.cs`; `WindowSort` → `TaskbarItemsProjector.cs` |
| **Clock instance** | 2 | `ClockShowSeconds`, `ClockShowDate` | |
| **Start instance** | 2 | `StartLabel`, `StartBadgeFullDetail` | |
| **Tray instance** | 2 | `TrayOverflowCap`, `ConsolidateMenuBar` | `ConsolidateMenuBar` → `TaskbarViewModel.cs` |
| **Visibility** (`visible: false`) | 3 | `ShowClock`, `ShowStart`, `ShowDesktopButton` | |
| **Instance multiplicity** | 1 | `TaskbarStacks` | `string[]` → N instances |

Three corrections that only surfaced from usage, and would have become implementation bugs:

- **`TaskbarButtonSize` is not a plain window-strip setting.** It configures a global static that determines bar height *and* the OS work-area claim, so it is inseparable from §4.4's measurement pass and cannot simply move into an instance bag.
- **`TaskbarStartMenuFrequentCount` is not a view setting at all.** It is consumed by `ShellModel` — the background data model — in the composition root. Making it a Start-instance setting requires the Start menu's *data* pipeline to read per-instance settings, a dependency the by-name reading hid entirely.
- **`StartBadgeFullDetail` is an ordinary per-instance value**, not visibility.

`TaskbarStacks` remains the headline win: a `string[]` becomes N stack instances each with a real settings bag instead of a bare path — and it is the precursor of §2.1.

**Both consumers migrate.** `OnboardingWindow.axaml.cs` hand-codes 25 of these keys; its taskbar sections are replaced by the schema-driven editor rather than updated key by key.

**`Show*` bools map to `visible: false`, not to removal.** Removing the instance would discard the user's configuration when they merely hide a component.

**Migration** is a one-time fold on first read of an old blob: synthesize the default ordered list, then distribute the legacy keys into instance settings. Legacy keys stay **readable for one release** — version skew is a live concern (`bevel-f4g5`, `bevel-hprc`) and a downgrade must not brick someone's bar.

### 5.3 Transport

Both buses run **NetMQ + protobuf**. Two separate buses — internal shell bus and component bus — with separate sockets and separate nonces. `gRPC`-over-UDS and raw UDS both retire onto NetMQ.

Protobuf needs no new tooling: `Grpc.Tools` 2.70.0 and `Google.Protobuf` 3.29.3 are already in `Directory.Packages.props` with codegen wired off `proto/`.

**Frames never go through any queue.** Pixels land in `MmfBgraPool`; the bus carries only *"frame N ready in slot K"*. Transport choice therefore has no effect on surface throughput.

#### 5.3.1 Accepted risk, and the mitigations it requires

This was chosen over reusing the existing AF_UNIX transport, with the following known. Recording it so it is mitigated rather than re-litigated:

- **NetMQ has no `ipc://` on Windows** ([libzmq#3691](https://github.com/zeromq/libzmq/issues/3691), [zmq_ipc(7)](https://api.zeromq.org/4-2:zmq-ipc)). It must use `tcp://127.0.0.1`, which is reachable by every local process and every local user.
- **Bevel already has AF_UNIX on Windows with filesystem-permission isolation.** `UdsMessageClient.cs:78` uses `AddressFamily.Unix`; `BevelRuntimeDir.cs:11` places the socket under the user profile's user-only ACL and documents it as *"the 0700 equivalent — no explicit DACL needed."* So this is a step **down** in isolation on Windows, not sideways.

Because filesystem permissions no longer protect the channel, the HMAC becomes the **only** barrier rather than the second one. Required:

| Mitigation | Detail |
|---|---|
| HMAC mandatory on connect | `Handshake.ComputeHmac` carries over unchanged — it is a pure function of nonce + capability and transport-agnostic by design. Immediate rejection; **no state disclosed to an unauthenticated peer.** |
| Separate nonce per bus | A component that discovers the internal port still cannot reach the internal bus. |
| Ephemeral random port | Port + nonce written to a per-session file with a user-only ACL — the replacement for socket-directory permissions. |
| Connection-flood limiting | Any local process can now attempt connects. |
| ZMQ CURVE on the component bus | Under consideration for the untrusted bus specifically. |
| **Verify NetMQ against `PublishAot` early** | `BevelPublishAot=true` targets a single Mach-O with no JIT, and NetMQ becomes load-bearing. De-risk in this sub-project; do not discover late. |

#### 5.3.2 Helper-channel sequencing

The Swift helper (`native/helper-macos/`, gRPC-swift) migrates **last**, as sub-project 5. Consolidating it means `libzmq` as a native Swift dependency to ship, sign and notarize — the `bevel-ka6c` class of pain. It is macOS-only while the component model is Windows-driven, so it must not gate this work.

---

## 6. Failure and isolation

The governing constraint: `ShellHealthMonitor.cs:158` holds at 3 respawns and then **deliberately stops bringing a role back**. In Windows shell mode that means no shell at all. **A component must never be able to trip it** — components get their own health budget, separate from `ShellRole`.

| Requirement | Rationale |
|---|---|
| **Quarantine beats infinite respawn** | A crash-looping component's slot becomes an inert placeholder with a "component failed" affordance, not retried until the user re-enables it or the bar restarts. |
| **Hangs count as failures** | A component that stops answering is not crashed. A bus watchdog with a deadline is required, or the slot silently freezes. |
| **Last frame must not persist** | A component dying mid-frame marks its surface inert rather than showing a stale image indefinitely. |
| **Validation rejects the component, never the bar** | More than one `greedy`, unknown primitive, missing a11y on a surface, `contractVersion` from the future. |
| **Zero resolvable components still renders a bar** | In Windows shell mode an empty bar is still a shell; a crashed bar is not. |
| **Unknown `typeId` keeps its slot** | An inert placeholder, so uninstalling and reinstalling a component does not silently reshuffle the bar. |

---

## 7. Testing

Conventions follow the repo: xUnit + Avalonia.Headless with `UseSkia()` and `UseHeadlessDrawing=false` for real pixels; theme/resource-mutating classes share `[Collection("TaskbarTheme")]`.

- **Conformance suite run twice** — once local-bound, once IPC-bound — so built-ins and third-party components pass *identical* assertions. This is what makes the dogfooding claim verifiable rather than rhetorical.
- **Golden layout fixtures** with the Win2000 and Win11 arrangements of §4.1 as named cases, plus one-greedy enforcement and spacer weighting.
- **Failure injection**: throws-on-start, hangs, crash-loops, future `contractVersion`, two `greedy` components. Each asserts bar survival and an inert slot.
- **Theme-switch guard**: push a theme change, assert surfaces receive the token push and repaint. This is the explicit `bevel-voqo` regression test.
- **Accessibility**: extend `TaskbarAccessibilityTests` so every instance, surfaces included, exposes name and role.
- **Migration**: an old 27-key blob produces the expected list; round-trips; preserves an unknown `typeId`.
- **Perf baseline** (`bevel-zhmr`) on the component channel **before the contract freezes**, so "blazingly fast and light" has a number to hold it to.

---

## 8. Risks

| Risk | Handling |
|---|---|
| **NetMQ may not be `PublishAot`-safe** | Verify first, before it is load-bearing (§5.3.1). If it fails, that is a transport decision to revisit — not something to discover during migration. |
| Loopback TCP on Windows widens the local attack surface | Mitigations in §5.3.1; HMAC is load-bearing and must be treated as such in review. |
| Migrating 27 keys touches live user settings | One-time fold, legacy keys readable for one release, round-trip tests. |
| Primitive vocabulary may prove too narrow | `surface` is the escape hatch, scoped to a region. If a component needs more than a surface, that is contract feedback worth a bead. |
| Retiring two transports at once is a large migration | Buses first, helper last (§5.3.2). The component model does not wait on the helper. |

---

## 9. Acceptance

The bar is composed from a component registry with an ordered list, multiple instances, per-instance settings and visibility. Adding a taskbar feature means adding a component, not editing the bar layout. Unknown persisted components are ignored without breaking the bar. Bevel's own components go through the same contract as third-party ones, verified by the same conformance suite.
