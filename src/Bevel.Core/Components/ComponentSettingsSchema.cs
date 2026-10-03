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
