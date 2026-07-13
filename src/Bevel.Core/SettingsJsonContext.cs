using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bevel.Core;

/// <summary>
/// Source-generated JSON metadata for the settings store (bevel-gww.7): serialization then carries no
/// reflection dependency, so it survives trimming and NativeAOT. Registers every type serialized
/// through <see cref="SettingsService"/>'s options — the raw string→element bag, the typed settings and
/// per-theme overrides, and the primitive element writes in SerializeRaw. The
/// <see cref="JsonSourceGenerationOptionsAttribute"/> pins the same formatting the service used before
/// (indented, case-insensitive, skip-null), so the on-disk blob is byte-compatible.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(BevelSettings))]
[JsonSerializable(typeof(ThemeOverrides))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
internal partial class SettingsJsonContext : JsonSerializerContext;
