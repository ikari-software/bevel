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
