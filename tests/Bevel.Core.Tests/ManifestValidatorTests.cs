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
