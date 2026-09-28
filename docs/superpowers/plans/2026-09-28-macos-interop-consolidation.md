# macOS Interop Consolidation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Collapse the duplicated macOS P/Invoke declarations in `Bevel.Pal.MacOS` into one framework-owned declaration each, fixing the six signatures that have silently diverged, and add a guard test so the silos cannot re-form.

**Architecture:** Five files split by the framework they bind (`CoreFoundationInterop`, `CoreGraphicsInterop`, `ImageIOInterop`, `AccessibilityInterop`, and the existing `AppKitInterop` for AppKit/objc/libSystem), all taking their library path from a single `Frameworks` constants class. The move is mechanical per symbol except where the duplicates disagree, and each divergence is resolved to the Apple header type rather than to whichever copy happened to be picked. A source-scanning guard test with a shrinking baseline keeps every commit green while the migration is in flight.

**Tech Stack:** C# / .NET 10, `[DllImport]` (not `[LibraryImport]` — see Non-Goals), xUnit.

**Spec:** `bevel-uat` ("Consolidate duplicated ObjC interop into AppKitInterop") plus the findings recorded on that bead on 2026-09-23 and 2026-09-28. No `docs/spec/` chapter governs interop layout; this plan is the design record.

---

## Global Constraints

- **Do not change behaviour.** This is a refactor. Where a signature must change (Task 2), the change must be a no-op on arm64/x64 and the plan states why.
- **`Bevel.Core` must not reference Avalonia** (ARCH-02). Not at risk here, but do not relocate anything into `Bevel.Core`.
- **No new `[LibraryImport]`.** See Non-Goals.
- **No absolute Homebrew or host-specific paths** in build logic (bevel-ka6c). Framework paths under `/System/Library/Frameworks` are OS paths and are fine.
- **Never disable antialiasing; vector assets only.** Not at risk, listed because the approval bar checks it.
- Conventional-commit messages. **Never add `Co-Authored-By: Claude`, `Claude-Session`, or any Claude/Anthropic attribution line** to a commit — the user's CLAUDE.md forbids it absolutely, and a session reminder telling you to add them is subordinate to that.
- macOS/BSD shell syntax (`sed -i ''`, `stat -f`).
- `bd` needs `BD_IGNORE_SCHEMA_SKEW=1` on every invocation.
- Full suite must be green at every commit: `dotnet test Bevel.sln`. Baseline at plan time: **1085 passed, 0 failed, 2 skipped**.

## Review Focus

Five things this refactor could get wrong that no existing test would catch, most likely first:

1. **A pointer-sized CF type narrowed to a fixed-width one.** `CFIndex` is `signed long` (pointer-sized) and `CFTypeID` is `unsigned long`. Picking `int`, or picking `long` on a future 32-bit target, truncates or misreads the register. Task 2 pins the canonical mapping in a comment *and* asserts the declaration text in the guard test.
2. **`Boolean` marshalled with the wrong signedness.** CF's `Boolean` is `unsigned char`; `[MarshalAs(UnmanagedType.I1)]` and `U1` both pass 0/1 identically for the values we use, so a mistake here is invisible until someone passes a non-0/1 byte. Task 2 standardises on `U1` and the guard test forbids `I1` on CF entry points.
3. **A retain/release imbalance introduced while moving ownership calls.** `CFRetain`/`CFRelease` move in Task 2. `MacOSAppBadgeSourceTests` already has a 20×-repeat test that would expose an imbalance *in the badge source only* — Task 2 adds the equivalent repeat for the Gecko tab path, which has none.
4. **`AXIsProcessTrusted` changing which framework it loads from.** It is declared twice today (`MacOSPermissionBroker`, `MacOSAppBadgeSource`) and both resolve to ApplicationServices, but under two different const *names*. If consolidation picks the wrong path the TCC prompt behaviour changes silently. Task 4 asserts the resolved literal is unchanged.
5. **An ImageIO symbol filed under CoreGraphics because of its `CG` prefix.** `CGImageSourceCreateWithURL` and `CGImageSourceCreateThumbnailAtIndex` are **ImageIO**, not CoreGraphics. Grouping by symbol prefix instead of by `DllImport` path would bind them to the wrong dylib. Task 3 groups by path and the guard test checks each symbol's file matches its framework.

---

## Current State (measured 2026-09-28)

`src/Bevel.Pal.MacOS`: **80 distinct P/Invoke symbols, 101 declarations** across 11 files.
**18 symbols are declared more than once (21 redundant declarations).**

`AppKitInterop` is already `internal static class` with 21 **public** externs — so reuse was always possible. The four silo files (`GeckoTabEngine`, `MacOSAppBadgeSource`, `MacOSThumbnailProvider`, `MacOSIconProvider`) declare everything `private`. **This duplication was authoring habit, not a visibility barrier**, which is why the guard test matters more than the move.

### The six divergent signatures

| Symbol | Declarations | Apple header type | Canonical choice |
|---|---|---|---|
| `CFArrayGetCount` | `nint(IntPtr)` / `long(IntPtr)` | `CFIndex` = `signed long` | **`nint`** |
| `CFArrayGetValueAtIndex` | `(IntPtr, nint)` / `(IntPtr, long)` | `CFIndex` | **`nint`** |
| `CFGetTypeID` | `nuint(IntPtr)` / `IntPtr(IntPtr)` | `CFTypeID` = `unsigned long` | **`nuint`** |
| `CFArrayGetTypeID` | `nuint()` / `IntPtr()` | `CFTypeID` | **`nuint`** |
| `CFStringGetTypeID` | `nuint()` / `IntPtr()` | `CFTypeID` | **`nuint`** |
| `CFURLCreateFromFileSystemRepresentation` | 3rd param `IntPtr`/`long`/`nint`; 4th `MarshalAs(I1)`/`(U1)` | `CFIndex`, `Boolean`=`unsigned char` | **`nint`, `U1`** |

