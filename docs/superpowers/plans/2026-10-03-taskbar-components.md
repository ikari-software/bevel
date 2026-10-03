# Taskbar Components Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Bevel taskbar a composition of manifest-declared components — ordered list, multiple instances, per-instance settings — so adding a taskbar feature means adding a component instead of editing `TaskbarView.axaml`.

**Architecture:** A component is declared by a **manifest** (pure data in `Bevel.Core`), not a C# type. Instances (`{ instanceId, typeId, settings, visible }`) persist in one settings key. The bar lays them out as a single ordered list where `spacer` is itself a weighted component, so Win2000's leading-pinned bar and Win11's centred bar are the same model. Built-ins bind locally; third-party components run as supervised processes on a NetMQ component bus. Pixels for the `surface` primitive travel through the existing `MmfBgraPool`, never through the queue.

**Tech Stack:** C# / .NET 10, Avalonia, NetMQ + Google.Protobuf, xUnit + Avalonia.Headless (`UseSkia()`, `UseHeadlessDrawing=false`), `bd` (beads) for task tracking.

**Spec:** [`docs/superpowers/specs/2026-10-03-taskbar-components-design.md`](../specs/2026-10-03-taskbar-components-design.md)

## Global Constraints

- **Task tracking is `bd` (beads), never TodoWrite or markdown TODO lists.** Run `bd update <id> --claim` when starting, `bd close <id>` when done. Parent epic: `bevel-aqr7`.
- **`Bevel.Core` must not reference Avalonia** (ARCH-02). All manifest/instance/validation types live in `Bevel.Core/Components/`; no `using Avalonia` in that folder.
- **Never block the Avalonia UI thread** — no `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` on the UI thread, no inline I/O in handlers or startup. Marshal with `Dispatcher.UIThread.Post/InvokeAsync`.
- **Vector-only assets.** SVG or code-drawn geometry bound to theme tokens, never bitmaps. **Never disable antialiasing.**
- **AOT:** every new type crossing `System.Text.Json` must be registered in `SettingsJsonContext` (`src/Bevel.Core/SettingsJsonContext.cs`) — it is a source-generated `JsonSerializerContext`. `BevelPublishAot=true` must keep working.
- **Theme/resource-mutating test classes** share `[Collection("TaskbarTheme")]`.
- **Headless render tests** use `[AvaloniaFact]` from `Avalonia.Headless.XUnit`.
- Conventional-commit messages. Commit after every task.
- Build: `dotnet build Bevel.sln -clp:ErrorsOnly`. Test: `dotnet test Bevel.sln`.
- **NetMQ owns background I/O threads.** A test assembly that creates sockets must call `NetMQConfig.Cleanup(block: false)` once after the last test, or the runner can hang at exit. Add it to the test assembly's existing fixture rather than per-test, and never call it between tests in the same assembly — it tears down the shared context.
- **Two pre-existing test failures are expected and not yours:** `SymlinkCopyTest.CopyDirectory_recreates_symlinks_without_following_them` (needs the create-symlink privilege) and `SettingsServiceSqliteTests.Concurrent_readers_and_writers_never_hit_database_is_locked` (`bevel-q3ck`, fails only under full-suite parallel load).

## Review Focus

Five conditions the spec implies that no task's happy path exercises. Each has a test pinned to the task that owns the code.

1. **A persisted list containing two instances of a `multiInstance:false` type** (hand-edited `settings.db`, or a downgrade that wrote it). Add-time enforcement does not cover data already on disk; the bar must keep the first and drop the rest rather than render two Start buttons. — Task 4.
2. **A settings value that no longer parses to its schema type** after a component changes its schema (`"abc"` where the field is now `int`). Must fall back to the schema default and keep the instance alive, not throw. — Task 3.
3. **An authenticated bus peer publishing a frame-ready for a slot it does not own.** Authentication proves *a* component, not *which* component; without an ownership check one component can inject pixels into another's surface. — Task 8.
4. **A persisted list with duplicate `instanceId`s.** Settings are keyed by instance; duplicates make per-instance settings ambiguous and silently cross-wire. — Task 4.
5. **A list containing only spacers** (no content component). Greedy weight distribution over zero content must not divide by zero or produce an infinite width. — Task 5.

---

## File Structure

**New — `src/Bevel.Core/Components/`** (pure data + validation, no Avalonia):

| File | Responsibility |
|---|---|
| `ComponentManifest.cs` | The manifest record, `ComponentSizing` enum |
| `ComponentSettingsSchema.cs` | `ComponentSettingsField`, typed parse + default fallback |
| `ComponentPrimitive.cs` | Primitive tree: glyph, label, badge, separator, flyout, surface |
| `ManifestValidator.cs` | Validation rules; returns errors, never throws |
| `ComponentInstance.cs` | `{ InstanceId, TypeId, Settings, Visible }` |
| `ComponentListNormalizer.cs` | Dedupe/repair a persisted list (Review Focus 1, 4) |
| `TaskbarComponentsMigration.cs` | One-time fold of the 29 legacy keys |

**New — `src/Bevel.Taskbar/Components/`** (hosting + layout):

| File | Responsibility |
|---|---|
| `ComponentRegistry.cs` | typeId → manifest + factory; capability filtering |
| `TaskbarComponentsPanel.cs` | The ordered-list layout panel (sizing + spacer weights) |
| `BarGeometry.cs` | Per-bar computed height; replaces `TaskbarTheme` statics |
| `IComponentChannel.cs` | The one interface both hosts implement |
| `LocalComponentChannel.cs` | Built-ins — same contract, no IPC |
| `RemoteComponentChannel.cs` | Third-party — NetMQ + supervision |

**New — `proto/bevel.components.v1.proto`** — component bus messages.

**Modified:**

| File | Change |
|---|---|
| `src/Bevel.Core/SettingsService.cs` | Add `TaskbarComponents`; `SetOrPrune` + load + **explicit `CopyFrom` deep clone** |
| `src/Bevel.Core/SettingsJsonContext.cs` | Register the new serializable types |
| `src/Bevel.Taskbar/TaskbarWindow.cs:495-538` | Delete `TaskbarTheme` statics, call `BarGeometry` |
| `src/Bevel.Taskbar/TaskbarView.axaml` | Replace the fixed `Auto,*,Auto` grid with the panel |
| `src/Bevel.App/App.axaml.cs:338` | Remove `TaskbarTheme.Configure` |
| `Directory.Packages.props` | Pin `NetMQ` |

---

### Task 1: Verify NetMQ against NativeAOT before it is load-bearing

The spec makes NetMQ load-bearing for both buses. `BevelPublishAot=true` targets a single binary with no JIT. If NetMQ is not AOT-safe this is a transport decision to revisit now, not during migration. **This task's deliverable is a verified answer plus a pinned package.**

**Files:**
- Modify: `Directory.Packages.props`
- Create: `tests/Bevel.Core.Tests/NetMqAotCompatTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: a pinned `NetMQ` `PackageVersion`; confirmation that `dotnet publish -p:BevelPublishAot=true` succeeds with NetMQ referenced.

- [ ] **Step 1: Claim the bead**

```bash
bd update bevel-aqr7 --claim
```

- [ ] **Step 2: Pin NetMQ**

In `Directory.Packages.props`, beside the existing `Google.Protobuf` entry:

```xml
<PackageVersion Include="NetMQ" Version="4.0.1.13" />
```

Add the reference to `src/Bevel.Core/Bevel.Core.csproj`:

```xml
<PackageReference Include="NetMQ" />
```

- [ ] **Step 3: Write a test that actually exercises a NetMQ round-trip**

A reference alone proves nothing — the AOT risk is reflection inside NetMQ's socket setup, which only runs on use. Create `tests/Bevel.Core.Tests/NetMqAotCompatTests.cs`:

```csharp
using System.Text;
using NetMQ;
using NetMQ.Sockets;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>
/// bevel-aqr7 Task 1: NetMQ is load-bearing for both buses, and BevelPublishAot=true targets a
/// single binary with no JIT. This exercises a real socket round-trip so that an AOT publish
/// surfaces any reflection NetMQ needs at connect time, not just at reference time.
/// </summary>
public class NetMqAotCompatTests
{
    [Fact]
    public void Loopback_round_trip_carries_bytes_both_ways()
    {
        using var server = new RouterSocket();
        var port = server.BindRandomPort("tcp://127.0.0.1");
        using var client = new DealerSocket();
        client.Connect($"tcp://127.0.0.1:{port}");

        client.SendFrame(Encoding.UTF8.GetBytes("ping"));

        var identity = server.ReceiveFrameBytes();
        var payload = server.ReceiveFrameBytes();
        Assert.Equal("ping", Encoding.UTF8.GetString(payload));

        server.SendMoreFrame(identity).SendFrame(Encoding.UTF8.GetBytes("pong"));
        Assert.Equal("pong", Encoding.UTF8.GetString(client.ReceiveFrameBytes()));
    }
}
```

- [ ] **Step 4: Run it**

Run: `dotnet test tests/Bevel.Core.Tests/Bevel.Core.Tests.csproj --filter NetMqAotCompatTests -v n`
Expected: PASS. If it hangs, NetMQ did not bind — treat that as a finding, not a flake.

- [ ] **Step 5: Publish with AOT and record the result**

Run (macOS):
```bash
dotnet publish src/Bevel.App/Bevel.App.csproj -r osx-arm64 -p:BevelPublishAot=true -clp:ErrorsOnly
```
Run (Windows):
```bash
dotnet publish src/Bevel.App/Bevel.App.csproj -r win-x64 -p:BevelPublishAot=true -clp:ErrorsOnly
```

Expected: publish succeeds. **Trim/AOT warnings mentioning NetMQ are the finding this task exists to surface** — capture the exact text.

- [ ] **Step 6: Record the answer on the bead and STOP if it failed**

```bash
bd update bevel-aqr7 --append-notes="Task 1: NetMQ 4.0.1.13 AOT check — <PASS|FAIL>. Publish output: <exact warnings or 'clean'>."
```

If the publish fails or emits NetMQ trim warnings, **stop and report to your human partner before Task 6.** The spec records the transport as an accepted risk; a failed AOT check changes that input and is their decision, not yours.

- [ ] **Step 7: Commit**

```bash
git add Directory.Packages.props src/Bevel.Core/Bevel.Core.csproj tests/Bevel.Core.Tests/NetMqAotCompatTests.cs
git commit -m "test(components): verify NetMQ survives NativeAOT before making it load-bearing"
```

---

### Task 2: The manifest, the primitive tree, and validation

**Files:**
- Create: `src/Bevel.Core/Components/ComponentManifest.cs`
- Create: `src/Bevel.Core/Components/ComponentPrimitive.cs`
- Create: `src/Bevel.Core/Components/ComponentSettingsSchema.cs`
- Create: `src/Bevel.Core/Components/ManifestValidator.cs`
- Test: `tests/Bevel.Core.Tests/ManifestValidatorTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `ComponentManifest`, `ComponentSizing`, `ComponentPrimitive` (+ subtypes `GlyphPrimitive`, `LabelPrimitive`, `BadgePrimitive`, `SeparatorPrimitive`, `FlyoutPrimitive`, `SurfacePrimitive`), `ComponentSettingsField`, `ComponentFieldKind`, `ManifestValidator.Validate(ComponentManifest) → ManifestValidation`, `ManifestValidation.IsValid`, `ManifestValidation.Errors`, and the constant `ManifestValidator.CurrentContractVersion`.

- [ ] **Step 1: Write the failing validation tests**

Create `tests/Bevel.Core.Tests/ManifestValidatorTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using Bevel.Core.Components;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>
/// bevel-aqr7 Task 2: validation rejects the COMPONENT, never the bar. Every case here must
/// produce errors rather than an exception, because an invalid third-party manifest must not be
/// able to take the shell down.
/// </summary>
public class ManifestValidatorTests
{
    private static ComponentManifest Clock(params ComponentPrimitive[] view) => new(
        Id: "run.bevel.clock",
        ContractVersion: ManifestValidator.CurrentContractVersion,
        DisplayName: "Clock",
        Description: "Shows the time",
        MultiInstance: true,
        Sizing: ComponentSizing.Content,
        RequiresCapability: null,
        SettingsSchema: new[]
        {
            new ComponentSettingsField("showSeconds", ComponentFieldKind.Bool, "Show seconds", "false", null, null),
        },
        View: view.Length == 0 ? new ComponentPrimitive[] { new LabelPrimitive("time", "Time") } : view);

    [Fact]
    public void A_well_formed_manifest_validates()
        => Assert.True(ManifestValidator.Validate(Clock()).IsValid);

    [Fact]
    public void A_future_contract_version_is_rejected()
    {
        var m = Clock() with { ContractVersion = ManifestValidator.CurrentContractVersion + 1 };
        var r = ManifestValidator.Validate(m);
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("contractVersion"));
    }

    [Fact]
    public void A_surface_without_an_accessible_name_is_rejected()
    {
        var m = Clock(new SurfacePrimitive("face", 32, 16, AccessibleName: "", AccessibleRole: "Image"));
        var r = ManifestValidator.Validate(m);
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("accessible name"));
    }

    [Fact]
    public void A_surface_without_an_accessible_role_is_rejected()
    {
        var m = Clock(new SurfacePrimitive("face", 32, 16, AccessibleName: "Clock face", AccessibleRole: ""));
        var r = ManifestValidator.Validate(m);
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("accessible role"));
    }

    [Fact]
    public void An_empty_id_is_rejected()
        => Assert.False(ManifestValidator.Validate(Clock() with { Id = "" }).IsValid);

    [Fact]
    public void Duplicate_settings_field_keys_are_rejected()
    {
        var dup = new ComponentSettingsField("showSeconds", ComponentFieldKind.Bool, "Again", "true", null, null);
        var m = Clock() with { SettingsSchema = Clock().SettingsSchema.Append(dup).ToArray() };
        Assert.False(ManifestValidator.Validate(m).IsValid);
    }

    [Fact]
    public void Validation_never_throws_on_a_null_view_or_schema()
    {
        var m = Clock() with { View = null!, SettingsSchema = null! };
        var r = ManifestValidator.Validate(m);   // must not throw
        Assert.False(r.IsValid);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Bevel.Core.Tests/Bevel.Core.Tests.csproj --filter ManifestValidatorTests`
Expected: FAIL — `Bevel.Core.Components` does not exist.

- [ ] **Step 3: Write the primitive tree**

Create `src/Bevel.Core/Components/ComponentPrimitive.cs`:

