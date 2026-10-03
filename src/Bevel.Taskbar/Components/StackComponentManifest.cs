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