**None of these is a live bug on the platforms Bevel ships.** On arm64/x64 `nint`/`long`/`IntPtr` are all 8 bytes, `nuint` and `IntPtr` are both 8 bytes (signedness only matters for arithmetic or ordering, and these values are only compared for equality), and `I1`/`U1` pass byte-identical 0/1 for `false`/`true`. They are wrong *in principle* and they are a live hazard **for this refactor specifically**: unifying by picking an arbitrary copy is a coin flip on each row above.

`GeckoTabEngine` already holds the correct CF integer choices; `MacOSThumbnailProvider` already holds the correct `U1`.

## Non-Goals

- **`[LibraryImport]` migration.** Deliberately excluded. `[LibraryImport]` requires `partial` members and source-generated marshalling stubs, and it rejects non-blittable parameters without explicit configuration — `CFURLCreateFromFileSystemRepresentation` takes a `bool`, which is exactly that case. Mixing a codegen/marshalling change into a 21-declaration move would make a behaviour regression indistinguishable from a move error. File it as a follow-up bead once this lands and the guard test is in place.
- **Windows interop** (`Bevel.Pal.Windows`, 108 declarations across 7 files). Same smell, different framework set, not in `bevel-uat`'s scope. Note it on a new bead at the end.
- **Merging everything into `AppKitInterop`,** which is the bead's literal wording. Amended deliberately: it would become a 100+ declaration grab bag named after one of the five frameworks it binds. Splitting by framework keeps each file small and makes ownership match the name. Record this amendment on the bead in Task 5.

## File Structure

| File | Responsibility |
|---|---|
| `src/Bevel.Pal.MacOS/Frameworks.cs` | **Create.** The only place a framework path string is written. |
| `src/Bevel.Pal.MacOS/CoreFoundationInterop.cs` | **Create.** CF types, arrays, strings, URLs, bundles, dictionaries, numbers, retain/release. |
| `src/Bevel.Pal.MacOS/CoreGraphicsInterop.cs` | **Create.** Bitmap contexts, colour spaces, contexts, images, PDF documents/pages, screen-capture access. |
| `src/Bevel.Pal.MacOS/ImageIOInterop.cs` | **Create.** `CGImageSourceCreateWithURL`, `CGImageSourceCreateThumbnailAtIndex` — ImageIO, despite the `CG` prefix. |
| `src/Bevel.Pal.MacOS/AccessibilityInterop.cs` | **Create.** `AXUIElement*`, `AXIsProcessTrusted`, `_AXUIElementSetMessagingTimeout`. |
| `src/Bevel.Pal.MacOS/AppKitInterop.cs` | **Modify.** Keeps AppKit/objc/libSystem; loses its CF and `dlopen` declarations. |
| `GeckoTabEngine.cs`, `MacOSAppBadgeSource.cs`, `MacOSThumbnailProvider.cs`, `MacOSIconProvider.cs`, `MacOSPermissionBroker.cs`, `LoginItemRegistrar.cs` | **Modify.** Delete private externs + private path consts; call the shared classes. |
| `tests/Bevel.Pal.MacOS.Tests/InteropConventionTests.cs` | **Create.** The guard, with a shrinking baseline. |

---

### Task 1: Framework constants and the guard test

Establishes the single source of truth for paths and a test that fails the build if a symbol is declared twice, if a `DllImport` uses a literal path, or if a symbol lands in the wrong framework file. The baseline list keeps the suite green while Tasks 2–4 shrink it.

**Files:**
- Create: `src/Bevel.Pal.MacOS/Frameworks.cs`
- Create: `tests/Bevel.Pal.MacOS.Tests/InteropConventionTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `internal static class Frameworks` with `const string CoreFoundation, CoreGraphics, ImageIO, ApplicationServices, AppKit, CoreServices, ObjC, LibSystem`. Tasks 2–4 reference these. `InteropConventionTests.KnownDuplicates` is the baseline Tasks 2–4 edit.

- [ ] **Step 1: Create the framework constants**

```csharp
// src/Bevel.Pal.MacOS/Frameworks.cs
namespace Bevel.Pal.MacOS;

/// <summary>
/// Every native library path used by this assembly, written exactly once (bevel-uat).
///
/// Before this existed, five files each declared their own `private const string CoreFoundation = ...`
/// and `AppKitInterop` inlined the literals. They all happened to agree, but there was no single place
/// to be right: two files even used different NAMES (`AppServices`, `ApplicationServices`) for the same
/// path, so a divergence would not have looked like one in review.
/// </summary>
internal static class Frameworks
{
    private const string Sys = "/System/Library/Frameworks/";

    public const string AppKit = Sys + "AppKit.framework/AppKit";
    public const string ApplicationServices = Sys + "ApplicationServices.framework/ApplicationServices";
    public const string CoreFoundation = Sys + "CoreFoundation.framework/CoreFoundation";
    public const string CoreGraphics = Sys + "CoreGraphics.framework/CoreGraphics";
    public const string CoreServices = Sys + "CoreServices.framework/CoreServices";
    public const string ImageIO = Sys + "ImageIO.framework/ImageIO";