```csharp
namespace Bevel.Core.Components;

/// <summary>
/// One node of a component's view. The BAR renders these with its own themed controls — a
/// component never ships pixels except through <see cref="SurfacePrimitive"/>, because a themable
/// shell cannot let a component paint chrome that goes stale on the next theme switch.
/// </summary>
public abstract record ComponentPrimitive(string Key);

/// <summary>A themed vector glyph, named from the component's own glyph set.</summary>
public sealed record GlyphPrimitive(string Key, string GlyphId) : ComponentPrimitive(Key);

/// <summary>A run of text. <paramref name="Text"/> is the initial value; state updates replace it.</summary>
public sealed record LabelPrimitive(string Key, string Text) : ComponentPrimitive(Key);

/// <summary>An unread/attention count drawn in the active skin's badge treatment.</summary>
public sealed record BadgePrimitive(string Key, int Count) : ComponentPrimitive(Key);

/// <summary>A themed divider.</summary>
public sealed record SeparatorPrimitive(string Key) : ComponentPrimitive(Key);

/// <summary>A popup whose children are themselves primitives.</summary>
public sealed record FlyoutPrimitive(string Key, IReadOnlyList<ComponentPrimitive> Children)
    : ComponentPrimitive(Key);

/// <summary>
/// A pixel region the component paints itself, delivered through <c>MmfBgraPool</c>. Accessibility
/// and theming degrade only INSIDE this region, which is why both accessible fields are mandatory
/// and the bar pushes theme tokens to the component (see the spec, §3.3).
/// </summary>
public sealed record SurfacePrimitive(
    string Key,
    int IntrinsicWidth,
    int IntrinsicHeight,
    string AccessibleName,
    string AccessibleRole) : ComponentPrimitive(Key);
```

- [ ] **Step 4: Write the settings schema**

Create `src/Bevel.Core/Components/ComponentSettingsSchema.cs`:

```csharp
using System.Globalization;

namespace Bevel.Core.Components;

/// <summary>Field types a component may declare. Values persist as strings; the schema says how to read them.</summary>
public enum ComponentFieldKind { Bool, Int, String, Enum, Path }

/// <summary>
/// One self-describing settings field. The arrangement UI renders an editor FROM this, which is what
/// removes the hand-written settings UI and hand-written serialization per key.
/// </summary>
public sealed record ComponentSettingsField(
    string Key,
    ComponentFieldKind Kind,
    string Label,
    string DefaultValue,
    IReadOnlyList<string>? AllowedValues,
    (int Min, int Max)? Range)
{
    /// <summary>
    /// Reads <paramref name="raw"/> as this field's type, falling back to the declared default when it
    /// does not parse. A component that changes a field's type must not brick instances that still hold
    /// the old value, so this NEVER throws (Review Focus 2).
    /// </summary>
    public bool TryRead(string? raw, out string value)
    {
        value = DefaultValue;
        if (raw is null) return false;
        switch (Kind)
        {
            case ComponentFieldKind.Bool:
                if (!bool.TryParse(raw, out var b)) return false;
                value = b ? "true" : "false";
                return true;
            case ComponentFieldKind.Int:
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return false;
                if (Range is { } r && (i < r.Min || i > r.Max)) return false;
                value = i.ToString(CultureInfo.InvariantCulture);
                return true;
            case ComponentFieldKind.Enum:
                if (AllowedValues is null || !AllowedValues.Contains(raw)) return false;
                value = raw;
                return true;
            default:
                value = raw;
                return true;
        }
    }
}
```

- [ ] **Step 5: Write the manifest and the validator**

Create `src/Bevel.Core/Components/ComponentManifest.cs`:

```csharp
namespace Bevel.Core.Components;

/// <summary>How a component claims horizontal space. At most one <c>Greedy</c> per bar list.</summary>
public enum ComponentSizing { Fixed, Content, Greedy }

/// <summary>
/// A component TYPE. Declared as data rather than as a C# type so the contract is authorable by a
/// stranger and so <see cref="Id"/> — never a .NET type name — is what instances persist against.
/// </summary>
public sealed record ComponentManifest(
    string Id,
    int ContractVersion,
    string DisplayName,
    string Description,
    bool MultiInstance,
    ComponentSizing Sizing,
    string? RequiresCapability,
    IReadOnlyList<ComponentSettingsField> SettingsSchema,
    IReadOnlyList<ComponentPrimitive> View);
```

Create `src/Bevel.Core/Components/ManifestValidator.cs`:

```csharp
namespace Bevel.Core.Components;

/// <summary>Outcome of validating one manifest. Errors are collected, never thrown.</summary>
public sealed record ManifestValidation(bool IsValid, IReadOnlyList<string> Errors);

/// <summary>
/// Validates a manifest. Every failure rejects the COMPONENT and leaves the bar intact — an invalid
/// third-party manifest must never be able to take the shell down.
/// </summary>
public static class ManifestValidator
{
    /// <summary>The contract version this build understands.</summary>
    public const int CurrentContractVersion = 1;

    public static ManifestValidation Validate(ComponentManifest m)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(m.Id)) errors.Add("id must not be empty");
        if (m.ContractVersion > CurrentContractVersion)
            errors.Add($"contractVersion {m.ContractVersion} is newer than this build supports ({CurrentContractVersion})");
        if (m.ContractVersion < 1) errors.Add("contractVersion must be at least 1");
        if (string.IsNullOrWhiteSpace(m.DisplayName)) errors.Add("displayName must not be empty");

        if (m.SettingsSchema is null) errors.Add("settingsSchema must not be null");
        else
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in m.SettingsSchema)
            {
                if (string.IsNullOrWhiteSpace(f.Key)) errors.Add("settings field key must not be empty");
                else if (!seen.Add(f.Key)) errors.Add($"duplicate settings field key '{f.Key}'");
            }
        }

        if (m.View is null) errors.Add("view must not be null");
        else ValidateView(m.View, errors);

        return new ManifestValidation(errors.Count == 0, errors);
    }

    private static void ValidateView(IReadOnlyList<ComponentPrimitive> view, List<string> errors)
    {
        foreach (var p in view)
        {
            if (string.IsNullOrWhiteSpace(p.Key)) errors.Add("primitive key must not be empty");
            switch (p)
            {
                case SurfacePrimitive s:
                    if (string.IsNullOrWhiteSpace(s.AccessibleName))
                        errors.Add($"surface '{s.Key}' must declare an accessible name");
                    if (string.IsNullOrWhiteSpace(s.AccessibleRole))
                        errors.Add($"surface '{s.Key}' must declare an accessible role");
                    if (s.IntrinsicWidth <= 0 || s.IntrinsicHeight <= 0)
                        errors.Add($"surface '{s.Key}' must declare a positive intrinsic size");
                    break;
                case FlyoutPrimitive f:
                    ValidateView(f.Children ?? Array.Empty<ComponentPrimitive>(), errors);
                    break;
            }
        }
    }
}
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/Bevel.Core.Tests/Bevel.Core.Tests.csproj --filter ManifestValidatorTests`
Expected: PASS (7 tests).

- [ ] **Step 7: Confirm ARCH-02 still holds**

Run: `grep -rn "using Avalonia" src/Bevel.Core/Components/`
Expected: no output.

- [ ] **Step 8: Commit**

```bash
git add src/Bevel.Core/Components tests/Bevel.Core.Tests/ManifestValidatorTests.cs
git commit -m "feat(components): manifest, primitive tree, and validation that rejects the component not the bar"
```

---

### Task 3: The instance model and per-instance settings reads

**Files:**
- Create: `src/Bevel.Core/Components/ComponentInstance.cs`
- Test: `tests/Bevel.Core.Tests/ComponentInstanceTests.cs`

**Interfaces:**
- Consumes: `ComponentManifest`, `ComponentSettingsField`, `ComponentFieldKind` (Task 2).
- Produces: `ComponentInstance` record with `InstanceId`, `TypeId`, `Settings` (`Dictionary<string,string>`), `Visible`; `ComponentInstance.NewId()`; `ComponentInstance.ReadSetting(ComponentManifest, string key) → string`.

Settings values are **strings**, parsed through the schema. That keeps the persisted shape AOT-friendly (no `JsonElement` in the source-generated context) and puts type knowledge in exactly one place.

- [ ] **Step 1: Write the failing tests**

Create `tests/Bevel.Core.Tests/ComponentInstanceTests.cs`:

```csharp
using System.Collections.Generic;
using Bevel.Core.Components;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>
/// bevel-aqr7 Task 3: per-instance settings. The SCHEMA belongs to the type; the VALUES belong to
/// the instance, so two instances of one type hold independent values.
/// </summary>
public class ComponentInstanceTests
{
    private static readonly ComponentManifest ClockType = new(
        "run.bevel.clock", 1, "Clock", "", MultiInstance: true, ComponentSizing.Content, null,
        new[]
        {
            new ComponentSettingsField("showSeconds", ComponentFieldKind.Bool, "Show seconds", "false", null, null),
            new ComponentSettingsField("tzOffset", ComponentFieldKind.Int, "Offset", "0", null, (-12, 14)),
        },
        new ComponentPrimitive[] { new LabelPrimitive("time", "Time") });

    private static ComponentInstance Inst(params (string, string)[] kv)
    {
        var d = new Dictionary<string, string>();
        foreach (var (k, v) in kv) d[k] = v;
        return new ComponentInstance(ComponentInstance.NewId(), "run.bevel.clock", d, Visible: true);
    }

    [Fact]
    public void Two_instances_of_one_type_hold_independent_values()
    {
        var a = Inst(("showSeconds", "true"));
        var b = Inst(("showSeconds", "false"));
        Assert.Equal("true", a.ReadSetting(ClockType, "showSeconds"));
        Assert.Equal("false", b.ReadSetting(ClockType, "showSeconds"));
        Assert.NotEqual(a.InstanceId, b.InstanceId);
    }

    [Fact]
    public void A_missing_value_falls_back_to_the_schema_default()
        => Assert.Equal("false", Inst().ReadSetting(ClockType, "showSeconds"));

    // Review Focus 2: a value that no longer parses must not throw or kill the instance.
    [Fact]
    public void An_unparseable_value_falls_back_to_the_default_instead_of_throwing()
        => Assert.Equal("0", Inst(("tzOffset", "abc")).ReadSetting(ClockType, "tzOffset"));

    [Fact]
    public void An_out_of_range_value_falls_back_to_the_default()
        => Assert.Equal("0", Inst(("tzOffset", "99")).ReadSetting(ClockType, "tzOffset"));

    [Fact]
    public void A_key_the_schema_does_not_declare_reads_as_empty_rather_than_throwing()
        => Assert.Equal("", Inst(("bogus", "x")).ReadSetting(ClockType, "bogus"));

    [Fact]
    public void NewId_produces_distinct_ids()
        => Assert.NotEqual(ComponentInstance.NewId(), ComponentInstance.NewId());
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Bevel.Core.Tests/Bevel.Core.Tests.csproj --filter ComponentInstanceTests`
Expected: FAIL — `ComponentInstance` not defined.

- [ ] **Step 3: Implement**

Create `src/Bevel.Core/Components/ComponentInstance.cs`:

```csharp
namespace Bevel.Core.Components;

/// <summary>
/// A PLACEMENT of a component type on a bar. Identity, settings values and runtime state all belong
/// to the instance rather than the type (spec §3.2). Settings nest here rather than in a side table
/// keyed by id, which makes orphaned settings structurally impossible.
/// </summary>
public sealed record ComponentInstance(
    string InstanceId,
    string TypeId,
    Dictionary<string, string> Settings,
    bool Visible)
{
    /// <summary>A fresh instance id. Stable for the life of the placement, so reordering never loses settings.</summary>
    public static string NewId() => Guid.NewGuid().ToString("n")[..12];

    /// <summary>
    /// This instance's value for <paramref name="key"/>, read through the type's schema and falling
    /// back to the declared default when absent, unparseable or out of range. Returns "" when the
    /// schema does not declare the key at all. Never throws.
    /// </summary>
    public string ReadSetting(ComponentManifest type, string key)
    {
        var field = type.SettingsSchema?.FirstOrDefault(f => f.Key == key);
        if (field is null) return "";
        Settings.TryGetValue(key, out var raw);
        field.TryRead(raw, out var value);
        return value;
    }

    /// <summary>A deep copy. Required because <see cref="Settings"/> is mutable reference state and
    /// <c>BevelSettings.CopyFrom</c> would otherwise alias it between snapshots.</summary>
    public ComponentInstance Clone()
        => this with { Settings = new Dictionary<string, string>(Settings) };
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Bevel.Core.Tests/Bevel.Core.Tests.csproj --filter ComponentInstanceTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Bevel.Core/Components/ComponentInstance.cs tests/Bevel.Core.Tests/ComponentInstanceTests.cs
git commit -m "feat(components): per-instance settings read through the type's schema"
```

---

### Task 4: Normalize a persisted list — repair what is already on disk

Add-time rules do not protect data that is already on disk. This task owns Review Focus 1 and 4.

**Files:**
- Create: `src/Bevel.Core/Components/ComponentListNormalizer.cs`
- Test: `tests/Bevel.Core.Tests/ComponentListNormalizerTests.cs`

**Interfaces:**
- Consumes: `ComponentInstance` (Task 3), `ComponentManifest` (Task 2).
- Produces: `ComponentListNormalizer.Normalize(IReadOnlyList<ComponentInstance>, Func<string, ComponentManifest?> resolve) → NormalizedList` with `Instances`, `EffectiveSizing` (instanceId → the sizing to lay out with, after one-greedy demotion) and `Repairs`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Bevel.Core.Tests/ComponentListNormalizerTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using Bevel.Core.Components;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>
/// bevel-aqr7 Task 4: a persisted list may have been hand-edited or written by another version.
/// Normalizing REPAIRS it; it never throws and never drops the whole bar.
/// </summary>
public class ComponentListNormalizerTests
{
    private static ComponentManifest Type(string id, bool multi, ComponentSizing sizing = ComponentSizing.Content) =>
        new(id, 1, id, "", multi, sizing, null,
            Array.Empty<ComponentSettingsField>(),
            new ComponentPrimitive[] { new LabelPrimitive("l", "x") });

    private static readonly Dictionary<string, ComponentManifest> Types = new()
    {
        ["run.bevel.start"] = Type("run.bevel.start", multi: false),
        ["run.bevel.clock"] = Type("run.bevel.clock", multi: true),
        ["run.bevel.spacer"] = Type("run.bevel.spacer", multi: true),
        ["run.bevel.strip"] = Type("run.bevel.strip", multi: true, ComponentSizing.Greedy),
        ["run.bevel.strip2"] = Type("run.bevel.strip2", multi: true, ComponentSizing.Greedy),
    };

    private static ComponentManifest? Resolve(string id) => Types.GetValueOrDefault(id);

    private static ComponentInstance I(string instanceId, string typeId)
        => new(instanceId, typeId, new Dictionary<string, string>(), true);

    // Review Focus 1
    [Fact]
    public void A_second_instance_of_a_single_instance_type_is_dropped_keeping_the_first()
    {
        var r = ComponentListNormalizer.Normalize(
            new[] { I("a", "run.bevel.start"), I("b", "run.bevel.start"), I("c", "run.bevel.clock") },
            Resolve);

        Assert.Equal(new[] { "a", "c" }, r.Instances.Select(x => x.InstanceId));
        Assert.Contains(r.Repairs, m => m.Contains("run.bevel.start"));
    }

    // Review Focus 4
    [Fact]
    public void A_duplicate_instance_id_is_reassigned_not_dropped()
    {
        var r = ComponentListNormalizer.Normalize(
            new[] { I("dup", "run.bevel.clock"), I("dup", "run.bevel.clock") }, Resolve);

        Assert.Equal(2, r.Instances.Count);
        Assert.Equal(2, r.Instances.Select(x => x.InstanceId).Distinct().Count());
        Assert.Contains(r.Repairs, m => m.Contains("instanceId"));
    }

