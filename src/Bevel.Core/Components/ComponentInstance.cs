using System.Text.Json.Serialization;

namespace Bevel.Core.Components;

/// <summary>
/// A PLACEMENT of a component type on a bar. Identity, settings values and runtime state all belong
/// to the instance rather than the type (spec §3.2). Settings nest here rather than in a side table
/// keyed by id, which makes orphaned settings structurally impossible.
/// </summary>
public sealed record ComponentInstance(
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("typeId")] string TypeId,
    [property: JsonPropertyName("settings")] Dictionary<string, string> Settings,
    [property: JsonPropertyName("visible")] bool Visible)
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
    /// <c>BevelSettings.CopyFrom</c> would otherwise alias it between snapshots.
    /// Named <c>DeepClone</c> rather than <c>Clone</c>: C# disallows any record member literally named
    /// <c>Clone</c> (CS8859) because that name is reserved for the compiler-synthesized copy used by
    /// <c>with</c> expressions.</summary>
    public ComponentInstance DeepClone()
        => this with { Settings = new Dictionary<string, string>(Settings) };
}
