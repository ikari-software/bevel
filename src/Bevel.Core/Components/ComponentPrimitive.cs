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