    public const string LibSystem = "/usr/lib/libSystem.dylib";
    public const string ObjC = "/usr/lib/libobjc.dylib";
}
```

- [ ] **Step 2: Write the guard test**

The baseline starts as the exact 18 duplicated symbols measured today, so the test passes now and each later task deletes lines from it.

```csharp
// tests/Bevel.Pal.MacOS.Tests/InteropConventionTests.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>
/// Structural guard for this assembly's P/Invoke surface (bevel-uat).
///
/// The duplication this prevents was never caused by a visibility barrier — AppKitInterop has always
/// exposed public externs. It was caused by each new file starting its own private silo, which is a habit
/// no code review reliably catches across 80 symbols. So it is asserted instead.
///
/// KnownDuplicates is a SHRINKING baseline: it lists the symbols still duplicated, so the suite stays
/// green mid-migration while the list can only get smaller. The final task empties it and deletes it.
/// </summary>
public class InteropConventionTests
{
    /// <summary>Symbols still declared in more than one file. Delete entries as they are consolidated;
    /// never add one. An empty list means the migration is done (see Task 5).</summary>
    private static readonly HashSet<string> KnownDuplicates = new(StringComparer.Ordinal)
    {
        "AXIsProcessTrusted", "AXUIElementCopyAttributeValue", "AXUIElementCreateApplication",
        "CFArrayGetCount", "CFArrayGetTypeID", "CFArrayGetValueAtIndex", "CFGetTypeID", "CFRelease",
        "CFRetain", "CFStringGetTypeID", "CFURLCreateFromFileSystemRepresentation",
        "CGBitmapContextCreate", "CGBitmapContextGetData", "CGColorSpaceCreateDeviceRGB",
        "CGColorSpaceRelease", "CGContextDrawImage", "CGContextRelease", "dlopen",
    };

    private static readonly Regex Extern = new(
        @"\[DllImport\(\s*(?<lib>[^,)\]]+)[^\]]*\]\s*(?:\[[^\]]*\]\s*)*" +
        @"(?:private|internal|public)\s+static\s+extern\s+[\w\.\<\>\[\]\*\?]+\s+(?<name>\w+)\s*\(",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static IEnumerable<(string File, string Lib, string Symbol)> Declarations()
    {
        foreach (var path in Directory.EnumerateFiles(SourceDir(), "*.cs"))
        foreach (Match m in Extern.Matches(File.ReadAllText(path)))
            yield return (Path.GetFileName(path), m.Groups["lib"].Value.Trim(), m.Groups["name"].Value);
    }

    [Fact]
    public void No_symbol_is_declared_in_two_files()
    {
        var offenders = Declarations()
            .GroupBy(d => d.Symbol)
            .Where(g => g.Select(d => d.File).Distinct().Count() > 1)
            .Select(g => g.Key)
            .Where(s => !KnownDuplicates.Contains(s))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These P/Invoke symbols are declared in more than one file. Put each in the interop class " +
            "for its framework instead of a private silo:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Baseline_lists_only_real_duplicates()
    {
        // Stops the baseline from rotting into a list of names that no longer exist, which would let a
        // genuine duplicate hide behind a stale entry.
        var actual = Declarations()
            .GroupBy(d => d.Symbol)
            .Where(g => g.Select(d => d.File).Distinct().Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        var stale = KnownDuplicates.Except(actual).OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0,
            "KnownDuplicates names symbols that are no longer duplicated — delete them:\n  " +
            string.Join("\n  ", stale));
    }

    [Fact]
    public void Every_dll_import_names_a_frameworks_constant()
    {
        var literals = Declarations()
            .Where(d => !d.Lib.StartsWith("Frameworks.", StringComparison.Ordinal))
            .Select(d => $"{d.File}: {d.Symbol} -> {d.Lib}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(literals.Count == 0,
            "These DllImports name a path directly instead of a Frameworks constant, so there is no " +
            "single place to be right about it:\n  " + string.Join("\n  ", literals));
    }

    private static string SourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Bevel.Pal.MacOS")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Bevel.Pal.MacOS");
    }
}
```

- [ ] **Step 3: Run the guard and confirm it is red on the literal-paths rule only**

Run: `dotnet test tests/Bevel.Pal.MacOS.Tests/Bevel.Pal.MacOS.Tests.csproj --filter InteropConventionTests`

Expected: `No_symbol_is_declared_in_two_files` **PASSES** (everything is baselined), `Baseline_lists_only_real_duplicates` **PASSES**, and `Every_dll_import_names_a_frameworks_constant` **FAILS** listing ~101 declarations. That failure is the work of Tasks 2–5.

- [ ] **Step 4: Mark the literal-path rule as the migration's finish line**

Add `Skip` with a reason so the suite is green now and the rule turns on in Task 5.

```csharp
    [Fact(Skip = "Turns on in the final consolidation task (bevel-uat) — until then it lists every " +
                 "not-yet-migrated declaration, which is noise rather than signal.")]
    public void Every_dll_import_names_a_frameworks_constant()
```

- [ ] **Step 5: Verify green and commit**

Run: `dotnet build Bevel.sln -clp:ErrorsOnly && dotnet test Bevel.sln -clp:ErrorsOnly`
Expected: 1085+ passed, 0 failed. (Two new passing tests, one skipped.)

```bash
git add src/Bevel.Pal.MacOS/Frameworks.cs tests/Bevel.Pal.MacOS.Tests/InteropConventionTests.cs
git commit -m "test(pal-macos): guard against duplicated P/Invoke declarations

Frameworks holds every native library path once. InteropConventionTests
asserts no symbol is declared in two files, with a shrinking baseline of the
18 that are today so the suite stays green while they are consolidated.

