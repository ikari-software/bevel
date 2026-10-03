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