    [Fact]
    public void An_unknown_type_keeps_its_slot_and_its_order()
    {
        var r = ComponentListNormalizer.Normalize(
            new[] { I("a", "run.bevel.clock"), I("b", "com.example.gone"), I("c", "run.bevel.spacer") },
            Resolve);

        Assert.Equal(new[] { "a", "b", "c" }, r.Instances.Select(x => x.InstanceId));
    }

    // Spec §4.3 and the Acceptance criteria: at most one greedy component per list, validated at LOAD.
    // Two greedy children would split slack between them and silently break every arrangement.
    [Fact]
    public void A_second_greedy_component_is_demoted_to_content_keeping_the_first_greedy()
    {
        var r = ComponentListNormalizer.Normalize(
            new[] { I("a", "run.bevel.strip"), I("b", "run.bevel.strip2"), I("c", "run.bevel.clock") },
            Resolve);

        Assert.Equal(ComponentSizing.Greedy, r.EffectiveSizing["a"]);
        Assert.Equal(ComponentSizing.Content, r.EffectiveSizing["b"]);
        Assert.Contains(r.Repairs, m => m.Contains("greedy"));
    }

    [Fact]
    public void A_spacer_counts_as_greedy_for_that_rule_only_when_the_manifest_says_so()
    {
        var r = ComponentListNormalizer.Normalize(
            new[] { I("a", "run.bevel.spacer"), I("b", "run.bevel.clock") }, Resolve);
        Assert.Equal(ComponentSizing.Content, r.EffectiveSizing["a"]);   // this spacer type is Content
    }

    [Fact]
    public void An_empty_list_normalizes_to_empty_without_throwing()
        => Assert.Empty(ComponentListNormalizer.Normalize(Array.Empty<ComponentInstance>(), Resolve).Instances);