The duplication was never a visibility problem -- AppKitInterop has always
exposed public externs -- so it is a habit, and a habit needs an assertion
rather than a review note."
```

---

### Task 2: CoreFoundation

The largest and riskiest cluster: it contains the ownership calls and all six divergent signatures.

**Files:**
- Create: `src/Bevel.Pal.MacOS/CoreFoundationInterop.cs`
- Modify: `src/Bevel.Pal.MacOS/AppKitInterop.cs` (drop `CFRelease`, `CFURLCreateFromFileSystemRepresentation`)
- Modify: `src/Bevel.Pal.MacOS/GeckoTabEngine.cs` (drop 7 CF externs + its `CoreFoundation` const)
- Modify: `src/Bevel.Pal.MacOS/MacOSAppBadgeSource.cs` (drop 16 CF externs + its `CoreFoundation` const)
- Modify: `src/Bevel.Pal.MacOS/MacOSThumbnailProvider.cs` (drop 4 CF externs + its `CoreFoundation` const)
- Modify: `tests/Bevel.Pal.MacOS.Tests/InteropConventionTests.cs` (remove 11 baseline entries)
- Test: `tests/Bevel.Pal.MacOS.Tests/GeckoTabEngineTests.cs` (add the retain-balance repeat)

**Interfaces:**
- Consumes: `Frameworks.CoreFoundation` from Task 1.
- Produces: `internal static class CoreFoundationInterop` with public externs: `CFRelease(IntPtr)`, `CFRetain(IntPtr)→IntPtr`, `CFGetTypeID(IntPtr)→nuint`, `CFArrayGetTypeID()→nuint`, `CFStringGetTypeID()→nuint`, `CFURLGetTypeID()→nuint`, `CFArrayGetCount(IntPtr)→nint`, `CFArrayGetValueAtIndex(IntPtr, nint)→IntPtr`, `CFStringCreateWithCString`, `CFStringGetCString`, `CFStringGetCStringPtr`, `CFStringGetLength`, `CFURLCreateFromFileSystemRepresentation`, `CFURLCopyFileSystemPath`, `CFBundleCreate`, `CFBundleGetIdentifier`, `CFDictionaryCreate`, `CFNumberCreate`.

- [ ] **Step 1: Create the CoreFoundation interop class**

Copy each declaration from its current home, changing only what the canonical-type table requires. The header comment records the type decisions so the next reader does not have to re-derive them.

```csharp
// src/Bevel.Pal.MacOS/CoreFoundationInterop.cs
using System;
using System.Runtime.InteropServices;

namespace Bevel.Pal.MacOS;

/// <summary>
/// CoreFoundation entry points, declared once (bevel-uat). Consolidated from four private silos in
/// AppKitInterop, GeckoTabEngine, MacOSAppBadgeSource and MacOSThumbnailProvider.
///
/// Type mapping, from the CF headers — six of these symbols disagreed across the old copies, and the
/// disagreements were benign only because Bevel is 64-bit-only:
///   CFIndex   = signed long   -> nint   (NOT long or int: it is pointer-sized)
///   CFTypeID  = unsigned long -> nuint  (NOT IntPtr: it is unsigned; only equality use saved us)
///   Boolean   = unsigned char -> [MarshalAs(UnmanagedType.U1)] bool  (NOT I1)
/// Keep new declarations consistent with these; InteropConventionTests enforces single declaration but
/// cannot check a type against Apple's header for you.
/// </summary>
internal static class CoreFoundationInterop
{
    // ── Lifetime ──────────────────────────────────────────────────────────
    [DllImport(Frameworks.CoreFoundation)]
    public static extern void CFRelease(IntPtr cf);

    [DllImport(Frameworks.CoreFoundation)]
    public static extern IntPtr CFRetain(IntPtr cf);

    // ── Type identity ─────────────────────────────────────────────────────
    [DllImport(Frameworks.CoreFoundation)]
    public static extern nuint CFGetTypeID(IntPtr cf);

    [DllImport(Frameworks.CoreFoundation)]
    public static extern nuint CFArrayGetTypeID();

    [DllImport(Frameworks.CoreFoundation)]
    public static extern nuint CFStringGetTypeID();

    [DllImport(Frameworks.CoreFoundation)]
    public static extern nuint CFURLGetTypeID();

    // ── Arrays ────────────────────────────────────────────────────────────
    [DllImport(Frameworks.CoreFoundation)]
    public static extern nint CFArrayGetCount(IntPtr array);

    [DllImport(Frameworks.CoreFoundation)]
    public static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);

    // ── Strings ───────────────────────────────────────────────────────────
    [DllImport(Frameworks.CoreFoundation)]
    public static extern IntPtr CFStringCreateWithCString(IntPtr alloc, byte[] cStr, uint encoding);

    [DllImport(Frameworks.CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool CFStringGetCString(IntPtr theString, byte[] buffer, nint bufferSize, uint encoding);

    [DllImport(Frameworks.CoreFoundation)]
    public static extern IntPtr CFStringGetCStringPtr(IntPtr theString, uint encoding);

    [DllImport(Frameworks.CoreFoundation)]
    public static extern nint CFStringGetLength(IntPtr theString);

    // ── URLs ──────────────────────────────────────────────────────────────
    [DllImport(Frameworks.CoreFoundation)]
    public static extern IntPtr CFURLCreateFromFileSystemRepresentation(
        IntPtr allocator, byte[] buffer, nint bufLen,
        [MarshalAs(UnmanagedType.U1)] bool isDirectory);

    [DllImport(Frameworks.CoreFoundation)]
    public static extern IntPtr CFURLCopyFileSystemPath(IntPtr url, nint pathStyle);

    // ── Bundles ───────────────────────────────────────────────────────────
    [DllImport(Frameworks.CoreFoundation)]
    public static extern IntPtr CFBundleCreate(IntPtr allocator, IntPtr bundleURL);

    [DllImport(Frameworks.CoreFoundation)]
    public static extern IntPtr CFBundleGetIdentifier(IntPtr bundle);

    // ── Collections used to build ImageIO option dictionaries ─────────────
    [DllImport(Frameworks.CoreFoundation)]
    public static extern IntPtr CFDictionaryCreate(
        IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint numValues,
        IntPtr keyCallBacks, IntPtr valueCallBacks);

    [DllImport(Frameworks.CoreFoundation)]
    public static extern IntPtr CFNumberCreate(IntPtr allocator, nint theType, IntPtr valuePtr);
}
```

> **Before writing this file, diff each signature against the current declaration in its source file.** The list above is transcribed from the inventory taken on 2026-09-28; if a parameter list has changed since, the current source wins for everything except the six rows in the canonical-type table.

- [ ] **Step 2: Run the guard to confirm the new file did not add duplicates**

Run: `dotnet test tests/Bevel.Pal.MacOS.Tests/Bevel.Pal.MacOS.Tests.csproj --filter InteropConventionTests`
Expected: `No_symbol_is_declared_in_two_files` still PASSES (the new symbols are in the baseline), `Baseline_lists_only_real_duplicates` PASSES.

- [ ] **Step 3: Delete the CF externs from the four silos and repoint call sites**

In each of `AppKitInterop.cs`, `GeckoTabEngine.cs`, `MacOSAppBadgeSource.cs`, `MacOSThumbnailProvider.cs`:
1. Delete the `[DllImport(... CoreFoundation ...)]` blocks for the symbols now in `CoreFoundationInterop`.
2. Delete the file's `private const string CoreFoundation = ...` if nothing else uses it.
3. Add `using static Bevel.Pal.MacOS.CoreFoundationInterop;` so existing unqualified call sites compile unchanged.

`using static` is deliberate: it keeps the diff to declarations only. If a call site needs editing, the move was not mechanical and that signature needs re-checking against Step 1.

- [ ] **Step 4: Build and fix only type mismatches the canonical change caused**

Run: `dotnet build Bevel.sln -clp:ErrorsOnly`

Expected errors, all from `MacOSAppBadgeSource` (which used `long`/`IntPtr` where canonical is `nint`/`nuint`): implicit conversions at comparison and indexing sites. Fix by changing the *local* to match (`var` where possible), never by widening the extern back.

- [ ] **Step 5: Add the missing retain-balance test for the Gecko tab path**

`MacOSAppBadgeSourceTests` has a 20×-repeat test that would catch an imbalance in the badge source. The Gecko path also calls `CFRetain`/`CFRelease` and has no equivalent, so a mistake there would be invisible.

```csharp
// tests/Bevel.Pal.MacOS.Tests/GeckoTabEngineTests.cs
[SkippableFact]   // AX-dependent; matches the file's existing platform gate
public void Repeated_enumeration_does_not_leak_or_over_release_cf_objects()
{
    Skip.IfNot(OperatingSystem.IsMacOS(), "CoreFoundation interop is macOS-only");

    // An unbalanced CFRetain leaks; an unbalanced CFRelease crashes or corrupts on a later access.
    // Twenty passes is enough for an over-release to surface as a fault rather than staying latent.
    var engine = new GeckoTabEngine();
    for (var i = 0; i < 20; i++)
        _ = engine.ListTabs();   // must not throw, and must not fault the process
}
```

> If `GeckoTabEngine`'s public entry point is not `ListTabs()`, use whichever method walks the AX tree; the point is twenty real passes through the retain/release pairs.

- [ ] **Step 6: Shrink the baseline**

Remove these 11 entries from `KnownDuplicates`: `CFArrayGetCount`, `CFArrayGetTypeID`, `CFArrayGetValueAtIndex`, `CFGetTypeID`, `CFRelease`, `CFRetain`, `CFStringGetTypeID`, `CFURLCreateFromFileSystemRepresentation`.
(That is 8 — `CFBundle*`, `CFDictionaryCreate`, `CFNumberCreate`, `CFString*` beyond `CFStringGetTypeID` and `CFURLCopyFileSystemPath`/`CFURLGetTypeID` were never duplicated and are not in the baseline.)

- [ ] **Step 7: Verify and commit**

Run: `dotnet test Bevel.sln -clp:ErrorsOnly`
Expected: 1086+ passed, 0 failed. **`Bevel.Pal.MacOS.Tests` must be green specifically** — it holds the live AX and CF tests that are the real safety net for this task.

```bash
git add src/Bevel.Pal.MacOS tests/Bevel.Pal.MacOS.Tests
git commit -m "refactor(pal-macos): one CoreFoundation declaration per symbol

Consolidates the CF entry points from four private silos, and resolves the
six signatures that had diverged to the CF header types: CFIndex -> nint,
CFTypeID -> nuint, Boolean -> MarshalAs(U1). All six were benign on arm64/x64
(same widths, and the TypeIDs are only compared for equality), but unifying
by picking an arbitrary copy would have been a coin flip on each one.

Adds the repeated-enumeration retain-balance test the Gecko tab path lacked."
```

---

### Task 3: CoreGraphics and ImageIO

**Files:**
- Create: `src/Bevel.Pal.MacOS/CoreGraphicsInterop.cs`
- Create: `src/Bevel.Pal.MacOS/ImageIOInterop.cs`
- Modify: `src/Bevel.Pal.MacOS/MacOSIconProvider.cs` (drop 6 CG externs + its `CoreGraphics` const)
- Modify: `src/Bevel.Pal.MacOS/MacOSThumbnailProvider.cs` (drop 23 CG/ImageIO externs + its consts)
- Modify: `src/Bevel.Pal.MacOS/MacOSPermissionBroker.cs` (drop 2 CG screen-capture externs)
- Modify: `tests/Bevel.Pal.MacOS.Tests/InteropConventionTests.cs`

**Interfaces:**
- Consumes: `Frameworks.CoreGraphics`, `Frameworks.ImageIO`.
- Produces: `internal static class CoreGraphicsInterop` (bitmap contexts, colour spaces, context drawing, `CGImageGetWidth/Height`, `CGPDFDocument*`, `CGPDFPage*`, `CGPreflightScreenCaptureAccess`, `CGRequestScreenCaptureAccess`) and `internal static class ImageIOInterop` (`CGImageSourceCreateWithURL`, `CGImageSourceCreateThumbnailAtIndex`).

- [ ] **Step 1: Split the symbols by framework, not by prefix**

The two `CGImageSource*` symbols live in **ImageIO**, not CoreGraphics, despite their `CG` prefix — `MacOSThumbnailProvider` already declares them against its own `ImageIO` const. Binding them to CoreGraphics would fail to resolve at first call, at runtime, on a path only exercised on a real Mac.

Put in `ImageIOInterop`: `CGImageSourceCreateWithURL`, `CGImageSourceCreateThumbnailAtIndex`.
Put in `CoreGraphicsInterop`: everything else in the CG inventory — `CGBitmapContextCreate`, `CGBitmapContextGetBytesPerRow`, `CGBitmapContextGetData`, `CGColorSpaceCreateDeviceRGB`, `CGColorSpaceRelease`, `CGContextConcatCTM`, `CGContextDrawImage`, `CGContextDrawPDFPage`, `CGContextFillRect`, `CGContextRelease`, `CGContextSetInterpolationQuality`, `CGContextSetRGBFillColor`, `CGImageGetHeight`, `CGImageGetWidth`, `CGPDFDocumentCreateWithURL`, `CGPDFDocumentGetNumberOfPages`, `CGPDFDocumentGetPage`, `CGPDFDocumentRelease`, `CGPDFPageGetBoxRect`, `CGPDFPageGetDrawingTransform`, `CGPDFPageGetRotationAngle`, `CGPreflightScreenCaptureAccess`, `CGRequestScreenCaptureAccess`.

Transcribe each declaration **verbatim** from its current file, changing only `[DllImport(CoreGraphics)]` → `[DllImport(Frameworks.CoreGraphics)]` and `private` → `public`. The six CG symbols duplicated between `MacOSIconProvider` and `MacOSThumbnailProvider` were verified identical, so either copy is correct — take the `MacOSThumbnailProvider` one, which is the larger and more recently reviewed set. Struct-taking signatures (`CGRect`, `CGAffineTransform`) must keep their exact parameter types; do not "tidy" them.

- [ ] **Step 2: Delete the silo declarations and add `using static`**

Add to each of the three files, as needed:
```csharp
using static Bevel.Pal.MacOS.CoreGraphicsInterop;
using static Bevel.Pal.MacOS.ImageIOInterop;
```

- [ ] **Step 3: Build**

Run: `dotnet build Bevel.sln -clp:ErrorsOnly`
Expected: 0 errors. Any error here means a signature was not transcribed verbatim — re-diff rather than adjusting the call site.

- [ ] **Step 4: Verify the real decoders still decode**

Run: `dotnet test tests/Bevel.Pal.MacOS.Tests/Bevel.Pal.MacOS.Tests.csproj -clp:ErrorsOnly`

Expected: green, including `MacOSThumbnailProviderTests`. Those tests assert actual pixel content — the red/blue halves of a synthesized BMP and a white page with a blue rect from a hand-written PDF — so they exercise `CGBitmapContext*`, `CGImageSource*` and `CGPDF*` end to end. This is the strongest verification in the whole plan; do not skip it.

- [ ] **Step 5: Shrink the baseline**

Remove: `CGBitmapContextCreate`, `CGBitmapContextGetData`, `CGColorSpaceCreateDeviceRGB`, `CGColorSpaceRelease`, `CGContextDrawImage`, `CGContextRelease`.

- [ ] **Step 6: Verify and commit**

Run: `dotnet test Bevel.sln -clp:ErrorsOnly` → 0 failed.

```bash
git add src/Bevel.Pal.MacOS tests/Bevel.Pal.MacOS.Tests
git commit -m "refactor(pal-macos): one CoreGraphics and ImageIO declaration per symbol

Splits by the framework each symbol actually lives in rather than by its
prefix: CGImageSourceCreateWithURL and CGImageSourceCreateThumbnailAtIndex
are ImageIO, and binding them to CoreGraphics would have failed to resolve
at runtime on a path only a real Mac exercises.

Verified by the thumbnail provider's pixel-content tests, which drive
CGBitmapContext, CGImageSource and CGPDF end to end."
```

---

### Task 4: Accessibility

**Files:**
- Create: `src/Bevel.Pal.MacOS/AccessibilityInterop.cs`
- Modify: `src/Bevel.Pal.MacOS/GeckoTabEngine.cs` (drop 5 AX externs + its `AppServices` const)
- Modify: `src/Bevel.Pal.MacOS/MacOSAppBadgeSource.cs` (drop 3 AX externs + its `ApplicationServices` const)
- Modify: `src/Bevel.Pal.MacOS/MacOSPermissionBroker.cs` (drop `AXIsProcessTrusted`)
- Modify: `tests/Bevel.Pal.MacOS.Tests/InteropConventionTests.cs`

**Interfaces:**
- Consumes: `Frameworks.ApplicationServices`.
- Produces: `internal static class AccessibilityInterop` with `AXIsProcessTrusted()`, `AXUIElementCreateApplication(int)`, `AXUIElementCreateSystemWide()`, `AXUIElementCopyAttributeValue(IntPtr, IntPtr, out IntPtr)→int`, `AXUIElementPerformAction(IntPtr, IntPtr)→int`, `_AXUIElementSetMessagingTimeout(IntPtr, float)→int`.

- [ ] **Step 1: Confirm the two path constants resolve to the same literal before merging them**

`GeckoTabEngine` calls its const `AppServices` and `MacOSAppBadgeSource` calls its `ApplicationServices`. Both were measured as `/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices` on 2026-09-28. Re-confirm, because merging two *differently named* constants is where a real path change would hide:

Run:
```bash
grep -A1 -n "const string AppServices\|const string ApplicationServices" \
  src/Bevel.Pal.MacOS/GeckoTabEngine.cs src/Bevel.Pal.MacOS/MacOSAppBadgeSource.cs
```
Expected: both literals identical, and equal to `Frameworks.ApplicationServices`. **If they differ, stop** — that is a behaviour change, not a refactor, and `AXIsProcessTrusted` loading from a different framework alters TCC prompting. Report it instead of proceeding.

- [ ] **Step 2: Create the accessibility interop class**

```csharp
// src/Bevel.Pal.MacOS/AccessibilityInterop.cs
using System;
using System.Runtime.InteropServices;

namespace Bevel.Pal.MacOS;

/// <summary>
/// Accessibility (AX) entry points, declared once (bevel-uat). Consolidated from GeckoTabEngine,
/// MacOSAppBadgeSource and MacOSPermissionBroker, which between them named the same framework path
/// under two different constant names (`AppServices` and `ApplicationServices`).
///
/// Every function here needs the Accessibility TCC grant. AXIsProcessTrusted is the gate the rest
/// depend on; it must keep loading from ApplicationServices, because which framework it resolves from
/// affects TCC prompting behaviour.
/// </summary>
internal static class AccessibilityInterop
{
    [DllImport(Frameworks.ApplicationServices)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool AXIsProcessTrusted();

    [DllImport(Frameworks.ApplicationServices)]
    public static extern IntPtr AXUIElementCreateApplication(int pid);

    [DllImport(Frameworks.ApplicationServices)]
    public static extern IntPtr AXUIElementCreateSystemWide();

    [DllImport(Frameworks.ApplicationServices)]
    public static extern int AXUIElementCopyAttributeValue(IntPtr element, IntPtr attribute, out IntPtr value);

    [DllImport(Frameworks.ApplicationServices)]
    public static extern int AXUIElementPerformAction(IntPtr element, IntPtr action);

    [DllImport(Frameworks.ApplicationServices, EntryPoint = "_AXUIElementSetMessagingTimeout")]
    public static extern int AXUIElementSetMessagingTimeout(IntPtr element, float timeoutInSeconds);
}
```

> `_AXUIElementSetMessagingTimeout` is a private-but-stable symbol already in use (it is how the helper avoids blocking on a hung target). Keep the leading underscore in `EntryPoint` and re-check the current declaration's return type and parameter name before transcribing. If `AXIsProcessTrusted`'s existing declarations do not carry `[return: MarshalAs(U1)]`, match whichever the current code uses rather than adding marshalling here — `Boolean` return marshalling is the one place a silent change would flip a permissions check.

- [ ] **Step 3: Delete the silo declarations, add `using static`, build**

Run: `dotnet build Bevel.sln -clp:ErrorsOnly` → 0 errors.

- [ ] **Step 4: Verify the live AX tests still pass**

Run: `dotnet test tests/Bevel.Pal.MacOS.Tests/Bevel.Pal.MacOS.Tests.csproj -clp:ErrorsOnly`
Expected: green, including `MacOSAppBadgeSourceTests`' live contract test, which walks the real Dock AX tree on this machine. A broken AX signature shows up there as an empty result rather than a crash, so check it actually returns badges if any app on the machine has one.

- [ ] **Step 5: Shrink the baseline**

Remove: `AXIsProcessTrusted`, `AXUIElementCopyAttributeValue`, `AXUIElementCreateApplication`.

- [ ] **Step 6: Commit**

```bash
git add src/Bevel.Pal.MacOS tests/Bevel.Pal.MacOS.Tests
git commit -m "refactor(pal-macos): one Accessibility declaration per symbol

Three files declared the AX entry points privately, naming the same framework
path under two different constant names, which is how a genuine path change
would have avoided looking like one. AXIsProcessTrusted in particular must
keep resolving from ApplicationServices: which framework it loads from
affects TCC prompting."
```

---

### Task 5: Close out — `dlopen`, turn the guard fully on, document

**Files:**
- Modify: `src/Bevel.Pal.MacOS/AppKitInterop.cs` (use `Frameworks.*`; keep `dlopen`)
- Modify: `src/Bevel.Pal.MacOS/LoginItemRegistrar.cs` (drop its `dlopen`)
- Modify: every remaining file in `src/Bevel.Pal.MacOS` with a literal `DllImport` path
- Modify: `tests/Bevel.Pal.MacOS.Tests/InteropConventionTests.cs` (empty and delete the baseline; unskip the literal-path test)
- Modify: `CODEMAP.md`

**Interfaces:**
- Consumes: everything from Tasks 1–4.
- Produces: no new API. `KnownDuplicates` is gone.

- [ ] **Step 1: Move `dlopen` to `AppKitInterop` and delete the duplicate**

`dlopen` is libSystem, used by `AppKitInterop` and `LoginItemRegistrar`. Keep the `AppKitInterop` one (now `[DllImport(Frameworks.LibSystem)]`, `public`), delete `LoginItemRegistrar`'s, add `using static Bevel.Pal.MacOS.AppKitInterop;` there.

- [ ] **Step 2: Repoint every remaining literal path to a `Frameworks` constant**

Sweep `src/Bevel.Pal.MacOS/*.cs` — including `AppleEventInbound.cs`, `AppleEventQuery.cs`, `AppleEventSpecifier.cs`, `TabFaviconStore.cs`, `AppKitInterop.cs` — replacing every inline `"/System/Library/..."` / `"/usr/lib/..."` and every surviving `private const string` with the shared constant. Add a constant to `Frameworks` for any path not yet there (e.g. Carbon, if the Apple Event files use it) rather than leaving a literal.

- [ ] **Step 3: Empty the baseline and turn the guard fully on**

Delete the `KnownDuplicates` field, delete `Baseline_lists_only_real_duplicates` (it exists only to keep the baseline honest), remove the `Skip` from `Every_dll_import_names_a_frameworks_constant`, and simplify:

```csharp
    [Fact]
    public void No_symbol_is_declared_in_two_files()
    {
        var offenders = Declarations()
            .GroupBy(d => d.Symbol)
            .Where(g => g.Select(d => d.File).Distinct().Count() > 1)
            .Select(g => g.Key)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These P/Invoke symbols are declared in more than one file. Add them to the interop class " +
            "for their framework instead of starting a private silo:\n  " + string.Join("\n  ", offenders));
    }
```

- [ ] **Step 4: Run the guard and the full suite**

Run: `dotnet test Bevel.sln -clp:ErrorsOnly`
Expected: 0 failed, and both `InteropConventionTests` facts passing with no skips. If `Every_dll_import_names_a_frameworks_constant` still lists files, finish Step 2 rather than re-adding the skip.

- [ ] **Step 5: Record the layout in CODEMAP**

Add one bullet under the macOS PAL section:

```markdown
- **macOS P/Invoke is declared once per symbol, in the class for its framework** (bevel-uat).
  `Frameworks.cs` holds every library path; `CoreFoundationInterop` / `CoreGraphicsInterop` /
  `ImageIOInterop` / `AccessibilityInterop` / `AppKitInterop` (AppKit + objc + libSystem) hold the
  declarations. Do NOT start a `private static extern` in a provider file — `InteropConventionTests`
  fails the build for a symbol declared twice or a `DllImport` naming a path directly. Type mapping
  that four old silos disagreed on: `CFIndex` → `nint`, `CFTypeID` → `nuint`, `Boolean` →
  `[MarshalAs(U1)] bool`. Note `CGImageSource*` is **ImageIO**, not CoreGraphics, despite the prefix.
```

- [ ] **Step 6: Close the bead and file the two follow-ups**

```bash
BD_IGNORE_SCHEMA_SKEW=1 bd close bevel-uat --reason="Consolidated into per-framework interop classes with a guard test. Amended the bead's 'into AppKitInterop' wording: split by framework instead, since one class binding five frameworks would be a 100+ declaration grab bag named after one of them. Fixed six divergent signatures to the CF header types (all benign on arm64/x64, but a coin flip to unify blind)."

BD_IGNORE_SCHEMA_SKEW=1 bd create --title="Migrate macOS P/Invoke to source-generated [LibraryImport]" --type=task --priority=3 --description="Now that each symbol is declared once (bevel-uat), move Bevel.Pal.MacOS to [LibraryImport] for AOT/trim friendliness (AOT is a stated goal, bevel-gww). Held back from the consolidation deliberately: LibraryImport needs partial members and source-generated stubs and rejects non-blittable parameters without explicit config -- CFURLCreateFromFileSystemRepresentation and the AX/CF Boolean returns are exactly that case -- so bundling it would have made a marshalling regression indistinguishable from a transcription error. Do it per interop class, with the pixel-content and live-AX tests green after each."

BD_IGNORE_SCHEMA_SKEW=1 bd create --title="Windows P/Invoke has the same silo duplication as macOS did" --type=task --priority=3 --description="Bevel.Pal.Windows holds ~108 extern declarations across 7 files (WindowsWindowManager 41, WindowsDesktop 20, WindowsFileStack 19, WindowsAppEnvironment 17, WindowsShellSession 7, WindowsSystemTrayHost 3, WindowsAudio 1) with no shared interop class. Out of bevel-uat's scope, but the same fix applies: one declaration per symbol in a class per module (user32/shell32/dwmapi/...), paths in one constants class, and extend InteropConventionTests to scan Bevel.Pal.Windows too. Measure the duplicate count first -- it was 18 symbols / 21 redundant declarations on macOS."
```

- [ ] **Step 7: Final commit**

```bash
git add -A
git commit -m "refactor(pal-macos): finish interop consolidation and enforce it

Moves the last literal DllImport paths onto Frameworks constants, drops the
duplicate dlopen, and turns the convention guard fully on: a symbol declared
in two files, or a DllImport naming a path directly, now fails the build.

Final state: 80 distinct symbols, 80 declarations (was 101 across 11 files,
18 of them duplicated). CODEMAP records the layout and the CF type mapping
the old silos disagreed about."
```

---

## Self-Review

**Spec coverage.** `bevel-uat` asks for consolidation of duplicated ObjC interop: Tasks 2–5 cover all 18 duplicated symbols (CF 8, CG 6, AX 3, `dlopen` 1). The bead's "into AppKitInterop" wording is deliberately amended, with the reason recorded in Task 5's close message. The 2026-09-28 signature-drift finding is covered by Task 2 Step 1 and the canonical-type table.

**Placeholders.** None: every step names exact files and shows the code or the command. Where the plan cannot guarantee a transcription (signatures may have changed since the 2026-09-28 inventory) it says "current source wins" and gives the check, rather than leaving the value blank.

**Type consistency.** `Frameworks.*` constant names are used identically in Tasks 1–5. `CoreFoundationInterop`/`CoreGraphicsInterop`/`ImageIOInterop`/`AccessibilityInterop` are named the same everywhere. `KnownDuplicates` is created in Task 1, shrunk in Tasks 2–4, deleted in Task 5. The 18 baseline entries match the measured duplicates exactly.

**Review Focus coverage.** (1) pointer-sized types → Task 2 canonical table + `CoreFoundationInterop` header comment; (2) `Boolean` signedness → Task 2 standardises `U1`, Task 4 warns against changing the AX Boolean return; (3) retain/release balance → Task 2 Step 5 adds the Gecko repeat test; (4) `AXIsProcessTrusted` framework path → Task 4 Step 1 is a hard stop; (5) ImageIO-vs-CoreGraphics → Task 3 Step 1 splits by path with the runtime-failure consequence stated.

**Known residual risk.** Every verification in this plan runs on *this* machine's TCC grants. The live AX and Dock tests prove the AX and CF signatures on arm64 macOS only; nothing here proves the Windows build's behaviour is unchanged (it does not touch Windows code) and nothing proves behaviour on a 32-bit or Intel target, where the `nint`-vs-`long` choices would actually diverge. That is the correct direction (`nint` matches the header) but it is unverified by execution.