    [Fact]
    public void A_null_list_normalizes_to_empty_without_throwing()
        => Assert.Empty(ComponentListNormalizer.Normalize(null!, Resolve).Instances);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Bevel.Core.Tests/Bevel.Core.Tests.csproj --filter ComponentListNormalizerTests`
Expected: FAIL — `ComponentListNormalizer` not defined.

- [ ] **Step 3: Implement**

Create `src/Bevel.Core/Components/ComponentListNormalizer.cs`:

```csharp
namespace Bevel.Core.Components;

/// <summary>
/// A repaired list, the sizing each surviving instance should actually be laid out with, and a
/// human-readable note per repair for logging. <paramref name="EffectiveSizing"/> is keyed by
/// instanceId and may differ from the manifest's declared sizing where a rule forced a demotion.
/// </summary>
public sealed record NormalizedList(
    IReadOnlyList<ComponentInstance> Instances,
    IReadOnlyDictionary<string, ComponentSizing> EffectiveSizing,
    IReadOnlyList<string> Repairs);

/// <summary>
/// Repairs a persisted component list. Settings on disk may have been hand-edited or written by a
/// different version, so add-time rules cannot be assumed. An unknown type KEEPS its slot (it renders
/// inert) so that uninstalling and reinstalling a component does not silently reshuffle the bar.
/// </summary>
public static class ComponentListNormalizer
{
    public static NormalizedList Normalize(
        IReadOnlyList<ComponentInstance> persisted,
        Func<string, ComponentManifest?> resolve)
    {
        var repairs = new List<string>();
        var sizing = new Dictionary<string, ComponentSizing>(StringComparer.Ordinal);
        if (persisted is null || persisted.Count == 0)
            return new NormalizedList(Array.Empty<ComponentInstance>(), sizing, repairs);

        var kept = new List<ComponentInstance>(persisted.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var singletonsSeen = new HashSet<string>(StringComparer.Ordinal);
        var greedyTaken = false;

        foreach (var inst in persisted)
        {
            if (inst is null) continue;
            var type = resolve(inst.TypeId);

            // multiInstance:false — keep the first, drop the rest. An unknown type is not a known
            // singleton, so it is never dropped on this rule.
            if (type is { MultiInstance: false } && !singletonsSeen.Add(inst.TypeId))
            {
                repairs.Add($"dropped a duplicate instance of single-instance type '{inst.TypeId}'");
                continue;
            }

            var instance = inst;
            if (string.IsNullOrWhiteSpace(instance.InstanceId) || !ids.Add(instance.InstanceId))
            {
                var fresh = ComponentInstance.NewId();
                while (!ids.Add(fresh)) fresh = ComponentInstance.NewId();
                repairs.Add($"reassigned a duplicate or empty instanceId for '{instance.TypeId}' to '{fresh}'");
                instance = instance with { InstanceId = fresh };
            }

            // Spec §4.3: at most ONE greedy per list. Two greedy children would split slack between
            // them and silently break every arrangement, so later ones are DEMOTED rather than
            // dropped — the component still renders, it just stops claiming slack.
            var declared = type?.Sizing ?? ComponentSizing.Content;
            if (declared == ComponentSizing.Greedy)
            {
                if (greedyTaken)
                {
                    declared = ComponentSizing.Content;
                    repairs.Add($"demoted a second greedy component '{instance.TypeId}' to content sizing");
                }
                else greedyTaken = true;
            }
            sizing[instance.InstanceId] = declared;

            kept.Add(instance);
        }

        return new NormalizedList(kept, sizing, repairs);
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Bevel.Core.Tests/Bevel.Core.Tests.csproj --filter ComponentListNormalizerTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Bevel.Core/Components/ComponentListNormalizer.cs tests/Bevel.Core.Tests/ComponentListNormalizerTests.cs
git commit -m "feat(components): repair persisted lists — singleton dupes, id collisions, unknown types"
```

---

### Task 5: The ordered-list layout panel

**Files:**
- Create: `src/Bevel.Taskbar/Components/TaskbarComponentsPanel.cs`
- Test: `tests/Bevel.Taskbar.Tests/TaskbarComponentsPanelTests.cs`

**Interfaces:**
- Consumes: `ComponentSizing` (Task 2).
- Produces: `TaskbarComponentsPanel : Panel`; attached properties `TaskbarComponentsPanel.SizingProperty` (`ComponentSizing`) and `TaskbarComponentsPanel.WeightProperty` (`double`, default `1.0`); static `TaskbarComponentsPanel.SetSizing(Control, ComponentSizing)` / `SetWeight(Control, double)`.

Slack is distributed across `Greedy` children by weight. A spacer is simply a child with `Sizing = Greedy` and no content, which is what makes centring an arrangement rather than a setting.

- [ ] **Step 1: Write the failing layout tests**

Create `tests/Bevel.Taskbar.Tests/TaskbarComponentsPanelTests.cs`:

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bevel.Core.Components;
using Bevel.Taskbar.Components;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-aqr7 Task 5: one ordered list, spacer-as-component. Win2000 and Win11 are the same model
/// with different arrangements, so both are golden fixtures here.
/// </summary>
[Collection("TaskbarTheme")]
public class TaskbarComponentsPanelTests
{
    private static Control Child(ComponentSizing sizing, double width, double weight = 1.0)
    {
        var c = new Border { Width = sizing == ComponentSizing.Greedy ? double.NaN : width, Height = 24 };
        TaskbarComponentsPanel.SetSizing(c, sizing);
        TaskbarComponentsPanel.SetWeight(c, weight);
        return c;
    }

    private static TaskbarComponentsPanel Measured(double width, params Control[] children)
    {
        var p = new TaskbarComponentsPanel();
        foreach (var c in children) p.Children.Add(c);
        p.Measure(new Size(width, 30));
        p.Arrange(new Rect(0, 0, width, 30));
        return p;
    }

    [AvaloniaFact]
    public void Win2000_arrangement_gives_all_slack_to_the_greedy_strip()
    {
        var start = Child(ComponentSizing.Fixed, 74);
        var strip = Child(ComponentSizing.Greedy, 0);
        var tray = Child(ComponentSizing.Content, 100);
        Measured(500, start, strip, tray);

        Assert.Equal(0, start.Bounds.X, 1);
        Assert.Equal(74, strip.Bounds.X, 1);
        Assert.Equal(326, strip.Bounds.Width, 1);   // 500 - 74 - 100
        Assert.Equal(400, tray.Bounds.X, 1);
    }

    [AvaloniaFact]
    public void Two_equal_spacers_centre_the_group_between_them()
    {
        var left = Child(ComponentSizing.Greedy, 0);
        var start = Child(ComponentSizing.Fixed, 40);
        var strip = Child(ComponentSizing.Content, 60);
        var right = Child(ComponentSizing.Greedy, 0);
        Measured(400, left, start, strip, right);

        Assert.Equal(150, left.Bounds.Width, 1);    // (400 - 100) / 2
        Assert.Equal(150, start.Bounds.X, 1);
        Assert.Equal(150, right.Bounds.Width, 1);
        // the group is centred: equal slack either side
        Assert.Equal(left.Bounds.Width, right.Bounds.Width, 1);
    }

    [AvaloniaFact]
    public void Weight_splits_slack_proportionally()
    {
        var a = Child(ComponentSizing.Greedy, 0, weight: 3);
        var b = Child(ComponentSizing.Greedy, 0, weight: 1);
        Measured(400, a, b);

        Assert.Equal(300, a.Bounds.Width, 1);
        Assert.Equal(100, b.Bounds.Width, 1);
    }

    // Review Focus 5
    [AvaloniaFact]
    public void A_list_of_only_spacers_shares_the_width_without_dividing_by_zero()
    {
        var a = Child(ComponentSizing.Greedy, 0);
        var b = Child(ComponentSizing.Greedy, 0);
        Measured(200, a, b);

        Assert.Equal(100, a.Bounds.Width, 1);
        Assert.Equal(100, b.Bounds.Width, 1);
    }

    [AvaloniaFact]
    public void Zero_weight_greedy_children_do_not_produce_NaN()
    {
        var a = Child(ComponentSizing.Greedy, 0, weight: 0);
        var b = Child(ComponentSizing.Greedy, 0, weight: 0);
        Measured(200, a, b);

        Assert.False(double.IsNaN(a.Bounds.Width));
        Assert.Equal(100, a.Bounds.Width, 1);   // equal split when all weights are zero
    }

    [AvaloniaFact]
    public void Content_wider_than_the_bar_is_clamped_not_negative()
    {
        var wide = Child(ComponentSizing.Content, 500);
        var strip = Child(ComponentSizing.Greedy, 0);
        Measured(200, wide, strip);

        Assert.True(strip.Bounds.Width >= 0);
    }

    [AvaloniaFact]
    public void An_empty_panel_measures_without_throwing()
        => Assert.Equal(0, Measured(300).Children.Count);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter TaskbarComponentsPanelTests`
Expected: FAIL — `TaskbarComponentsPanel` not defined.

- [ ] **Step 3: Implement the panel**

Create `src/Bevel.Taskbar/Components/TaskbarComponentsPanel.cs`:

```csharp
using Avalonia;
using Avalonia.Controls;
using Bevel.Core.Components;

namespace Bevel.Taskbar.Components;

/// <summary>
/// Lays out one bar's component instances as a single ordered list. Slack goes to <see
/// cref="ComponentSizing.Greedy"/> children, split by weight — so a spacer is just a greedy child
/// with no content, and centring is an ARRANGEMENT rather than a setting (spec §4.1).
/// </summary>
public class TaskbarComponentsPanel : Panel
{
    public static readonly AttachedProperty<ComponentSizing> SizingProperty =
        AvaloniaProperty.RegisterAttached<TaskbarComponentsPanel, Control, ComponentSizing>(
            "Sizing", ComponentSizing.Content);

    public static readonly AttachedProperty<double> WeightProperty =
        AvaloniaProperty.RegisterAttached<TaskbarComponentsPanel, Control, double>("Weight", 1.0);

    public static void SetSizing(Control c, ComponentSizing v) => c.SetValue(SizingProperty, v);
    public static ComponentSizing GetSizing(Control c) => c.GetValue(SizingProperty);
    public static void SetWeight(Control c, double v) => c.SetValue(WeightProperty, v);
    public static double GetWeight(Control c) => c.GetValue(WeightProperty);

    protected override Size MeasureOverride(Size available)
    {
        var height = 0.0;
        var fixedWidth = 0.0;

        foreach (var child in Children)
        {
            if (GetSizing(child) == ComponentSizing.Greedy)
            {
                // Measured at zero width: a greedy child's width comes from slack, not from content.
                child.Measure(new Size(0, available.Height));
            }
            else
            {
                child.Measure(new Size(double.PositiveInfinity, available.Height));
                fixedWidth += child.DesiredSize.Width;
            }
            height = Math.Max(height, child.DesiredSize.Height);
        }

        var width = double.IsInfinity(available.Width) ? fixedWidth : available.Width;
        return new Size(width, double.IsInfinity(available.Height) ? height : available.Height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var greedy = new List<Control>();
        var totalWeight = 0.0;
        var fixedWidth = 0.0;

        foreach (var child in Children)
        {
            if (GetSizing(child) == ComponentSizing.Greedy)
            {
                greedy.Add(child);
                totalWeight += Math.Max(0, GetWeight(child));
            }
            else fixedWidth += child.DesiredSize.Width;
        }

        // Clamp: content wider than the bar must never produce negative slack.
        var slack = Math.Max(0, final.Width - fixedWidth);

        // All-zero weights (or a spacer-only list) split evenly rather than dividing by zero.
        var even = greedy.Count > 0 && totalWeight <= 0;

        var x = 0.0;
        foreach (var child in Children)
        {
            double w;
            if (GetSizing(child) == ComponentSizing.Greedy)
            {
                w = even
                    ? slack / greedy.Count
                    : slack * (Math.Max(0, GetWeight(child)) / totalWeight);
            }
            else w = child.DesiredSize.Width;

            child.Arrange(new Rect(x, 0, w, final.Height));
            x += w;
        }

        return final;
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter TaskbarComponentsPanelTests`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Bevel.Taskbar/Components/TaskbarComponentsPanel.cs tests/Bevel.Taskbar.Tests/TaskbarComponentsPanelTests.cs
git commit -m "feat(components): ordered-list layout panel — spacer is a component, centring is an arrangement"
```

---

### Task 6: Persist the component list

**Files:**
- Modify: `src/Bevel.Core/SettingsService.cs` (property near line 182; `SetOrPrune` site near 541; load site near 664; `CopyFrom` near 932)
- Modify: `src/Bevel.Core/SettingsJsonContext.cs`
- Test: `tests/Bevel.Core.Tests/ComponentPersistenceTests.cs`

**Interfaces:**
- Consumes: `ComponentInstance` (Task 3).
- Produces: `BevelSettings.TaskbarComponents` (`ComponentInstance[]`), persisted under the raw key `taskbarComponents`.

`CopyFrom` copies properties by **reflection**, skipping `string[]` and hand-cloning `TaskbarStacks`. `ComponentInstance[]` is also mutable reference state, so it needs the same explicit treatment or snapshots will alias each other's settings dictionaries.

- [ ] **Step 1: Write the failing tests**

Create `tests/Bevel.Core.Tests/ComponentPersistenceTests.cs`:

```csharp
using System.Collections.Generic;
using Bevel.Core;
using Bevel.Core.Components;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>bevel-aqr7 Task 6: the component list round-trips, and snapshots never alias each other.</summary>
public class ComponentPersistenceTests
{
    private static ComponentInstance Clock(string id, string seconds) =>
        new(id, "run.bevel.clock", new Dictionary<string, string> { ["showSeconds"] = seconds }, true);

    [Fact]
    public void Default_component_list_is_not_null()
        => Assert.NotNull(new BevelSettings().TaskbarComponents);

    [Fact]
    public void CopyFrom_deep_clones_the_instances_so_snapshots_do_not_alias()
    {
        var a = new BevelSettings { TaskbarComponents = new[] { Clock("x", "true") } };
        var b = new BevelSettings();
        b.CopyFrom(a);

        b.TaskbarComponents[0].Settings["showSeconds"] = "false";

        Assert.Equal("true", a.TaskbarComponents[0].Settings["showSeconds"]);
        Assert.NotSame(a.TaskbarComponents, b.TaskbarComponents);
        Assert.NotSame(a.TaskbarComponents[0].Settings, b.TaskbarComponents[0].Settings);
    }

    [Fact]
    public void CopyFrom_copies_the_list_contents()
    {
        var a = new BevelSettings { TaskbarComponents = new[] { Clock("x", "true"), Clock("y", "false") } };
        var b = new BevelSettings();
        b.CopyFrom(a);
        Assert.Equal(2, b.TaskbarComponents.Length);
        Assert.Equal("y", b.TaskbarComponents[1].InstanceId);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Bevel.Core.Tests/Bevel.Core.Tests.csproj --filter ComponentPersistenceTests`
Expected: FAIL — `TaskbarComponents` not defined.

- [ ] **Step 3: Add the property**

In `src/Bevel.Core/SettingsService.cs`, after `TaskbarShowDesktopButton`:

```csharp
    /// <summary>bevel-aqr7: the bar as an ordered list of component instances. Empty means "not yet
    /// migrated" — <c>TaskbarComponentsMigration</c> folds the legacy flat keys on first read.</summary>
    public ComponentInstance[] TaskbarComponents { get; set; } = Array.Empty<ComponentInstance>();
```

Add `using Bevel.Core.Components;` to the file's usings.

- [ ] **Step 4: Register for AOT JSON**

In `src/Bevel.Core/SettingsJsonContext.cs`, above the `partial class` declaration:

```csharp
[JsonSerializable(typeof(Components.ComponentInstance[]))]
[JsonSerializable(typeof(Components.ComponentInstance))]
[JsonSerializable(typeof(Dictionary<string, string>))]
```

- [ ] **Step 5: Wire save, load, and the explicit deep clone**

Save — beside the `taskbarStacks` array handling (~line 541):

```csharp
        if (_settings.TaskbarComponents.Length == 0) _raw.Remove("taskbarComponents");
        else _raw["taskbarComponents"] = JsonSerializer.SerializeToElement(
            _settings.TaskbarComponents, SettingsJsonContext.Default.ComponentInstanceArray);
```

Load — beside `TaskbarStacks` (~line 664):

```csharp
            TaskbarComponents = GetComponentInstances("taskbarComponents") ?? Array.Empty<ComponentInstance>(),
```

Add the getter next to `GetStringArray` (~line 719):

```csharp
    private ComponentInstance[]? GetComponentInstances(string key)
    {
        if (!_raw.TryGetValue(key, out var el)) return null;
        try
        {
            return el.Deserialize(SettingsJsonContext.Default.ComponentInstanceArray);
        }
        catch (JsonException)
        {
            // Corrupt or foreign-shaped value: fall back to "not migrated" rather than failing the
            // whole settings load. A broken component list must never cost the user their settings.
            return null;
        }
    }
```

`CopyFrom` — beside the `TaskbarStacks` clone (~line 932):

```csharp
        TaskbarComponents = Array.ConvertAll(other.TaskbarComponents, i => i.Clone());
```

Exclude it from the reflection loop by widening the existing type guard:

```csharp
            if (p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0
                && p.PropertyType != typeof(string[]) && p.PropertyType != typeof(ComponentInstance[]))
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/Bevel.Core.Tests/Bevel.Core.Tests.csproj --filter ComponentPersistenceTests`
Expected: PASS (3 tests).

- [ ] **Step 7: Run the whole settings suite for regressions**

Run: `dotnet test tests/Bevel.Core.Tests/Bevel.Core.Tests.csproj`
Expected: only the two known failures from Global Constraints.

- [ ] **Step 8: Commit**

```bash
git add src/Bevel.Core/SettingsService.cs src/Bevel.Core/SettingsJsonContext.cs tests/Bevel.Core.Tests/ComponentPersistenceTests.cs
git commit -m "feat(components): persist the component list with an explicit deep clone in CopyFrom"
```

---

### Task 7: Migrate the 29 legacy keys

**Files:**
- Create: `src/Bevel.Core/Components/TaskbarComponentsMigration.cs`
- Test: `tests/Bevel.Core.Tests/TaskbarComponentsMigrationTests.cs`

**Interfaces:**
- Consumes: `BevelSettings` (Task 6), `ComponentInstance` (Task 3).
- Produces: `TaskbarComponentsMigration.BuildDefaultList(BevelSettings) → ComponentInstance[]`, and the type-id constants `TaskbarComponentTypes.Start`, `.WindowStrip`, `.Stack`, `.Tray`, `.Clock`, `.ShowDesktop`, `.Spacer`.

Legacy keys stay readable for one release: migration **reads** them and does not delete them.

- [ ] **Step 1: Write the failing tests**

Create `tests/Bevel.Core.Tests/TaskbarComponentsMigrationTests.cs`:

```csharp
using System.Linq;
using Bevel.Core;
using Bevel.Core.Components;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>
/// bevel-aqr7 Task 7: fold the 29 legacy flat keys into an ordered instance list. Show* bools become
/// visible:false rather than removal, so hiding a component preserves its configuration.
/// </summary>
public class TaskbarComponentsMigrationTests
{
    [Fact]
    public void Default_settings_produce_the_Win2000_arrangement_in_order()
    {
        var list = TaskbarComponentsMigration.BuildDefaultList(new BevelSettings());
        Assert.Equal(
            new[]
            {
                TaskbarComponentTypes.Start,
                TaskbarComponentTypes.WindowStrip,
                TaskbarComponentTypes.Stack,
                TaskbarComponentTypes.Tray,
                TaskbarComponentTypes.Clock,
            },
            list.Where(i => i.TypeId != TaskbarComponentTypes.ShowDesktop).Select(i => i.TypeId));
    }

    [Fact]
    public void One_stack_instance_is_created_per_configured_folder()
    {
        var s = new BevelSettings { TaskbarStacks = new[] { "/a", "/b", "/c" } };
        var stacks = TaskbarComponentsMigration.BuildDefaultList(s)
            .Where(i => i.TypeId == TaskbarComponentTypes.Stack).ToArray();

        Assert.Equal(3, stacks.Length);
        Assert.Equal(new[] { "/a", "/b", "/c" }, stacks.Select(i => i.Settings["folder"]));
        Assert.Equal(3, stacks.Select(i => i.InstanceId).Distinct().Count());
    }

    [Fact]
    public void ShowClock_false_hides_the_clock_instead_of_removing_it()
    {
        var list = TaskbarComponentsMigration.BuildDefaultList(new BevelSettings { TaskbarShowClock = false });
        var clock = Assert.Single(list.Where(i => i.TypeId == TaskbarComponentTypes.Clock));
        Assert.False(clock.Visible);
    }

    [Fact]
    public void Clock_settings_move_onto_the_clock_instance()
    {
        var s = new BevelSettings
        {
            TaskbarClockShowSeconds = true, TaskbarClockShowDate = true, TaskbarClock24Hour = false,
        };
        var clock = TaskbarComponentsMigration.BuildDefaultList(s)
            .Single(i => i.TypeId == TaskbarComponentTypes.Clock);

        Assert.Equal("true", clock.Settings["showSeconds"]);
        Assert.Equal("true", clock.Settings["showDate"]);
        Assert.Equal("false", clock.Settings["use24Hour"]);
    }

    [Fact]
    public void Window_strip_settings_include_windowlessAppsLast()
    {
        var s = new BevelSettings { WindowlessAppsLast = true, TaskbarMinButtonWidth = 55 };
        var strip = TaskbarComponentsMigration.BuildDefaultList(s)
            .Single(i => i.TypeId == TaskbarComponentTypes.WindowStrip);

        Assert.Equal("true", strip.Settings["windowlessAppsLast"]);
        Assert.Equal("55", strip.Settings["minButtonWidth"]);
    }

    [Fact]
    public void The_window_strip_is_the_only_greedy_participant_by_construction()
    {
        var list = TaskbarComponentsMigration.BuildDefaultList(new BevelSettings());
        Assert.Single(list.Where(i => i.TypeId == TaskbarComponentTypes.WindowStrip));
        Assert.DoesNotContain(TaskbarComponentTypes.Spacer, list.Select(i => i.TypeId));
    }

    [Fact]
    public void Migration_does_not_mutate_the_legacy_keys()
    {
        var s = new BevelSettings { TaskbarShowClock = false, TaskbarStacks = new[] { "/a" } };
        TaskbarComponentsMigration.BuildDefaultList(s);
        Assert.False(s.TaskbarShowClock);                 // still readable for one release
        Assert.Equal(new[] { "/a" }, s.TaskbarStacks);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Bevel.Core.Tests/Bevel.Core.Tests.csproj --filter TaskbarComponentsMigrationTests`
Expected: FAIL — `TaskbarComponentsMigration` not defined.

- [ ] **Step 3: Implement**

Create `src/Bevel.Core/Components/TaskbarComponentsMigration.cs`:

```csharp
using System.Globalization;

namespace Bevel.Core.Components;

/// <summary>Stable type-ids for Bevel's own components. Instances persist against these strings.</summary>
public static class TaskbarComponentTypes
{
    public const string Start = "run.bevel.start";
    public const string WindowStrip = "run.bevel.window-strip";
    public const string Stack = "run.bevel.stack";
    public const string Tray = "run.bevel.tray";
    public const string Clock = "run.bevel.clock";
    public const string ShowDesktop = "run.bevel.show-desktop";
    public const string Spacer = "run.bevel.spacer";
}

/// <summary>
/// One-time fold of the legacy flat taskbar keys into an ordered component list (spec §5.2). Reads the
/// legacy keys and leaves them in place: they stay readable for one release so a downgrade does not
/// brick someone's bar. Keys that describe the BAR (rows, locked, opacity, …) stay on BevelSettings,
/// and TaskbarStartMenuFrequentCount stays put because ShellModel consumes it, not the view.
/// </summary>
public static class TaskbarComponentsMigration
{
    private static string B(bool v) => v ? "true" : "false";
    private static string N(int v) => v.ToString(CultureInfo.InvariantCulture);

    public static ComponentInstance[] BuildDefaultList(BevelSettings s)
    {
        var list = new List<ComponentInstance>();

        list.Add(new ComponentInstance(ComponentInstance.NewId(), TaskbarComponentTypes.Start,
            new Dictionary<string, string>
            {
                ["label"] = s.TaskbarStartLabel,
                ["badgeFullDetail"] = B(s.StartBadgeFullDetail),
            },
            Visible: s.TaskbarShowStart));

        list.Add(new ComponentInstance(ComponentInstance.NewId(), TaskbarComponentTypes.WindowStrip,
            new Dictionary<string, string>
            {
                ["buttonWidth"] = N(s.TaskbarButtonWidth),
                ["buttonWidthMode"] = s.TaskbarButtonWidthMode.ToString(),
                ["minButtonWidth"] = N(s.TaskbarMinButtonWidth),
                ["grouping"] = s.TaskbarGrouping.ToString(),
                ["buttonLabels"] = s.TaskbarButtonLabels.ToString(),
                ["middleClickCloses"] = B(s.TaskbarMiddleClickCloses),
                ["reclickMinimize"] = s.TaskbarReclickMinimize.ToString(),
                ["windowSort"] = s.TaskbarWindowSort.ToString(),
                ["windowlessAppsLast"] = B(s.WindowlessAppsLast),
            },
            Visible: true));

        foreach (var folder in s.TaskbarStacks)
            list.Add(new ComponentInstance(ComponentInstance.NewId(), TaskbarComponentTypes.Stack,
                new Dictionary<string, string> { ["folder"] = folder }, Visible: true));

        list.Add(new ComponentInstance(ComponentInstance.NewId(), TaskbarComponentTypes.Tray,
            new Dictionary<string, string>
            {
                ["overflowCap"] = N(s.TaskbarTrayOverflowCap),
                ["consolidateMenuBar"] = B(s.TaskbarConsolidateMenuBar),
            },
            Visible: true));

        list.Add(new ComponentInstance(ComponentInstance.NewId(), TaskbarComponentTypes.Clock,
            new Dictionary<string, string>
            {
                ["showSeconds"] = B(s.TaskbarClockShowSeconds),
                ["showDate"] = B(s.TaskbarClockShowDate),
                ["use24Hour"] = B(s.TaskbarClock24Hour),
            },
            Visible: s.TaskbarShowClock));

        list.Add(new ComponentInstance(ComponentInstance.NewId(), TaskbarComponentTypes.ShowDesktop,
            new Dictionary<string, string>(), Visible: s.TaskbarShowDesktopButton));

        return list.ToArray();
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Bevel.Core.Tests/Bevel.Core.Tests.csproj --filter TaskbarComponentsMigrationTests`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Bevel.Core/Components/TaskbarComponentsMigration.cs tests/Bevel.Core.Tests/TaskbarComponentsMigrationTests.cs
git commit -m "feat(components): fold the 29 legacy taskbar keys into an ordered instance list"
```

---

### Task 8: Bar geometry derived from components

Deletes `TaskbarTheme`'s mutable statics. The bar's height **and** the OS work-area claim must come from one composed value (spec §4.4).

**Files:**
- Create: `src/Bevel.Taskbar/Components/BarGeometry.cs`
- Modify: `src/Bevel.Taskbar/TaskbarWindow.cs:495-538` (delete `TaskbarTheme`), and its six `HeightForRows` call sites
- Modify: `src/Bevel.App/App.axaml.cs:338` (remove `TaskbarTheme.Configure`)
- Test: `tests/Bevel.Taskbar.Tests/BarGeometryTests.cs`

**Interfaces:**
- Consumes: `TaskbarButtonSize` (existing enum in `Bevel.Core`).
- Produces: `BarGeometry` instance class with constructor `BarGeometry(int rows)`; `Contribute(int heightDip)`; `int ButtonHeight`, `int TaskIconSize`, `int RowHeight`, `int TaskbarHeight`, `int Height`; `static int ButtonHeightFor(TaskbarButtonSize)`, `static int TaskIconSizeFor(TaskbarButtonSize)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Bevel.Taskbar.Tests/BarGeometryTests.cs`:

```csharp
using Bevel.Core;
using Bevel.Taskbar.Components;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-aqr7 Task 8: bar height is COMPOSED from component contributions, not read from a global
/// static. Two bars must be able to differ, which bevel-tjr2 (multi-monitor) requires.
/// </summary>
public class BarGeometryTests
{
    [Fact]
    public void Classic_tier_reproduces_the_Win2000_metrics_exactly()
    {
        var g = new BarGeometry(rows: 1);
        g.Contribute(BarGeometry.ButtonHeightFor(TaskbarButtonSize.Normal));
        Assert.Equal(24, g.ButtonHeight);
        Assert.Equal(28, g.RowHeight);
        Assert.Equal(30, g.TaskbarHeight);
        Assert.Equal(30, g.Height);
    }

    [Theory]
    [InlineData(TaskbarButtonSize.Small, 18, 22, 24)]
    [InlineData(TaskbarButtonSize.Normal, 24, 28, 30)]
    [InlineData(TaskbarButtonSize.Large, 30, 34, 36)]
    [InlineData(TaskbarButtonSize.Big, 40, 44, 46)]
    public void Every_tier_keeps_its_documented_metrics(TaskbarButtonSize size, int btn, int row, int bar)
    {
        var g = new BarGeometry(rows: 1);
        g.Contribute(BarGeometry.ButtonHeightFor(size));
        Assert.Equal(btn, g.ButtonHeight);
        Assert.Equal(row, g.RowHeight);
        Assert.Equal(bar, g.TaskbarHeight);
    }

    [Fact]
    public void The_tallest_contribution_wins()
    {
        var g = new BarGeometry(rows: 1);
        g.Contribute(18);
        g.Contribute(40);
        g.Contribute(24);
        Assert.Equal(40, g.ButtonHeight);
    }

    [Fact]
    public void Extra_rows_add_one_row_height_each()
    {
        var g = new BarGeometry(rows: 3);
        g.Contribute(24);
        Assert.Equal(30 + 28 + 28, g.Height);
    }

    [Fact]
    public void A_bar_with_no_components_still_has_a_usable_height()
    {
        var g = new BarGeometry(rows: 1);
        Assert.Equal(30, g.Height);   // falls back to the Normal tier rather than collapsing to zero
    }

    [Fact]
    public void Two_bars_hold_independent_geometry()
    {
        var a = new BarGeometry(rows: 1); a.Contribute(40);
        var b = new BarGeometry(rows: 1); b.Contribute(18);
        Assert.Equal(40, a.ButtonHeight);
        Assert.Equal(18, b.ButtonHeight);
    }

    [Fact]
    public void Rows_below_one_are_clamped()
        => Assert.Equal(30, new BarGeometry(rows: 0).Height);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter BarGeometryTests`
Expected: FAIL — `BarGeometry` not defined.

- [ ] **Step 3: Implement `BarGeometry`**

Create `src/Bevel.Taskbar/Components/BarGeometry.cs`:

```csharp
using Bevel.Core;

namespace Bevel.Taskbar.Components;

/// <summary>
/// One bar's composed geometry. Replaces <c>TaskbarTheme</c>'s mutable statics: height is MEASURED
/// from the components actually on this bar, and the same composed value drives both the window
/// height and the OS work-area claim — one derivation, not two (spec §4.4). Per-bar rather than
/// static because two displays may carry different components (bevel-tjr2).
/// </summary>
public sealed class BarGeometry
{
    /// <summary>Win2000 classic default, used when no component has contributed.</summary>
    private const int DefaultButtonHeight = 24;

    private readonly int _rows;
    private int _buttonHeight = DefaultButtonHeight;
    private int _iconSize = 16;

    public BarGeometry(int rows) => _rows = Math.Max(1, rows);

    /// <summary>Window-button height in logical px: the tallest contribution on this bar.</summary>
    public int ButtonHeight => _buttonHeight;

    /// <summary>Task-button glyph edge in logical px, derived from the winning button height.</summary>
    public int TaskIconSize => _iconSize;

    /// <summary>Height per button row: the button plus its 4px (2+2) vertical margin.</summary>
    public int RowHeight => ButtonHeight + 4;

    /// <summary>Single-row bar height in logical px: one row plus the 2px chrome inset.</summary>
    public int TaskbarHeight => RowHeight + 2;

    /// <summary>Total bar height for this bar's row count.</summary>
    public int Height => TaskbarHeight + (_rows - 1) * RowHeight;

    /// <summary>
    /// Records one component's desired button height. The tallest wins, so a single Big-tier strip
    /// grows the bar while a clock asking for 16 does not shrink it.
    /// </summary>
    public void Contribute(int heightDip)
    {
        if (heightDip <= 0) return;
        if (heightDip <= _buttonHeight) return;
        _buttonHeight = heightDip;
        _iconSize = IconSizeForButtonHeight(heightDip);
    }

    /// <summary>Button height for a user-facing size tier. Normal reproduces Win2000's 24/28/30.</summary>
    public static int ButtonHeightFor(TaskbarButtonSize size) => size switch
    {
        TaskbarButtonSize.Small => 18,
        TaskbarButtonSize.Large => 30,
        TaskbarButtonSize.Big => 40,
        _ => DefaultButtonHeight,
    };

    /// <summary>Glyph edge for a size tier. Icons arrive at 64px, so every value is a downscale.</summary>
    public static int TaskIconSizeFor(TaskbarButtonSize size) => size switch
    {
        TaskbarButtonSize.Large => 24,
        TaskbarButtonSize.Big => 32,
        _ => 16,
    };

    private static int IconSizeForButtonHeight(int h) => h switch
    {
        >= 40 => 32,
        >= 30 => 24,
        _ => 16,
    };
}
```

- [ ] **Step 4: Run the geometry tests**

`dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter BarGeometryTests`
Expected: PASS (7 tests + 4 theory cases).

- [ ] **Step 5: Replace `TaskbarTheme` at every call site**

Delete the `public static class TaskbarTheme` block (`src/Bevel.Taskbar/TaskbarWindow.cs`, ~lines 493–538).

In `TaskbarWindow`, add a field and replace all six `TaskbarTheme.HeightForRows(_rows)` calls:

```csharp
    private BarGeometry _geometry = new(1);

    /// <summary>This bar's composed geometry. Rebuilt whenever rows or component sizing change.</summary>
    internal BarGeometry Geometry => _geometry;
```

Each `var h = TaskbarTheme.HeightForRows(_rows);` becomes `var h = _geometry.Height;`.

In `MaxRows`, replace `TaskbarTheme.TaskbarHeight` / `TaskbarTheme.RowHeight`:

```csharp
            var cap = (int)((heightPts * 0.4 - _geometry.TaskbarHeight) / _geometry.RowHeight) + 1;
```

In `SetRows`, rebuild geometry before using it:

```csharp
        _rows = clamped;
        _geometry = new BarGeometry(_rows);
        foreach (var c in _contributors) _geometry.Contribute(c);
        var h = _geometry.Height;
```

Add the contributor list beside `_geometry`:

```csharp
    private readonly List<int> _contributors = new();

    /// <summary>Records a component's height contribution and re-derives the bar's geometry.</summary>
    internal void ContributeHeight(int heightDip)
    {
        _contributors.Add(heightDip);
        _geometry.Contribute(heightDip);
    }
```

- [ ] **Step 6: Remove the startup `Configure` call**

In `src/Bevel.App/App.axaml.cs`, delete line 338:

```csharp
        Taskbar.TaskbarTheme.Configure(settings.Current.TaskbarButtonSize);
```

- [ ] **Step 7: Build and run the full taskbar suite**

Run: `dotnet build Bevel.sln -clp:ErrorsOnly`
Expected: 0 errors. Any remaining `TaskbarTheme` reference is a compile error — fix each by reading the bar's `Geometry`.

Run: `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj`
Expected: all pass. Work-area assertions are the ones to watch — `bevel-kbx8` exists because this number is load-bearing.

- [ ] **Step 8: Commit**

```bash
git add src/Bevel.Taskbar/Components/BarGeometry.cs src/Bevel.Taskbar/TaskbarWindow.cs src/Bevel.App/App.axaml.cs tests/Bevel.Taskbar.Tests/BarGeometryTests.cs
git commit -m "refactor(components): derive bar height from components, deleting TaskbarTheme's mutable statics"
```

---

### Task 9: The registry, and the component channel seam

**Files:**
- Create: `src/Bevel.Taskbar/Components/ComponentRegistry.cs`
- Create: `src/Bevel.Taskbar/Components/IComponentChannel.cs`
- Test: `tests/Bevel.Taskbar.Tests/ComponentRegistryTests.cs`

**Interfaces:**
- Consumes: `ComponentManifest`, `ManifestValidator` (Task 2).
- Produces: `IComponentChannel` with `Task<ComponentState> ConnectAsync(ComponentInstance, CancellationToken)`, `event Action<ComponentState>? StateChanged`, `Task SendAsync(ComponentInput, CancellationToken)`, `ValueTask DisposeAsync()`; `ComponentState(string InstanceId, IReadOnlyDictionary<string,string> Values, bool Inert)`; `ComponentInput(string InstanceId, string PrimitiveKey, string Kind)`; `ComponentRegistry.Register(ComponentManifest, Func<ComponentInstance, IComponentChannel>)`, `TryResolve(string typeId, out ComponentManifest)`, `CreateChannel(ComponentInstance)`, `IReadOnlyCollection<ComponentManifest> Manifests`.

The same interface backs both built-ins and third-party components. That is what makes the conformance suite (Task 13) able to run identical assertions against both paths.

- [ ] **Step 1: Write the failing tests**

Create `tests/Bevel.Taskbar.Tests/ComponentRegistryTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Bevel.Core.Components;
using Bevel.Taskbar.Components;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>bevel-aqr7 Task 9: the registry refuses invalid and unavailable types without throwing.</summary>
public class ComponentRegistryTests
{
    private static ComponentManifest Type(string id, string? capability = null) =>
        new(id, 1, id, "", true, ComponentSizing.Content, capability,
            Array.Empty<ComponentSettingsField>(),
            new ComponentPrimitive[] { new LabelPrimitive("l", "x") });

    private static ComponentRegistry Registry(params string[] capabilities)
        => new(new HashSet<string>(capabilities, StringComparer.Ordinal));

    [Fact]
    public void A_registered_type_resolves_by_id()
    {
        var r = Registry();
        r.Register(Type("run.bevel.clock"), _ => new FakeChannel());
        Assert.True(r.TryResolve("run.bevel.clock", out var m));
        Assert.Equal("run.bevel.clock", m.Id);
    }

    [Fact]
    public void An_unregistered_id_does_not_resolve_and_does_not_throw()
        => Assert.False(Registry().TryResolve("com.example.nope", out _));

    [Fact]
    public void An_invalid_manifest_is_refused_at_registration()
    {
        var r = Registry();
        Assert.False(r.Register(Type("run.bevel.bad") with { ContractVersion = 99 }, _ => new FakeChannel()));
        Assert.False(r.TryResolve("run.bevel.bad", out _));
    }

    [Fact]
    public void A_type_whose_capability_is_unavailable_is_absent_rather_than_broken()
    {
        var r = Registry("audio.endpoint");
        Assert.True(r.Register(Type("run.bevel.volume", "audio.endpoint"), _ => new FakeChannel()));
        Assert.False(r.Register(Type("run.bevel.battery", "power.battery"), _ => new FakeChannel()));
        Assert.True(r.TryResolve("run.bevel.volume", out _));
        Assert.False(r.TryResolve("run.bevel.battery", out _));
    }

    [Fact]
    public void Registering_the_same_id_twice_keeps_the_first()
    {
        var r = Registry();
        r.Register(Type("run.bevel.clock"), _ => new FakeChannel());
        Assert.False(r.Register(Type("run.bevel.clock") with { DisplayName = "Second" }, _ => new FakeChannel()));
        r.TryResolve("run.bevel.clock", out var m);
        Assert.Equal("run.bevel.clock", m.DisplayName);
    }

    [Fact]
    public void Manifests_lists_only_what_registered()
    {
        var r = Registry();
        r.Register(Type("a"), _ => new FakeChannel());
        r.Register(Type("b") with { ContractVersion = 99 }, _ => new FakeChannel());
        Assert.Equal(new[] { "a" }, r.Manifests.Select(m => m.Id));
    }

    private sealed class FakeChannel : IComponentChannel
    {
        public event Action<ComponentState>? StateChanged;
        public Task<ComponentState> ConnectAsync(ComponentInstance i, CancellationToken ct)
        {
            StateChanged?.Invoke(new ComponentState(i.InstanceId, new Dictionary<string, string>(), false));
            return Task.FromResult(new ComponentState(i.InstanceId, new Dictionary<string, string>(), false));
        }
        public Task SendAsync(ComponentInput input, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter ComponentRegistryTests`
Expected: FAIL — `ComponentRegistry` not defined.

- [ ] **Step 3: Define the channel seam**

Create `src/Bevel.Taskbar/Components/IComponentChannel.cs`:

```csharp
using Bevel.Core.Components;

namespace Bevel.Taskbar.Components;

/// <summary>
/// One instance's live state: current values for its view's primitive keys. <paramref name="Inert"/>
/// marks a failed or quarantined component, whose slot renders a placeholder rather than stale content.
/// </summary>
public sealed record ComponentState(string InstanceId, IReadOnlyDictionary<string, string> Values, bool Inert);

/// <summary>A semantic input event the bar forwards to a component. The bar handled the gesture.</summary>
public sealed record ComponentInput(string InstanceId, string PrimitiveKey, string Kind);

/// <summary>
/// The ONE seam both hosts implement: built-ins bind locally, third-party components speak over the
/// component bus. Because the interface is identical, the conformance suite runs the same assertions
/// against both paths — which is what keeps the public contract honest.
/// </summary>
public interface IComponentChannel : IAsyncDisposable
{
    /// <summary>Raised whenever the component publishes new state. May arrive off the UI thread.</summary>
    event Action<ComponentState>? StateChanged;

    /// <summary>Starts the component and returns its first state.</summary>
    Task<ComponentState> ConnectAsync(ComponentInstance instance, CancellationToken ct);

    /// <summary>Forwards a semantic input event.</summary>
    Task SendAsync(ComponentInput input, CancellationToken ct);
}
```

- [ ] **Step 4: Implement the registry**

Create `src/Bevel.Taskbar/Components/ComponentRegistry.cs`:

```csharp
using Bevel.Core.Components;

namespace Bevel.Taskbar.Components;

/// <summary>
/// typeId → manifest + channel factory. Registration VALIDATES, so an invalid or unavailable type is
/// simply absent rather than a broken slot — and a third-party manifest can never take the bar down
/// by registering.
/// </summary>
public sealed class ComponentRegistry
{
    private readonly Dictionary<string, (ComponentManifest Manifest, Func<ComponentInstance, IComponentChannel> Factory)> _types
        = new(StringComparer.Ordinal);
    private readonly IReadOnlySet<string> _capabilities;

    /// <param name="availableCapabilities">PAL capability names this platform reports.</param>
    public ComponentRegistry(IReadOnlySet<string> availableCapabilities) => _capabilities = availableCapabilities;

    /// <summary>Every successfully registered manifest.</summary>
    public IReadOnlyCollection<ComponentManifest> Manifests
        => _types.Values.Select(v => v.Manifest).ToArray();

    /// <summary>
    /// Registers a type. Returns false — without throwing — when the manifest is invalid, when its
    /// required capability is unavailable, or when the id is already taken (first registration wins).
    /// </summary>
    public bool Register(ComponentManifest manifest, Func<ComponentInstance, IComponentChannel> factory)
    {
        if (!ManifestValidator.Validate(manifest).IsValid) return false;
        if (manifest.RequiresCapability is { } cap && !_capabilities.Contains(cap)) return false;
        if (_types.ContainsKey(manifest.Id)) return false;
        _types[manifest.Id] = (manifest, factory);
        return true;
    }

    public bool TryResolve(string typeId, out ComponentManifest manifest)
    {
        if (typeId is not null && _types.TryGetValue(typeId, out var e)) { manifest = e.Manifest; return true; }
        manifest = null!;
        return false;
    }

    /// <summary>Creates a channel for an instance, or null when its type is unknown (slot renders inert).</summary>
    public IComponentChannel? CreateChannel(ComponentInstance instance)
        => _types.TryGetValue(instance.TypeId, out var e) ? e.Factory(instance) : null;
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter ComponentRegistryTests`
Expected: PASS (6 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Bevel.Taskbar/Components/ComponentRegistry.cs src/Bevel.Taskbar/Components/IComponentChannel.cs tests/Bevel.Taskbar.Tests/ComponentRegistryTests.cs
git commit -m "feat(components): registry plus the single channel seam shared by both hosts"
```

---

### Task 10: The component bus — protobuf schema and authenticated transport

**Files:**
- Create: `proto/bevel.components.v1.proto`
- Create: `src/Bevel.ShellCore.Ipc/ComponentBusServer.cs`
- Create: `src/Bevel.ShellCore.Ipc/ComponentBusEndpointFile.cs`
- Test: `tests/Bevel.ShellCore.Ipc.Tests/ComponentBusAuthTests.cs`

**Interfaces:**
- Consumes: `Handshake.ComputeHmac(byte[] nonce, string capability)` (existing, `src/Bevel.ShellCore.Ipc/Handshake.cs`).
- Produces: `ComponentBusServer(byte[] nonce, string capability)` with `int BindLoopback()`, `event Action<string, ComponentEnvelope>? MessageReceived`, `IReadOnlySet<string> AuthenticatedPeers`, `void SendTo(string identity, byte[] payload)`, `bool WaitForPeer(string, TimeSpan)`, `Dispose()`; `ComponentBusEndpointFile.Write(string path, int port, byte[] nonce)` / `Read(string path)`.

On Windows NetMQ has no `ipc://`, so this binds `tcp://127.0.0.1` and the HMAC is the **only** barrier. The endpoint file replaces socket-directory permissions.

- [ ] **Step 1: Write the proto schema**

Create `proto/bevel.components.v1.proto`:

```proto
syntax = "proto3";
package bevel.components.v1;

// The component bus. Separate from the internal shell bus: separate socket, separate nonce, so a
// third-party component that discovers the internal port still cannot reach it.
message ComponentEnvelope {
  oneof payload {
    Hello hello = 1;
    StatePublish state = 2;
    InputEvent input = 3;
    FrameReady frame_ready = 4;
    ThemeTokens theme = 5;
  }
}

// First frame on every connection. Rejected immediately if the hmac does not verify.
message Hello {
  string instance_id = 1;
  string capability = 2;
  string hmac_hex = 3;
  int32 contract_version = 4;
}

message StatePublish {
  string instance_id = 1;
  map<string, string> values = 2;
  bool inert = 3;
}

message InputEvent {
  string instance_id = 1;
  string primitive_key = 2;
  string kind = 3;
}

// Pixels NEVER travel on the bus: this only names the shared-memory slot that now holds frame n.
message FrameReady {
  string instance_id = 1;
  string primitive_key = 2;
  uint32 slot = 3;
  uint64 frame = 4;
  uint32 width = 5;
  uint32 height = 6;
}

// Pushed by the bar so a surface can paint in-skin, and re-pushed on theme or colourway change.
message ThemeTokens {
  map<string, string> argb = 1;
  uint32 revision = 2;
}
```

Wire codegen in `src/Bevel.ShellCore.Ipc/Bevel.ShellCore.Ipc.csproj`:

```xml
<ItemGroup>
  <PackageReference Include="Google.Protobuf" />
  <PackageReference Include="Grpc.Tools" PrivateAssets="all" />
  <PackageReference Include="NetMQ" />
  <Protobuf Include="..\..\proto\bevel.components.v1.proto" GrpcServices="None"
            Link="Proto\bevel.components.v1.proto" />
</ItemGroup>
```

- [ ] **Step 2: Write the failing auth tests**

Create `tests/Bevel.ShellCore.Ipc.Tests/ComponentBusAuthTests.cs`:

```csharp
using System.Text;
using Bevel.ShellCore.Ipc;
using NetMQ;
using NetMQ.Sockets;
using Xunit;

namespace Bevel.ShellCore.Ipc.Tests;

/// <summary>
/// bevel-aqr7 Task 10: on Windows NetMQ has no ipc://, so the bus is loopback TCP and ANY local
/// process can connect. The HMAC is therefore the only barrier, not defence-in-depth: an
/// unauthenticated peer must learn nothing and be dropped.
/// </summary>
public class ComponentBusAuthTests
{
    private static readonly byte[] Nonce = Encoding.UTF8.GetBytes("test-nonce-0123456789");
    private const string Cap = "components";

    [Fact]
    public void A_correct_hmac_authenticates_the_peer()
    {
        using var server = new ComponentBusServer(Nonce, Cap);
        var port = server.BindLoopback();

        using var client = new DealerSocket();
        client.Options.Identity = Encoding.UTF8.GetBytes("peer-1");
        client.Connect($"tcp://127.0.0.1:{port}");
        client.SendFrame(ComponentBusServer.BuildHello("inst-1", Cap, Handshake.ComputeHmac(Nonce, Cap), 1));

        Assert.True(server.WaitForPeer("peer-1", TimeSpan.FromSeconds(5)));
        Assert.Contains("peer-1", server.AuthenticatedPeers);
    }

    [Fact]
    public void A_wrong_hmac_is_rejected_and_discloses_nothing()
    {
        using var server = new ComponentBusServer(Nonce, Cap);
        var port = server.BindLoopback();

        using var client = new DealerSocket();
        client.Options.Identity = Encoding.UTF8.GetBytes("attacker");
        client.Connect($"tcp://127.0.0.1:{port}");
        client.SendFrame(ComponentBusServer.BuildHello("inst-1", Cap, "deadbeef", 1));

        Assert.False(server.WaitForPeer("attacker", TimeSpan.FromSeconds(1)));
        Assert.DoesNotContain("attacker", server.AuthenticatedPeers);
        Assert.False(client.TryReceiveFrameBytes(TimeSpan.FromMilliseconds(300), out _));
    }

    [Fact]
    public void A_non_hello_first_frame_is_rejected()
    {
        using var server = new ComponentBusServer(Nonce, Cap);
        var port = server.BindLoopback();

        using var client = new DealerSocket();
        client.Options.Identity = Encoding.UTF8.GetBytes("rude");
        client.Connect($"tcp://127.0.0.1:{port}");
        client.SendFrame(ComponentBusServer.BuildState("inst-1"));

        Assert.False(server.WaitForPeer("rude", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void The_two_buses_use_different_nonces_so_a_component_nonce_does_not_open_the_internal_bus()
    {
        var internalNonce = Encoding.UTF8.GetBytes("internal-nonce-xxxxx");
        Assert.NotEqual(Handshake.ComputeHmac(Nonce, Cap), Handshake.ComputeHmac(internalNonce, Cap));
    }

    [Fact]
    public void The_endpoint_file_round_trips_port_and_nonce()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bevel-bus-{Guid.NewGuid():n}.json");
        try
        {
            ComponentBusEndpointFile.Write(path, 54321, Nonce);
            var (port, nonce) = ComponentBusEndpointFile.Read(path);
            Assert.Equal(54321, port);
            Assert.Equal(Nonce, nonce);
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/Bevel.ShellCore.Ipc.Tests/Bevel.ShellCore.Ipc.Tests.csproj --filter ComponentBusAuthTests`
Expected: FAIL — `ComponentBusServer` not defined.

- [ ] **Step 4: Implement the endpoint file**

Create `src/Bevel.ShellCore.Ipc/ComponentBusEndpointFile.cs`:

```csharp
using System.Text.Json;

namespace Bevel.ShellCore.Ipc;

/// <summary>
/// Where a component learns the bus port and nonce. This file is the REPLACEMENT for socket-directory
/// permissions: NetMQ has no ipc:// on Windows, so the bus is loopback TCP with no filesystem gate of
/// its own. It is written user-only, and on Unix with 0600.
/// </summary>
public static class ComponentBusEndpointFile
{
    private sealed record Endpoint(int Port, string NonceBase64);

    public static void Write(string path, int port, byte[] nonce)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(new Endpoint(port, Convert.ToBase64String(nonce)));
        File.WriteAllText(path, json);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        // On Windows the file inherits the user profile's user-only ACL — the same "0700 equivalent"
        // BevelRuntimeDir documents for the existing sockets.
    }

    public static (int Port, byte[] Nonce) Read(string path)
    {
        var e = JsonSerializer.Deserialize<Endpoint>(File.ReadAllText(path))
                ?? throw new InvalidDataException($"empty component-bus endpoint file: {path}");
        return (e.Port, Convert.FromBase64String(e.NonceBase64));
    }
}
```

- [ ] **Step 5: Implement the bus server**

Create `src/Bevel.ShellCore.Ipc/ComponentBusServer.cs`:

```csharp
using System.Collections.Concurrent;
using System.Text;
using Bevel.Components.V1;
using Google.Protobuf;
using NetMQ;
using NetMQ.Sockets;

namespace Bevel.ShellCore.Ipc;

/// <summary>
/// The component bus. Loopback TCP, because NetMQ has no ipc:// on Windows — which makes the HMAC the
/// ONLY barrier rather than the second one, so a peer that fails it learns nothing and is dropped
/// before any state is disclosed.
/// </summary>
public sealed class ComponentBusServer : IDisposable
{
    private readonly byte[] _nonce;
    private readonly string _capability;
    private readonly ConcurrentDictionary<string, string> _authenticated = new(StringComparer.Ordinal);

    // ALL socket I/O happens on the poller thread. NetMQ sockets are not thread-safe, so outbound
    // sends are posted through a NetMQQueue rather than touching the socket from a caller's thread —
    // getting this wrong yields intermittent frame corruption that is miserable to diagnose later.
    private readonly NetMQQueue<(string Identity, byte[] Payload)> _outbound = new();
    private readonly RouterSocket _socket = new();
    private readonly NetMQPoller _poller;
    private int _port;

    public ComponentBusServer(byte[] nonce, string capability)
    {
        _nonce = nonce;
        _capability = capability;
        _poller = new NetMQPoller { _socket, _outbound };
        _socket.ReceiveReady += OnReceiveReady;
        _outbound.ReceiveReady += OnSendReady;
    }

    /// <summary>Peer identities that have passed the handshake.</summary>
    public IReadOnlySet<string> AuthenticatedPeers => _authenticated.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>Raised for authenticated peers only. Never fires for an unauthenticated connection.</summary>
    public event Action<string, ComponentEnvelope>? MessageReceived;

    /// <summary>Binds an EPHEMERAL loopback port and starts the poller. Returns the chosen port.</summary>
    public int BindLoopback()
    {
        _port = _socket.BindRandomPort("tcp://127.0.0.1");
        _poller.RunAsync();
        return _port;
    }

    /// <summary>Queues an envelope for one peer. Safe to call from any thread.</summary>
    public void SendTo(string identity, byte[] payload) => _outbound.Enqueue((identity, payload));

    private void OnSendReady(object? sender, NetMQQueueEventArgs<(string Identity, byte[] Payload)> e)
    {
        while (e.Queue.TryDequeue(out var item, TimeSpan.Zero))
            _socket.SendMoreFrame(Encoding.UTF8.GetBytes(item.Identity)).SendFrame(item.Payload);
    }

    private void OnReceiveReady(object? sender, NetMQSocketEventArgs e)
    {
        if (!e.Socket.TryReceiveFrameBytes(out var identityBytes)) return;
        if (!e.Socket.TryReceiveFrameBytes(out var payload)) return;

        var identity = Encoding.UTF8.GetString(identityBytes!);
        ComponentEnvelope env;
        try { env = ComponentEnvelope.Parser.ParseFrom(payload); }
        catch (InvalidProtocolBufferException) { return; }   // malformed: drop, never throw

        if (!_authenticated.ContainsKey(identity))
        {
            // First frame MUST be a valid Hello. Anything else is dropped with NO reply, so an
            // unauthenticated peer learns nothing — on Windows this is loopback TCP and any local
            // process can connect, which makes the HMAC the only barrier rather than the second one.
            if (env.PayloadCase != ComponentEnvelope.PayloadOneofCase.Hello) return;
            if (!FixedTimeEquals(env.Hello.HmacHex, Handshake.ComputeHmac(_nonce, _capability))) return;
            if (env.Hello.ContractVersion < 1) return;
            _authenticated[identity] = env.Hello.InstanceId;
            return;
        }

        MessageReceived?.Invoke(identity, env);
    }

    /// <summary>Compares two lowercase hex strings without an early-exit on first difference.</summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        if (a is null || b is null || a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    /// <summary>Blocks until <paramref name="identity"/> authenticates, or the timeout elapses. Tests only.</summary>
    public bool WaitForPeer(string identity, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_authenticated.ContainsKey(identity)) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    public static byte[] BuildHello(string instanceId, string capability, string hmacHex, int contractVersion)
        => new ComponentEnvelope
        {
            Hello = new Hello
            {
                InstanceId = instanceId, Capability = capability,
                HmacHex = hmacHex, ContractVersion = contractVersion,
            },
        }.ToByteArray();

    public static byte[] BuildState(string instanceId)
        => new ComponentEnvelope { State = new StatePublish { InstanceId = instanceId } }.ToByteArray();

    public void Dispose()
    {
        if (_poller.IsRunning) _poller.Stop();
        _poller.Dispose();
        _outbound.Dispose();
        _socket.Dispose();
    }
}
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/Bevel.ShellCore.Ipc.Tests/Bevel.ShellCore.Ipc.Tests.csproj --filter ComponentBusAuthTests`
Expected: PASS (5 tests).

- [ ] **Step 7: Commit**

```bash
git add proto/bevel.components.v1.proto src/Bevel.ShellCore.Ipc tests/Bevel.ShellCore.Ipc.Tests/ComponentBusAuthTests.cs
git commit -m "feat(components): component bus over NetMQ with mandatory HMAC and an ACL'd endpoint file"
```

---

### Task 11: Surface ownership and the remote channel

Owns **Review Focus 3**: authentication proves *a* component, not *which* component.

**Files:**
- Create: `src/Bevel.Taskbar/Components/RemoteComponentChannel.cs`
- Create: `src/Bevel.Taskbar/Components/SurfaceOwnership.cs`
- Test: `tests/Bevel.Taskbar.Tests/SurfaceOwnershipTests.cs`

**Interfaces:**
- Consumes: `IComponentChannel`, `ComponentState` (Task 9); `ComponentBusServer` (Task 10); `MmfBgraPool` (`src/Bevel.UI/MmfBgraPool.cs`).
- Produces: `SurfaceOwnership.Assign(string instanceId, string primitiveKey) → uint slot`, `TryAccept(string instanceId, uint slot) → bool`, `Release(string instanceId)`; `RemoteComponentChannel : IComponentChannel`.

- [ ] **Step 1: Write the failing ownership tests**

Create `tests/Bevel.Taskbar.Tests/SurfaceOwnershipTests.cs`:

```csharp
using Bevel.Taskbar.Components;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-aqr7 Task 11, Review Focus 3: the handshake proves a peer is A component, not WHICH one.
/// Without an ownership check an authenticated component could publish frames into another
/// component's surface slot — cross-component pixel injection.
/// </summary>
public class SurfaceOwnershipTests
{
    [Fact]
    public void An_owner_may_publish_to_its_own_slot()
    {
        var o = new SurfaceOwnership();
        var slot = o.Assign("inst-a", "face");
        Assert.True(o.TryAccept("inst-a", slot));
    }

    [Fact]
    public void A_different_instance_may_not_publish_to_someone_elses_slot()
    {
        var o = new SurfaceOwnership();
        var slotA = o.Assign("inst-a", "face");
        o.Assign("inst-b", "face");
        Assert.False(o.TryAccept("inst-b", slotA));
    }

    [Fact]
    public void An_unassigned_slot_is_refused()
        => Assert.False(new SurfaceOwnership().TryAccept("inst-a", 9999));

    [Fact]
    public void Each_surface_gets_a_distinct_slot()
    {
        var o = new SurfaceOwnership();
        Assert.NotEqual(o.Assign("inst-a", "one"), o.Assign("inst-a", "two"));
    }

    [Fact]
    public void Releasing_an_instance_revokes_all_of_its_slots()
    {
        var o = new SurfaceOwnership();
        var slot = o.Assign("inst-a", "face");
        o.Release("inst-a");
        Assert.False(o.TryAccept("inst-a", slot));
    }

    [Fact]
    public void A_released_slot_is_not_handed_to_the_next_instance()
    {
        var o = new SurfaceOwnership();
        var first = o.Assign("inst-a", "face");
        o.Release("inst-a");
        Assert.NotEqual(first, o.Assign("inst-b", "face"));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter SurfaceOwnershipTests`
Expected: FAIL — `SurfaceOwnership` not defined.

- [ ] **Step 3: Implement ownership**

Create `src/Bevel.Taskbar/Components/SurfaceOwnership.cs`:

```csharp
namespace Bevel.Taskbar.Components;

/// <summary>
/// Maps shared-memory surface slots to the instance allowed to write them. The bus handshake proves a
/// peer is A component; this proves it is the one that owns the slot it is publishing to. Slot numbers
/// are never reused after release, so a late frame from a dead component cannot land in a live slot.
/// </summary>
public sealed class SurfaceOwnership
{
    private readonly Dictionary<uint, string> _owner = new();
    private uint _next = 1;

    /// <summary>Assigns a fresh slot for one surface primitive of one instance.</summary>
    public uint Assign(string instanceId, string primitiveKey)
    {
        var slot = _next++;
        _owner[slot] = instanceId;
        return slot;
    }

    /// <summary>True only when <paramref name="instanceId"/> owns <paramref name="slot"/>.</summary>
    public bool TryAccept(string instanceId, uint slot)
        => _owner.TryGetValue(slot, out var owner) && string.Equals(owner, instanceId, StringComparison.Ordinal);

    /// <summary>Revokes every slot held by an instance, on quarantine or teardown.</summary>
    public void Release(string instanceId)
    {
        foreach (var slot in _owner.Where(kv => kv.Value == instanceId).Select(kv => kv.Key).ToArray())
            _owner.Remove(slot);
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter SurfaceOwnershipTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Write the failing remote-channel test, against a REAL bus**

The IPC path has to be exercised for real, or Task 13's "runs against both channels" claim is hollow. Append to `tests/Bevel.Taskbar.Tests/SurfaceOwnershipTests.cs` a new file instead — create `tests/Bevel.Taskbar.Tests/RemoteComponentChannelTests.cs`:

```csharp
using System.Text;
using Bevel.Components.V1;
using Bevel.Core.Components;
using Bevel.ShellCore.Ipc;
using Bevel.Taskbar.Components;
using Google.Protobuf;
using NetMQ;
using NetMQ.Sockets;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-aqr7 Task 11: the bar-side handle for a component running in another process. The bar hosts
/// the bus (RouterSocket) and the component connects to it (DealerSocket), so this drives a real
/// socket — no in-process double.
/// </summary>
public class RemoteComponentChannelTests
{
    private static readonly byte[] Nonce = Encoding.UTF8.GetBytes("remote-nonce-01234567");
    private const string Cap = "components";

    /// <summary>A stand-in for the component PROCESS: connects, says Hello, then answers with state.</summary>
    private sealed class FakePeer : IDisposable
    {
        private readonly DealerSocket _sock = new();
        public FakePeer(int port, string identity, string instanceId)
        {
            _sock.Options.Identity = Encoding.UTF8.GetBytes(identity);
            _sock.Connect($"tcp://127.0.0.1:{port}");
            _sock.SendFrame(ComponentBusServer.BuildHello(
                instanceId, Cap, Handshake.ComputeHmac(Nonce, Cap), 1));
        }
        public void PublishFolder(string instanceId, string folder)
        {
            var env = new ComponentEnvelope { State = new StatePublish { InstanceId = instanceId, Inert = false } };
            env.State.Values.Add("folder", folder);
            _sock.SendFrame(env.ToByteArray());
        }
        public void Dispose() => _sock.Dispose();
    }

    [Fact]
    public async Task Connect_surfaces_the_state_the_remote_peer_publishes()
    {
        using var bus = new ComponentBusServer(Nonce, Cap);
        var port = bus.BindLoopback();
        var inst = new ComponentInstance("inst-1", TaskbarComponentTypes.Stack,
            new Dictionary<string, string> { ["folder"] = "/Downloads" }, true);

        using var peer = new FakePeer(port, "peer-1", inst.InstanceId);
        Assert.True(bus.WaitForPeer("peer-1", TimeSpan.FromSeconds(5)));

        await using var channel = new RemoteComponentChannel(bus, "peer-1");
        var connect = channel.ConnectAsync(inst, CancellationToken.None);
        peer.PublishFolder(inst.InstanceId, "/Downloads");

        var state = await connect.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(inst.InstanceId, state.InstanceId);
        Assert.Equal("/Downloads", state.Values["folder"]);
        Assert.False(state.Inert);
    }

    [Fact]
    public async Task A_peer_that_never_answers_yields_an_inert_state_rather_than_hanging_forever()
    {
        using var bus = new ComponentBusServer(Nonce, Cap);
        var port = bus.BindLoopback();
        var inst = new ComponentInstance("inst-2", TaskbarComponentTypes.Stack,
            new Dictionary<string, string>(), true);

        using var peer = new FakePeer(port, "peer-2", inst.InstanceId);   // says Hello, never publishes
        Assert.True(bus.WaitForPeer("peer-2", TimeSpan.FromSeconds(5)));

        await using var channel = new RemoteComponentChannel(bus, "peer-2", connectTimeoutMs: 500);
        var state = await channel.ConnectAsync(inst, CancellationToken.None);
        Assert.True(state.Inert);   // a hang is a failure: the slot goes inert, it does not freeze
    }

    [Fact]
    public async Task State_published_for_a_different_instance_is_ignored()
    {
        using var bus = new ComponentBusServer(Nonce, Cap);
        var port = bus.BindLoopback();
        var mine = new ComponentInstance("inst-mine", TaskbarComponentTypes.Stack,
            new Dictionary<string, string>(), true);

        using var peer = new FakePeer(port, "peer-3", mine.InstanceId);
        Assert.True(bus.WaitForPeer("peer-3", TimeSpan.FromSeconds(5)));

        await using var channel = new RemoteComponentChannel(bus, "peer-3", connectTimeoutMs: 600);
        var connect = channel.ConnectAsync(mine, CancellationToken.None);
        peer.PublishFolder("someone-else", "/Elsewhere");

        var state = await connect.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(state.Inert);                       // our instance never got state
        Assert.DoesNotContain("folder", state.Values);  // and did not absorb another instance's
    }
}
```

- [ ] **Step 6: Run to verify it fails**

Run: `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter RemoteComponentChannelTests`
Expected: FAIL — `RemoteComponentChannel` not defined.

- [ ] **Step 7: Implement the remote channel**

Create `src/Bevel.Taskbar/Components/RemoteComponentChannel.cs`:

```csharp
using Bevel.Components.V1;
using Bevel.Core.Components;
using Bevel.ShellCore.Ipc;
using Google.Protobuf;

namespace Bevel.Taskbar.Components;

/// <summary>
/// The bar-side handle for one component running in another process. Same <see
/// cref="IComponentChannel"/> contract as a built-in, so the conformance suite asserts identical
/// behaviour across both paths — which is the whole argument for the contract being public.
///
/// A peer that authenticates but never publishes is a HANG, not a crash, so nothing else would
/// notice: connecting past the timeout yields an inert state rather than waiting forever.
/// </summary>
public sealed class RemoteComponentChannel : IComponentChannel
{
    private readonly ComponentBusServer _bus;
    private readonly string _peerIdentity;
    private readonly int _connectTimeoutMs;
    private readonly object _gate = new();
    private string? _instanceId;
    private ComponentState? _last;

    public RemoteComponentChannel(ComponentBusServer bus, string peerIdentity, int connectTimeoutMs = 5000)
    {
        _bus = bus;
        _peerIdentity = peerIdentity;
        _connectTimeoutMs = connectTimeoutMs;
        _bus.MessageReceived += OnBusMessage;
    }

    public event Action<ComponentState>? StateChanged;

    public async Task<ComponentState> ConnectAsync(ComponentInstance instance, CancellationToken ct)
    {
        lock (_gate) _instanceId = instance.InstanceId;

        var first = new TaskCompletionSource<ComponentState>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Capture(ComponentState s)
        {
            if (s.InstanceId == instance.InstanceId) first.TrySetResult(s);
        }

        StateChanged += Capture;

        // A component may publish before the bar finishes connecting — the bus pump runs on its own
        // thread — so an already-received state must satisfy this connect rather than being missed.
        lock (_gate)
            if (_last is { } cached && cached.InstanceId == instance.InstanceId)
                first.TrySetResult(cached);
        try
        {
            using var timeout = new CancellationTokenSource(_connectTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            var delay = Task.Delay(Timeout.Infinite, linked.Token);

            var done = await Task.WhenAny(first.Task, delay).ConfigureAwait(false);
            if (done == first.Task) return await first.Task.ConfigureAwait(false);

            // Timed out: inert, so the slot shows a placeholder instead of freezing.
            return new ComponentState(instance.InstanceId, new Dictionary<string, string>(), Inert: true);
        }
        finally { StateChanged -= Capture; }
    }

    public Task SendAsync(ComponentInput input, CancellationToken ct)
    {
        var env = new ComponentEnvelope
        {
            Input = new InputEvent
            {
                InstanceId = input.InstanceId, PrimitiveKey = input.PrimitiveKey, Kind = input.Kind,
            },
        };
        _bus.SendTo(_peerIdentity, env.ToByteArray());
        return Task.CompletedTask;
    }

    private void OnBusMessage(string identity, ComponentEnvelope env)
    {
        if (identity != _peerIdentity) return;
        if (env.PayloadCase != ComponentEnvelope.PayloadOneofCase.State) return;

        var state = new ComponentState(
            env.State.InstanceId,
            new Dictionary<string, string>(env.State.Values),
            env.State.Inert);

        lock (_gate)
        {
            // A peer may only speak for the instance this channel was created for. Before ConnectAsync
            // has run there is no instance yet, so cache it and let the connect validate.
            if (_instanceId is not null && state.InstanceId != _instanceId) return;
            _last = state;
        }

        StateChanged?.Invoke(state);
    }

    public ValueTask DisposeAsync()
    {
        _bus.MessageReceived -= OnBusMessage;
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 8: Run the tests**

Run: `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter "SurfaceOwnershipTests|RemoteComponentChannelTests"`
Expected: PASS (6 + 3 tests).

Add the project reference if the build complains that `Bevel.ShellCore.Ipc` is not visible from `Bevel.Taskbar`:

```xml
<ProjectReference Include="..\Bevel.ShellCore.Ipc\Bevel.ShellCore.Ipc.csproj" />
```

- [ ] **Step 9: Commit**

```bash
git add src/Bevel.Taskbar/Components tests/Bevel.Taskbar.Tests/SurfaceOwnershipTests.cs tests/Bevel.Taskbar.Tests/RemoteComponentChannelTests.cs
git commit -m "feat(components): slot ownership plus the bar-side remote channel over a real bus"
```

---

### Task 12: Failure isolation — quarantine, watchdog, and a separate health budget

`ShellHealthMonitor` holds a `CrashLoop` fault at 3 respawns and then stops bringing a role back. In Windows shell mode that is **no shell**. A component must never reach that counter.

**Files:**
- Create: `src/Bevel.Taskbar/Components/ComponentHealth.cs`
- Test: `tests/Bevel.Taskbar.Tests/ComponentHealthTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `ComponentHealth(int crashBudget = 3, int watchdogMs = 5000)`; `RecordCrash(string instanceId) → ComponentVerdict`; `RecordHeartbeat(string instanceId)`; `CheckWatchdog(string instanceId, DateTime now) → ComponentVerdict`; `Reset(string instanceId)`; `enum ComponentVerdict { Retry, Quarantine, Healthy }`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Bevel.Taskbar.Tests/ComponentHealthTests.cs`:

```csharp
using System;
using Bevel.Taskbar.Components;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-aqr7 Task 12: components get their OWN health budget. ShellHealthMonitor holds a CrashLoop
/// fault at 3 respawns and then stops bringing the role back — in Windows shell mode that means no
/// shell at all — so a crash-looping component must be quarantined locally and never counted there.
/// </summary>
public class ComponentHealthTests
{
    [Fact]
    public void Crashes_under_budget_retry()
    {
        var h = new ComponentHealth(crashBudget: 3);
        Assert.Equal(ComponentVerdict.Retry, h.RecordCrash("a"));
        Assert.Equal(ComponentVerdict.Retry, h.RecordCrash("a"));
    }

    [Fact]
    public void Exhausting_the_budget_quarantines_rather_than_respawning_forever()
    {
        var h = new ComponentHealth(crashBudget: 3);
        h.RecordCrash("a");
        h.RecordCrash("a");
        Assert.Equal(ComponentVerdict.Quarantine, h.RecordCrash("a"));
        Assert.Equal(ComponentVerdict.Quarantine, h.RecordCrash("a"));   // stays quarantined
    }

    [Fact]
    public void One_components_budget_is_independent_of_another()
    {
        var h = new ComponentHealth(crashBudget: 3);
        h.RecordCrash("a"); h.RecordCrash("a"); h.RecordCrash("a");
        Assert.Equal(ComponentVerdict.Retry, h.RecordCrash("b"));
    }

    [Fact]
    public void A_hang_is_a_failure_even_though_nothing_crashed()
    {
        var h = new ComponentHealth(watchdogMs: 5000);
        var t0 = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        h.RecordHeartbeat("a");
        Assert.Equal(ComponentVerdict.Quarantine, h.CheckWatchdog("a", t0.AddMilliseconds(6000)));
    }

    [Fact]
    public void A_live_heartbeat_keeps_the_component_healthy()
    {
        var h = new ComponentHealth(watchdogMs: 5000);
        var t0 = DateTime.UtcNow;
        h.RecordHeartbeat("a");
        Assert.Equal(ComponentVerdict.Healthy, h.CheckWatchdog("a", t0.AddMilliseconds(100)));
    }

    [Fact]
    public void Reset_clears_a_quarantine_so_the_user_can_re_enable()
    {
        var h = new ComponentHealth(crashBudget: 1);
        Assert.Equal(ComponentVerdict.Quarantine, h.RecordCrash("a"));
        h.Reset("a");
        Assert.Equal(ComponentVerdict.Retry, h.RecordCrash("a"));
    }

    [Fact]
    public void An_unknown_instance_is_healthy_rather_than_throwing()
        => Assert.Equal(ComponentVerdict.Healthy, new ComponentHealth().CheckWatchdog("never-seen", DateTime.UtcNow));
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter ComponentHealthTests`
Expected: FAIL — `ComponentHealth` not defined.

- [ ] **Step 3: Implement**

Create `src/Bevel.Taskbar/Components/ComponentHealth.cs`:

```csharp
namespace Bevel.Taskbar.Components;

/// <summary>What to do with a component after a crash or a watchdog check.</summary>
public enum ComponentVerdict { Healthy, Retry, Quarantine }

/// <summary>
/// Per-component crash and liveness budget, deliberately SEPARATE from ShellHealthMonitor's role
/// budget: a looping component must never trip the shell's CrashLoop hold, because that hold stops
/// respawning the taskbar and in Windows shell mode leaves the user with no shell.
///
/// Quarantine beats infinite respawn — an exhausted component's slot renders inert with a "component
/// failed" affordance until the user re-enables it or the bar restarts.
/// </summary>
public sealed class ComponentHealth
{
    private readonly int _crashBudget;
    private readonly int _watchdogMs;
    private readonly Dictionary<string, int> _crashes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _heartbeats = new(StringComparer.Ordinal);
    private readonly HashSet<string> _quarantined = new(StringComparer.Ordinal);

    public ComponentHealth(int crashBudget = 3, int watchdogMs = 5000)
    {
        _crashBudget = Math.Max(1, crashBudget);
        _watchdogMs = Math.Max(100, watchdogMs);
    }

    /// <summary>Records a crash and says whether to retry or quarantine. Idempotent once quarantined.</summary>
    public ComponentVerdict RecordCrash(string instanceId)
    {
        if (_quarantined.Contains(instanceId)) return ComponentVerdict.Quarantine;

        var n = _crashes.TryGetValue(instanceId, out var prior) ? prior + 1 : 1;
        _crashes[instanceId] = n;

        if (n < _crashBudget) return ComponentVerdict.Retry;
        _quarantined.Add(instanceId);
        return ComponentVerdict.Quarantine;
    }

    /// <summary>Notes that the component is alive.</summary>
    public void RecordHeartbeat(string instanceId) => _heartbeats[instanceId] = DateTime.UtcNow;

    /// <summary>
    /// A component that stopped answering is not crashed, so nothing else would notice. Past the
    /// deadline it is quarantined and its slot goes inert rather than silently freezing.
    /// </summary>
    public ComponentVerdict CheckWatchdog(string instanceId, DateTime now)
    {
        if (_quarantined.Contains(instanceId)) return ComponentVerdict.Quarantine;
        if (!_heartbeats.TryGetValue(instanceId, out var last)) return ComponentVerdict.Healthy;

        if ((now - last).TotalMilliseconds <= _watchdogMs) return ComponentVerdict.Healthy;
        _quarantined.Add(instanceId);
        return ComponentVerdict.Quarantine;
    }

    /// <summary>Clears crash count and quarantine, for an explicit user re-enable or a bar restart.</summary>
    public void Reset(string instanceId)
    {
        _crashes.Remove(instanceId);
        _heartbeats.Remove(instanceId);
        _quarantined.Remove(instanceId);
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter ComponentHealthTests`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Bevel.Taskbar/Components/ComponentHealth.cs tests/Bevel.Taskbar.Tests/ComponentHealthTests.cs
git commit -m "feat(components): per-component crash budget and watchdog, isolated from the shell's CrashLoop hold"
```

---

### Task 13: The Downloads stack as the first component — via the third-party route

The precursor. Built deliberately through the **public** path so the contract cannot grow a shortcut only built-ins can take.

**Files:**
- Create: `src/Bevel.Taskbar/Components/LocalComponentChannel.cs`
- Create: `src/Bevel.Taskbar/Components/StackComponentManifest.cs`
- Test: `tests/Bevel.Taskbar.Tests/ComponentConformanceTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces: `LocalComponentChannel : IComponentChannel`; `StackComponentManifest.Create() → ComponentManifest`.

- [ ] **Step 1: Write the conformance tests — the same assertions over both channels**

Create `tests/Bevel.Taskbar.Tests/ComponentConformanceTests.cs`:

```csharp
using System.Text;
using Bevel.Components.V1;
using Bevel.Core.Components;
using Bevel.ShellCore.Ipc;
using Bevel.Taskbar.Components;
using Google.Protobuf;
using NetMQ;
using NetMQ.Sockets;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-aqr7 Task 13: the conformance suite. Every assertion runs against BOTH channel
/// implementations, because that identity is the whole argument for the contract being public rather
/// than theoretical — if a built-in can do something a third-party component cannot, the contract has
/// grown a privileged shortcut and this suite fails.
/// </summary>
public class ComponentConformanceTests
{
    public static IEnumerable<object[]> Channels() => new[]
    {
        new object[] { "local" },
        new object[] { "remote" },   // a REAL bus and a real socket, not an in-process double
    };

    /// <summary>
    /// Builds a channel plus whatever must stay alive behind it. "remote" stands up an actual
    /// ComponentBusServer and a Dealer peer, because a conformance suite that compares a built-in
    /// against a test double proves nothing about the IPC path.
    /// </summary>
    private static (IComponentChannel Channel, IDisposable Scope) Make(string kind, ComponentInstance inst)
    {
        if (kind == "local")
            return (new LocalComponentChannel(
                i => new ComponentState(i.InstanceId,
                    new Dictionary<string, string> { ["folder"] = i.Settings.GetValueOrDefault("folder", "") },
                    false)),
                new NullScope());

        var scope = new RemoteScope(inst);
        return (scope.Channel, scope);
    }

    private sealed class NullScope : IDisposable { public void Dispose() { } }

    /// <summary>Owns the bus and the peer for one remote-channel case, and answers its state.</summary>
    private sealed class RemoteScope : IDisposable
    {
        private static readonly byte[] Nonce = Encoding.UTF8.GetBytes("conformance-nonce-012");
        private const string Cap = "components";

        private readonly ComponentBusServer _bus;
        private readonly DealerSocket _peer = new();
        public RemoteComponentChannel Channel { get; }

        public RemoteScope(ComponentInstance inst)
        {
            _bus = new ComponentBusServer(Nonce, Cap);
            var port = _bus.BindLoopback();

            var identity = "peer-" + inst.InstanceId;
            _peer.Options.Identity = Encoding.UTF8.GetBytes(identity);
            _peer.Connect($"tcp://127.0.0.1:{port}");
            _peer.SendFrame(ComponentBusServer.BuildHello(
                inst.InstanceId, Cap, Handshake.ComputeHmac(Nonce, Cap), 1));
            Assert.True(_bus.WaitForPeer(identity, TimeSpan.FromSeconds(5)));

            Channel = new RemoteComponentChannel(_bus, identity, connectTimeoutMs: 4000);

            // Answer the bar's connect with this instance's state, mirroring what a real component does.
            var env = new ComponentEnvelope { State = new StatePublish { InstanceId = inst.InstanceId } };
            env.State.Values.Add("folder", inst.Settings.GetValueOrDefault("folder", ""));
            _peer.SendFrame(env.ToByteArray());
        }

        public void Dispose()
        {
            _peer.Dispose();
            _bus.Dispose();
        }
    }

    private static ComponentInstance StackInstance(string folder) => new(
        ComponentInstance.NewId(), TaskbarComponentTypes.Stack,
        new Dictionary<string, string> { ["folder"] = folder }, Visible: true);

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task Connect_returns_state_for_the_instance_that_asked(string kind)
    {
        var inst = StackInstance("/Downloads");
        var (ch, scope) = Make(kind, inst);
        using (scope)
        await using (ch)
        {
            var state = await ch.ConnectAsync(inst, CancellationToken.None);
            Assert.Equal(inst.InstanceId, state.InstanceId);
            Assert.False(state.Inert);
        }
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task An_instances_own_settings_reach_its_state(string kind)
    {
        var inst = StackInstance("/Pictures");
        var (ch, scope) = Make(kind, inst);
        using (scope)
        await using (ch)
        {
            var state = await ch.ConnectAsync(inst, CancellationToken.None);
            Assert.Equal("/Pictures", state.Values["folder"]);
        }
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task Two_instances_do_not_share_state(string kind)
    {
        var ia = StackInstance("/one");
        var ib = StackInstance("/two");
        var (ca, sa) = Make(kind, ia);
        var (cb, sb) = Make(kind, ib);
        using (sa) using (sb)
        await using (ca)
        await using (cb)
        {
            var a = await ca.ConnectAsync(ia, CancellationToken.None);
            var b = await cb.ConnectAsync(ib, CancellationToken.None);
            Assert.NotEqual(a.InstanceId, b.InstanceId);
            Assert.Equal("/one", a.Values["folder"]);
            Assert.Equal("/two", b.Values["folder"]);
        }
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task Input_is_accepted_without_throwing(string kind)
    {
        var inst = StackInstance("/Downloads");
        var (ch, scope) = Make(kind, inst);
        using (scope)
        await using (ch)
        {
            await ch.ConnectAsync(inst, CancellationToken.None);
            await ch.SendAsync(new ComponentInput(inst.InstanceId, "grid", "tapped"), CancellationToken.None);
        }
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task Disposing_twice_is_safe(string kind)
    {
        var inst = StackInstance("/Downloads");
        var (ch, scope) = Make(kind, inst);
        using (scope)
        {
            await ch.DisposeAsync();
            await ch.DisposeAsync();
        }
    }

    [Fact]
    public void The_stack_manifest_is_valid_and_multi_instance()
    {
        var m = StackComponentManifest.Create();
        Assert.True(ManifestValidator.Validate(m).IsValid);
        Assert.True(m.MultiInstance);
        Assert.Equal(TaskbarComponentTypes.Stack, m.Id);
        Assert.Contains(m.SettingsSchema, f => f.Key == "folder");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter ComponentConformanceTests`
Expected: FAIL — `LocalComponentChannel` not defined.

- [ ] **Step 3: Implement the local channel**

Create `src/Bevel.Taskbar/Components/LocalComponentChannel.cs`:

```csharp
using Bevel.Core.Components;

namespace Bevel.Taskbar.Components;

/// <summary>
/// A built-in component's channel: the same <see cref="IComponentChannel"/> contract with the IPC hop
/// removed. Built-ins are first-party and share the taskbar's trust, so a clock does not cost a
/// process — but it goes through the identical manifest, primitive vocabulary and settings schema, and
/// the conformance suite asserts the two paths behave the same.
/// </summary>
public sealed class LocalComponentChannel : IComponentChannel
{
    private readonly Func<ComponentInstance, ComponentState> _project;

    public LocalComponentChannel(Func<ComponentInstance, ComponentState> project) => _project = project;

    public event Action<ComponentState>? StateChanged;

    public Task<ComponentState> ConnectAsync(ComponentInstance instance, CancellationToken ct)
    {
        var state = _project(instance);
        StateChanged?.Invoke(state);
        return Task.FromResult(state);
    }

    public Task SendAsync(ComponentInput input, CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

- [ ] **Step 4: Declare the stack's manifest**

Create `src/Bevel.Taskbar/Components/StackComponentManifest.cs`:

```csharp
using Bevel.Core.Components;

namespace Bevel.Taskbar.Components;

/// <summary>
/// The Downloads stack as a component, and the contract's first proof. Chosen as the precursor because
/// it already exercises every hard part: multi-instance (TaskbarStacks was a string[]), per-instance
/// runtime state (one PreviewLoader per stack, so caches never evict each other), a real flyout, and
/// decoded content previews as the natural first `surface`.
/// </summary>
public static class StackComponentManifest
{
    public static ComponentManifest Create() => new(
        Id: TaskbarComponentTypes.Stack,
        ContractVersion: ManifestValidator.CurrentContractVersion,
        DisplayName: "Folder stack",
        Description: "A folder's most recent contents, as a grid flyout.",
        MultiInstance: true,
        Sizing: ComponentSizing.Content,
        RequiresCapability: null,
        SettingsSchema: new[]
        {
            new ComponentSettingsField("folder", ComponentFieldKind.Path, "Folder", "", null, null),
            new ComponentSettingsField("maxItems", ComponentFieldKind.Int, "Items shown", "16", null, (1, 64)),
        },
        View: new ComponentPrimitive[]
        {
            new GlyphPrimitive("icon", "folder"),
            new FlyoutPrimitive("grid", new ComponentPrimitive[]
            {
                new SurfacePrimitive("previews", 320, 98, "Recent items", "List"),
            }),
        });
}
```

- [ ] **Step 5: Run the conformance suite**

Run: `dotnet test tests/Bevel.Taskbar.Tests/Bevel.Taskbar.Tests.csproj --filter ComponentConformanceTests`
Expected: PASS (9 cases — 4 theories × 2 channels, plus the manifest test).

- [ ] **Step 6: Run everything**

Run: `dotnet build Bevel.sln -clp:ErrorsOnly && dotnet test Bevel.sln`
Expected: only the two known failures from Global Constraints.

- [ ] **Step 7: Close out on the bead**

```bash
bd update bevel-aqr7 --append-notes="Sub-project 1 complete: contract, registry, ordered-list panel, persistence + 29-key migration, BarGeometry replacing TaskbarTheme statics, component bus with mandatory HMAC, surface slot ownership, per-component health budget, and the conformance suite running local-bound and IPC-bound. Downloads stack is the first component via the third-party route."
```

- [ ] **Step 8: Commit**

```bash
git add src/Bevel.Taskbar/Components tests/Bevel.Taskbar.Tests/ComponentConformanceTests.cs
git commit -m "feat(components): Downloads stack as the first component, with conformance over both channels"
```

---

## Deferred to later sub-projects

Named here so no one implements them by accident:

| Deferred | Sub-project |
|---|---|
| Discovery and loading of out-of-repo components; contract versioning policy | 2 |
| **Connection-flood limiting on the component bus** — required by spec §5.3.1, but until sub-project 2 ships external loading there are no third-party peers, so the bus has no untrusted connectors yet. It must land **with** external loading, not after. | 2 |
| ZMQ CURVE on the component bus (spec §5.3.1, "under consideration") | 2 |
| Replacing `OnboardingWindow`'s taskbar sections with the schema-driven editor | 3 |
| Zone helpers and wizards (leading / centre / trailing buckets) | 4 (v2) |
| Migrating the Swift helper off gRPC-swift | 5 |
| `ComponentSlot.cs` (rendered slot, inert/quarantine chrome) and `SurfaceHost.cs` (`surface` over `MmfBgraPool`) | follow-on to this plan |
| Wiring `TaskbarView.axaml` to the panel and migrating start/strip/tray/clock | follow-on to this plan |
| `bevel-zhmr` perf baseline on the component channel | before the contract freezes |
